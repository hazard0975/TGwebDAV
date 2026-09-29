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

    public class FileUploadResult
    {
        public int MessageId { get; set; }
        public int? PreviewMessageId { get; set; }

        public FileUploadResult(int messageId, int? previewMessageId = null)
        {
            MessageId = messageId;
            PreviewMessageId = previewMessageId;
        }

        public static implicit operator int?(FileUploadResult? res) => res?.MessageId;
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

        // Быстрый кольцевой кэш чанков MTProto в оперативной памяти (~128 МБ) для мгновенного чтения плеерами без повторных обращений к сети
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] data, DateTime expiresAt)> _chunkMemoryCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>> _inFlightChunkTasks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _activeFilePrefetches = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _fileDownloadLocks = new();
        private readonly SemaphoreSlim _downloadRpcSemaphore = new SemaphoreSlim(3, 3);
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _availableWorkerIds = new(new[] { 1, 2, 3 });
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WTelegram.Client> _workerClientsMap = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, NetworkTransferAudit> _networkAudits = new();

        private class FileReadSequence
        {
            public long LastChunkIndex { get; set; } = -1;
            public int SequentialCount { get; set; } = 0;
            public DateTime LastReadTime { get; set; } = DateTime.UtcNow;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, FileReadSequence> _fileReadSequences = new();

        public class NetworkTransferAudit
        {
            public string FileName { get; set; } = string.Empty;
            public long FileSize { get; set; }
            private long _networkBytesDownloaded;
            private long _ramBytesDelivered;
            private int _completionLogged;
            private int _metadataCompletedLogged;

            private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _receivedChunkIndexes = new();

            public int TotalChunks => FileSize > 0 ? (int)Math.Ceiling((double)FileSize / 1048576.0) : 1;
            public int ReceivedChunksCount => _receivedChunkIndexes.Count;
            public string ProgressSummary
            {
                get
                {
                    double pct = TotalChunks > 0 ? (double)ReceivedChunksCount * 100.0 / TotalChunks : 100.0;
                    return $"Скачано всего: {ReceivedChunksCount} из {TotalChunks} чанков ({pct:0.#}%)";
                }
            }

            public void AddNetworkBytes(long bytes) => Interlocked.Add(ref _networkBytesDownloaded, bytes);
            public void AddRamBytes(long bytes) => Interlocked.Add(ref _ramBytesDelivered, bytes);

            public long NetworkBytes => Interlocked.Read(ref _networkBytesDownloaded);
            public long RamBytes => Interlocked.Read(ref _ramBytesDelivered);

            /// <summary>
            /// Помечает диапазон байт как полученный (вычисляя все перекрываемые 1 МБ чанки).
            /// Возвращает true, если получены ВСЕ чанки файла (100% загрузка).
            /// </summary>
            public bool MarkRangeReceived(long offset, long length)
            {
                if (FileSize <= 0) return true;
                if (length <= 0) return IsAllChunksReceived();

                int total = TotalChunks;
                long endPos = Math.Min(FileSize, offset + length);
                if (endPos <= offset) return IsAllChunksReceived();

                int startChunk = (int)(offset / 1048576);
                int endChunk = (int)((endPos - 1) / 1048576);

                for (int i = startChunk; i <= endChunk && i < total; i++)
                {
                    if (i >= 0)
                    {
                        _receivedChunkIndexes.TryAdd(i, true);
                    }
                }

                return IsAllChunksReceived();
            }

            public bool IsAllChunksReceived()
            {
                if (FileSize <= 0) return true;
                return _receivedChunkIndexes.Count >= TotalChunks;
            }

            public bool LogMetadataCompletionOnce()
            {
                return Interlocked.CompareExchange(ref _metadataCompletedLogged, 1, 0) == 0;
            }

            public bool LogCompletionOnce()
            {
                if (Interlocked.CompareExchange(ref _completionLogged, 1, 0) == 0)
                {
                    long net = NetworkBytes;
                    long ram = RamBytes;
                    double ratio = FileSize > 0 ? (double)net / FileSize : 1.0;
                    string verdict;
                    if (ratio <= 1.05)
                        verdict = "1.00x (Идеально — 0% дублирования)";
                    else if (ratio <= 1.25)
                        verdict = $"{ratio:F2}x (Допустимо — остаток хвоста чанка)";
                    else
                        verdict = $"{ratio:F2}x (ВНИМАНИЕ — обнаружено избыточное дублирование сетевых запросов!)";

                    string msg = $"[NetworkAudit] Завершено для '{FileName}': Размер: {FileSize:N0} б ({FileSize / 1048576.0:F2} МБ) | Из сети TG: {net:N0} б ({net / 1048576.0:F2} МБ) | Из ОЗУ: {ram:N0} б ({ram / 1048576.0:F2} МБ) | Эффективность сети: {verdict}";

                    if (ratio > 1.25)
                        AppLogger.Warn("TelegramService", msg);
                    else
                        AppLogger.Info("TelegramService", msg);

                    return true;
                }
                return false;
            }
        }

        private bool TryGetFromMemoryCache(int messageId, long pos, out byte[]? data, out int offsetInChunk)
        {
            int chunkIdx = (int)(pos / 1048576);
            string chunkKey = $"{messageId}:{chunkIdx}";
            var now = DateTime.UtcNow;

            if (_chunkMemoryCache.TryGetValue(chunkKey, out var entry) && entry.expiresAt > now && entry.data != null)
            {
                long chunkBaseOffset = (long)chunkIdx * 1048576;
                int off = (int)(pos - chunkBaseOffset);
                if (off >= 0 && off < entry.data.Length)
                {
                    data = entry.data;
                    offsetInChunk = off;
                    return true;
                }
            }

            data = null;
            offsetInChunk = 0;
            return false;
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
        /// Событие завершения получения метаданных файла.
        /// </summary>
        public event Action<string>? OnMetadataCompleted;

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
            AppLogger.Info("TelegramService", $"[Streaming Config] RAM Кэш: {_currentSettings.Server.MemoryCacheSizeMb} МБ (TTL: {_currentSettings.Server.ChunkMemoryCacheTtlMinutes} мин) | Автовыкачка треков до: {_currentSettings.Server.FullTrackPrefetchMaxFileSizeMb} МБ | Окно стриминга: {_currentSettings.Server.StreamingPrefetchWindowMb} МБ | Дисковый кэш: {(_currentSettings.Server.EnableDiskReadCache ? "ВКЛ" : "ВЫКЛ (100% RAM)")}");
            
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
        /// Возвращает сессию DC основного клиента MTProto для параллельных воркеров пула.
        /// Гарантирует изоляцию сокетов воркеров для предотвращения RpcError 420 FLOOD_WAIT.
        /// </summary>
        public async Task<WTelegram.Client> GetWorkerClientAsync(int workerId, int dcId)
        {
            if (_client == null)
                throw new InvalidOperationException("Основной клиент Telegram не инициализирован.");

            int targetDc = dcId != 0 ? dcId : 0;
            string key = $"{targetDc}:{workerId}";

            if (_workerClientsMap.TryGetValue(key, out var cachedClient) && cachedClient != null)
            {
                return cachedClient;
            }

            WTelegram.Client clientToUse;
            if (targetDc != 0)
            {
                clientToUse = await _client.GetClientForDC(targetDc);
            }
            else
            {
                clientToUse = _client;
            }

            _workerClientsMap[key] = clientToUse;
            return clientToUse;
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
        public async Task<FileUploadResult?> UploadFileChunkAsync(Stream source, string fileName, long offset, long totalSize, string? caption = null)
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
                    AudioMetadataResult? chunkAudioMeta = null;
                    VideoMetadataResult? chunkVideoMeta = null;

                    if (AudioMetadataExtractor.IsPotentialAudio(fileName))
                    {
                        chunkAudioMeta = AudioMetadataExtractor.ExtractFromFile(tempFilePath, fileName);
                    }
                    else if (VideoMetadataExtractor.IsPotentialVideo(fileName))
                    {
                        chunkVideoMeta = VideoMetadataExtractor.ExtractFromFile(tempFilePath, fileName);
                    }

                    using (var completeStream = File.OpenRead(tempFilePath))
                    {
                        var uploadResult = await UploadFileAsync(
                            completeStream, 
                            fileName, 
                            caption: caption, 
                            audioMeta: chunkAudioMeta, 
                            videoMeta: chunkVideoMeta
                        );
                        return uploadResult;
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
        /// Для аудио- и видеофайлов автоматически формирует InputMediaUploadedDocument с атрибутами стриминга (DocumentAttributeAudio / DocumentAttributeVideo) и обложкой (thumb).
        /// Возвращает реальный ID сообщения из Telegram, либо null если файл пустой.
        /// </summary>
        public async Task<FileUploadResult?> UploadFileAsync(
            Stream source, 
            string fileName, 
            long length = -1, 
            string? displayFileName = null, 
            string? caption = null,
            AudioMetadataResult? audioMeta = null,
            VideoMetadataResult? videoMeta = null)
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
                bool isGallery = _configManager.CurrentSettings.Server.CreatePhotoGalleryPreview && IsGalleryImage(effectiveFileName);

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

                    if (actualLength > 0 && !isGallery)
                    {
                        // Прямой сквозной стриминг с поддержкой обратного давления TCP (только если не картинка для галереи)
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
                        // Буферизация во временный файл: для потоков неизвестного размера, а также для картинок галереи (чтобы создать превью и отправить оригинал)
                        string subDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_Buffer", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(subDir);
                        tempFilePath = Path.Combine(subDir, effectiveFileName);
                        
                        AppLogger.Info("TelegramService", $"Буферизация изображения во временный файл: {tempFilePath}");
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

                byte[]? galleryPhotoBytes = null;
                if (isGallery && uploadStream.CanSeek && uploadStream.Length > 1)
                {
                    galleryPhotoBytes = CreateOptimizedGalleryThumbnail(uploadStream);
                    uploadStream.Seek(0, SeekOrigin.Begin);
                }

                AppLogger.Info("TelegramService", $"Прямая потоковая передача файла '{effectiveFileName}' ({uploadStream.Length} байт) в Telegram...");
                
                // Передаем прогресс-колбэк также в WTelegramClient для детального трекинга MTProto частей
                var inputFile = await _client.UploadFileAsync(
                    uploadStream, 
                    effectiveFileName, 
                    progress: (pos, total) => OnUploadProgress?.Invoke(effectiveFileName, pos, total)
                );

                AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' загружен в MTProto, финализация сообщения в канале (подпись: '{effectiveCaption}')...");
                
                bool isAudio = AudioMetadataExtractor.IsAudioFile(effectiveFileName);
                bool isVideo = VideoMetadataExtractor.IsVideoFile(effectiveFileName);
                TL.Message? message = null;

                if (isAudio)
                {
                    string ext = Path.GetExtension(effectiveFileName).ToLowerInvariant();
                    string mimeType = ext switch
                    {
                        ".mp3" => "audio/mpeg",
                        ".flac" => "audio/flac",
                        ".m4a" => "audio/mp4",
                        ".ogg" => "audio/ogg",
                        ".wav" => "audio/x-wav",
                        ".aac" => "audio/aac",
                        ".opus" => "audio/opus",
                        ".wma" => "audio/x-ms-wma",
                        _ => "audio/mpeg"
                    };

                    string title = !string.IsNullOrWhiteSpace(audioMeta?.Title)
                        ? audioMeta.Title
                        : Path.GetFileNameWithoutExtension(effectiveFileName);
                    string? performer = !string.IsNullOrWhiteSpace(audioMeta?.Artist)
                        ? audioMeta.Artist
                        : null;
                    int duration = audioMeta?.DurationSeconds ?? 0;

                    var audioFlags = TL.DocumentAttributeAudio.Flags.has_title;
                    if (!string.IsNullOrEmpty(performer))
                    {
                        audioFlags |= TL.DocumentAttributeAudio.Flags.has_performer;
                    }

                    var audioAttr = new TL.DocumentAttributeAudio
                    {
                        duration = duration > 0 ? duration : 0,
                        title = title,
                        performer = performer ?? string.Empty,
                        flags = audioFlags
                    };

                    var fileNameAttr = new TL.DocumentAttributeFilename
                    {
                        file_name = effectiveFileName
                    };

                    var attributes = new TL.DocumentAttribute[] { fileNameAttr, audioAttr };

                    TL.InputFileBase? thumbFile = null;
                    if (audioMeta?.AlbumCover != null && audioMeta.AlbumCover.Length > 0)
                    {
                        try
                        {
                            using var thumbMs = new MemoryStream(audioMeta.AlbumCover, false);
                            thumbFile = await _client.UploadFileAsync(thumbMs, "thumb.jpg");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TelegramService", $"Не удалось загрузить обложку альбома для '{effectiveFileName}': {ex.Message}");
                        }
                    }

                    var mediaDoc = new TL.InputMediaUploadedDocument(inputFile, mimeType, attributes);
                    if (thumbFile != null)
                    {
                        mediaDoc.thumb = thumbFile;
                        mediaDoc.flags |= TL.InputMediaUploadedDocument.Flags.has_thumb;
                    }

                    try
                    {
                        message = await _client.SendMessageAsync(peer, effectiveCaption, mediaDoc);
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                    {
                        AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                        InvalidateStoragePeer();
                        peer = await GetStoragePeerAsync();
                        message = await _client.SendMessageAsync(peer, effectiveCaption, mediaDoc);
                    }
                }
                else if (isVideo)
                {
                    string mimeType = VideoMetadataExtractor.GetVideoMimeType(effectiveFileName);
                    int duration = videoMeta?.DurationSeconds ?? 0;
                    int width = (videoMeta != null && videoMeta.Width > 0) ? videoMeta.Width : 1280;
                    int height = (videoMeta != null && videoMeta.Height > 0) ? videoMeta.Height : 720;

                    var videoFlags = TL.DocumentAttributeVideo.Flags.supports_streaming;
                    var videoAttr = new TL.DocumentAttributeVideo
                    {
                        duration = duration,
                        w = width,
                        h = height,
                        flags = videoFlags
                    };

                    var fileNameAttr = new TL.DocumentAttributeFilename
                    {
                        file_name = effectiveFileName
                    };

                    var attributes = new TL.DocumentAttribute[] { fileNameAttr, videoAttr };

                    TL.InputFileBase? thumbFile = null;
                    if (videoMeta?.Thumbnail != null && videoMeta.Thumbnail.Length > 0)
                    {
                        try
                        {
                            using var thumbMs = new MemoryStream(videoMeta.Thumbnail, false);
                            thumbFile = await _client.UploadFileAsync(thumbMs, "thumb.jpg");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TelegramService", $"Не удалось загрузить обложку видео для '{effectiveFileName}': {ex.Message}");
                        }
                    }

                    var mediaDoc = new TL.InputMediaUploadedDocument(inputFile, mimeType, attributes);
                    if (thumbFile != null)
                    {
                        mediaDoc.thumb = thumbFile;
                        mediaDoc.flags |= TL.InputMediaUploadedDocument.Flags.has_thumb;
                    }

                    try
                    {
                        message = await _client.SendMessageAsync(peer, effectiveCaption, mediaDoc);
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                    {
                        AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                        InvalidateStoragePeer();
                        peer = await GetStoragePeerAsync();
                        message = await _client.SendMessageAsync(peer, effectiveCaption, mediaDoc);
                    }
                }
                else
                {
                    TL.Message? photoMessage = null;
                    if (galleryPhotoBytes != null && galleryPhotoBytes.Length > 0)
                    {
                        try
                        {
                            using var photoMs = new MemoryStream(galleryPhotoBytes, false);
                            var photoInput = await _client.UploadFileAsync(photoMs, "photo.jpg");
                            var photoMedia = new TL.InputMediaUploadedPhoto { file = photoInput };
                            photoMessage = await _client.SendMessageAsync(peer, effectiveCaption, photoMedia);
                            AppLogger.Info("TelegramService", $"Фото-превью для галереи успешно опубликовано в Telegram (Message ID: {photoMessage.ID}).");
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                        {
                            InvalidateStoragePeer();
                            peer = await GetStoragePeerAsync();
                            using var photoMs = new MemoryStream(galleryPhotoBytes, false);
                            var photoInput = await _client.UploadFileAsync(photoMs, "photo.jpg");
                            var photoMedia = new TL.InputMediaUploadedPhoto { file = photoInput };
                            photoMessage = await _client.SendMessageAsync(peer, effectiveCaption, photoMedia);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TelegramService", $"Не удалось отправить фото-превью для галереи: {ex.Message}");
                        }
                    }

                    string mimeType = GetDocumentMimeType(effectiveFileName);
                    var fileNameAttr = new TL.DocumentAttributeFilename
                    {
                        file_name = effectiveFileName
                    };
                    var attributes = new TL.DocumentAttribute[] { fileNameAttr };
                    var mediaDoc = new TL.InputMediaUploadedDocument(inputFile, mimeType, attributes);

                    int replyToId = photoMessage != null ? photoMessage.ID : 0;
                    string docCaption = photoMessage != null ? string.Empty : effectiveCaption;

                    try
                    {
                        message = await _client.SendMessageAsync(peer, docCaption, mediaDoc, reply_to_msg_id: replyToId);
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                    {
                        AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                        InvalidateStoragePeer();
                        peer = await GetStoragePeerAsync();
                        message = await _client.SendMessageAsync(peer, docCaption, mediaDoc, reply_to_msg_id: replyToId);
                    }

                    if (message != null)
                    {
                        AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' успешно сохранен в Telegram. Message ID: {message.ID}" + (photoMessage != null ? $", Preview ID: {photoMessage.ID}" : ""));
                        return new FileUploadResult(message.ID, photoMessage?.ID);
                    }
                }

                if (message != null)
                {
                    AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' успешно сохранен в Telegram. Message ID: {message.ID}");
                    return new FileUploadResult(message.ID);
                }

                AppLogger.Warn("TelegramService", "Сообщение отправлено, но ID не определен, возвращаем 1.");
                return new FileUploadResult(1);
            }
            finally
            {
                OnUploadCompleted?.Invoke(effectiveFileName);

                if (tempFilePath != null)
                {
                    try { uploadStream.Dispose(); } catch { }
                    try 
                    { 
                        if (File.Exists(tempFilePath)) File.Delete(tempFilePath); 
                        string? parentDir = Path.GetDirectoryName(tempFilePath);
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            Directory.Delete(parentDir, true);
                        }
                    } 
                    catch { }
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

        private static string GetDocumentMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                ".tif" or ".tiff" => "image/tiff",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".rar" => "application/x-rar-compressed",
                ".7z" => "application/x-7z-compressed",
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".html" or ".htm" => "text/html",
                _ => "application/octet-stream"
            };
        }

        public static bool IsGalleryImage(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";
        }

        public static byte[]? CreateOptimizedGalleryThumbnail(Stream stream, int maxW = 1920, int maxH = 1920)
        {
            try
            {
                if (!stream.CanSeek) return null;
                long origin = stream.Position;
                stream.Seek(0, SeekOrigin.Begin);

                using var originalBmp = System.Drawing.Image.FromStream(stream, false, false);
                stream.Seek(origin, SeekOrigin.Begin);

                int origW = originalBmp.Width;
                int origH = originalBmp.Height;
                if (origW <= 0 || origH <= 0) return null;

                double ratioW = (double)maxW / origW;
                double ratioH = (double)maxH / origH;
                double ratio = Math.Min(ratioW, ratioH);
                if (ratio > 1.0) ratio = 1.0;

                int newW = Math.Max(1, (int)(origW * ratio));
                int newH = Math.Max(1, (int)(origH * ratio));

                using var resized = new System.Drawing.Bitmap(newW, newH);
                using (var g = System.Drawing.Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.DrawImage(originalBmp, 0, 0, newW, newH);
                }

                using var outMs = new MemoryStream();
                var encoder = GetEncoder(System.Drawing.Imaging.ImageFormat.Jpeg);
                if (encoder != null)
                {
                    using var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    resized.Save(outMs, encoder, encoderParams);
                }
                else
                {
                    resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                return outMs.ToArray();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TelegramService", $"Не удалось сформировать превью галереи: {ex.Message}");
                return null;
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo? GetEncoder(System.Drawing.Imaging.ImageFormat format)
        {
            var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
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
        /// Централизованное получение или скачивание чанка MTProto.
        /// Если чанк уже скачан и содержит требуемое количество байт — мгновенно возвращает его из RAM-кэша.
        /// Для зондов метаданных адаптивно запрашивает минимально достаточный блок (64 КБ / 128 КБ / 256 КБ / 512 КБ),
        /// а при реальном стриминге и копировании — полноценные 1 МБ блоки.
        /// Если чанк прямо сейчас качается другим потоком — присоединяется к существующей Task<byte[]?> без дублирования сетевых запросов.
        /// </summary>
        private async Task<byte[]?> GetOrDownloadChunkAsync(
            int messageId,
            TL.Document document,
            int chunkIndex,
            string fileName,
            long totalFileSize,
            NetworkTransferAudit audit,
            int minRequiredBytes = 1048576,
            bool isMetadataProbe = false,
            CancellationToken cancellationToken = default)
        {
            int cacheTtlMinutes = _configManager?.CurrentSettings?.Server?.ChunkMemoryCacheTtlMinutes ?? 10;
            string chunkKey = $"{messageId}:{chunkIndex}";
            long chunkOffset = (long)chunkIndex * 1048576;
            long actualTotal = totalFileSize > 0 ? totalFileSize : document.size;

            byte[]? existingPrefix = null;

            // 1. Проверяем наличие в RAM-кэше
            if (_chunkMemoryCache.TryGetValue(chunkKey, out var cached) && cached.expiresAt > DateTime.UtcNow && cached.data != null)
            {
                // Если в кэше уже есть нужный объем байт, или полный 1 МБ, или хвост до конца файла — отдаем из ОЗУ
                if (cached.data.Length >= minRequiredBytes || cached.data.Length >= 1048576 || (actualTotal > 0 && chunkOffset + cached.data.Length >= actualTotal))
                {
                    AppLogger.Info("TelegramService", $"[Cache RAM] Точечное чтение из ОЗУ для '{fileName}' (ID {messageId}): Чанк #{chunkIndex}/{audit.TotalChunks} (смещение {chunkOffset:N0} б, размер {cached.data.Length / 1024} КБ). {audit.ProgressSummary}.");
                    return cached.data;
                }

                // Если в кэше есть начальная часть (префикс), выравнивание по 4 КБ (4096 байт)
                if (cached.data.Length > 0 && cached.data.Length % 4096 == 0)
                {
                    existingPrefix = cached.data;
                }
            }

            // 2. Если чанк уже качается другой задачей / потоком — присоединяемся к существующей Task<byte[]?>
            return await _inFlightChunkTasks.GetOrAdd(chunkKey, _ => Task.Run(async () =>
            {
                try
                {
                    await EnsureFloodWaitDelayAsync();
                    if (_client == null) return null;

                    var location = document.ToFileLocation();

                    if (chunkOffset >= actualTotal && actualTotal > 0)
                    {
                        return Array.Empty<byte>();
                    }

                    int existingLength = existingPrefix?.Length ?? 0;

                    // Адаптивный расчет целевого размера блока:
                    int targetSize;
                    if (isMetadataProbe)
                    {
                        if (minRequiredBytes <= 65536) targetSize = 65536;       // 64 КБ
                        else if (minRequiredBytes <= 131072) targetSize = 131072; // 128 КБ
                        else if (minRequiredBytes <= 262144) targetSize = 262144; // 256 КБ
                        else if (minRequiredBytes <= 524288) targetSize = 524288; // 512 КБ
                        else targetSize = 1048576;                               // 1 МБ
                    }
                    else
                    {
                        targetSize = 1048576; // 1 МБ для стриминга и копирования
                    }

                    if (targetSize <= existingLength)
                    {
                        return existingPrefix;
                    }

                    long fetchOffset = chunkOffset + existingLength;
                    int requestLimit = targetSize - existingLength;

                    // Защита: requestLimit должен быть выравнен и кратен 4 КБ
                    if (requestLimit % 4096 != 0)
                    {
                        existingPrefix = null;
                        existingLength = 0;
                        fetchOffset = chunkOffset;
                        requestLimit = targetSize;
                    }

                    TL.Upload_FileBase? fileBase = null;
                    await _downloadRpcSemaphore.WaitAsync(cancellationToken);
                    int workerId = 1;
                    if (!_availableWorkerIds.TryDequeue(out workerId)) workerId = 1;

                    try
                    {
                        var activeClient = await GetWorkerClientAsync(workerId, document.dc_id);
                        int totalChunks = audit.TotalChunks;
                        string tailTag = existingLength > 0 ? $" [Докачка хвоста +{requestLimit / 1024} КБ до {targetSize / 1024} КБ]" : (requestLimit < 1048576 ? $" [Зонд метаданных {requestLimit / 1024} КБ]" : "");
                        AppLogger.Info("TelegramService", $"[MTProto] [Воркер #{workerId}] Запрос чанка #{chunkIndex}/{totalChunks} для '{fileName}' (ID {messageId}): смещение {fetchOffset:N0} б, размер {requestLimit / 1024} КБ{tailTag}...");
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        fileBase = await activeClient.Upload_GetFile(location, fetchOffset, requestLimit, precise: true);
                        sw.Stop();
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 303) // FILE_MIGRATE_X
                    {
                        var activeClient = await GetWorkerClientAsync(workerId, rpcEx.X);
                        fileBase = await activeClient.Upload_GetFile(location, fetchOffset, requestLimit, precise: true);
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && rpcEx.Message.Contains("FILE_REFERENCE_EXPIRED"))
                    {
                        var refreshedDoc = await GetDocumentFromMessageAsync(messageId, forceRefresh: true);
                        if (refreshedDoc != null)
                        {
                            location = refreshedDoc.ToFileLocation();
                            var activeClient = await GetWorkerClientAsync(workerId, refreshedDoc.dc_id);
                            fileBase = await activeClient.Upload_GetFile(location, fetchOffset, requestLimit, precise: true);
                        }
                        else throw;
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                    {
                        int waitSec = rpcEx.X > 0 ? rpcEx.X : 5;
                        AppLogger.Warn("TelegramService", $"[FLOOD_WAIT] [Воркер #{workerId}] Telegram запросил паузу {waitSec} сек.");
                        _floodWaitUntil = DateTime.UtcNow.AddSeconds(waitSec);
                        await Task.Delay(waitSec * 1000, cancellationToken);
                        var activeClient = await GetWorkerClientAsync(workerId, document.dc_id);
                        fileBase = await activeClient.Upload_GetFile(location, fetchOffset, requestLimit, precise: true);
                    }
                    finally
                    {
                        _availableWorkerIds.Enqueue(workerId);
                        _downloadRpcSemaphore.Release();
                    }

                    if (fileBase is TL.Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                    {
                        byte[] tailRaw = uploadFile.bytes;
                        audit.AddNetworkBytes(tailRaw.Length);
                        bool allReceived = audit.MarkRangeReceived(fetchOffset, tailRaw.Length);

                        byte[] fullData;
                        if (existingPrefix != null && existingPrefix.Length > 0)
                        {
                            fullData = new byte[existingPrefix.Length + tailRaw.Length];
                            Buffer.BlockCopy(existingPrefix, 0, fullData, 0, existingPrefix.Length);
                            Buffer.BlockCopy(tailRaw, 0, fullData, existingPrefix.Length, tailRaw.Length);
                        }
                        else
                        {
                            fullData = tailRaw;
                        }

                        EnsureChunkCacheCapacity();
                        _chunkMemoryCache[chunkKey] = (fullData, DateTime.UtcNow.AddMinutes(cacheTtlMinutes));

                        AppLogger.Info("TelegramService", $"[MTProto] Получен чанк #{chunkIndex}/{audit.TotalChunks} для '{fileName}': скачано {tailRaw.Length / 1024} КБ (итого в RAM: {fullData.Length / 1024} КБ). {audit.ProgressSummary}.");

                        OnChunkCached?.Invoke(fileName, chunkOffset + fullData.Length, actualTotal);

                        if (allReceived && audit.LogCompletionOnce())
                        {
                            OnDownloadCompleted?.Invoke(fileName);
                        }

                        return fullData;
                    }
                    return existingPrefix;
                }
                finally
                {
                    _inFlightChunkTasks.TryRemove(chunkKey, out Task<byte[]?>? _);
                }
            }));
        }

        /// <summary>
        /// Потоковое скачивание чанков напрямую из Telegram через MTProto Upload_GetFile.
        /// В режиме Pure RAM Mode качает данные 100% через ОЗУ (без файлов на диске), сохраняя чанки в динамический 128 МБ RAM-кэш.
        /// Гарантирует точную поблочную адресацию без дублирования очередей и без искажения байтов.
        /// </summary>
        public async Task DownloadFileAsync(int messageId, Stream destination, long offset, long length, string fileName = "файл", long totalFileSize = -1)
        {
            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            if (length <= 0) return;

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
            if (offset >= actualTotalSize) return;
            long effectiveLength = Math.Min(length, actualTotalSize - offset);
            if (effectiveLength <= 0) return;

            long startChunkIdx = offset / 1048576;
            long endChunkIdx = (offset + effectiveLength - 1) / 1048576;

            var audit = _networkAudits.GetOrAdd(messageId, _ => new NetworkTransferAudit { FileName = fileName, FileSize = actualTotalSize });
            audit.FileName = fileName;
            audit.FileSize = actualTotalSize;

            var readSeq = _fileReadSequences.GetOrAdd(messageId, _ => new FileReadSequence());
            lock (readSeq)
            {
                var now = DateTime.UtcNow;
                if ((now - readSeq.LastReadTime).TotalSeconds > 10)
                {
                    readSeq.SequentialCount = 0;
                    readSeq.LastChunkIndex = -1;
                }

                if (readSeq.LastChunkIndex != -1 && startChunkIdx == readSeq.LastChunkIndex + 1)
                {
                    readSeq.SequentialCount++;
                }
                else if (readSeq.LastChunkIndex != startChunkIdx)
                {
                    readSeq.SequentialCount = 1;
                }

                readSeq.LastChunkIndex = startChunkIdx;
                readSeq.LastReadTime = now;
            }

            // Проверка зонда метаданных / превью:
            bool isFirstChunkProbe = startChunkIdx == 0 && effectiveLength <= 262144;
            bool isTailChunkProbe = (actualTotalSize > 2097152 && offset >= actualTotalSize - 2097152) ||
                                    (audit.TotalChunks > 1 && startChunkIdx >= audit.TotalChunks - 1);
            bool isMetadataProbe = isFirstChunkProbe || isTailChunkProbe || readSeq.SequentialCount < 2;

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
                            audit.AddNetworkBytes(new FileInfo(cacheFilePath).Length);
                            audit.MarkRangeReceived(0, actualTotalSize);
                            if (!isMetadataProbe && audit.IsAllChunksReceived() && audit.LogCompletionOnce())
                            {
                                OnDownloadCompleted?.Invoke(fileName);
                            }
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

            // Для аудио и больших файлов запускаем строго ОДИН фоновый конвейер упреждающей выкачки чанков в RAM
            if (!enableDiskCache && actualTotalSize > 262144 && !isMetadataProbe)
            {
                TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, offset, audit);
            }

            // Чтение запрошенного диапазона чанк за чанком через GetOrDownloadChunkAsync
            long currentPos = offset;
            long remainingBytes = effectiveLength;

            for (int chunkIdx = (int)startChunkIdx; chunkIdx <= (int)endChunkIdx && remainingBytes > 0; chunkIdx++)
            {
                long chunkBaseOffset = (long)chunkIdx * 1048576;
                int sliceOffset = (int)(currentPos - chunkBaseOffset);
                int neededInChunk = (int)Math.Min(1048576 - sliceOffset, remainingBytes);
                int minRequiredBytes = sliceOffset + neededInChunk;

                byte[]? chunkData = await GetOrDownloadChunkAsync(
                    messageId,
                    document,
                    chunkIdx,
                    fileName,
                    actualTotalSize,
                    audit,
                    minRequiredBytes: minRequiredBytes,
                    isMetadataProbe: isMetadataProbe,
                    cancellationToken: CancellationToken.None);

                if (chunkData == null || chunkData.Length == 0)
                {
                    AppLogger.Warn("TelegramService", $"Не удалось получить чанк #{chunkIdx} для '{fileName}' (ID {messageId}).");
                    break;
                }

                if (sliceOffset < 0 || sliceOffset >= chunkData.Length)
                {
                    break;
                }

                int availableInChunk = chunkData.Length - sliceOffset;
                int bytesToWrite = (int)Math.Min(availableInChunk, remainingBytes);

                if (bytesToWrite > 0)
                {
                    try
                    {
                        await destination.WriteAsync(chunkData, sliceOffset, bytesToWrite);
                        await destination.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"Клиент прервал соединение для '{fileName}' (ID {messageId}): {ex.Message}");
                        return;
                    }

                    currentPos += bytesToWrite;
                    remainingBytes -= bytesToWrite;
                    audit.AddRamBytes(bytesToWrite);

                    if (!isMetadataProbe)
                    {
                        OnDownloadProgress?.Invoke(fileName, currentPos, actualTotalSize);
                    }
                    else
                    {
                        long transferredMetaBytes = audit.NetworkBytes > 0 ? audit.NetworkBytes : Math.Min(actualTotalSize, (long)audit.ReceivedChunksCount * 65536);
                        OnMetadataProgress?.Invoke(fileName, transferredMetaBytes, actualTotalSize);
                        OnChunkCached?.Invoke(fileName, currentPos, actualTotalSize);
                    }
                }
            }

            if (isMetadataProbe)
            {
                if (audit.LogMetadataCompletionOnce())
                {
                    OnMetadataCompleted?.Invoke(fileName);
                }
            }
            else if (audit.IsAllChunksReceived() && audit.LogCompletionOnce())
            {
                OnDownloadCompleted?.Invoke(fileName);
            }
        }

        /// <summary>
        /// Запускает непрерывный конвейер из до 3 параллельных воркеров MTProto для упреждающей выкачки оставшихся чанков в RAM.
        /// Гарантирует строго 1 активный пул воркеров на файл, предотвращая параллельные коллизии сессий и дублирование очередей.
        /// </summary>
        private void TriggerContinuousPrefetch(int messageId, TL.Document document, string fileName, long actualTotalSize, long currentReadOffset, NetworkTransferAudit audit)
        {
            bool enableDiskCache = _configManager?.CurrentSettings?.Server?.EnableDiskReadCache ?? false;
            if (enableDiskCache || document == null || actualTotalSize <= 262144 || _client == null) return;

            var cts = new CancellationTokenSource();
            if (!_activeFilePrefetches.TryAdd(messageId, cts))
            {
                cts.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    int fullTrackMaxMb = _configManager?.CurrentSettings?.Server?.FullTrackPrefetchMaxFileSizeMb ?? 2;
                    bool isAudio = IsAudioFileName(fileName);
                    int windowMb = isAudio
                        ? (_configManager?.CurrentSettings?.Server?.AudioPrefetchWindowMb ?? 2)
                        : (_configManager?.CurrentSettings?.Server?.StreamingPrefetchWindowMb ?? 20);

                    long fullTrackMaxBytes = (long)fullTrackMaxMb * 1024 * 1024;
                    long windowBytes = (long)windowMb * 1024 * 1024;

                    long maxPrefetchLimit = actualTotalSize <= fullTrackMaxBytes
                        ? actualTotalSize
                        : Math.Min(actualTotalSize, currentReadOffset + windowBytes);

                    long startChunkOffset = (currentReadOffset / 1048576) * 1048576;
                    long scanOffset = actualTotalSize <= fullTrackMaxBytes ? 0 : startChunkOffset;

                    var missingChunkIndices = new List<int>();
                    while (scanOffset < maxPrefetchLimit)
                    {
                        int chunkIdx = (int)(scanOffset / 1048576);
                        string chunkKey = $"{messageId}:{chunkIdx}";
                        if (!_chunkMemoryCache.TryGetValue(chunkKey, out var entry) || entry.expiresAt <= DateTime.UtcNow || entry.data == null || (entry.data.Length < 1048576 && ((long)chunkIdx * 1048576 + entry.data.Length < actualTotalSize)))
                        {
                            missingChunkIndices.Add(chunkIdx);
                        }
                        scanOffset += 1048576;
                    }

                    if (missingChunkIndices.Count == 0) return;

                    var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(missingChunkIndices);
                    int workersCount = Math.Min(3, missingChunkIndices.Count);
                    var workerTasks = new Task[workersCount];

                    for (int w = 0; w < workersCount; w++)
                    {
                        workerTasks[w] = Task.Run(async () =>
                        {
                            while (queue.TryDequeue(out int chunkIdx))
                            {
                                if (cts.Token.IsCancellationRequested) break;
                                await GetOrDownloadChunkAsync(messageId, document, chunkIdx, fileName, actualTotalSize, audit, minRequiredBytes: 1048576, isMetadataProbe: false, cancellationToken: cts.Token);
                            }
                        }, cts.Token);
                    }

                    await Task.WhenAll(workerTasks);

                    if (audit.IsAllChunksReceived() && audit.LogCompletionOnce())
                    {
                        OnDownloadCompleted?.Invoke(fileName);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.Debug("TelegramService", $"[RAM Streaming] Фоновый конвейер для '{fileName}' (ID {messageId}) завершился: {ex.Message}");
                }
                finally
                {
                    if (_activeFilePrefetches.TryRemove(messageId, out var removedCts))
                    {
                        try { removedCts.Dispose(); } catch { }
                    }
                }
            }, cts.Token);
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

        private static bool IsAudioFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext is ".mp3" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wav" or ".wma" or ".opus" or ".alac" or ".aiff" or ".ape";
        }

        public void Dispose()
        {
            try
            {
                foreach (var cts in _activeFilePrefetches.Values)
                {
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
                _activeFilePrefetches.Clear();
            }
            catch { }
            try
            {
                _client?.Dispose();
                _client = null;
            }
            catch { }
            _floodLock?.Dispose();
            _uploadSemaphore?.Dispose();
        }
    }
}
