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
        public int DcId { get; set; }
        public int FloodWaitSecondsRemaining { get; set; }

        public string FullName
        {
            get
            {
                string fn = (FirstName ?? "").Trim();
                string ln = (LastName ?? "").Trim();
                if (!string.IsNullOrEmpty(fn) && !string.IsNullOrEmpty(ln)) return $"{fn} {ln}";
                if (!string.IsNullOrEmpty(fn)) return fn;
                if (!string.IsNullOrEmpty(ln)) return ln;
                return "Без имени";
            }
        }

        public string FormattedUsername => !string.IsNullOrWhiteSpace(Username) ? $"@{Username.TrimStart('@')}" : "Не задан";

        public string FormattedPhone
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Phone)) return "Не указан";
                return Phone.StartsWith("+") ? Phone : "+" + Phone;
            }
        }

        public string PremiumDescription => IsPremium
            ? "⭐ Telegram Premium (лимит 1 файла: 4 ГБ)"
            : "Базовый аккаунт (лимит 1 файла: 2 ГБ)";

        public string DcDescription
        {
            get
            {
                return DcId switch
                {
                    1 => "DC 1 (Майами, США)",
                    2 => "DC 2 (Амстердам, Нидерланды)",
                    3 => "DC 3 (Майами, США)",
                    4 => "DC 4 (Амстердам, Нидерланды)",
                    5 => "DC 5 (Сингапур)",
                    _ => DcId > 0 ? $"DC {DcId}" : "Автоопределение"
                };
            }
        }
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
        private static DateTime _globalFloodWaitUntil = DateTime.MinValue;
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

        // Быстрый кольцевой кэш чанков MTProto в оперативной памяти (настраивается в UI, по умолчанию 128 МБ) для мгновенного чтения плеерами без повторных обращений к сети
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] data, DateTime expiresAt)> _chunkMemoryCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _fileDownloadLocks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, NetworkTransferAudit> _networkAudits = new();
        private MtprotoDownloadWorkerPool? _workerPool;

        private class FileReadSequence
        {
            public long LastChunkIndex { get; set; } = -1;
            public int SequentialCount { get; set; } = 0;
            public long AccumulatedSequentialBytes { get; set; } = 0;
            public DateTime LastReadTime { get; set; } = DateTime.UtcNow;
            public System.Collections.Concurrent.ConcurrentDictionary<long, long> ChunkBytesRead { get; } = new();
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
            private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _chunkBytesAccumulator = new();

            public int TotalChunks => FileSize > 0 ? (int)Math.Ceiling((double)FileSize / 1048576.0) : 1;
            public int ReceivedChunksCount => _receivedChunkIndexes.Count;
            public string ProgressSummary
            {
                get
                {
                    double pct = TotalChunks > 0 ? (double)ReceivedChunksCount * 100.0 / TotalChunks : 100.0;
                    return $"Скачано {ReceivedChunksCount} из {TotalChunks} чанков ({pct:0.#}%)";
                }
            }

            public void AddNetworkBytes(long bytes) => Interlocked.Add(ref _networkBytesDownloaded, bytes);
            public void AddRamBytes(long bytes) => Interlocked.Add(ref _ramBytesDelivered, bytes);

            public long NetworkBytes => Interlocked.Read(ref _networkBytesDownloaded);
            public long RamBytes => Interlocked.Read(ref _ramBytesDelivered);

            /// <summary>
            /// Помечает диапазон байт как полученный. Чанк отмечается как завершенный при получении 100% байт данного блока (в том числе слайсами по 256 КБ).
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
                        long chunkStart = (long)i * 1048576;
                        long chunkEnd = Math.Min(FileSize, chunkStart + 1048576);
                        long expectedChunkBytes = chunkEnd - chunkStart;

                        // Считаем пересечение диапазона с данным чанком
                        long overlapStart = Math.Max(offset, chunkStart);
                        long overlapEnd = Math.Min(endPos, chunkEnd);
                        long overlapBytes = Math.Max(0, overlapEnd - overlapStart);

                        if (overlapBytes > 0)
                        {
                            long accumulated = _chunkBytesAccumulator.AddOrUpdate(i, overlapBytes, (_, old) => old + overlapBytes);
                            if (accumulated >= expectedChunkBytes || (offset <= chunkStart && endPos >= chunkEnd))
                            {
                                _receivedChunkIndexes.TryAdd(i, true);
                            }
                        }
                    }
                }

                return IsAllChunksReceived();
            }

            public bool IsAllChunksReceived()
            {
                if (FileSize <= 0) return true;
                return _receivedChunkIndexes.Count >= TotalChunks;
            }

            public bool LogMetadataCompletedOnce()
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

        private void StoreChunkInMemoryCache(int messageId, long chunkOffset, byte[] raw, int cacheTtlMinutes)
        {
            EnsureChunkCacheCapacity();
            long megaStart = (chunkOffset / 1048576) * 1048576;

            // Если пришел хвост к существующему зонду головы того же 1 МБ блока:
            if (chunkOffset > megaStart)
            {
                // Ищем существующий зонд головы в ОЗУ
                string headProbeKey = $"{messageId}:{megaStart}:{(int)(chunkOffset - megaStart)}";
                if (_chunkMemoryCache.TryRemove(headProbeKey, out var headProbeItem) && headProbeItem.data != null)
                {
                    byte[] probeData = headProbeItem.data;
                    int fullSize = probeData.Length + raw.Length;
                    byte[] stitched = new byte[fullSize];
                    Buffer.BlockCopy(probeData, 0, stitched, 0, probeData.Length);
                    Buffer.BlockCopy(raw, 0, stitched, probeData.Length, raw.Length);

                    string fullChunkKey = $"{messageId}:{megaStart}:{fullSize}";
                    _chunkMemoryCache[fullChunkKey] = (stitched, DateTime.UtcNow.AddMinutes(cacheTtlMinutes));
                    return;
                }
            }

            // Очищаем частичные фрагменты только при получении полного 1 МБ блока
            if (raw.Length >= 1048576)
            {
                foreach (var key in _chunkMemoryCache.Keys)
                {
                    var parts = key.Split(':');
                    if (parts.Length == 3 && int.TryParse(parts[0], out var mId) && mId == messageId && long.TryParse(parts[1], out var cStart) && cStart >= megaStart && cStart < megaStart + 1048576)
                    {
                        _chunkMemoryCache.TryRemove(key, out _);
                    }
                }
            }
            else
            {
                string exactKey = $"{messageId}:{chunkOffset}:{raw.Length}";
                _chunkMemoryCache.TryRemove(exactKey, out _);
            }

            string canonicalKey = $"{messageId}:{chunkOffset}:{raw.Length}";
            _chunkMemoryCache[canonicalKey] = (raw, DateTime.UtcNow.AddMinutes(cacheTtlMinutes));
        }

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
            StartDeletionQueueWorker();
        }

        public void SetRepository(NodeRepository repository)
        {
            _repository = repository;
            StartCaptionQueueWorker();
            StartDeletionQueueWorker();
        }

        private System.Threading.CancellationTokenSource? _deletionQueueCts;
        private readonly SemaphoreSlim _deletionSignal = new SemaphoreSlim(0, int.MaxValue);

        /// <summary>
        /// Сигнализирует фоновому воркеру об отправке новых сообщений в очередь перманентного удаления
        /// </summary>
        public void TriggerDeletionQueueProcessing()
        {
            try
            {
                if (_deletionSignal.CurrentCount == 0)
                {
                    _deletionSignal.Release();
                }
            }
            catch { }
        }

        private void StartDeletionQueueWorker()
        {
            if (_deletionQueueCts != null) return;
            _deletionQueueCts = new System.Threading.CancellationTokenSource();
            Task.Run(() => ProcessDeletionQueueAsync(_deletionQueueCts.Token));
            TriggerDeletionQueueProcessing();
        }

        private async Task ProcessDeletionQueueAsync(System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 1. Ожидаем сигнала на удаление. Когда очередь пуста — поток спит без таймаутов и холостых опросов SQLite!
                try
                {
                    await _deletionSignal.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 2. Обрабатываем очередь до тех пор, пока она не опустеет
                while (!token.IsCancellationRequested)
                {
                    if (!IsAuthorized || _client == null || _repository == null)
                    {
                        // Не авторизованы — прерываем цикл, обработка возобновится при успешном входе
                        break;
                    }

                    List<int> batch;
                    try
                    {
                        batch = _repository.GetPendingDeletions(limit: 100);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"[DeletionQueue] Ошибка чтения очереди: {ex.Message}");
                        await Task.Delay(3000, token);
                        break;
                    }

                    // Если очередь пуста — выходим из внутреннего цикла и спим глубоким сном до следующего сигнала
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    AppLogger.Info("TelegramService", $"[DeletionQueue] Фоновое перманентное удаление {batch.Count} сообщений из Telegram...");

                    bool success = false;
                    try
                    {
                        success = await DeleteFilesFromTelegramAsync(batch);
                    }
                    catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                    {
                        AppLogger.Warn("TelegramService", $"[DeletionQueue] FloodWait при удалении сообщений: пауза {rpcEx.X} сек...");
                        await Task.Delay(Math.Max(5000, rpcEx.X * 1000), token);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"[DeletionQueue] Ошибка при обращении к Telegram: {ex.Message}");
                        await Task.Delay(5000, token);
                        continue;
                    }

                    if (success)
                    {
                        try
                        {
                            _repository.RemovePendingDeletions(batch);
                            AppLogger.Info("TelegramService", $"[DeletionQueue] Пакет из {batch.Count} сообщений успешно очищен из очереди.");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("TelegramService", $"[DeletionQueue] Ошибка удаления из базы: {ex.Message}");
                        }
                    }
                    else
                    {
                        // При неудаче в сети/биссекции повторяем попытку через 5 секунд только пока в очереди есть записи
                        AppLogger.Warn("TelegramService", $"[DeletionQueue] Не удалось завершить пакетное удаление сообщений из Telegram. Повтор через 5 секунд...");
                        await Task.Delay(5000, token);
                    }
                }
            }
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
            SetPacingDelay(_currentSettings.Server.PacingDelayMs);
            AppLogger.Info("TelegramService", $"[Streaming Config] Воркеры: {_currentSettings.Server.DownloadWorkerCount} | Пейсинг: {_globalPacingDelayMs} мс | RAM Кэш: {_currentSettings.Server.MemoryCacheSizeMb} МБ (TTL: {_currentSettings.Server.ChunkMemoryCacheTtlMinutes} мин) | Буфер аудио: {_currentSettings.Server.AudioPrefetchWindowMb} МБ | Окно стриминга: {_currentSettings.Server.StreamingPrefetchWindowMb} МБ | Дисковый кэш: {(_currentSettings.Server.EnableDiskReadCache ? "ВКЛ" : "ВЫКЛ (100% RAM)")}");
            
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

        private void InitWorkerPool()
        {
            if (_client == null) return;
            int configuredWorkers = Math.Clamp(_currentSettings.Server.DownloadWorkerCount > 0 ? _currentSettings.Server.DownloadWorkerCount : 1, 1, 3);
            _workerPool?.Dispose();
            _workerPool = new MtprotoDownloadWorkerPool(
                _client,
                workerCount: configuredWorkers,
                clientProvider: GetWorkerClientAsync,
                existingChunkProvider: (msgId, off) => TryGetFromMemoryCache(msgId, off, out var d, out _) ? d : null,
                documentRefresher: async (msgId) => await GetDocumentFromMessageAsync(msgId, forceRefresh: true)
            );
        }

        /// <summary>
        /// Мгновенное применение новых настроек в рантайме без перезапуска сервиса.
        /// </summary>
        public void UpdateSettings(AppSettings settings)
        {
            _currentSettings = settings;
            SetPacingDelay(_currentSettings.Server.PacingDelayMs);
            if (_workerPool != null)
            {
                _workerPool.SetWorkerCount(_currentSettings.Server.DownloadWorkerCount);
            }
            else if (_client != null && IsAuthorized)
            {
                InitWorkerPool();
            }
            AppLogger.Info("TelegramService", $"[Config Update] Настройки сервиса обновлены: воркеров={_currentSettings.Server.DownloadWorkerCount}, пейсинг={_currentSettings.Server.PacingDelayMs} мс, RAM кэш={_currentSettings.Server.MemoryCacheSizeMb} МБ.");
        }

        /// <summary>
        /// Возвращает сессию DC основного клиента MTProto для параллельных воркеров пула.
        /// Протокол MTProto поддерживает мультиплексирование параллельных запросов чанков без повторных файловых блокировок.
        /// </summary>
        public async Task<WTelegram.Client> GetWorkerClientAsync(int workerId, int dcId)
        {
            if (_client == null)
                throw new InvalidOperationException("Основной клиент Telegram не инициализирован.");

            return dcId != 0 ? await _client.GetClientForDC(dcId) : _client;
        }

        private void HandleWTelegramResult(string? result)
        {
            if (result == null)
            {
                // Успешная авторизация
                IsAuthorized = true;
                CurrentStep = AuthStep.Authorized;
                LastError = null;
                InitWorkerPool();

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
                        IsPremium = u.flags.HasFlag(TL.User.Flags.premium),
                        DcId = 0
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

                    // Фоновый прогрев канала-хранилища и получение конфигурации DC сразу после успешной авторизации,
                    // чтобы первая операция копирования/чтения файлов выполнялась мгновенно
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_client != null)
                            {
                                var config = await _client.Help_GetConfig();
                                if (config != null && CurrentUser != null)
                                {
                                    CurrentUser.DcId = config.this_dc;
                                }
                            }
                        }
                        catch { }

                        try
                        {
                            await GetStoragePeerAsync();
                            TriggerDeletionQueueProcessing();
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
                _workerPool?.Dispose();
                _workerPool = null;
            }
            catch { }

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
        /// Сквозной для всех воркеров, файлов и пулов приложения.
        /// </summary>
        public static async Task EnsureFloodWaitDelayAsync(CancellationToken cancellationToken = default)
        {
            if (_globalFloodWaitUntil > DateTime.UtcNow)
            {
                var delay = _globalFloodWaitUntil - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 0)
                {
                    AppLogger.Warn("TelegramService", $"FLOOD_WAIT активен: задержка потока на {delay.TotalSeconds:F1} сек...");
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        // Глобальный сквозной семафор и таймер пейсинга для всех исходящих запросов Upload_GetFile в приложении
        private static readonly SemaphoreSlim _globalPacingLock = new SemaphoreSlim(1, 1);
        private static DateTime _globalLastResponseUtc = DateTime.MinValue;
        private static int _globalPacingDelayMs = ServerSettings.DefaultPacingDelayMs;

        /// <summary>
        /// Гарантирует минимальный интервал запуска между любыми исходящими запросами Upload_GetFile в Telegram MTProto.
        /// Предотвращает возникновение пиковых наложений (0-5 мс) между разными файлами и воркерами.
        /// </summary>
        public static async Task EnsurePacingDelayAsync(CancellationToken cancellationToken = default)
        {
            await _globalPacingLock.WaitAsync(cancellationToken);
            try
            {
                if (_globalLastResponseUtc != DateTime.MinValue && _globalPacingDelayMs > 0)
                {
                    var elapsed = (DateTime.UtcNow - _globalLastResponseUtc).TotalMilliseconds;
                    if (elapsed < _globalPacingDelayMs)
                    {
                        await Task.Delay((int)(_globalPacingDelayMs - elapsed), cancellationToken);
                    }
                }
            }
            finally
            {
                _globalPacingLock.Release();
            }
        }

        /// <summary>
        /// Фиксирует завершение сетевого вызова к серверам Telegram для корректного отсчёта паузы между запросами.
        /// </summary>
        public static void NotifyRequestCompleted()
        {
            _globalLastResponseUtc = DateTime.UtcNow;
        }

        public static void SetPacingDelay(int delayMs)
        {
            int effective = (delayMs >= ServerSettings.MinPacingDelayMs && delayMs <= ServerSettings.MaxPacingDelayMs)
                ? delayMs
                : ServerSettings.DefaultPacingDelayMs;
            Interlocked.Exchange(ref _globalPacingDelayMs, effective);
        }

        public static void AdaptPacingDelay(int deltaMs)
        {
            Interlocked.Exchange(ref _globalPacingDelayMs, Math.Clamp(_globalPacingDelayMs + deltaMs, ServerSettings.MinPacingDelayMs, ServerSettings.MaxPacingDelayMs));
        }

        public void TriggerFloodWait(int seconds)
        {
            TriggerGlobalFloodWait(seconds);
        }

        public static void TriggerGlobalFloodWait(int seconds)
        {
            var targetTime = DateTime.UtcNow.AddSeconds(seconds);
            if (targetTime > _globalFloodWaitUntil)
            {
                _globalFloodWaitUntil = targetTime;
            }
            AdaptPacingDelay(20);
            AppLogger.Warn("TelegramService", $"Получен FLOOD_WAIT на {seconds} сек от серверов Telegram. Сквозная пауза до {_globalFloodWaitUntil:HH:mm:ss}. Пейсинг адаптирован до {_globalPacingDelayMs} мс.");
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

                long fileLength = uploadStream.Length;
                byte[]? galleryPhotoBytes = null;
                if (isGallery && uploadStream.CanSeek && fileLength > 1)
                {
                    galleryPhotoBytes = CreateOptimizedGalleryThumbnail(uploadStream);
                    uploadStream.Seek(0, SeekOrigin.Begin);
                }

                AppLogger.Info("TelegramService", $"Прямая потоковая передача файла '{effectiveFileName}' ({fileLength} байт) в Telegram...");
                
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
                    string previewFileName = $"{Path.GetFileNameWithoutExtension(effectiveFileName)}_preview.jpg";
                    if (galleryPhotoBytes != null && galleryPhotoBytes.Length > 0)
                    {
                        try
                        {
                            using var photoMs = new MemoryStream(galleryPhotoBytes, false);
                            var photoInput = await _client.UploadFileAsync(photoMs, previewFileName);
                            var photoMedia = new TL.InputMediaUploadedPhoto { file = photoInput };
                            photoMessage = await _client.SendMessageAsync(peer, effectiveCaption, photoMedia);
                            AppLogger.Info("TelegramService", $"Фото-превью для галереи успешно опубликовано в Telegram (Message ID: {photoMessage.ID}, имя: '{previewFileName}').");
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
                        {
                            InvalidateStoragePeer();
                            peer = await GetStoragePeerAsync();
                            using var photoMs = new MemoryStream(galleryPhotoBytes, false);
                            var photoInput = await _client.UploadFileAsync(photoMs, previewFileName);
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

                    // Если размер файла больше 10 МБ, Telegram не создает серверное превью для документов - прикрепляем локально созданный thumb
                    if (galleryPhotoBytes != null && galleryPhotoBytes.Length > 0 && fileLength > 10 * 1024 * 1024)
                    {
                        try
                        {
                            using var thumbMs = new MemoryStream(galleryPhotoBytes, false);
                            var thumbFile = await _client.UploadFileAsync(thumbMs, previewFileName);
                            if (thumbFile != null)
                            {
                                mediaDoc.thumb = thumbFile;
                                mediaDoc.flags |= TL.InputMediaUploadedDocument.Flags.has_thumb;
                                AppLogger.Info("TelegramService", $"К документу >10 МБ '{effectiveFileName}' успешно прикреплена миниатюра (thumb).");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("TelegramService", $"Не удалось прикрепить thumb к документу >10 МБ: {ex.Message}");
                        }
                    }

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

                using var memStream = new MemoryStream();
                stream.CopyTo(memStream);
                stream.Seek(origin, SeekOrigin.Begin);
                memStream.Seek(0, SeekOrigin.Begin);

                using var originalBmp = System.Drawing.Image.FromStream(memStream, false, false);

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
                    g.DrawImage(originalBmp, new System.Drawing.Rectangle(0, 0, newW, newH), 0, 0, origW, origH, System.Drawing.GraphicsUnit.Pixel);
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
            var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
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
            long currentChunkIdx = offset / 1048576;
            long totalChunks = actualTotalSize > 0 ? (long)Math.Ceiling((double)actualTotalSize / 1048576.0) : 1;

            var audit = _networkAudits.GetOrAdd(messageId, _ => new NetworkTransferAudit { FileName = fileName, FileSize = actualTotalSize });
            audit.FileName = fileName;
            audit.FileSize = actualTotalSize;

            var readSeq = _fileReadSequences.GetOrAdd(messageId, _ => new FileReadSequence());
            int cacheTtlMinutes = _currentSettings.Server.ChunkMemoryCacheTtlMinutes;
            long currentChunkReadBytes = 0;
            lock (readSeq)
            {
                var now = DateTime.UtcNow;
                // Время жизни непрерывной сессии воспроизведения привязано к TTL кэша из настроек
                if ((now - readSeq.LastReadTime).TotalMinutes > cacheTtlMinutes)
                {
                    readSeq.SequentialCount = 0;
                    readSeq.AccumulatedSequentialBytes = 0;
                    readSeq.LastChunkIndex = -1;
                    readSeq.ChunkBytesRead.Clear();
                }

                if (readSeq.LastChunkIndex != -1 && currentChunkIdx == readSeq.LastChunkIndex + 1)
                {
                    readSeq.SequentialCount++;
                    readSeq.AccumulatedSequentialBytes += length;
                }
                else if (readSeq.LastChunkIndex == currentChunkIdx)
                {
                    readSeq.AccumulatedSequentialBytes += length;
                }
                else
                {
                    readSeq.SequentialCount = 1;
                    readSeq.AccumulatedSequentialBytes = length;
                    if (readSeq.LastChunkIndex != -1)
                    {
                        _workerPool?.CancelPendingChunksForFile(messageId);
                    }
                }

                currentChunkReadBytes = readSeq.ChunkBytesRead.AddOrUpdate(currentChunkIdx, length, (_, old) => old + length);
                readSeq.LastChunkIndex = currentChunkIdx;
                readSeq.LastReadTime = now;
            }

            // ПРОВЕРКА ПОЛНОГО СКАЧИВАНИЯ 1-ГО ЧАНКА И СТРИМИНГА:
            // 1) Первый 1 МБ чанк (#0) уже скачан полностью в ОЗУ
            long expectedFirstChunkSize = Math.Min(1048576L, actualTotalSize);
            bool isFirstChunkFullyCached = TryGetFromMemoryCache(messageId, 0, out var firstChunkData, out _)
                                          && firstChunkData != null && firstChunkData.Length >= expectedFirstChunkSize;

            // 2) Запрос в хвост файла (последние 512 КБ для ID3v1/тетрисов метаданных)
            bool isTailProbe = actualTotalSize > 1048576 && offset >= Math.Max(0, actualTotalSize - 524288);

            // 3) Воспроизведение/стриминг:
            //    - Первый чанк (#0) уже скачан на 100% (для аудио/треков),
            //    - ИЛИ внутри текущего 1 МБ чанка клиент вычитал объём порога (по умолчанию 1 МБ),
            //    - ИЛИ в непрерывном потоке суммарно вычитано от порога и более (по умолчанию 1 МБ),
            //    - ИЛИ клиент сразу запросил большой блок данных (> 256 КБ).
            //    Одиночные точечные запросы (эскизы видео, теги ID3/moov) с объемом <= 256 КБ не вызывают стриминг.
            long activationThresholdBytes = Math.Max(1, _currentSettings.Server.StreamingActivationThresholdMb) * 1048576L;
            bool isShortRead = length <= 262144;
            bool isSequentialPlayback = !isTailProbe && (isFirstChunkFullyCached 
                                                        || currentChunkReadBytes >= activationThresholdBytes
                                                        || readSeq.AccumulatedSequentialBytes >= activationThresholdBytes 
                                                        || !isShortRead);

            bool isMetadataProbe = !isSequentialPlayback;

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
                        int diskWorkers = Math.Clamp(_currentSettings.Server.DownloadWorkerCount > 0 ? _currentSettings.Server.DownloadWorkerCount : 1, 1, 3);
                        AppLogger.Info("TelegramService", $"[Disk Cache Mode] Скачивание файла '{fileName}' (ID {messageId}, {actualTotalSize:N0} байт) через {diskWorkers} воркеров в дисковый кэш...");
                        if (_workerPool == null) InitWorkerPool();
                        bool success = _workerPool != null && await _workerPool.DownloadFileAsync(
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

            // 1. Проверяем, есть ли запрашиваемый чанк уже в ОЗУ (был ранее упреждающе выкачан воркером)
            if (!enableDiskCache && TryGetFromMemoryCache(messageId, offset, out var ramCachedRaw, out var ramCachedOffset) && ramCachedRaw != null)
            {
                int availableInChunk = ramCachedRaw.Length - ramCachedOffset;
                int bytesToSend = (int)Math.Min(availableInChunk, length);
                if (bytesToSend == length) // Возвращаем результат сразу, только если кэш закрыл ВСЮ запрошенную клиентом длину
                {
                    try
                    {
                        await destination.WriteAsync(ramCachedRaw, ramCachedOffset, bytesToSend);
                        await destination.FlushAsync();
                        audit.AddRamBytes(bytesToSend);
                        bool allReceived = audit.MarkRangeReceived(offset, bytesToSend);
                        AppLogger.Info("TelegramService", $"[Cache RAM] Точечное чтение из ОЗУ для '{fileName}' (ID {messageId}): Глобальный Чанк #{(offset / 1048576) + 1} (смещение {offset:N0}, {bytesToSend:N0} байт). {audit.ProgressSummary}.");

                        if (!isMetadataProbe && allReceived && audit.LogCompletionOnce())
                        {
                            OnDownloadCompleted?.Invoke(fileName);
                        }

                        // Запускаем непрерывный фоновый конвейер скачивания оставшихся чанков файла только при реальном воспроизведении/скачивании
                        if (!isMetadataProbe)
                        {
                            TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, offset, audit);
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"Клиент прервал соединение при чтении из RAM кэша: {ex.Message}");
                        return;
                    }
                }
            }

            long currentPos = offset;
            long remainingBytes = length;
            long totalSent = 0;

            if (!enableDiskCache && !isSmallFile && !isMetadataProbe && remainingBytes > 262144)
            {
                // Запускаем фоновый конвейер воркеров MTProto ДО входа в цикл чтения,
                // чтобы все чанки очереди (#1..#N) были СИНХРОННО зарегистрированы в _inFlightChunkWaiters
                TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
            }

            while (remainingBytes > 0)
            {
                await EnsureFloodWaitDelayAsync();

                // 1. Проверяем, есть ли уже нужные байты в быстром кэше оперативной памяти
                if (TryGetFromMemoryCache(messageId, currentPos, out var cachedRaw, out var cachedOffset) && cachedRaw != null)
                {
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
                    audit.AddRamBytes(toSend);

                    bool allReceived = audit.MarkRangeReceived(currentPos - toSend, toSend);

                    AppLogger.Info("TelegramService", $"[Cache RAM] Потоковое чтение из ОЗУ для '{fileName}' (ID {messageId}): смещение {currentPos - toSend:N0}, отдано {toSend:N0} байт ({currentPos:N0} / {actualTotalSize:N0} байт, {(double)currentPos * 100 / Math.Max(1, actualTotalSize):F1}%). {audit.ProgressSummary}.");

                    if (allReceived && audit.LogCompletionOnce())
                    {
                        OnDownloadCompleted?.Invoke(fileName);
                    }

                    // Если мы в режиме стриминга и префетч еще не запущен, запускаем префетч
                    if (!enableDiskCache && !isSmallFile && !isMetadataProbe && remainingBytes > 0)
                    {
                        TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
                    }

                    continue;
                }

                // 2. Адаптивный выбор размера чанка MTProto и загрузка строго через единый пул воркеров:
                // Метаданные, превью видео и точечные сэмплы (isMetadataProbe) ВСЕГДА качаем квантом 256 КБ (262 144 байт)
                // со строгим выравниванием chunkOffset по сетке 256 КБ и БЕЗ упреждающего префетча.
                // При реальном скачивании или воспроизведении качаем полным 1 МБ (1 048 576 байт) с высоким приоритетом.
                bool use256kQuantum = isSmallFile || isMetadataProbe;
                int quantum = use256kQuantum ? 262144 : 1048576;

                long chunkAlignment = quantum;
                long chunkOffset = (currentPos / chunkAlignment) * chunkAlignment;
                int internalOffset = (int)(currentPos - chunkOffset);
                int requestLimit = quantum;

                if (_workerPool == null) InitWorkerPool();
                if (_workerPool != null)
                {
                    var chunkTask = _workerPool.EnqueueChunk(
                        messageId,
                        document,
                        fileName,
                        actualTotalSize,
                        chunkOffset,
                        requestLimit: requestLimit,
                        isHighPriority: true,
                        onChunkReceived: (chunkBytes, offset) =>
                        {
                            audit.AddNetworkBytes(chunkBytes.Length);
                            bool allReceived = audit.MarkRangeReceived(offset, chunkBytes.Length);
                            StoreChunkInMemoryCache(messageId, offset, chunkBytes, cacheTtlMinutes);
                            OnChunkCached?.Invoke(fileName, offset + chunkBytes.Length, actualTotalSize);
                            if (allReceived && audit.LogCompletionOnce())
                            {
                                OnDownloadCompleted?.Invoke(fileName);
                            }
                        },
                        onProgress: (transferred, total) =>
                        {
                            if (!isMetadataProbe && !audit.IsAllChunksReceived())
                            {
                                OnDownloadProgress?.Invoke(fileName, chunkOffset + transferred, actualTotalSize);
                            }
                        });

                    // Запускаем упреждающий префетч следующих чанков ТОЛЬКО для стриминга (НЕ для зондов метаданных!)
                    if (!enableDiskCache && !isSmallFile && !isMetadataProbe && remainingBytes > 0)
                    {
                        TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
                    }

                    var raw = await chunkTask;
                    if (raw != null && raw.Length > 0)
                    {
                        if (internalOffset >= raw.Length)
                        {
                            AppLogger.Warn("TelegramService", $"[MTProto] Предупреждение: internalOffset ({internalOffset}) >= raw.Length ({raw.Length}) для '{fileName}' (ID {messageId}, chunkOffset {chunkOffset}). Завершение чтения диапазона.");
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
                            AppLogger.Debug("TelegramService", $"Клиент прервал соединение для '{fileName}' (ID {messageId}): {ex.Message}");
                            return;
                        }

                        currentPos += toSend;
                        remainingBytes -= toSend;
                        totalSent += toSend;

                        if (!isMetadataProbe && !audit.IsAllChunksReceived())
                        {
                            OnDownloadProgress?.Invoke(fileName, currentPos, actualTotalSize);
                        }
                        else
                        {
                            OnMetadataProgress?.Invoke(fileName, audit.NetworkBytes, actualTotalSize);
                            OnChunkCached?.Invoke(fileName, audit.NetworkBytes, actualTotalSize);
                        }

                        // Если Telegram вернул меньше данных, чем requestLimit — достигнут конец файла
                        if (raw.Length < requestLimit && remainingBytes > 0)
                        {
                            break;
                        }

                        if (!enableDiskCache && !isSmallFile && !isMetadataProbe && remainingBytes > 0)
                        {
                            TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
                        }

                        continue;
                    }
                    else
                    {
                        AppLogger.Warn("TelegramService", $"Не удалось получить чанк смещением {chunkOffset} для '{fileName}' (ID {messageId}) из пула воркеров.");
                        break;
                    }
                }
            }

            if (audit.IsAllChunksReceived())
            {
                if (audit.LogCompletionOnce())
                {
                    OnDownloadCompleted?.Invoke(fileName);
                }
            }
            else if (isMetadataProbe)
            {
                if (audit.LogMetadataCompletedOnce())
                {
                    OnMetadataCompleted?.Invoke(fileName);
                }
            }
            else if (!enableDiskCache && !isSmallFile)
            {
                // Запускаем непрерывный фоновый конвейер воркеров MTProto строго ПОСЛЕ того, как текущий чанк скачан и готов в RAM
                TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
            }
        }

        /// <summary>
        /// Постановка упреждающих чанков в единый глобальный пул воркеров MTProto.
        /// Не создает новых пулов и исключает дублирование сетевых запросов.
        /// </summary>
        private void TriggerContinuousPrefetch(int messageId, TL.Document document, string fileName, long actualTotalSize, long currentReadOffset, NetworkTransferAudit audit)
        {
            bool enableDiskCache = _currentSettings.Server.EnableDiskReadCache;
            if (enableDiskCache || document == null || actualTotalSize <= 262144 || _client == null) return;

            if (_workerPool == null)
            {
                InitWorkerPool();
                if (_workerPool == null) return;
            }

            int cacheTtlMinutes = _currentSettings.Server.ChunkMemoryCacheTtlMinutes;
            bool isAudio = IsAudioFileName(fileName);
            int windowMb = isAudio
                ? _currentSettings.Server.AudioPrefetchWindowMb
                : _currentSettings.Server.StreamingPrefetchWindowMb;

            long windowBytes = (long)windowMb * 1024 * 1024;
            long maxPrefetchLimit = Math.Min(actualTotalSize, currentReadOffset + windowBytes);

            long startChunkOffset = (currentReadOffset / 1048576) * 1048576;
            long scanOffset = startChunkOffset;

            var newChunksToQueue = new List<long>();
            long totalBytesToQueue = 0;

            while (scanOffset < maxPrefetchLimit)
            {
                int expectedSize = (int)Math.Min(1048576L, actualTotalSize - scanOffset);
                bool inRam = TryGetFromMemoryCache(messageId, scanOffset, out var cachedData, out _) && (cachedData != null && cachedData.Length >= expectedSize);
                bool inFlight = _workerPool.IsChunkInFlight(messageId, scanOffset);

                if (!inRam && !inFlight)
                {
                    newChunksToQueue.Add(scanOffset);
                    totalBytesToQueue += expectedSize;
                }
                scanOffset += 1048576;
            }

            if (newChunksToQueue.Count > 0)
            {
                int totalChunks = (int)Math.Ceiling((double)actualTotalSize / 1048576.0);
                int firstChunk = (int)(newChunksToQueue[0] / 1048576) + 1;
                int lastChunk = (int)(newChunksToQueue[^1] / 1048576) + 1;
                double mb = (double)totalBytesToQueue / (1024.0 * 1024.0);
                int activeWorkers = _workerPool.ActiveWorkerCount;

                string rangeStr = firstChunk == lastChunk
                    ? $"чанк #{firstChunk}"
                    : $"чанки #{firstChunk}..#{lastChunk}";

                AppLogger.Info("MtprotoWorkerPool", $"[RAM Streaming] Скачивание {mb:0.00} МБ ({rangeStr} из {totalChunks}) для '{fileName}' через {activeWorkers} воркеров MTProto...");

                foreach (var chunkOffsetToQueue in newChunksToQueue)
                {
                    _workerPool.EnqueueChunk(
                        messageId,
                        document,
                        fileName,
                        actualTotalSize,
                        chunkOffsetToQueue,
                        requestLimit: 1048576,
                        isHighPriority: false,
                        onChunkReceived: (chunkBytes, chunkOffset) =>
                        {
                            audit.AddNetworkBytes(chunkBytes.Length);
                            bool allReceived = audit.MarkRangeReceived(chunkOffset, chunkBytes.Length);
                            StoreChunkInMemoryCache(messageId, chunkOffset, chunkBytes, cacheTtlMinutes);

                            OnChunkCached?.Invoke(fileName, chunkOffset + chunkBytes.Length, actualTotalSize);

                            if (allReceived && audit.LogCompletionOnce())
                            {
                                OnDownloadCompleted?.Invoke(fileName);
                            }
                        },
                        onProgress: (transferred, total) =>
                        {
                            if (!audit.IsAllChunksReceived())
                            {
                                OnDownloadProgress?.Invoke(fileName, chunkOffsetToQueue + transferred, actualTotalSize);
                            }
                        });
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
                _deletionQueueCts?.Cancel();
                _deletionQueueCts?.Dispose();
                _deletionQueueCts = null;
            }
            catch { }
            try
            {
                _workerPool?.Dispose();
                _workerPool = null;
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
