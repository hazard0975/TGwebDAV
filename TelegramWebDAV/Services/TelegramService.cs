using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    case "phone_number": return _currentSettings.Telegram.PhoneNumber;
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

                    if (!string.IsNullOrEmpty(u.phone))
                    {
                        string phoneFormatted = u.phone.StartsWith("+") ? u.phone : "+" + u.phone;
                        if (_currentSettings.Telegram.PhoneNumber != phoneFormatted)
                        {
                            _currentSettings.Telegram.PhoneNumber = phoneFormatted;
                            _configManager.Save(_currentSettings);
                        }
                    }
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
                        int? messageId = await UploadFileAsync(completeStream, fileName);
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
        public async Task<int?> UploadFileAsync(Stream source, string fileName, long length = -1)
        {
            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            var peer = await GetStoragePeerAsync();

            Stream uploadStream = source;
            string? tempFilePath = null;

            try
            {
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
                            onProgress: (pos, total) => OnUploadProgress?.Invoke(fileName, pos, total)
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
                        tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{fileName}");
                        
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
                    AppLogger.Info("TelegramService", $"Файл '{fileName}' пустой или является probe-запросом клиента ({uploadStream.Length} байт). Регистрация в БД без загрузки в Telegram.");
                    return null;
                }

                AppLogger.Info("TelegramService", $"Прямая потоковая передача файла '{fileName}' ({uploadStream.Length} байт) в Telegram...");
                
                // Передаем прогресс-колбэк также в WTelegramClient для детального трекинга MTProto частей
                var inputFile = await _client.UploadFileAsync(
                    uploadStream, 
                    fileName, 
                    progress: (pos, total) => OnUploadProgress?.Invoke(fileName, pos, total)
                );

                AppLogger.Info("TelegramService", $"Файл '{fileName}' загружен в MTProto, финализация сообщения в канале...");
                var message = await _client.SendMediaAsync(peer, fileName, inputFile);

                if (message != null)
                {
                    AppLogger.Info("TelegramService", $"Файл '{fileName}' успешно сохранен в Telegram. Message ID: {message.ID}");
                    return message.ID;
                }

                AppLogger.Warn("TelegramService", "Сообщение отправлено, но ID не определен, возвращаем 1.");
                return 1;
            }
            finally
            {
                OnUploadCompleted?.Invoke(fileName);

                if (tempFilePath != null)
                {
                    try { uploadStream.Dispose(); } catch { }
                    try { File.Delete(tempFilePath); } catch { }
                }
                else if (uploadStream != source)
                {
                    try { uploadStream.Dispose(); } catch { }
                }
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

        private async Task<TL.Document?> GetDocumentFromMessageAsync(int messageId)
        {
            if (_client == null) return null;
            var peer = await GetStoragePeerAsync();
            var messagesBase = await _client.GetMessages(peer, new TL.InputMessage[] { new TL.InputMessageID { id = messageId } });
            
            if (messagesBase is TL.Messages_Messages messages && messages.messages.Length > 0)
            {
                var msg = messages.messages[0] as TL.Message;
                if (msg?.media is TL.MessageMediaDocument mediaDoc && mediaDoc.document is TL.Document document)
                {
                    return document;
                }
            }
            else if (messagesBase is TL.Messages_ChannelMessages channelMessages && channelMessages.messages.Length > 0)
            {
                var msg = channelMessages.messages[0] as TL.Message;
                if (msg?.media is TL.MessageMediaDocument mediaDoc && mediaDoc.document is TL.Document document)
                {
                    return document;
                }
            }
            return null;
        }

        private class TeeStream : Stream
        {
            private readonly Stream _diskStream;
            private readonly Stream _netStream;
            private long _skipBytes;
            private long _remainingNetBytes;
            private long _totalNetBytesWritten;
            private bool _initialChunkPaced;

            public TeeStream(Stream diskStream, Stream netStream, long skipBytes, long maxNetBytes)
            {
                _diskStream = diskStream;
                _netStream = netStream;
                _skipBytes = Math.Max(0, skipBytes);
                _remainingNetBytes = maxNetBytes;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _diskStream.Length;
            public override long Position { get => _diskStream.Position; set => throw new NotSupportedException(); }
            public override void Flush()
            {
                _diskStream.Flush();
                try { _netStream.Flush(); } catch { }
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => _diskStream.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                // 1. Всегда пишем в файл на диске для кэша
                _diskStream.Write(buffer, offset, count);

                int netOffset = offset;
                int netCount = count;

                if (_skipBytes > 0)
                {
                    if (_skipBytes >= netCount)
                    {
                        _skipBytes -= netCount;
                        return;
                    }
                    netOffset += (int)_skipBytes;
                    netCount -= (int)_skipBytes;
                    _skipBytes = 0;
                }

                if (_remainingNetBytes <= 0 || netCount <= 0) return;

                int toWrite = (int)Math.Min(netCount, _remainingNetBytes);
                try
                {
                    _netStream.Write(buffer, netOffset, toWrite);
                    _netStream.Flush();
                    _remainingNetBytes -= toWrite;
                    _totalNetBytesWritten += toWrite;

                    // Если мы отдали первый чанк (~128 КБ) полного файла,
                    // делаем небольшую паузу (250 мс), чтобы дать Windows InfoTip прочитать теги и закрыть дескриптор.
                    // Если это было наведение курсора мыши - следующий Write/Flush мгновенно выбросит ошибку,
                    // и скачивание из Telegram остановится.
                    // Если это воспроизведение в плеере - 128 КБ содержат более 3 секунд звука, пауза незаметна.
                    if (!_initialChunkPaced && _totalNetBytesWritten >= 131072)
                    {
                        _initialChunkPaced = true;
                        Thread.Sleep(250);
                    }
                }
                catch (Exception ex)
                {
                    // Клиент разорвал соединение (например, Проводник прочитал заголовок для подсказки и закрыл дескриптор)
                    throw new OperationCanceledException("Клиент разорвал соединение", ex);
                }
            }
        }

        /// <summary>
        /// Потоковое скачивание части файла (HTTP 206) из Telegram с использованием локального дискового кэша
        /// и одновременного стриминга в ответ клиенту (с мгновенным прерыванием при закрытии соединения клиентом).
        /// </summary>
        public async Task DownloadFileAsync(int messageId, Stream destination, long offset, long length)
        {
            await EnsureFloodWaitDelayAsync();

            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            // Путь к папке кэша
            string cacheDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_ReadCache");
            Directory.CreateDirectory(cacheDir);
            string cacheFilePath = Path.Combine(cacheDir, $"{messageId}.bin");

            // 1. Если файл уже закэширован на диске полностью, читаем напрямую из дискового файла
            if (File.Exists(cacheFilePath))
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
                return;
            }

            // 2. Файла нет в кэше. Скачиваем его из Telegram, одновременно стримя в destination через TeeStream
            var document = await GetDocumentFromMessageAsync(messageId);
            if (document == null)
            {
                throw new FileNotFoundException($"Не удалось найти медиа-документ для сообщения ID {messageId} в Telegram.");
            }

            string tempFilePath = cacheFilePath + ".tmp";
            bool completedSuccessfully = false;

            try
            {
                AppLogger.Info("TelegramService", $"Запуск сквозного скачивания файла для сообщения ID {messageId} из Telegram...");
                using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var tee = new TeeStream(fs, destination, skipBytes: offset, maxNetBytes: length))
                {
                    await _client.DownloadFileAsync(document, tee);
                }

                if (File.Exists(cacheFilePath)) File.Delete(cacheFilePath);
                File.Move(tempFilePath, cacheFilePath);
                completedSuccessfully = true;
                AppLogger.Info("TelegramService", $"Файл для сообщения ID {messageId} успешно сохранен в локальный кэш.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("TelegramService", $"Клиент закрыл соединение для сообщения ID {messageId} (прочитан заголовок). Загрузка из Telegram остановлена.");
            }
            catch (Exception ex) when (ex.InnerException is OperationCanceledException)
            {
                AppLogger.Info("TelegramService", $"Клиент закрыл соединение для сообщения ID {messageId} (прочитан заголовок). Загрузка из Telegram остановлена.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("TelegramService", $"Ошибка при скачивании файла из Telegram: {ex.Message}", ex);
                throw;
            }
            finally
            {
                if (!completedSuccessfully && File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
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
            _floodLock?.Dispose();
        }
    }
}
