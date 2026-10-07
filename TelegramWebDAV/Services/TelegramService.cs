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
            long incomingStart = chunkOffset;

            // Собираем все фрагменты этого же 1 МБ блока в ОЗУ для интервального слияния
            var fragmentsToMerge = new System.Collections.Generic.List<(string key, long start, byte[] data)>
            {
                (string.Empty, incomingStart, raw)
            };

            foreach (var kvp in _chunkMemoryCache)
            {
                var parts = kvp.Key.Split(':');
                if (parts.Length == 3 && int.TryParse(parts[0], out var mId) && mId == messageId && long.TryParse(parts[1], out var start))
                {
                    if (start >= megaStart && start < megaStart + 1048576 && kvp.Value.data != null)
                    {
                        fragmentsToMerge.Add((kvp.Key, start, kvp.Value.data));
                    }
                }
            }

            // Сортируем фрагменты по возрастанию начального смещения
            fragmentsToMerge.Sort((a, b) => a.start.CompareTo(b.start));

            // Проверяем непрерывность цепочки фрагментов
            long mergedStart = fragmentsToMerge[0].start;
            long mergedEnd = fragmentsToMerge[0].start + fragmentsToMerge[0].data.Length;
            bool canMergeAll = true;

            for (int i = 1; i < fragmentsToMerge.Count; i++)
            {
                if (fragmentsToMerge[i].start <= mergedEnd)
                {
                    mergedEnd = Math.Max(mergedEnd, fragmentsToMerge[i].start + fragmentsToMerge[i].data.Length);
                }
                else
                {
                    canMergeAll = false;
                    break;
                }
            }

            if (canMergeAll && fragmentsToMerge.Count > 1)
            {
                int mergedLength = (int)(mergedEnd - mergedStart);
                byte[] mergedBuffer = new byte[mergedLength];

                foreach (var frag in fragmentsToMerge)
                {
                    int destOffset = (int)(frag.start - mergedStart);
                    Buffer.BlockCopy(frag.data, 0, mergedBuffer, destOffset, frag.data.Length);
                    if (!string.IsNullOrEmpty(frag.key))
                    {
                        _chunkMemoryCache.TryRemove(frag.key, out _);
                    }
                }

                string mergedKey = $"{messageId}:{mergedStart}:{mergedLength}";
                _chunkMemoryCache[mergedKey] = (mergedBuffer, DateTime.UtcNow.AddMinutes(cacheTtlMinutes));
                return;
            }

            // Если пришел полный 1 МБ блок (>= 1048576 байт), вычищаем любые старые частичные фрагменты этого же блока
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
            StartChannelMigrationQueueWorker();
        }

        private System.Threading.CancellationTokenSource? _deletionQueueCts;
        private readonly SemaphoreSlim _deletionSignal = new SemaphoreSlim(0, int.MaxValue);

        private System.Threading.CancellationTokenSource? _migrationQueueCts;
        private readonly SemaphoreSlim _migrationSignal = new SemaphoreSlim(0, int.MaxValue);

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

        /// <summary>
        /// Сигнализирует фоновому воркеру о новых элементах в очереди миграции каналов
        /// </summary>
        public void TriggerChannelMigrationProcessing()
        {
            try
            {
                if (_migrationSignal.CurrentCount == 0)
                {
                    _migrationSignal.Release();
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

        private void StartChannelMigrationQueueWorker()
        {
            if (_migrationQueueCts != null) return;
            _migrationQueueCts = new System.Threading.CancellationTokenSource();
            Task.Run(() => ProcessChannelMigrationsAsync(_migrationQueueCts.Token));
            TriggerChannelMigrationProcessing();
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

        private async Task ProcessChannelMigrationsAsync(System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _migrationSignal.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (!token.IsCancellationRequested)
                {
                    if (!IsAuthorized || _client == null || _repository == null)
                    {
                        break;
                    }

                    List<ChannelMigrationItem> batch;
                    try
                    {
                        batch = _repository.GetPendingChannelMigrations(limit: 10);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("TelegramService", $"[MigrationQueue] Ошибка чтения очереди миграции: {ex.Message}");
                        await Task.Delay(3000, token);
                        break;
                    }

                    if (batch.Count == 0)
                    {
                        break;
                    }

                    AppLogger.Info("TelegramService", $"[MigrationQueue] Фоновая обработка пачки из {batch.Count} файлов миграции каналов...");

                    foreach (var item in batch)
                    {
                        if (token.IsCancellationRequested) break;

                        await EnsureFloodWaitDelayAsync();
                        await EnsurePacingDelayAsync(token);

                        var file = _repository.GetNodeById(item.NodeId);
                        if (file == null || file.InTrash || !file.TgMessageId.HasValue || file.TgMessageId.Value <= 0)
                        {
                            _repository.DeleteChannelMigration(item.Id);
                            continue;
                        }

                        var targetPeer = await GetStoragePeerAsync(item.TargetChannelId);
                        var sourcePeer = await GetStoragePeerAsync(item.SourceChannelId);

                        if (targetPeer == null || sourcePeer == null)
                        {
                            AppLogger.Warn("TelegramService", $"[MigrationQueue] Канал назначения ID {item.TargetChannelId} или источника ID {item.SourceChannelId} недоступен. Пропуск элемента #{item.Id}.");
                            _repository.DeleteChannelMigration(item.Id);
                            continue;
                        }

                        try
                        {
                            int oldMessageId = file.TgMessageId.Value;
                            int? oldPreviewId = file.TgPreviewMessageId;

                            int newMsgId = 0;
                            int? newPreviewId = null;

                            // 1. Сначала пересылаем превью для галереи (если есть), чтобы оно появилось в ленте первым
                            if (oldPreviewId.HasValue && oldPreviewId.Value > 0)
                            {
                                var fwdPreviewReq = new TL.Methods.Messages_ForwardMessages
                                {
                                    from_peer = sourcePeer,
                                    to_peer = targetPeer,
                                    id = new int[] { oldPreviewId.Value },
                                    random_id = new long[] { Random.Shared.NextInt64() },
                                    flags = TL.Methods.Messages_ForwardMessages.Flags.drop_author
                                };

                                var previewUpdates = await _client.Invoke(fwdPreviewReq);
                                if (previewUpdates is TL.Updates pUpdates)
                                {
                                    foreach (var update in pUpdates.updates)
                                    {
                                        if (update is TL.UpdateNewMessage unm && unm.message is TL.Message m)
                                        {
                                            newPreviewId = m.ID;
                                            break;
                                        }
                                        else if (update is TL.UpdateNewChannelMessage uncm && uncm.message is TL.Message cm)
                                        {
                                            newPreviewId = cm.ID;
                                            break;
                                        }
                                    }
                                }
                                await EnsurePacingDelayAsync(token);
                            }

                            // 2. Затем пересылаем основной документ (файл) в целевой канал (drop_author = true)
                            var fwdReq = new TL.Methods.Messages_ForwardMessages
                            {
                                from_peer = sourcePeer,
                                to_peer = targetPeer,
                                id = new int[] { oldMessageId },
                                random_id = new long[] { Random.Shared.NextInt64() },
                                flags = TL.Methods.Messages_ForwardMessages.Flags.drop_author
                            };

                            var fwdUpdates = await _client.Invoke(fwdReq);
                            if (fwdUpdates is TL.Updates updates)
                            {
                                foreach (var update in updates.updates)
                                {
                                    if (update is TL.UpdateNewMessage unm && unm.message is TL.Message m)
                                    {
                                        newMsgId = m.ID;
                                        break;
                                    }
                                    else if (update is TL.UpdateNewChannelMessage uncm && uncm.message is TL.Message cm)
                                    {
                                        newMsgId = cm.ID;
                                        break;
                                    }
                                }
                            }

                            if (newMsgId > 0)
                            {
                                // 3. Помещаем старые сообщения в очередь удаления из исходного канала
                                var oldIdsToDelete = new List<int> { oldMessageId };
                                if (oldPreviewId.HasValue && oldPreviewId.Value > 0)
                                {
                                    oldIdsToDelete.Add(oldPreviewId.Value);
                                }
                                _repository.EnqueuePermanentDeletion(oldIdsToDelete, new List<int>());

                                // 4. Обновляем данные файла в БД
                                _repository.UpdateNodeTelegramData(file.Id, newMsgId, newPreviewId, item.TargetChannelId);

                                // 5. Обновляем текстовые подписи
                                string fullPathWithVersion = _repository.GetNodeFullPathWithVersion(file.Id);
                                string newCaption = NodeRepository.FormatTelegramCaption(fullPathWithVersion, newMsgId, isLatest: !file.InTrash);
                                _repository.EnqueueCaptionUpdate(file.Id, newMsgId, newCaption);

                                _repository.DeleteChannelMigration(item.Id);
                                AppLogger.Info("TelegramService", $"[MigrationQueue] Файл '{file.Name}' успешно перенесен: старый msg #{oldMessageId} -> новый msg #{newMsgId} в канале ID {item.TargetChannelId}.");
                            }
                        }
                        catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                        {
                            AppLogger.Warn("TelegramService", $"[MigrationQueue] FloodWait при пересылке файла '{file.Name}': пауза {rpcEx.X} сек...");
                            TriggerGlobalFloodWait(rpcEx.X);
                            await Task.Delay(Math.Max(5000, rpcEx.X * 1000), token);
                            break;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("TelegramService", $"[MigrationQueue] Ошибка пересылки файла '{file.Name}': {ex.Message}", ex);
                            _repository.DeleteChannelMigration(item.Id);
                        }
                    }

                    TriggerDeletionQueueProcessing();
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
                PendingCaptionItem? currentItem = null;
                try
                {
                    if (IsAuthorized && _client != null && _repository != null)
                    {
                        currentItem = _repository.GetNextPendingCaptionUpdate();
                        if (currentItem != null)
                        {
                            await EnsureFloodWaitDelayAsync();
                            await EnsurePacingDelayAsync(token);
                            await UpdateMessageCaptionAsync(currentItem.TgMessageId, currentItem.NewCaption);
                            _repository.RemovePendingCaptionUpdate(currentItem.Id);
                            currentItem = null;
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
                catch (TL.RpcException rpcEx) when (rpcEx.Code == 400)
                {
                    AppLogger.Warn("TelegramService", $"Неустранимая ошибка Telegram при обновлении подписи: {rpcEx.Message}. Удаление элемента из очереди.");
                    if (currentItem != null && _repository != null)
                    {
                        _repository.RemovePendingCaptionUpdate(currentItem.Id);
                    }
                    await Task.Delay(500, token);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TelegramService", $"Ошибка обработки фоновой очереди подписей: {ex.Message}");
                    await Task.Delay(2000, token);
                }

                await Task.Delay(1000, token);
            }
        }

        private TL.InputPeer? _storagePeer;
        private readonly SemaphoreSlim _storageLock = new SemaphoreSlim(1, 1);

        public void UpdateApiCredentials(int apiId, string apiHash, string? channelTitle = null)
        {
            if (_repository != null)
            {
                var account = _repository.SaveTelegramAccount(apiId, apiHash);
                if (!string.IsNullOrWhiteSpace(channelTitle))
                {
                    var primary = _repository.GetPrimaryTelegramChannel(account.Id);
                    if (primary != null)
                    {
                        _repository.UpdateChannelTitle(primary.ChannelId, channelTitle.Trim());
                    }
                    else
                    {
                        _repository.SaveOrUpdateTelegramChannel(account.Id, 0, 0, channelTitle.Trim(), isPrimary: true);
                    }
                }
            }

            _storagePeer = null;
            try { _client?.Dispose(); } catch { }
            _client = null;
            _ = ConnectAsync();
        }

        public void UpdateStorageChannelTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || _repository == null) return;
            var account = _repository.GetActiveTelegramAccount();
            if (account == null) return;

            var primary = _repository.GetPrimaryTelegramChannel(account.Id);
            if (primary != null && primary.Title != title.Trim())
            {
                _repository.UpdateChannelTitle(primary.ChannelId, title.Trim());
                _storagePeer = null;
            }
        }

        /// <summary>
        /// Сбрасывает закэшированный канал-хранилище (например, при удалении канала пользователем).
        /// </summary>
        public void InvalidateStoragePeer()
        {
            _storagePeer = null;
        }

        /// <summary>
        /// Получает или создает приватный канал-хранилище в Telegram для WebDAV файлов.
        /// Поддерживает явный выбор канала по specificChannelId.
        /// </summary>
        public async Task<TL.InputPeer> GetStoragePeerAsync(long? specificChannelId = null)
        {
            // Если передан конкретный ID канала (например, папка привязана к отдельной группе):
            if (specificChannelId.HasValue && specificChannelId.Value != 0)
            {
                var channelInfo = _repository?.GetTelegramChannel(specificChannelId.Value);
                if (channelInfo != null && channelInfo.AccessHash != 0)
                {
                    return new TL.InputPeerChannel(channelInfo.ChannelId, channelInfo.AccessHash);
                }

                if (_client != null && IsAuthorized)
                {
                    var chats = await _client.Messages_GetAllChats();
                    if (chats.chats.TryGetValue(specificChannelId.Value, out var ch) && ch is TL.Channel channel)
                    {
                        var account = _repository?.GetActiveTelegramAccount();
                        if (account != null && _repository != null)
                        {
                            _repository.SaveOrUpdateTelegramChannel(account.Id, channel.ID, channel.access_hash, channel.Title, isPrimary: false);
                        }
                        return channel.ToInputPeer();
                    }
                }
            }

            if (_storagePeer != null)
                return _storagePeer;

            await _storageLock.WaitAsync();
            try
            {
                if (_storagePeer != null)
                    return _storagePeer;

                if (_client == null || !IsAuthorized)
                    throw new InvalidOperationException("Клиент Telegram не авторизован.");

                var account = _repository?.GetActiveTelegramAccount();
                if (account == null)
                    throw new InvalidOperationException("Аккаунт Telegram не найден в базе данных.");

                var primaryChannel = _repository?.GetPrimaryTelegramChannel(account.Id);
                string targetTitle = primaryChannel != null && !string.IsNullOrWhiteSpace(primaryChannel.Title)
                    ? primaryChannel.Title.Trim()
                    : "Telegram WebDAV Drive";

                // 1. Если StorageChannelId и StorageChannelAccessHash уже сохранены в базе SQLite — мгновенно используем их без сетевых вызовов!
                if (primaryChannel != null && primaryChannel.ChannelId != 0 && primaryChannel.AccessHash != 0)
                {
                    _storagePeer = new TL.InputPeerChannel(primaryChannel.ChannelId, primaryChannel.AccessHash);
                    AppLogger.Info("TelegramService", $"Подключен канал-хранилище из базы SQLite: '{primaryChannel.Title}' (ID: {primaryChannel.ChannelId}).");
                    return _storagePeer;
                }

                // 2. Если StorageChannelId есть, но хэш еще не сохранен — находим канал в диалогах
                if (primaryChannel != null && primaryChannel.ChannelId != 0)
                {
                    var chats = await _client.Messages_GetAllChats();
                    if (chats.chats.TryGetValue(primaryChannel.ChannelId, out var savedChat) &&
                        savedChat is TL.Channel sc)
                    {
                        _storagePeer = sc.ToInputPeer();
                        _repository?.SaveOrUpdateTelegramChannel(account.Id, sc.ID, sc.access_hash, sc.Title, isPrimary: true);
                        AppLogger.Info("TelegramService", $"Подключен существующий приватный канал-хранилище: {sc.Title} (ID: {sc.ID}, AccessHash сохранен в SQLite).");
                        return _storagePeer;
                    }
                    else
                    {
                        AppLogger.Warn("TelegramService", $"Канал с сохраненным ID {primaryChannel.ChannelId} не найден в диалогах пользователя. Создаем новый.");
                    }
                }

                // 3. Если ID канала нет в БД, создаем новый приватный канал
                AppLogger.Info("TelegramService", $"Основной канал-хранилище не найден. Создание нового приватного канала '{targetTitle}'...");
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
                            _repository?.SaveOrUpdateTelegramChannel(account.Id, newCh.ID, newCh.access_hash, newCh.Title, isPrimary: true);
                            AppLogger.Info("TelegramService", $"Создан новый приватный канал '{newCh.Title}' (ID: {newCh.ID}). Записан в SQLite.");
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
        /// Создает новый приватный канал Telegram с названием папки и сохраняет его в SQLite.
        /// </summary>
        public async Task<TelegramChannel?> CreateCustomChannelAsync(string title)
        {
            await EnsureFloodWaitDelayAsync();
            if (_client == null || !IsAuthorized)
                throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

            var account = _repository?.GetActiveTelegramAccount();
            if (account == null)
                throw new InvalidOperationException("Аккаунт Telegram не найден в базе данных.");

            string cleanTitle = title.Trim();
            AppLogger.Info("TelegramService", $"Создание нового приватного канала '{cleanTitle}'...");

            var createReq = new TL.Methods.Channels_CreateChannel
            {
                flags = TL.Methods.Channels_CreateChannel.Flags.broadcast,
                title = cleanTitle,
                about = "Приватный канал хранилища папки " + cleanTitle
            };

            var createdUpdates = await _client.Invoke(createReq);
            if (createdUpdates is TL.Updates updates)
            {
                foreach (var chat in updates.chats.Values)
                {
                    if (chat is TL.Channel newCh)
                    {
                        _repository?.SaveOrUpdateTelegramChannel(account.Id, newCh.ID, newCh.access_hash, newCh.Title, isPrimary: false);
                        AppLogger.Info("TelegramService", $"Создан новый кастомный канал '{newCh.Title}' (ID: {newCh.ID}). Записан в SQLite.");
                        return _repository?.GetTelegramChannel(newCh.ID);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Обновляет название канала в Telegram при переименовании привязанной папки.
        /// </summary>
        public async Task EditChannelTitleAsync(long channelId, string newTitle)
        {
            await EnsureFloodWaitDelayAsync();
            if (_client == null || !IsAuthorized || channelId == 0) return;

            string cleanTitle = newTitle.Trim();
            try
            {
                var peer = await GetStoragePeerAsync(channelId);
                if (peer is TL.InputPeerChannel pc)
                {
                    var req = new TL.Methods.Channels_EditTitle
                    {
                        channel = new TL.InputChannel(pc.channel_id, pc.access_hash),
                        title = cleanTitle
                    };
                    await _client.Invoke(req);
                    _repository?.UpdateChannelTitle(channelId, cleanTitle);
                    AppLogger.Info("TelegramService", $"Название канала Telegram (ID: {channelId}) сменено на '{cleanTitle}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TelegramService", $"Не удалось изменить название канала ID {channelId} в Telegram: {ex.Message}");
            }
        }

        /// <summary>
        /// Перманентно удаляет канал из Telegram при полном уничтожении привязанной папки.
        /// </summary>
        public async Task DeleteChannelAsync(long channelId)
        {
            await EnsureFloodWaitDelayAsync();
            if (_client == null || !IsAuthorized || channelId == 0) return;

            try
            {
                var peer = await GetStoragePeerAsync(channelId);
                if (peer is TL.InputPeerChannel pc)
                {
                    var req = new TL.Methods.Channels_DeleteChannel
                    {
                        channel = new TL.InputChannel(pc.channel_id, pc.access_hash)
                    };
                    await _client.Invoke(req);
                    _repository?.DeleteTelegramChannel(channelId);
                    AppLogger.Info("TelegramService", $"Канал Telegram ID {channelId} полностью удален.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TelegramService", $"Не удалось удалить канал ID {channelId} из Telegram: {ex.Message}");
            }
        }

        /// <summary>
        /// Мигрирует всё содержимое папки (и её подпапок) в новый канал Telegram через механизмы ForwardMessages и очистки старого канала.
        /// </summary>
        public Task MigrateSubtreeToChannelAsync(int folderNodeId, long targetChannelId, Action<string, int, int>? onProgress = null)
        {
            if (_repository == null) return Task.CompletedTask;

            // Назначаем tg_channel_id для самой привязываемой папки и всех ее дочерних папок
            _repository.SetFolderChannelId(folderNodeId, targetChannelId);
            var subtree = _repository.GetSubtreeNodes(folderNodeId);
            foreach (var dir in subtree.Where(n => n.IsDir))
            {
                _repository.SetFolderChannelId(dir.Id, targetChannelId);
            }

            var filesToMigrate = subtree
                .Where(n => !n.IsDir && n.TgMessageId.HasValue && n.TgMessageId.Value > 0)
                .OrderBy(n => n.TgMessageId!.Value)
                .ToList();
            if (filesToMigrate.Count == 0)
            {
                AppLogger.Info("TelegramService", $"[Migration] В папке ID {folderNodeId} нет файлов для миграции.");
                return Task.CompletedTask;
            }

            AppLogger.Info("TelegramService", $"[Migration] Добавление {filesToMigrate.Count} файлов папки ID {folderNodeId} в очередь фоновой миграции в канал ID {targetChannelId}...");

            var nodeIdsBySource = filesToMigrate
                .GroupBy(f => f.TgChannelId ?? _repository.GetEffectiveChannelId(f.ParentId) ?? 0)
                .Where(g => g.Key != targetChannelId);

            foreach (var group in nodeIdsBySource)
            {
                _repository.EnqueueChannelMigrations(group.Select(f => f.Id), group.Key, targetChannelId);
            }

            TriggerChannelMigrationProcessing();
            AppLogger.Info("TelegramService", $"[Migration] Папка ID {folderNodeId} успешно привязана к каналу ID {targetChannelId}. Поставлено в очередь фоновой миграции: {filesToMigrate.Count} файлов.");
            return Task.CompletedTask;
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
            
            var account = _repository?.GetActiveTelegramAccount();
            if (account == null || account.ApiId == 0 || string.IsNullOrWhiteSpace(account.ApiHash))
            {
                LastError = "API ID или API Hash не заданы в базе данных.";
                IsAuthorized = false;
                CurrentStep = AuthStep.NeedsPhone;
                return;
            }

            string sessionPath = account.SessionPath;
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

            var account = _repository?.GetActiveTelegramAccount();
            int apiId = account?.ApiId ?? 0;
            string apiHash = account?.ApiHash ?? string.Empty;
            string sessionPath = account?.SessionPath ?? "user.session";
            string? phoneNumber = account?.PhoneNumber;

            _client = new WTelegram.Client(what =>
            {
                switch (what)
                {
                    case "api_id": return apiId > 0 ? apiId.ToString() : null;
                    case "api_hash": return !string.IsNullOrEmpty(apiHash) ? apiHash : null;
                    case "session_pathname": return sessionPath;
                    case "phone_number": return phoneNumber;
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

                    if (!string.IsNullOrEmpty(u.phone) && _repository != null)
                    {
                        string phoneFormatted = u.phone.StartsWith("+") ? u.phone : "+" + u.phone;
                        var account = _repository.GetActiveTelegramAccount();
                        if (account != null && account.PhoneNumber != phoneFormatted)
                        {
                            _repository.UpdateAccountPhone(account.Id, phoneFormatted);
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

            var account = _repository?.GetActiveTelegramAccount();
            if (account == null || account.ApiId == 0 || string.IsNullOrWhiteSpace(account.ApiHash))
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
                _repository?.UpdateAccountPhone(account.Id, phoneFormatted);

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

            var account = _repository?.GetActiveTelegramAccount();
            string sessionPath = account?.SessionPath ?? "user.session";

            if (File.Exists(sessionPath))
            {
                try
                {
                    File.Delete(sessionPath);
                }
                catch { }
            }
            if (account != null && _repository != null)
            {
                _repository.ClearAccountSession(account.Id);
            }

            IsAuthorized = false;
            CurrentStep = AuthStep.NeedsPhone;
            CurrentUser = null;
            _storagePeer = null;
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

        // Глобальный сквозной семафор и планировщик слотов времени отправки (Token-Bucket Pacing)
        // Гарантирует строгое квантование интервалов запуска исходящих MTProto-пакетов Upload_GetFile
        private static readonly SemaphoreSlim _globalPacingLock = new SemaphoreSlim(1, 1);
        private static DateTime _nextAllowedSendUtc = DateTime.MinValue;
        private static int _globalPacingDelayMs = ServerSettings.DefaultPacingDelayMs;

        /// <summary>
        /// Резервирует уникальный временной слот для отправки исходящего пакета Upload_GetFile.
        /// Исключает одновременную отправку пакетов разными воркерами в одну миллисекунду.
        /// </summary>
        public static async Task EnsurePacingDelayAsync(CancellationToken cancellationToken = default)
        {
            DateTime scheduledTime;
            await _globalPacingLock.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                if (_nextAllowedSendUtc < now)
                {
                    _nextAllowedSendUtc = now;
                }
                scheduledTime = _nextAllowedSendUtc;
                _nextAllowedSendUtc = _nextAllowedSendUtc.AddMilliseconds(_globalPacingDelayMs);
            }
            finally
            {
                _globalPacingLock.Release();
            }

            var waitMs = (scheduledTime - DateTime.UtcNow).TotalMilliseconds;
            if (waitMs > 0)
            {
                await Task.Delay((int)waitMs, cancellationToken);
            }
        }

        /// <summary>
        /// Фиксирует завершение сетевого вызова к серверам Telegram.
        /// </summary>
        public static void NotifyRequestCompleted()
        {
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
            _globalPacingLock.Wait();
            try
            {
                if (targetTime > _nextAllowedSendUtc)
                {
                    _nextAllowedSendUtc = targetTime;
                }
            }
            finally
            {
                _globalPacingLock.Release();
            }
            AdaptPacingDelay(20);
            AppLogger.Warn("TelegramService", $"Получен FLOOD_WAIT на {seconds} сек от серверов Telegram. Сквозная пауза до {_globalFloodWaitUntil:HH:mm:ss}. Пейсинг адаптирован до {_globalPacingDelayMs} мс.");
        }

        /// <summary>
        /// Надежная загрузка чанка с поддержкой докачки и отправкой собранного файла в канал Telegram по завершении.
        /// </summary>
        public async Task<FileUploadResult?> UploadFileChunkAsync(Stream source, string fileName, long offset, long totalSize, string? caption = null, long? targetChannelId = null)
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
                            videoMeta: chunkVideoMeta,
                            targetChannelId: targetChannelId
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
            VideoMetadataResult? videoMeta = null,
            string? originalFilePath = null,
            long? targetChannelId = null)
        {
            Interlocked.Increment(ref _pendingUploadsCount);
            await _uploadSemaphore.WaitAsync();
            string effectiveFileName = !string.IsNullOrEmpty(displayFileName) ? displayFileName : fileName;
            string rawCaptionPath = !string.IsNullOrEmpty(caption) ? caption : effectiveFileName;
            string effectiveCaption = rawCaptionPath.StartsWith("🟢") || rawCaptionPath.StartsWith("🗑️")
                ? rawCaptionPath
                : NodeRepository.FormatTelegramCaption(rawCaptionPath, null, isLatest: true);

            Stream uploadStream = source;
            string? tempFilePath = null;

            try
            {
                await EnsureFloodWaitDelayAsync();

                if (_client == null || !IsAuthorized)
                    throw new InvalidOperationException("Клиент Telegram не подключен или не авторизован.");

                var peer = await GetStoragePeerAsync(targetChannelId);
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
                if (isGallery && fileLength > 1)
                {
                    if (!string.IsNullOrEmpty(originalFilePath) && File.Exists(originalFilePath))
                    {
                        galleryPhotoBytes = CreateOptimizedGalleryThumbnailFromFile(originalFilePath);
                    }
                    else if (uploadStream is not StreamingPipeStream && uploadStream is not StreamingUploadStream && uploadStream.CanSeek)
                    {
                        galleryPhotoBytes = CreateOptimizedGalleryThumbnail(uploadStream);
                        uploadStream.Seek(0, SeekOrigin.Begin);
                    }
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
                        peer = await GetStoragePeerAsync(targetChannelId);
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
                        peer = await GetStoragePeerAsync(targetChannelId);
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
                            peer = await GetStoragePeerAsync(targetChannelId);
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
                        peer = await GetStoragePeerAsync(targetChannelId);
                        message = await _client.SendMessageAsync(peer, docCaption, mediaDoc, reply_to_msg_id: replyToId);
                    }

                    if (message != null)
                    {
                        // Обновляем подпись в Telegram, добавляя точный ID сообщения
                        if (message.ID > 0)
                        {
                            try
                            {
                                string finalizedCaption = NodeRepository.FormatTelegramCaption(rawCaptionPath, message.ID, isLatest: true);
                                var editReq = new TL.Methods.Messages_EditMessage
                                {
                                    flags = TL.Methods.Messages_EditMessage.Flags.has_message,
                                    peer = peer,
                                    id = message.ID,
                                    message = finalizedCaption
                                };
                                await _client.Invoke(editReq);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Debug("TelegramService", $"Не удалось обновить финальную подпись с ID для #{message.ID}: {ex.Message}");
                            }
                        }

                        AppLogger.Info("TelegramService", $"Файл '{effectiveFileName}' успешно сохранен в Telegram. Message ID: {message.ID}" + (photoMessage != null ? $", Preview ID: {photoMessage.ID}" : ""));
                        return new FileUploadResult(message.ID, photoMessage?.ID);
                    }
                }

                if (message != null)
                {
                    if (message.ID > 0)
                    {
                        try
                        {
                            string finalizedCaption = NodeRepository.FormatTelegramCaption(rawCaptionPath, message.ID, isLatest: true);
                            var editReq = new TL.Methods.Messages_EditMessage
                            {
                                flags = TL.Methods.Messages_EditMessage.Flags.has_message,
                                peer = peer,
                                id = message.ID,
                                message = finalizedCaption
                            };
                            await _client.Invoke(editReq);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("TelegramService", $"Не удалось обновить финальную подпись с ID для #{message.ID}: {ex.Message}");
                        }
                    }

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
            catch (TL.RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
            {
                TriggerGlobalFloodWait(rpcEx.X);
                throw;
            }
            catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("MESSAGE_ID_INVALID") || rpcEx.Message.Contains("CHAT_ADMIN_REQUIRED")))
            {
                // Сообщение удалено или нет прав — логируем и не прерываем очередь
                AppLogger.Warn("TelegramService", $"Невозможно обновить подпись сообщения #{messageId} (ошибка Telegram: {rpcEx.Message}). Запись будет пропущена.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TelegramService", $"Не удалось обновить подпись сообщения #{messageId} в Telegram: {ex.Message}");
                throw;
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

        public static byte[]? CreateOptimizedGalleryThumbnailFromFile(string filePath, int maxW = 1920, int maxH = 1920)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return CreateOptimizedGalleryThumbnail(fs, maxW, maxH);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TelegramService", $"Не удалось сформировать превью галереи из '{filePath}': {ex.Message}");
                return null;
            }
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

        private async Task<TL.Document?> GetDocumentFromMessageAsync(int messageId, long? channelId = null, bool forceRefresh = false)
        {
            if (!forceRefresh && _documentCache.TryGetValue(messageId, out var cached) && cached.expiresAt > DateTime.UtcNow)
            {
                return cached.document;
            }

            if (_client == null) return null;

            long? effectiveChannelId = (channelId.HasValue && channelId.Value != 0)
                ? channelId.Value
                : _repository?.GetChannelIdByTgMessageId(messageId);

            var peer = await GetStoragePeerAsync(effectiveChannelId);
            TL.Messages_MessagesBase messagesBase;
            try
            {
                messagesBase = await _client.GetMessages(peer, new TL.InputMessage[] { new TL.InputMessageID { id = messageId } });
            }
            catch (TL.RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("CHANNEL_PRIVATE")))
            {
                AppLogger.Warn("TelegramService", "Канал недоступен по сохраненному хэшу. Сброс хэша и повторный поиск...");
                InvalidateStoragePeer();
                peer = await GetStoragePeerAsync(effectiveChannelId);
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
        public async Task DownloadFileAsync(int messageId, Stream destination, long offset, long length, string fileName = "файл", long totalFileSize = -1, long? channelId = null)
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
            var document = await GetDocumentFromMessageAsync(messageId, channelId);
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
            long activationThresholdBytes = Math.Max(1, _currentSettings.Server.StreamingActivationThresholdMb) * 1048576L;
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

                if (readSeq.LastChunkIndex != -1 && (currentChunkIdx == readSeq.LastChunkIndex || currentChunkIdx == readSeq.LastChunkIndex + 1))
                {
                    if (currentChunkIdx == readSeq.LastChunkIndex + 1)
                    {
                        readSeq.SequentialCount++;
                    }
                }
                else
                {
                    // Перемотка (Seek) или новый старт: сбрасываем накопленные байты и отменяем старый префетч
                    readSeq.SequentialCount = 1;
                    readSeq.AccumulatedSequentialBytes = 0;
                    readSeq.ChunkBytesRead.Clear();
                    if (readSeq.LastChunkIndex != -1)
                    {
                        _workerPool?.CancelPendingChunksForFile(messageId);
                    }
                }

                readSeq.LastChunkIndex = currentChunkIdx;
                readSeq.LastReadTime = now;
            }

            // ПРОВЕРКА ЗОНДОВ МЕТАДАННЫХ И СТРИМИНГА:
            // 1) Запрос в хвост файла (последние 512 КБ для ID3v1/тетрисов метаданных)
            bool isTailProbe = actualTotalSize > 1048576 && offset >= Math.Max(0, actualTotalSize - 524288);

            // 2) Зонд метаданных / эскиза обложки:
            // - Хвостовой запрос (ID3v1)
            // - Либо одиночный точечный запрос малого размера (length <= 262144) при единичном последовательном счетчике (SequentialCount <= 1)
            // - Либо чтение самого первого 1 МБ файла (offset < 1048576) до того, как суммарно вычитан первый мегабайт (AccumulatedSequentialBytes < 1048576)
            bool isFirstMb = offset < 1048576 && length <= 1048576 && readSeq.AccumulatedSequentialBytes < 1048576;
            bool isSinglePointProbe = length <= 262144 && readSeq.SequentialCount <= 1;
            bool isMetadataProbe = isTailProbe || isFirstMb || isSinglePointProbe;

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
                        lock (readSeq)
                        {
                            readSeq.AccumulatedSequentialBytes += bytesToSend;
                            readSeq.ChunkBytesRead.AddOrUpdate(offset / 1048576, bytesToSend, (_, old) => old + bytesToSend);
                            readSeq.LastReadTime = DateTime.UtcNow;
                        }
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

                    lock (readSeq)
                    {
                        readSeq.AccumulatedSequentialBytes += toSend;
                        readSeq.ChunkBytesRead.AddOrUpdate(currentPos / 1048576, toSend, (_, old) => old + toSend);
                        readSeq.LastReadTime = DateTime.UtcNow;
                    }

                    bool allReceived = audit.MarkRangeReceived(currentPos - toSend, toSend);

                    AppLogger.Info("TelegramService", $"[Cache RAM] Потоковое чтение из ОЗУ для '{fileName}' (ID {messageId}): смещение {currentPos - toSend:N0}, отдано {toSend:N0} байт ({currentPos:N0} / {actualTotalSize:N0} байт, {(double)currentPos * 100 / Math.Max(1, actualTotalSize):F1}%). {audit.ProgressSummary}.");

                    if (allReceived && audit.LogCompletionOnce())
                    {
                        OnDownloadCompleted?.Invoke(fileName);
                    }

                    // Если мы в режиме стриминга, проверяем необходимость запуска префетча
                    if (!enableDiskCache && !isSmallFile && (!isMetadataProbe || readSeq.AccumulatedSequentialBytes >= 1048576) && remainingBytes > 0)
                    {
                        TriggerContinuousPrefetch(messageId, document, fileName, actualTotalSize, currentPos, audit);
                    }

                    continue;
                }

                // 2. Адаптивный выбор размера чанка MTProto и загрузка строго через единый пул воркеров:
                // - Для маленьких файлов (<= 256 КБ): качаем файл целиком [Зонд 256 КБ].
                // - Первый мегабайт (0..1 МБ):
                //     * При чтении в пределах первой четверти (0..256 КБ): качаем ровно 256 КБ [Зонд 256 КБ - 1/4].
                //       Это мгновенно закрывает чтение тегов/обложек плеерами и экономит 75% сетевого трафика.
                //     * При чтении дальше первой четверти (256 КБ..1 МБ): клиент явно копирует или воспроизводит файл.
                //       Вместо трех отдельных запросов по 256 КБ делаем ОДИН монолитный запрос на оставшиеся 768 КБ [Зонд 768 КБ - 2..4/4].
                //       Universal Interval Merger сшивает обе части в готовый 1 МБ блок в ОЗУ.
                // - Хвостовой зонд (isTailProbe в последних 512 КБ больших файлов): качаем квантом 256 КБ без префетча.
                // - Начиная со второго мегабайта (>= 1 МБ) при обычном стриминге/копировании: качаем полными 1 МБ (1 048 576 байт).

                long chunkOffset;
                int requestLimit;
                int internalOffset;
                bool isCurrentChunkProbe;

                if (isSmallFile)
                {
                    chunkOffset = 0;
                    requestLimit = (int)Math.Min(262144, actualTotalSize);
                    internalOffset = (int)currentPos;
                    isCurrentChunkProbe = true;
                }
                else if (currentPos < 1048576)
                {
                    if (currentPos < 262144)
                    {
                        // 1-я четверть: быстрый зонд 256 КБ для ID3/обложек
                        chunkOffset = 0;
                        requestLimit = (int)Math.Min(262144, actualTotalSize);
                        internalOffset = (int)currentPos;
                        isCurrentChunkProbe = true;
                    }
                    else
                    {
                        // 2..4 четверти: монолитная докачка 768 КБ при копировании или воспроизведении
                        chunkOffset = 262144;
                        requestLimit = (int)Math.Min(786432, actualTotalSize - 262144);
                        internalOffset = (int)(currentPos - 262144);
                        isCurrentChunkProbe = false;
                    }
                }
                else
                {
                    if (isTailProbe)
                    {
                        int quantum = 262144;
                        chunkOffset = (currentPos / quantum) * quantum;
                        internalOffset = (int)(currentPos - chunkOffset);
                        requestLimit = (int)Math.Min(quantum, actualTotalSize - chunkOffset);
                        isCurrentChunkProbe = true;
                    }
                    else
                    {
                        int quantum = 1048576;
                        chunkOffset = (currentPos / quantum) * quantum;
                        internalOffset = (int)(currentPos - chunkOffset);
                        requestLimit = (int)Math.Min(quantum, actualTotalSize - chunkOffset);
                        isCurrentChunkProbe = false;
                    }
                }

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
                            if (!isCurrentChunkProbe && !audit.IsAllChunksReceived())
                            {
                                OnDownloadProgress?.Invoke(fileName, chunkOffset + transferred, actualTotalSize);
                            }
                        });

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

                        lock (readSeq)
                        {
                            readSeq.AccumulatedSequentialBytes += toSend;
                            readSeq.ChunkBytesRead.AddOrUpdate(currentPos / 1048576, toSend, (_, old) => old + toSend);
                            readSeq.LastReadTime = DateTime.UtcNow;
                        }

                        if (!isCurrentChunkProbe && !audit.IsAllChunksReceived())
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

                        if (!enableDiskCache && !isSmallFile && !isCurrentChunkProbe && remainingBytes > 0)
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
            else if (isMetadataProbe && readSeq.AccumulatedSequentialBytes < 1048576)
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

            // Проверяем порог активации упреждения: пока не вычитано N МБ от точки старта/перемотки, префетч не запускается
            long activationThresholdBytes = Math.Max(1, _currentSettings.Server.StreamingActivationThresholdMb) * 1048576L;
            if (_fileReadSequences.TryGetValue(messageId, out var seq))
            {
                lock (seq)
                {
                    if (seq.AccumulatedSequentialBytes < activationThresholdBytes)
                    {
                        return;
                    }
                }
            }

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
            long startChunkOffset = (currentReadOffset / 1048576) * 1048576;

            var newChunksToQueue = new List<long>();
            long totalBytesToQueue = 0;

            // 1. Проверяем текущий чанк: если он скачан лишь частично (например, 256 КБ зонд), сначала докачиваем его!
            int startExpectedSize = (int)Math.Min(1048576L, actualTotalSize - startChunkOffset);
            bool currentInRam = TryGetFromMemoryCache(messageId, startChunkOffset, out var currData, out _) && (currData != null && currData.Length >= startExpectedSize);
            bool currentInFlight = _workerPool.IsChunkInFlight(messageId, startChunkOffset);

            if (!currentInRam && !currentInFlight)
            {
                newChunksToQueue.Add(startChunkOffset);
                totalBytesToQueue += startExpectedSize;
            }

            // 2. Сканируем строго следующие чанки в пределах окна упреждения
            long scanOffset = startChunkOffset + 1048576;
            long maxPrefetchLimit = Math.Min(actualTotalSize, scanOffset + windowBytes);

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

                string workerWord = activeWorkers switch
                {
                    >= 1 and <= 4 => "воркера",
                    _ => "воркеров"
                };

                AppLogger.Info("MtprotoWorkerPool", $"[RAM Streaming] Скачивание {mb:0.00} МБ ({rangeStr} из {totalChunks}) для '{fileName}' через {activeWorkers} {workerWord} MTProto...");

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

        public async Task<bool> DeleteFileFromTelegramAsync(int messageId, long? channelId = null)
        {
            return await DeleteFilesFromTelegramAsync(new System.Collections.Generic.List<int> { messageId }, channelId);
        }

        public async Task<bool> DeleteFilesFromTelegramAsync(System.Collections.Generic.List<int> messageIds, long? channelId = null)
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
                const int batchSize = 100;

                if (channelId.HasValue && channelId.Value != 0)
                {
                    var peer = await GetStoragePeerAsync(channelId.Value);
                    bool isChannel = peer is TL.InputPeerChannel;

                    for (int i = 0; i < validIds.Count; i += batchSize)
                    {
                        var count = Math.Min(batchSize, validIds.Count - i);
                        var batch = validIds.GetRange(i, count).ToArray();

                        await DeleteBatchWithBisectAsync(peer, isChannel, batch);
                    }
                }
                else
                {
                    // Группируем сообщения по их каналам для корректного удаления из разных каналов
                    var channelGroups = new Dictionary<long, List<int>>();
                    foreach (var id in validIds)
                    {
                        long chKey = _repository?.GetChannelIdByTgMessageId(id) ?? 0L;
                        if (!channelGroups.TryGetValue(chKey, out var list))
                        {
                            list = new List<int>();
                            channelGroups[chKey] = list;
                        }
                        list.Add(id);
                    }

                    foreach (var kvp in channelGroups)
                    {
                        var peer = await GetStoragePeerAsync(kvp.Key != 0L ? kvp.Key : (long?)null);
                        bool isChannel = peer is TL.InputPeerChannel;

                        for (int i = 0; i < kvp.Value.Count; i += batchSize)
                        {
                            var count = Math.Min(batchSize, kvp.Value.Count - i);
                            var batch = kvp.Value.GetRange(i, count).ToArray();

                            await DeleteBatchWithBisectAsync(peer, isChannel, batch);
                        }
                    }
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
            await EnsurePacingDelayAsync();

            AppLogger.Debug("TelegramService", $"[DeleteBatchWithBisectAsync] Отправка запроса на удаление {ids.Length} сообщений: [{string.Join(", ", ids)}]");

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
                AppLogger.Debug("TelegramService", $"Удалены Telegram ID: [{string.Join(", ", ids)}]");
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
