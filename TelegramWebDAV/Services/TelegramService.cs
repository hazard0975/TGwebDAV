using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TelegramWebDAV.Config;
using TelegramWebDAV.Database;
using TelegramWebDAV.Models;
using TL;

namespace TelegramWebDAV.Services
{
    public enum AuthStep
    {
        NeedsPhone,
        NeedsCode,
        Needs2FA,
        Authorized
    }

    public class TelegramUserInfo
    {
        public long Id { get; set; }
        public string? Username { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Phone { get; set; }
        public bool IsPremium { get; set; }
        public int FloodWaitSecondsRemaining { get; set; }
    }

    /// <summary>
    /// Сервис для работы с Telegram API через WTelegramClient.
    /// Управляет подключением, сессией (.session), многошаговой авторизацией и защитой от FLOOD_WAIT.
    /// </summary>
    public class TelegramService : IDisposable
    {
        private readonly ConfigManager _configManager;
        private NodeRepository? _repository;
        private AppSettings _currentSettings;
        private readonly SemaphoreSlim _floodLock = new SemaphoreSlim(1, 1);
        private DateTime _floodWaitUntil = DateTime.MinValue;
        private WTelegram.Client? _client;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, WTelegram.Client> _workerClientsPool = new();
        private System.Threading.CancellationTokenSource? _queueCts;

        // Очередь последовательной загрузки файлов в Telegram (Upload Queue)
        // Предотвращает конкуренцию за полосу пропускания, мерцание оверлея и FLOOD_WAIT
        private readonly SemaphoreSlim _uploadSemaphore = new SemaphoreSlim(1, 1);
        private int _pendingUploadsCount = 0;

        /// <summary>
        /// Количество файлов в очереди на отправку в Telegram (включая текущий передаваемый).
        /// </summary>
        public int PendingUploadsCount => _pendingUploadsCount;

        // Кэш дескрипторов документов Telegram (TL.Document) для устранения лишних сетевых вызовов Channels_GetMessages
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (TL.Document document, DateTime expiresAt)> _documentCache = new();

        // Быстрый кольцевой кэш чанков MTProto в оперативной памяти (~32 МБ) для мгновенного чтения плеерами без повторных обращений к сети
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] data, DateTime expiresAt)> _chunkMemoryCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>> _pendingPrefetches = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _fileDownloadLocks = new();
        private readonly SemaphoreSlim _downloadRpcSemaphore = new SemaphoreSlim(1, 1);

        private bool TryGetFromMemoryCache(int messageId, long pos, out byte[]? data, out int offsetInChunk)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _chunkMemoryCache)
            {
                var parts = kvp.Key.Split(':');
                if (parts.Length == 3 && int.TryParse(parts[0], out var mId) && mId == messageId)
                {
                    if (long.TryParse(parts[1], out var chunkStart) && int.TryParse(parts[2], out var _))
                    {
                        var chunkData = kvp.Value.data;
                        if (pos >= chunkStart && pos < chunkStart + chunkData.Length && kvp.Value.expiresAt > now)
                        {
                            data = chunkData;
                            offsetInChunk = (int)(pos - chunkStart);
                            return true;
                        }
                    }
                }
            }
            data = null;
            offsetInChunk = 0;
            return false;
        }

        private void TriggerPrefetch(WTelegram.Client client, TL.InputFileLocationBase location, int messageId, long offset, int limit)
        {
            string chunkKey = $"{messageId}:{offset}:{limit}";
            if (_chunkMemoryCache.ContainsKey(chunkKey)) return;

            _pendingPrefetches.GetOrAdd(chunkKey, _ => Task.Run(async () =>
            {
                await EnsureFloodWaitDelayAsync();
                await _downloadRpcSemaphore.WaitAsync();
                try
                {
                    await EnsureFloodWaitDelayAsync();
                    AppLogger.Info("TelegramService", $"[Prefetch] Запрос упреждающего чанка из Telegram (ID {messageId}, смещение {offset:N0}, размер {limit / 1024} КБ)...");
                    var fileBase = await client.Upload_GetFile(location, offset, limit, precise: true);
                    if (fileBase is TL.Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                    {
                        EnsureChunkCacheCapacity();
                        _chunkMemoryCache[chunkKey] = (uploadFile.bytes, DateTime.UtcNow.AddMinutes(5));
                        AppLogger.Info("TelegramService", $"[Prefetch] Чанк успешно сохранен в RAM (ID {messageId}, смещение {offset:N0}, получено {uploadFile.bytes.Length:N0} байт).");
                        return uploadFile.bytes;
                    }
                }
                catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                {
                    int waitSec = rpcEx.X > 0 ? rpcEx.X : 2;
                    AppLogger.Warn("TelegramService", $"[FLOOD_WAIT] Префетч получил запрос паузы от Telegram на {waitSec} сек.");
                    _floodWaitUntil = DateTime.UtcNow.AddSeconds(waitSec);
                }
                catch
                {
                    // Фоновый префетч не должен выбрасывать необработанных исключений
                }
                finally
                {
                    _downloadRpcSemaphore.Release();
                    _pendingPrefetches.TryRemove(chunkKey, out Task<byte[]?>? _);
                }
                return null;
            }));
        }

        private void EnsureChunkCacheCapacity()
        {
            int maxChunks = 128; // По умолчанию 128 МБ в ОЗУ
            try
            {
                int configMb = _configManager?.CurrentSettings?.Server?.MemoryCacheSizeMb ?? 128;
                maxChunks = Math.Max(16, configMb); // 1 чанк = 1 МБ
            }
            catch { }

            if (_chunkMemoryCache.Count > maxChunks)
            {
                var now = DateTime.UtcNow;
                foreach (var key in _chunkMemoryCache.Keys)
                {
                    if (_chunkMemoryCache.TryGetValue(key, out var item) && item.expiresAt <= now)
                    {
                        _chunkMemoryCache.TryRemove(key, out _);
                    }
                }
                if (_chunkMemoryCache.Count > maxChunks)
                {
                    int toRemove = Math.Max(8, _chunkMemoryCache.Count - maxChunks);
                    var oldest = _chunkMemoryCache.OrderBy(k => k.Value.expiresAt).Take(toRemove).ToList();
                    foreach (var kvp in oldest)
                    {
                        _chunkMemoryCache.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        public bool IsAuthorized { get; private set; }
        public AuthStep CurrentStep { get; private set; } = AuthStep.NeedsPhone;
        public TelegramUserInfo? CurrentUser { get; private set; }
        public string? LastError { get; private set; }

        /// <summary>
        /// Событие прогресса загрузки файла в Telegram (имя файла, передано байт, всего байт).
        /// </summary>
        public event Action<string, long, long>? OnUploadProgress;

        public void TriggerUploadProgress(string fileName, long current, long total)
        {
            OnUploadProgress?.Invoke(fileName, current, total);
        }

        /// <summary>
        /// Событие завершения загрузки файла в Telegram.
        /// </summary>
        public event Action<string>? OnUploadCompleted;

        public void TriggerUploadCompleted(string fileName)
        {
            OnUploadCompleted?.Invoke(fileName);
        }

        /// <summary>
        /// Событие прогресса скачивания файла из Telegram (имя файла, скачано байт, всего байт).
        /// </summary>
        public event Action<string, long, long>? OnDownloadProgress;

        /// <summary>
        /// Событие запроса метаданных / тегов файла из Telegram.
        /// </summary>
        public event Action<string, long, long>? OnMetadataProgress;

        /// <summary>
        /// Событие кэширования отдельных чанков / метаданных / тегов файла в ОЗУ.
        /// </summary>
        public event Action<string, long, long>? OnChunkCached;

        /// <summary>
        /// Событие завершения скачивания файла из Telegram.
        /// </summary>
        public event Action<string>? OnDownloadCompleted;

        public TelegramService(ConfigManager configManager, NodeRepository? repository = null)
        {
            _configManager = configManager;
            _repository = repository;
            _currentSettings = _configManager.Load();
            StartCaptionQueueWorker();
        }

        public void SetRepository(NodeRepository repository)
        {
            _repository = repository;
            StartCaptionQueueWorker();
        }

        private void StartCaptionQueueWorker()
        {
            if (_queueCts != null) return;
            _queueCts = new System.Threading.CancellationTokenSource();
            Task.Run(() => ProcessCaptionQueueAsync(_queueCts.Token));
        }

        private async Task ProcessCaptionQueueAsync(System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (IsAuthorized && _client != null && _repository != null)
                    {
                        var item = _repository.GetNextPendingCaptionUpdate();
                        if (item != null)
                        {
                            await EnsureFloodWaitDelayAsync();
                            await UpdateMessageCaptionAsync(item.TgMessageId, item.NewCaption);
                            _repository.RemovePendingCaptionUpdate(item.Id);
                            await Task.Delay(120, token); // ~8 файлов в секунду: ровно, плавно, без лимитов Telegram
                            continue;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                {
                    AppLogger.Warn("TelegramService", $"FloodWait при фоновом обновлении подписей: пауза {rpcEx.X} секунд...");
                    await Task.Delay(Math.Max(5000, rpcEx.X * 1000), token);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("TelegramService", $"Ошибка обработки фоновой очереди подписей: {ex.Message}");
                    await Task.Delay(2000, token);
                }

                await Task.Delay(1000, token);
            }
        }

        private TL.InputPeer? _storagePeer;
        private readonly SemaphoreSlim _storageLock = new SemaphoreSlim(1, 1);

        public void UpdateApiCredentials(int apiId, string apiHash)
        {
            _currentSettings.Telegram.ApiId = apiId;
            _currentSettings.Telegram.ApiHash = apiHash;
            _storagePeer = null;
            try { _client?.Dispose(); } catch { }
            _client = null;
            foreach (var workerClient in _workerClientsPool.Values)
            {
                try { workerClient.Dispose(); } catch { }
            }
            _workerClientsPool.Clear();
            _ = ConnectAsync();
        }

        public void UpdateStorageChannelTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            if (_currentSettings.Telegram.StorageChannelTitle != title)
            {
                _currentSettings.Telegram.StorageChannelTitle = title.Trim();
                _currentSettings.Telegram.StorageChannelId = 0; // Сбрасываем ID для поиска или создания с новым именем
                _currentSettings.Telegram.StorageChannelAccessHash = 0;
                _storagePeer = null;
                _configManager.Save(_currentSettings);
            }
        }

        /// <summary>
        /// Сбрасывает закэшированный канал-хранилище (например, при удалении канала пользователем).
        /// </summary>
        public void InvalidateStoragePeer()
        {
            _storagePeer = null;
            _currentSettings.Telegram.StorageChannelAccessHash = 0;
            _configManager.Save(_currentSettings);
        }

        /// <summary>
        /// Получает или создает приватный канал-хранилище в Telegram для WebDAV файлов.
        /// </summary>
        public async Task<TL.InputPeer> GetStoragePeerAsync()
        {
            if (_storagePeer != null)
                return _storagePeer;

            await _storageLock.WaitAsync();
            try
            {
                if (_storagePeer != null)
                    return _storagePeer;

                if (_client == null || !IsAuthorized)
                    throw new InvalidOperationException("Клиент Telegram не авторизован.");

                string targetTitle = string.IsNullOrWhiteSpace(_currentSettings.Telegram.StorageChannelTitle)
                    ? "Telegram WebDAV Drive"
                    : _currentSettings.Telegram.StorageChannelTitle.Trim();

                // 1. Если StorageChannelId и StorageChannelAccessHash уже сохранены в настройках — мгновенно используем их без сетевых запросов!
                if (_currentSettings.Telegram.StorageChannelId != 0 && _currentSettings.Telegram.StorageChannelAccessHash != 0)
                {
                    _storagePeer = new TL.InputPeerChannel(_currentSettings.Telegram.StorageChannelId, _currentSettings.Telegram.StorageChannelAccessHash);
                    AppLogger.Info("TelegramService", $"Подключен канал-хранилище из настроек: '{targetTitle}' (ID: {_currentSettings.Telegram.StorageChannelId}).");
                    return _storagePeer;
                }

                // 2. Если StorageChannelId есть, но хэш еще не сохранен (или сброшен) — находим канал в диалогах
                if (_currentSettings.Telegram.StorageChannelId != 0)
                {
                    var chats = await _client.Messages_GetAllChats();
                    if (chats.chats.TryGetValue(_currentSettings.Telegram.StorageChannelId, out var savedChat) &&
                        savedChat is TL.Channel sc)
                    {
                        _storagePeer = sc.ToInputPeer();
                        _currentSettings.Telegram.StorageChannelAccessHash = sc.access_hash;
                        _configManager.Save(_currentSettings);
                        AppLogger.Info("TelegramService", $"Подключен существующий приватный канал-хранилище: {sc.Title} (ID: {sc.ID}, AccessHash сохранен в конфиг).");
                        return _storagePeer;
                    }
                    else
                    {
                        AppLogger.Warn("TelegramService", $"Канал с сохраненным ID {_currentSettings.Telegram.StorageChannelId} не найден в диалогах пользователя. Создаем новый.");
                    }
                }

                // 3. Если ID канала нет в конфиге (StorageChannelId == 0), создаем новый приватный канал
                AppLogger.Info("TelegramService", $"ID канала-хранилища не задан. Создание нового приватного канала '{targetTitle}'...");
                var createReq = new TL.Methods.Channels_CreateChannel
                {
                    flags = TL.Methods.Channels_CreateChannel.Flags.broadcast,
                    title = targetTitle,
                    about = "Приватное облачное хранилище файлов для Telegram WebDAV Drive"
                };

                var createdUpdates = await _client.Invoke(createReq);

                if (createdUpdates is TL.Updates updates)
                {
                    foreach (var chat in updates.chats.Values)
                    {
                        if (chat is TL.Channel newCh)
                        {
                            _storagePeer = newCh.ToInputPeer();
                            _currentSettings.Telegram.StorageChannelId = newCh.ID;
                            _currentSettings.Telegram.StorageChannelAccessHash = newCh.access_hash;
                            _configManager.Save(_currentSettings);
                            AppLogger.Info("TelegramService", $"Создан новый приватный канал '{newCh.Title}' (ID: {newCh.ID}). ID и AccessHash сохранены в конфиг.");
                            return _storagePeer;
                        }
                    }
                }

                throw new InvalidOperationException($"Не удалось создать приватный канал '{targetTitle}' в Telegram для хранения файлов.");
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Подключение к серверам Telegram и проверка наличия готовой сессии через WTelegramClient.
        /// </summary>
        public async Task ConnectAsync()
        {
            AppLogger.Info("TelegramService", "Проверка сессии и подключение к Telegram...");
            _currentSettings = _configManager.Load();
            
            if (_currentSettings.Telegram.ApiId == 0 || string.IsNullOrWhiteSpace(_currentSettings.Telegram.ApiHash))
            {
                LastError = "API ID или API Hash не заданы.";
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsPhone;
                return;
            }

            string sessionPath = _currentSettings.Telegram.SessionPath;
            if (!File.Exists(sessionPath))
            {
                AppLogger.Info("TelegramService", "Файл сессии не найден. Ожидается ввод номера телефона пользователем.");
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsPhone;
                LastError = null;
                try { _client?.Dispose(); } catch { }
                _client = null;
                return;
            }

            try
            {
                InitClient();
                if (_client == null) return;

                // Попытка войти по существующей сессии на диске
                string? result = await _client.Login(null);
                HandleWTelegramResult(result);
            }
            catch (Exception ex)
            {
                AppLogger.Error("TelegramService", $"Ошибка подключения существующей сессии: {ex.Message}", ex);
                LastError = ex.Message;
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsPhone;
                // Сбрасываем экземпляр клиента, чтобы не оставлять его в сбойном (Faulted) состоянии
                try { _client?.Dispose(); } catch { }
                _client = null;
            }
        }

        private void InitClient()
        {
            if (_client != null) return;

            string sessionPath = _currentSettings.Telegram.SessionPath;
            _client = new WTelegram.Client(what =>
            {
                switch (what)
                {
                    case "api_id": return _currentSettings.Telegram.ApiId.ToString();
                    case "api_hash": return _currentSettings.Telegram.ApiHash;
                    case "session_pathname": return sessionPath;
                    case "phone_number": return _currentSettings.Telegram.PhoneNumber;
                    default: return null;
                }
            });
        }

        /// <summary>
        /// Возвращает или создает независимый клиент MTProto со своим TCP-соединением для каждого параллельного воркера.
        /// </summary>
        public async Task<WTelegram.Client> GetWorkerClientAsync(int workerId, int dcId)
        {
            WTelegram.Client targetClient;

            if (workerId <= 1 || _client == null)
            {
                targetClient = _client!;
            }
            else if (_workerClientsPool.TryGetValue(workerId, out var existingClient))
            {
                targetClient = existingClient;
            }
            else
            {
                try
                {
                    string sessionPath = _currentSettings.Telegram.SessionPath;
                    var extraClient = new WTelegram.Client(what =>
                    {
                        switch (what)
                        {
                            case "api_id": return _currentSettings.Telegram.ApiId.ToString();
                            case "api_hash": return _currentSettings.Telegram.ApiHash;
                            case "session_pathname": return sessionPath;
                            case "phone_number": return _currentSettings.Telegram.PhoneNumber;
                            default: return null;
                        }
                    });

                    string? loginResult = await extraClient.Login(null);
                    if (loginResult == null)
                    {
                        _workerClientsPool[workerId] = extraClient;
                        targetClient = extraClient;
                        AppLogger.Info("TelegramService", $"[Воркер #{workerId}] Успешно запущен дополнительный независимый TCP-клиент MTProto.");
                    }
                    else
                    {
                        AppLogger.Warn("TelegramService", $"[Воркер #{workerId}] Не удалось авторизовать дочерний клиент ({loginResult}), используем основной.");
                        targetClient = _client!;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TelegramService", $"[Воркер #{workerId}] Ошибка инициализации независимого TCP-клиента: {ex.Message}. Используем основной.");
                    targetClient = _client!;
                }
            }

            if (dcId != 0 && targetClient != null)
            {
                return await targetClient.GetClientForDC(dcId);
            }

            return targetClient ?? _client!;
        }

        private void HandleWTelegramResult(string? result)
        {
            if (result == null)
            {
                // Успешная авторизация
                IsAuthorized = true;
                CurrentStep = AuthStep.Authorized;
                LastError = null;

                if (_client?.User != null)
                {
                    var u = _client.User;
                    CurrentUser = new TelegramUserInfo
                    {
                        Id = u.ID,
                        Username = u.MainUsername,
                        FirstName = u.first_name,
                        LastName = u.last_name,
                        Phone = u.phone,
                        IsPremium = u.flags.HasFlag(TL.User.Flags.premium)
                    };
                    AppLogger.Info("TelegramService", $"Успешная авторизация пользователя: {u.first_name} (ID: {u.ID})");

                    if (!string.IsNullOrEmpty(u.phone))
                    {
                        string phoneFormatted = u.phone.StartsWith("+") ? u.phone : "+" + u.phone;
                        if (_currentSettings.Telegram.PhoneNumber != phoneFormatted)
                        {
                            _currentSettings.Telegram.PhoneNumber = phoneFormatted;
                            _configManager.Save(_currentSettings);
                        }
                    }

                    // Фоновый прогрев канала-хранилища сразу после успешной авторизации,
                    // чтобы первая операция копирования/чтения файлов выполнялась мгновенно
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await GetStoragePeerAsync();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TelegramService", $"Фоновый прогрев канала-хранилища: {ex.Message}");
                        }
                    });
                }
            }
            else if (result == "verification_code")
            {
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsCode;
                LastError = null;
                AppLogger.Info("TelegramService", "Код подтверждения отправлен в Telegram.");
            }
            else if (result == "password")
            {
                IsAuthorized = false;
                CurrentStep = AuthStep.Needs2FA;
                LastError = null;
                AppLogger.Info("TelegramService", "Требуется ввод двухфакторного (2FA) облачного пароля.");
            }
            else
            {
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsPhone;
                LastError = $"Ожидался ввод: {result}";
            }
        }

        /// <summary>
        /// Пошаговый вход в Telegram:
        /// 1. Передаем телефон ("+7999...") -> Telegram отправляет код в официальное приложение
        /// 2. Передаем код подтверждения -> если включен 2FA, требуется "password", иначе успех
        /// 3. Передаем 2FA пароль -> успех
        /// </summary>
        public async Task<string?> LoginStepAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Входные данные не могут быть пустыми", nameof(input));

            if (_currentSettings.Telegram.ApiId == 0 || string.IsNullOrWhiteSpace(_currentSettings.Telegram.ApiHash))
            {
                LastError = "Сначала укажите и сохраните API ID и API Hash.";
                throw new InvalidOperationException(LastError);
            }

            // На первом шаге (NeedsPhone) или если клиент отсутствует/сброшен, создаем чистый экземпляр клиента
            if (CurrentStep == AuthStep.NeedsPhone || _client == null)
            {
                string phoneFormatted = input.Trim();
                if (!phoneFormatted.StartsWith("+") && char.IsDigit(phoneFormatted[0]))
                {
                    phoneFormatted = "+" + phoneFormatted;
                }
                _currentSettings.Telegram.PhoneNumber = phoneFormatted;
                _configManager.Save(_currentSettings);

                try { _client?.Dispose(); } catch { }
                _client = null;
                InitClient();
            }

            if (_client == null)
                throw new InvalidOperationException("Не удалось инициализировать Telegram Client.");

            AppLogger.Info("TelegramService", $"Отправка данных на шаге {CurrentStep} в Telegram API...");

            try
            {
                string? result = await _client.Login(input.Trim());
                HandleWTelegramResult(result);
                return result;
            }
            catch (TL.RpcException rpcEx)
            {
                AppLogger.Error("TelegramService", $"RPC Ошибка: {rpcEx.Message} (Код: {rpcEx.Code})", rpcEx);
                LastError = rpcEx.Message;
                if (rpcEx.Code == 420) // FLOOD_WAIT_X
                {
                    TriggerFloodWait(rpcEx.X);
                }
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TelegramService", $"Ошибка входа: {ex.Message}", ex);
                LastError = ex.Message;
                // При ошибке на шаге ввода телефона сбрасываем клиент для чистой следующей попытки
                if (CurrentStep == AuthStep.NeedsPhone)
                {
                    try { _client?.Dispose(); } catch { }
                    _client = null;
                }
                throw;
            }
        }

        /// <summary>
        /// Выход из аккаунта и удаление файла сессии.
        /// </summary>
        public void Logout()
        {
            try
            {
                _client?.Dispose();
                _client = null;
            }
            catch { }

            if (File.Exists(_currentSettings.Telegram.SessionPath))
            {
                try
                {
                    File.Delete(_currentSettings.Telegram.SessionPath);
                }
                catch { }
            }
            _currentSettings.Telegram.PhoneNumber = null;
            _configManager.Save(_currentSettings);

            IsAuthorized = false;
            CurrentStep = AuthStep.NeedsPhone;
            CurrentUser = null;
            AppLogger.Info("TelegramService", "Сессия сброшена.");
        }

        /// <summary>
        /// Механизм перехвата FLOOD_WAIT и плавного ожидания без разрыва соединения с Проводником Windows.
        /// </summary>
        private async Task EnsureFloodWaitDelayAsync()
        {
            if (_floodWaitUntil > DateTime.UtcNow)
            {
                var delay = _floodWaitUntil - DateTime.UtcNow;
                AppLogger.Warn("TelegramService", $"FLOOD_WAIT активен: задержка потока на {delay.TotalSeconds:F1} сек...");
                await Task.Delay(delay);
            }
        }

        public void TriggerFloodWait(int seconds)
        {
            _floodWaitUntil = DateTime.UtcNow.AddSeconds(seconds);
            AppLogger.Warn("TelegramService", $"Получен FLOOD_WAIT на {seconds} сек от серверов Telegram.");
        }

        /// <summary>
        /// Надежная загрузка чанка с поддержкой докачки и отправкой собранного файла в канал Telegram по завершении.
        /// </summary>
        public async Task<int?> UploadFileChunkAsync(Stream source, string fileName, long offset, long totalSize, string? caption = null)
        {
            await EnsureFloodWaitDelayAsync();

            string tempDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_Uploads");
            Directory.CreateDirectory(tempDir);
            string tempFilePath = Path.Combine(tempDir, $"{fileName}.part");

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072); // 128 KB
            long uploadedBytes = 0;
            try
            {
                using (var fileStream = new FileStream(tempFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    int bytesRead;
                    while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        uploadedBytes += bytesRead;
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            // Если чанк был последним и файл полностью собран на диске
            if (offset + uploadedBytes >= totalSize)
            {
                AppLogger.Info("TelegramService", $"Все чанки файла '{fileName}' получены. Загрузка в канал Telegram...");
                try
                {
                    using (var completeStream = File.OpenRead(tempFilePath))
                    {
                        int? messageId = await UploadFileAsync(completeStream, fileName, caption: caption);
                        return messageId;
                    }
                }
                finally
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }

            return null;
        }

        /// <summary>
        /// Потоковая прямая загрузка файла в приватный канал-хранилище через WTelegramClient.
        /// Обеспечивает TCP Flow Control (обратное давление) для синхронизации шкалы прогресса в Проводнике Windows.
        /// Возвращает реальный ID сообщения из Telegram, либо null если файл пустой.
        /// </summary>
        public async Task<int?> UploadFileAsync(Stream source, string fileName, long length = -1, string? displayFileName = null, string? caption = null)
        {
            Interlocked.Increment(ref _pendingUploadsCount);
            await _uploadSemaphore.WaitAsync();
            string effectiveFileName = !string.IsNullOrEmpty(displayFileName) ? displayFileName : fileName;
            string effectiveCaption = !string.IsNullOrEmpty(caption) ? caption : effectiveFileName;

            Stream uploadStream = source;
            string? tempFilePath = null;

            try
            {
                await EnsureFloodWaitDelayAsync();

                if (_client == null || !IsAuthorized)
                    throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

                var peer = await GetStoragePeerAsync();

                // Если поток не поддерживает Seek (входящий сетевой поток WebDAV от Проводника)
                // или если это уже StreamingUploadStream (переданный после извлечения аудио-тегов)
                if (source is StreamingUploadStream existingStreaming)
                {
                    // Уже сквозной поток, регистрируем прогресс для TelegramService
                    uploadStream = source;
                }
                else if (!source.CanSeek)
                {
                    long actualLength = length > 0 ? length : GetStreamLengthSafe(source);

                    if (actualLength > 0)
                    {
                        // Прямой сквозной стриминг с поддержкой обратного давления TCP
                        uploadStream = new StreamingUploadStream(
                            source,
                            actualLength,
                            prefixBuffer: null,
                            onProgress: null
                        );
                    }
                    else if (actualLength == 0)
                    {
                        // Пустой файл
                        uploadStream = new MemoryStream();
                    }
                    else
                    {
                        // Резервный случай для потоков неизвестного размера (Chunked Transfer без Content-Length)
                        string tempDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_Buffer");
                        Directory.CreateDirectory(tempDir);
                        tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{effectiveFileName}");
                        
                        AppLogger.Info("TelegramService", $"Поток без заголовка длины. Буферизация во временный файл: {tempFilePath}");
                        using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await source.CopyToAsync(fs);
                        }
                        
                        uploadStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                }

                // Предотвращаем отправку файлов размером <= 1 байт в Telegram (защита от FILE_PART_0_MISSING и probe-запросов Total Commander / Проводника)
                if (uploadStream.Length <= 1)
                {
                    AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' пустой или является probe-запросом клиента ({uploadStream.Length} байт). Регистрация в БД без загрузки в Telegram.");
                    return null;
                }

                AppLogger.Info("TelegramService", $"Прямая потоковая передача файла '{effectiveFileName}' ({uploadStream.Length} байт) в Telegram...");
                
                // Передаем прогресс-колбэк также в WTelegramClient для детального трекинга MTProto частей
                var inputFile = await _client.UploadFileAsync(
                    uploadStream, 
                    effectiveFileName, 
                    progress: (pos, total) => OnUploadProgress?.Invoke(effectiveFileName, pos, total)
                );

                AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' загружен в MTProto, финализация сообщения в канале (подпись: '{effectiveCaption}')...");
                TL.Message? message = null;
                try
                {
                    message = await _client.SendMediaAsync(peer, effectiveCaption, inputFile);
                }
                catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                {
                    AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                    InvalidateStoragePeer();
                    peer = await GetStoragePeerAsync();
                    message = await _client.SendMediaAsync(peer, effectiveCaption, inputFile);
                }

                if (message != null)
                {
                    AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' успешно сохранен в Telegram. Message ID: {message.ID}");
                    return message.ID;
                }

                AppLogger.Warn("TelegramService", "Сообщение отправлено, но ID не определен, возвращаем 1.");
                return 1;
            }
            finally
            {
                OnUploadCompleted?.Invoke(effectiveFileName);

                if (tempFilePath != null)
                {
                    try { uploadStream.Dispose(); } catch { }
                    try { File.Delete(tempFilePath); } catch { }
                }
                else if (uploadStream != source)
                {
                    try { uploadStream.Dispose(); } catch { }
                }

                _uploadSemaphore.Release();
                Interlocked.Decrement(ref _pendingUploadsCount);
            }
        }

        /// <summary>
        /// Обновляет текстовую подпись (Caption) у существующего сообщения в Telegram (например, при переименовании из .tmp)
        /// </summary>
        public async Task UpdateMessageCaptionAsync(int messageId, string newCaption)
        {
            if (_client == null || !IsAuthorized || messageId <= 1) return;
            try
            {
                var peer = await GetStoragePeerAsync();
                var editReq = new TL.Methods.Messages_EditMessage
                {
                    flags = TL.Methods.Messages_EditMessage.Flags.has_message,
                    peer = peer,
                    id = messageId,
                    message = newCaption
                };
                await _client.Invoke(editReq);
                AppLogger.Info("TelegramService", $"Подпись сообщения #{messageId} в Telegram успешно обновлена на: '{newCaption}'.");
            }
            catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && rpcEx.Message.Contains("MESSAGE_NOT_MODIFIED"))
            {
                AppLogger.Debug("TelegramService", $"Подпись сообщения #{messageId} уже актуальна в Telegram ({newCaption}).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TelegramService", $"Не удалось обновить подпись сообщения #{messageId} в Telegram: {ex.Message}");
            }
        }

        private long GetStreamLengthSafe(Stream stream)
        {
            try
            {
                return stream.Length;
            }
            catch
            {
                return -1;
            }
        }

        private async Task<TL.Document?> GetDocumentFromMessageAsync(int messageId, bool forceRefresh = false)
        {
            if (!forceRefresh && _documentCache.TryGetValue(messageId, out var cached) && cached.expiresAt > DateTime.UtcNow)
            {
                return cached.document;
            }

            if (_client == null) return null;
            var peer = await GetStoragePeerAsync();
            TL.Messages_MessagesBase messagesBase;
            try
            {
                messagesBase = await _client.GetMessages(peer, new TL.InputMessage[] { new TL.InputMessageID { id = messageId } });
            }
            catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
            {
                AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                InvalidateStoragePeer();
                peer = await GetStoragePeerAsync();
                messagesBase = await _client.GetMessages(peer, new TL.InputMessage[] { new TL.InputMessageID { id = messageId } });
            }
            
            TL.Document? document = null;
            if (messagesBase is TL.Messages_Messages messages && messages.messages.Length > 0)
            {
                var msg = messages.messages[0] as TL.Message;
                if (msg?.media is TL.MessageMediaDocument mediaDoc && mediaDoc.document is TL.Document doc)
                {
                    document = doc;
                }
            }
            else if (messagesBase is TL.Messages_ChannelMessages channelMessages && channelMessages.messages.Length > 0)
            {
                var msg = channelMessages.messages[0] as TL.Message;
                if (msg?.media is TL.MessageMediaDocument mediaDoc && mediaDoc.document is TL.Document doc)
                {
                    document = doc;
                }
            }

            if (document != null)
            {
                _documentCache[messageId] = (document, DateTime.UtcNow.AddMinutes(15));
            }

            return document;
        }

        private async Task ReadFromFileCacheAsync(string cacheFilePath, Stream destination, long offset, long length, int messageId)
        {
            using (var fs = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                long remaining = length;
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
                try
                {
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await fs.ReadAsync(buffer, 0, toRead);
                        if (read <= 0) break;

                        await destination.WriteAsync(buffer, 0, read);
                        remaining -= read;
                    }
                    await destination.FlushAsync();
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("TelegramService", $"Клиент прервал чтение из кэша для сообщения {messageId}: {ex.Message}");
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        /// <summary>
        /// Потоковое скачивание чанков напрямую из Telegram через MTProto Upload_GetFile.
        /// В режиме Pure RAM Mode качает данные 100% через ОЗУ (без файлов на диске), сохраняя чанки в динамический 128 МБ RAM-кэш.
        /// </summary>
        public async Task DownloadFileAsync(int messageId, Stream destination, long offset, long length, string fileName = "файл", long totalFileSize = -1)
        {
            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            bool enableDiskCache = _configManager?.CurrentSettings?.Server?.EnableDiskReadCache ?? false;

            // 1. Если дисковый кэш включен и файл уже закэширован на диске полностью
            if (enableDiskCache)
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_ReadCache");
                Directory.CreateDirectory(cacheDir);
                string cacheFilePath = Path.Combine(cacheDir, $"{messageId}.bin");

                if (File.Exists(cacheFilePath))
                {
                    await ReadFromFileCacheAsync(cacheFilePath, destination, offset, length, messageId);
                    return;
                }
            }

            // 2. Запрашиваем дескриптор документа
            var document = await GetDocumentFromMessageAsync(messageId);
            if (document == null)
            {
                throw new FileNotFoundException($"Не удалось найти медиа-документ для сообщения ID {messageId} в Telegram.");
            }

            long actualTotalSize = totalFileSize > 0 ? totalFileSize : (document.size > 0 ? document.size : offset + length);
            bool isSmallFile = actualTotalSize <= 262144; // Файл меньше 256 КБ
            bool isMetadataProbe = length <= 262144 && offset == 0; // Быстрый запрос заголовков Проводником Windows

            // Если дисковый кэш включен в настройках: скачиваем файл в дисковый кэш %TEMP%
            if (enableDiskCache && offset == 0 && !isMetadataProbe && actualTotalSize > 262144)
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_ReadCache");
                Directory.CreateDirectory(cacheDir);
                string cacheFilePath = Path.Combine(cacheDir, $"{messageId}.bin");

                var fileLock = _fileDownloadLocks.GetOrAdd(messageId, _ => new SemaphoreSlim(1, 1));
                await fileLock.WaitAsync();
                try
                {
                    if (!File.Exists(cacheFilePath))
                    {
                        AppLogger.Info("TelegramService", $"[Disk Cache Mode] Скачивание файла '{fileName}' (ID {messageId}, {actualTotalSize:N0} байт) воркерами в дисковый кэш...");
                        var workerPool = new MtprotoDownloadWorkerPool(_client, workerCount: 3, clientProvider: GetWorkerClientAsync);
                        bool success = await workerPool.DownloadFileAsync(
                            document,
                            cacheFilePath,
                            onProgress: (transferred, total) => OnDownloadProgress?.Invoke(fileName, transferred, total));

                        if (success && File.Exists(cacheFilePath))
                        {
                            AppLogger.Info("TelegramService", $"[Disk Cache Mode] Файл '{fileName}' (ID {messageId}) успешно сохранен в кэш.");
                            OnDownloadCompleted?.Invoke(fileName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TelegramService", $"[Disk Cache Mode] Ошибка при фоновом скачивании файла ID {messageId}: {ex.Message}.");
                }
                finally
                {
                    fileLock.Release();
                }

                if (File.Exists(cacheFilePath))
                {
                    await ReadFromFileCacheAsync(cacheFilePath, destination, offset, length, messageId);
                    return;
                }
            }

            // РЕЖИМ 100% PURE RAM STREAMING (без файлов на диске):
            // 1. Проверяем, есть ли запрашиваемый чанк уже в ОЗУ (был ранее упреждающе выкачан воркером)
            if (!enableDiskCache && TryGetFromMemoryCache(messageId, offset, out var ramCachedRaw, out var ramCachedOffset) && ramCachedRaw != null)
            {
                int availableInChunk = ramCachedRaw.Length - ramCachedOffset;
                int bytesToSend = (int)Math.Min(availableInChunk, length);
                try
                {
                    await destination.WriteAsync(ramCachedRaw, ramCachedOffset, bytesToSend);
                    await destination.FlushAsync();
                    AppLogger.Info("TelegramService", $"[Cache RAM] Мгновенная отдача из ОЗУ для '{fileName}' (ID {messageId}): Глобальный Чанк #{offset / 1048576} (смещение {offset:N0}, {bytesToSend:N0} байт).");

                    // Запускаем воркеров на упреждающую прокачку следующих 3 МБ в ОЗУ
                    if (actualTotalSize > offset + bytesToSend)
                    {
                        long nextPrefetchOffset = offset + bytesToSend;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var prefetchPool = new MtprotoDownloadWorkerPool(_client, workerCount: 3, clientProvider: GetWorkerClientAsync);
                                await prefetchPool.DownloadToStreamAsync(
                                    document,
                                    Stream.Null,
                                    nextPrefetchOffset,
                                    Math.Min(3 * 1048576, actualTotalSize - nextPrefetchOffset),
                                    onChunkReceived: (chunkBytes, chunkOffset) =>
                                    {
                                        EnsureChunkCacheCapacity();
                                        string chunkKey = $"{messageId}:{chunkOffset}:{chunkBytes.Length}";
                                        _chunkMemoryCache[chunkKey] = (chunkBytes, DateTime.UtcNow.AddMinutes(5));
                                    });
                            }
                            catch { }
                        });
                    }
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("TelegramService", $"Клиент прервал соединение при чтении из RAM кэша: {ex.Message}");
                    return;
                }
            }

            // 2. Для скачивания архивов и больших файлов качаем чанки через MtprotoDownloadWorkerPool напрямую в ОЗУ с заполнением 128 МБ RAM-кэша
            if (!enableDiskCache && !isSmallFile && !isMetadataProbe && length > 262144)
            {
                bool isFullFileDownload = (offset == 0 && length >= actualTotalSize);
                var workerPool = new MtprotoDownloadWorkerPool(_client, workerCount: 3, clientProvider: GetWorkerClientAsync);
                bool success = await workerPool.DownloadToStreamAsync(
                    document,
                    destination,
                    offset,
                    length,
                    onChunkReceived: (chunkBytes, chunkOffset) =>
                    {
                        EnsureChunkCacheCapacity();
                        string chunkKey = $"{messageId}:{chunkOffset}:{chunkBytes.Length}";
                        _chunkMemoryCache[chunkKey] = (chunkBytes, DateTime.UtcNow.AddMinutes(5));
                    },
                    onProgress: (transferred, total) =>
                    {
                        if (!isMetadataProbe && (isFullFileDownload || transferred > 524288))
                            OnDownloadProgress?.Invoke(fileName, offset + transferred, actualTotalSize);
                        else
                        {
                            OnMetadataProgress?.Invoke(fileName, transferred, actualTotalSize);
                            OnChunkCached?.Invoke(fileName, transferred, actualTotalSize);
                        }
                    });

                if (success)
                {
                    if (!isMetadataProbe && isFullFileDownload)
                    {
                        OnDownloadCompleted?.Invoke(fileName);
                    }
                    return;
                }
            }

            var activeClient = document.dc_id != 0 ? await _client.GetClientForDC(document.dc_id) : _client;
            TL.InputFileLocationBase location = document.ToFileLocation();

            long currentPos = offset;
            long remainingBytes = length;
            long totalSent = 0;

            // При старте последовательного чтения большого файла (копирование/воспроизведение) прогреваем 1 упреждающий блок
            if (!isSmallFile && !isMetadataProbe && offset == 0 && actualTotalSize > 1048576)
            {
                TriggerPrefetch(activeClient, location, messageId, 1048576, 1048576);
            }

            while (remainingBytes > 0)
            {
                await EnsureFloodWaitDelayAsync();

                // 1. Проверяем, есть ли уже нужные байты в быстром кэше оперативной памяти
                if (TryGetFromMemoryCache(messageId, currentPos, out var cachedRaw, out var cachedOffset) && cachedRaw != null)
                {
                    // Конвейер Double Buffering: упреждающая загрузка строго следующего 1 блока без перегрузки Telegram API
                    if (actualTotalSize > 0 && !isMetadataProbe && (totalSent > 0 || length > 262144))
                    {
                        long currentBlock = (currentPos / 1048576) * 1048576;
                        if (currentBlock + 1048576 < actualTotalSize)
                            TriggerPrefetch(activeClient, location, messageId, currentBlock + 1048576, 1048576);
                    }

                    int available = cachedRaw.Length - cachedOffset;
                    int toSend = (int)Math.Min(available, remainingBytes);

                    try
                    {
                        await destination.WriteAsync(cachedRaw, cachedOffset, toSend);
                        await destination.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"Клиент прервал соединение для '{fileName}' (ID {messageId}): {ex.Message}");
                        return;
                    }

                    currentPos += toSend;
                    remainingBytes -= toSend;
                    totalSent += toSend;

                    AppLogger.Info("TelegramService", $"[Cache RAM] Чтение из памяти RAM '{fileName}' (ID {messageId}): смещение {currentPos - toSend:N0}, отдано {toSend:N0} байт ({currentPos:N0} / {actualTotalSize:N0} байт, {(double)currentPos * 100 / Math.Max(1, actualTotalSize):F1}%).");

                    // Уведомление о прогрессе вызываем в зависимости от типа чтения и переданного объёма
                    bool isFullDownload = (offset == 0 && length >= actualTotalSize) || (totalSent >= actualTotalSize - 65536 && offset <= 262144);

                    if (!isMetadataProbe && (isFullDownload || totalSent > 524288))
                    {
                        OnDownloadProgress?.Invoke(fileName, currentPos, actualTotalSize);
                        if (isFullDownload && currentPos >= actualTotalSize)
                        {
                            OnDownloadCompleted?.Invoke(fileName);
                        }
                    }
                    else
                    {
                        OnMetadataProgress?.Invoke(fileName, totalSent, actualTotalSize);
                        OnChunkCached?.Invoke(fileName, totalSent, actualTotalSize);
                    }
                    continue;
                }

                // 2. Адаптивный выбор размера чанка MTProto
                int baseChunkSize = (isSmallFile || isMetadataProbe) ? 262144 : 1048576;

                // MTProto строго запрещает запросам выходить за пределы одного 1-мегабайтного блока (1048576 байт).
                // Выравниваем chunkOffset по границе baseChunkSize.
                long chunkOffset = (currentPos / baseChunkSize) * baseChunkSize;
                int internalOffset = (int)(currentPos - chunkOffset);
                int requestLimit = baseChunkSize;

                string chunkKey = $"{messageId}:{chunkOffset}:{requestLimit}";
                byte[]? raw = null;

                if (_pendingPrefetches.TryGetValue(chunkKey, out var pendingPrefetch))
                {
                    raw = await pendingPrefetch;
                    AppLogger.Info("TelegramService", $"[Prefetch Hit] Получен ранее запрошенный упреждающий чанк для '{fileName}' (смещение {chunkOffset:N0}, размер {raw?.Length ?? 0:N0} байт).");
                }
                else
                {
                    TL.Upload_FileBase fileBase;
                    await _downloadRpcSemaphore.WaitAsync();
                    try
                    {
                        await EnsureFloodWaitDelayAsync();
                        try
                        {
                            AppLogger.Info("TelegramService", $"[MTProto] Запрос чанка для '{fileName}' (ID {messageId}): смещение {chunkOffset:N0}, размер {requestLimit:N0} байт ({currentPos:N0} / {actualTotalSize:N0} байт)...");
                            fileBase = await activeClient.Upload_GetFile(location, chunkOffset, requestLimit, precise: true);
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 303) // FILE_MIGRATE_X
                        {
                            activeClient = await _client.GetClientForDC(rpcEx.X);
                            fileBase = await activeClient.Upload_GetFile(location, chunkOffset, requestLimit, precise: true);
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && rpcEx.Message.Contains("FILE_REFERENCE_EXPIRED"))
                        {
                            var refreshedDoc = await GetDocumentFromMessageAsync(messageId, forceRefresh: true);
                            if (refreshedDoc != null)
                            {
                                location = refreshedDoc.ToFileLocation();
                                activeClient = refreshedDoc.dc_id != 0 ? await _client.GetClientForDC(refreshedDoc.dc_id) : _client;
                                fileBase = await activeClient.Upload_GetFile(location, chunkOffset, requestLimit, precise: true);
                            }
                            else throw;
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                        {
                            int waitSec = rpcEx.X > 0 ? rpcEx.X : 5;
                            AppLogger.Warn("TelegramService", $"[FLOOD_WAIT] Telegram запросил паузу {waitSec} сек.");
                            _floodWaitUntil = DateTime.UtcNow.AddSeconds(waitSec);
                            await Task.Delay(waitSec * 1000);
                            fileBase = await activeClient.Upload_GetFile(location, chunkOffset, requestLimit, precise: true);
                        }
                    }
                    finally
                    {
                        _downloadRpcSemaphore.Release();
                    }

                    if (fileBase is TL.Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                    {
                        raw = uploadFile.bytes;
                        EnsureChunkCacheCapacity();
                        _chunkMemoryCache[chunkKey] = (raw, DateTime.UtcNow.AddMinutes(5));
                        AppLogger.Info("TelegramService", $"[MTProto] Получен чанк для '{fileName}': смещение {chunkOffset:N0}, размер {raw.Length:N0} байт, сохранен в RAM кэш.");
                    }
                }

                // Конвейер Double Buffering: упреждающая загрузка следующего блока
                if (baseChunkSize == 1048576 && actualTotalSize > 0 && !isMetadataProbe)
                {
                    long nextOffset1 = chunkOffset + baseChunkSize;
                    if (nextOffset1 < actualTotalSize)
                        TriggerPrefetch(activeClient, location, messageId, nextOffset1, baseChunkSize);
                }

                if (raw != null && raw.Length > 0)
                {
                    if (internalOffset >= raw.Length)
                    {
                        // Смещение вышло за пределы доступных байт
                        break;
                    }

                    int available = raw.Length - internalOffset;
                    int toSend = (int)Math.Min(available, remainingBytes);

                    try
                    {
                        await destination.WriteAsync(raw, internalOffset, toSend);
                        await destination.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        // Клиент (AIMP, Проводник) получил нужные байты и закрыл соединение
                        AppLogger.Debug("TelegramService", $"Клиент прервал соединение для '{fileName}' (ID {messageId}): {ex.Message}");
                        return;
                    }

                    currentPos += toSend;
                    remainingBytes -= toSend;
                    totalSent += toSend;

                    bool isFullDownload = (offset == 0 && length >= actualTotalSize) || (totalSent >= actualTotalSize - 65536 && offset <= 262144);

                    if (!isMetadataProbe && (isFullDownload || totalSent > 524288))
                    {
                        OnDownloadProgress?.Invoke(fileName, currentPos, actualTotalSize);
                        if (isFullDownload && currentPos >= actualTotalSize)
                        {
                            OnDownloadCompleted?.Invoke(fileName);
                        }
                    }
                    else
                    {
                        OnMetadataProgress?.Invoke(fileName, totalSent, actualTotalSize);
                        OnChunkCached?.Invoke(fileName, totalSent, actualTotalSize);
                    }

                    // Если Telegram вернул меньше данных, чем requestLimit — достигнут конец файла
                    if (raw.Length < requestLimit && remainingBytes > 0)
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (!isSmallFile && !isMetadataProbe && totalSent >= 524288 && currentPos >= actualTotalSize)
            {
                OnDownloadCompleted?.Invoke(fileName);
            }
        }

        public async Task<bool> DeleteFileFromTelegramAsync(int messageId)
        {
            return await DeleteFilesFromTelegramAsync(new System.Collections.Generic.List<int> { messageId });
        }

        public async Task<bool> DeleteFilesFromTelegramAsync(System.Collections.Generic.List<int> messageIds)
        {
            if (messageIds == null || messageIds.Count == 0) return true;

            // Фильтруем только валидные положительные ID, исключая дубликаты
            var validIds = messageIds.Where(id => id > 0).Distinct().ToList();
            if (validIds.Count == 0) return true;

            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            try
            {
                var peer = await GetStoragePeerAsync();
                bool isChannel = peer is TL.InputPeerChannel;

                // Разбиваем список по 100 элементов (максимальный размер пакета Telegram)
                const int batchSize = 100;
                for (int i = 0; i < validIds.Count; i += batchSize)
                {
                    var count = Math.Min(batchSize, validIds.Count - i);
                    var batch = validIds.GetRange(i, count).ToArray();

                    await DeleteBatchWithBisectAsync(peer, isChannel, batch);
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TelegramService", $"Критическая ошибка при удалении сообщений из Telegram: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Отказоустойчивое удаление пакета сообщений с применением алгоритма бинарного деления (Биссекции).
        /// При возникновении ошибки (например, MESSAGE_ID_INVALID из-за уже удаленного сообщения)
        /// пакет делится пополам, позволяя успешно удалить все валидные сообщения за минимальное число запросов.
        /// </summary>
        private async Task DeleteBatchWithBisectAsync(TL.InputPeer peer, bool isChannel, int[] ids)
        {
            if (ids == null || ids.Length == 0 || _client == null) return;

            try
            {
                if (isChannel && peer is TL.InputPeerChannel pc)
                {
                    var channel = new TL.InputChannel(pc.channel_id, pc.access_hash);
                    var deleteReq = new TL.Methods.Channels_DeleteMessages
                    {
                        channel = channel,
                        id = ids
                    };
                    await _client.Invoke(deleteReq);
                }
                else
                {
                    var deleteReq = new TL.Methods.Messages_DeleteMessages
                    {
                        id = ids
                    };
                    await _client.Invoke(deleteReq);
                }

                AppLogger.Info("TelegramService", $"Пакет из {ids.Length} сообщений успешно удален из Telegram.");
            }
            catch (Exception ex)
            {
                // Если остался всего 1 элемент и он упал (например, MESSAGE_ID_INVALID или уже удален в TG вручную)
                if (ids.Length == 1)
                {
                    AppLogger.Warn("TelegramService", $"Пропущено невалидное или уже удаленное сообщение ID {ids[0]}: {ex.Message}");
                    return;
                }

                // Иначе делим группу пополам (биссекция)
                int mid = ids.Length / 2;
                var left = ids.Take(mid).ToArray();
                var right = ids.Skip(mid).ToArray();

                AppLogger.Warn("TelegramService", $"Сбой при пакетном удалении группы из {ids.Length} сообщений ({ex.Message}). Разделяем пополам на {left.Length} и {right.Length}...");
                await DeleteBatchWithBisectAsync(peer, isChannel, left);
                await DeleteBatchWithBisectAsync(peer, isChannel, right);
            }
        }

        public void Dispose()
        {
            try
            {
                _client?.Dispose();
                _client = null;
            }
            catch { }
            foreach (var workerClient in _workerClientsPool.Values)
            {
                try { workerClient.Dispose(); } catch { }
            }
            _workerClientsPool.Clear();
            _floodLock?.Dispose();
            _uploadSemaphore?.Dispose();
        }
    }
}
