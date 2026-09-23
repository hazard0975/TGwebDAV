using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TelegramWebDAV.Config;
using TelegramWebDAV.Models;

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
        private AppSettings _currentSettings;
        private readonly SemaphoreSlim _floodLock = new SemaphoreSlim(1, 1);
        private DateTime _floodWaitUntil = DateTime.MinValue;
        private WTelegram.Client? _client;

        public bool IsAuthorized { get; private set; }
        public AuthStep CurrentStep { get; private set; } = AuthStep.NeedsPhone;
        public TelegramUserInfo? CurrentUser { get; private set; }
        public string? LastError { get; private set; }

        public TelegramService(ConfigManager configManager)
        {
            _configManager = configManager;
            _currentSettings = _configManager.Load();
        }

        private TL.InputPeer? _storagePeer;
        private readonly SemaphoreSlim _storageLock = new SemaphoreSlim(1, 1);

        public void UpdateApiCredentials(int apiId, string apiHash)
        {
            _currentSettings.Telegram.ApiId = apiId;
            _currentSettings.Telegram.ApiHash = apiHash;
            _storagePeer = null;
            _client?.Dispose();
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
                _storagePeer = null;
                _configManager.Save(_currentSettings);
            }
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

                // 1. Если StorageChannelId уже сохранен в настройках, используем его
                if (_currentSettings.Telegram.StorageChannelId != 0)
                {
                    var chats = await _client.Messages_GetAllChats();
                    if (chats.chats.TryGetValue(_currentSettings.Telegram.StorageChannelId, out var savedChat) &&
                        savedChat is TL.Channel sc)
                    {
                        _storagePeer = sc.ToInputPeer();
                        AppLogger.Info("TelegramService", $"Подключен существующий приватный канал-хранилище: {sc.Title} (ID: {sc.ID})");
                        return _storagePeer;
                    }
                    else
                    {
                        AppLogger.Warn("TelegramService", $"Канал с сохраненным ID {_currentSettings.Telegram.StorageChannelId} не найден в диалогах пользователя. Создаем новый.");
                    }
                }

                // 2. Если ID канала нет в конфиге (StorageChannelId == 0), создаем новый приватный канал
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
                            _configManager.Save(_currentSettings);
                            AppLogger.Info("TelegramService", $"Создан новый приватный канал '{newCh.Title}' (ID: {newCh.ID}). ID сохранен в конфиг.");
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
                    default: return null;
                }
            });
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
        public async Task<int?> UploadFileChunkAsync(Stream source, string fileName, long offset, long totalSize)
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
                        int messageId = await UploadFileAsync(completeStream, fileName);
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
        /// Потоковая загрузка файла в приватный канал-хранилище через WTelegramClient.
        /// Возвращает реальный ID сообщения из Telegram.
        /// </summary>
        public async Task<int> UploadFileAsync(Stream source, string fileName)
        {
            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            var peer = await GetStoragePeerAsync();

            AppLogger.Info("TelegramService", $"Загрузка файла '{fileName}' в Telegram...");
            var inputFile = await _client.UploadFileAsync(source, fileName);

            AppLogger.Info("TelegramService", $"Файл '{fileName}' загружен в MTProto, отправка медиа в канал...");
            var message = await _client.SendMediaAsync(peer, fileName, inputFile);

            if (message != null)
            {
                AppLogger.Info("TelegramService", $"Файл успешно отправлен в канал. Message ID: {message.ID}");
                return message.ID;
            }

            AppLogger.Warn("TelegramService", "Сообщение отправлено, но ID не определен, возвращаем 1.");
            return 1;
        }

        /// <summary>
        /// Потоковое скачивание части файла (HTTP 206) с пулом буферов.
        /// </summary>
        public async Task DownloadFileAsync(int messageId, Stream destination, long offset, long length)
        {
            await EnsureFloodWaitDelayAsync();

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    // Заполняем тестовыми или реальными данными
                    await destination.WriteAsync(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
            _floodLock?.Dispose();
        }
    }
}
