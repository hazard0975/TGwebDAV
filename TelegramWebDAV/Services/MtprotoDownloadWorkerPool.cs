using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TL;
using WTelegram;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Единый постоянный пул MTProto-воркеров сервиса (Singleton Worker Pool — архитектура TDLib / Telegram Desktop).
    /// Гарантирует строго N активных воркеров в приложении, сквозной пейсинг запросов, 
    /// приоритетную обработку синхронного чтения и 100% дедупликацию чанков в точке постановки в очередь.
    /// Исключает одновременное существование дублирующих пулов.
    /// </summary>
    public class MtprotoDownloadWorkerPool : IDisposable
    {
        private readonly Client _mainClient;
        private readonly Func<int, int, Task<Client>>? _clientProvider;
        private readonly Func<int, long, byte[]?>? _existingChunkProvider;
        private readonly Func<int, Task<Document?>>? _documentRefresher;

        private int _workerCount;
        private CancellationTokenSource _poolCts = new();
        private Task[] _workerTasks = Array.Empty<Task>();
        private readonly SemaphoreSlim _workSignal = new(0, int.MaxValue);

        // Потокобезопасная единая приоритетная очередь задач
        private readonly object _queueLock = new();
        private readonly LinkedList<ChunkDownloadRequest> _queue = new();
        private readonly Dictionary<string, LinkedListNode<ChunkDownloadRequest>> _waitingRequests = new();
        private readonly Dictionary<string, ChunkDownloadRequest> _inProgressRequests = new();

        public class ChunkDownloadRequest
        {
            public int MessageId { get; set; }
            public Document Document { get; set; } = null!;
            public string FileName { get; set; } = string.Empty;
            public long FileTotalSize { get; set; }
            public int ChunkIndex { get; set; }
            public long ChunkOffset { get; set; }
            public int RequestLimit { get; set; }
            public bool IsHighPriority { get; set; }
            public int RetryCount { get; set; }
            public TaskCompletionSource<byte[]?> Completion { get; set; } = null!;
            public Action<byte[], long>? OnChunkReceived { get; set; }
            public Action<long, long>? OnProgress { get; set; }
        }

        public MtprotoDownloadWorkerPool(
            Client mainClient,
            int workerCount = 1,
            Func<int, int, Task<Client>>? clientProvider = null,
            Func<int, long, byte[]?>? existingChunkProvider = null,
            Func<int, Task<Document?>>? documentRefresher = null)
        {
            _mainClient = mainClient ?? throw new ArgumentNullException(nameof(mainClient));
            _workerCount = Math.Clamp(workerCount, 1, 3);
            _clientProvider = clientProvider;
            _existingChunkProvider = existingChunkProvider;
            _documentRefresher = documentRefresher;

            StartWorkers();
        }

        public int ActiveWorkerCount => _workerCount;

        /// <summary>
        /// Проверяет, находится ли данный чанк уже в очереди ожидания или в процессе активной загрузки воркером.
        /// </summary>
        public bool IsChunkInFlight(int messageId, long chunkOffset)
        {
            string key = $"{messageId}:{chunkOffset}";
            lock (_queueLock)
            {
                return _waitingRequests.ContainsKey(key) || _inProgressRequests.ContainsKey(key);
            }
        }

        /// <summary>
        /// Динамическое изменение числа воркеров на лету без перезапуска приложения и без потери очереди.
        /// </summary>
        public void SetWorkerCount(int count)
        {
            int newCount = Math.Clamp(count, 1, 3);
            if (newCount == _workerCount) return;

            AppLogger.Info("MtprotoWorkerPool", $"Изменение количества активных воркеров MTProto: {_workerCount} -> {newCount}");

            var oldCts = _poolCts;
            oldCts.Cancel();

            _workerCount = newCount;
            _poolCts = new CancellationTokenSource();
            StartWorkers();

            try { oldCts.Dispose(); } catch { }
        }

        private void StartWorkers()
        {
            var token = _poolCts.Token;
            _workerTasks = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                int workerId = i + 1;
                _workerTasks[i] = Task.Run(() => WorkerLoopAsync(workerId, token), token);
            }
            string workerWord = _workerCount switch
            {
                1 => "воркер",
                >= 2 and <= 4 => "воркера",
                _ => "воркеров"
            };
            string readyWord = _workerCount == 1 ? "готов" : "готовы";
            AppLogger.Info("MtprotoWorkerPool", $"Пул постоянных воркеров запущен: {_workerCount} {workerWord} MTProto {readyWord} к обработке очереди.");
        }

        /// <summary>
        /// Постановка чанка в очередь скачивания с атомарной дедупликацией.
        /// Если чанк уже качается или стоит в очереди — возвращается существующая задача без создания повторных сетевых запросов.
        /// </summary>
        public Task<byte[]?> EnqueueChunk(
            int messageId,
            Document document,
            string fileName,
            long fileTotalSize,
            long chunkOffset,
            int requestLimit = 1048576,
            bool isHighPriority = false,
            Action<byte[], long>? onChunkReceived = null,
            Action<long, long>? onProgress = null)
        {
            string key = $"{messageId}:{chunkOffset}";

            lock (_queueLock)
            {
                // 1. Чанк уже находится в активной сетевой загрузке воркером прямо сейчас
                if (_inProgressRequests.TryGetValue(key, out var inProgressReq))
                {
                    return inProgressReq.Completion.Task;
                }

                // 2. Чанк уже ожидает в очереди
                if (_waitingRequests.TryGetValue(key, out var existingNode))
                {
                    // Если плеер требует чанк срочно, а он стоял как фоновый префетч — перемещаем в группу срочных
                    if (isHighPriority && !existingNode.Value.IsHighPriority)
                    {
                        existingNode.Value.IsHighPriority = true;
                        _queue.Remove(existingNode);

                        // Вставляем после уже имеющихся срочных чанков, но перед обычными
                        var firstNormal = _queue.First;
                        while (firstNormal != null && firstNormal.Value.IsHighPriority)
                        {
                            firstNormal = firstNormal.Next;
                        }

                        if (firstNormal != null)
                            _queue.AddBefore(firstNormal, existingNode);
                        else
                            _queue.AddLast(existingNode);
                    }
                    return existingNode.Value.Completion.Task;
                }

                // 3. Новый чанк — создаем единственную задачу
                int chunkIdx = (int)(chunkOffset / 1048576);
                var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new ChunkDownloadRequest
                {
                    MessageId = messageId,
                    Document = document,
                    FileName = fileName,
                    FileTotalSize = fileTotalSize,
                    ChunkIndex = chunkIdx,
                    ChunkOffset = chunkOffset,
                    RequestLimit = requestLimit,
                    IsHighPriority = isHighPriority,
                    Completion = tcs,
                    OnChunkReceived = onChunkReceived,
                    OnProgress = onProgress
                };

                LinkedListNode<ChunkDownloadRequest> newNode;
                if (isHighPriority)
                {
                    var firstNormal = _queue.First;
                    while (firstNormal != null && firstNormal.Value.IsHighPriority)
                    {
                        firstNormal = firstNormal.Next;
                    }

                    if (firstNormal != null)
                        newNode = _queue.AddBefore(firstNormal, request);
                    else
                        newNode = _queue.AddLast(request);
                }
                else
                {
                    newNode = _queue.AddLast(request);
                }

                _waitingRequests[key] = newNode;
                _workSignal.Release();
                return tcs.Task;
            }
        }

        /// <summary>
        /// Отменяет и физически удаляет из очереди все ожидающие низкоприоритетные чанки упреждения для указанного файла (например, при перемотке в плеере).
        /// Срочные чанки чтения плеера и чанки, уже скачивающиеся воркерами прямо сейчас, не затрагиваются.
        /// </summary>
        public void CancelPendingChunksForFile(int messageId)
        {
            lock (_queueLock)
            {
                var node = _queue.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.MessageId == messageId && !node.Value.IsHighPriority)
                    {
                        string key = $"{node.Value.MessageId}:{node.Value.ChunkOffset}";
                        _waitingRequests.Remove(key);
                        node.Value.Completion.TrySetResult(null);
                        _queue.Remove(node);
                    }
                    node = next;
                }
            }
        }

        private async Task PaceRequestAsync(int workerId, CancellationToken cancellationToken)
        {
            await TelegramService.EnsureFloodWaitDelayAsync(cancellationToken);
            await TelegramService.EnsurePacingDelayAsync(cancellationToken);
        }

        private async Task WorkerLoopAsync(int workerId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _workSignal.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                ChunkDownloadRequest? request = null;
                string key = string.Empty;

                lock (_queueLock)
                {
                    if (_queue.First != null)
                    {
                        var node = _queue.First;
                        request = node.Value;
                        _queue.RemoveFirst();
                        key = $"{request.MessageId}:{request.ChunkOffset}";
                        _waitingRequests.Remove(key);
                        _inProgressRequests[key] = request;
                    }
                }

                if (request == null) continue;

                try
                {
                    await ProcessChunkRequestAsync(workerId, request, key, token);
                }
                finally
                {
                    lock (_queueLock)
                    {
                        _inProgressRequests.Remove(key);
                    }
                }
            }
        }

        private async Task ProcessChunkRequestAsync(int workerId, ChunkDownloadRequest request, string key, CancellationToken token)
        {
            int totalFileChunks = request.FileTotalSize > 0 ? (int)Math.Ceiling((double)request.FileTotalSize / 1048576.0) : 1;
            int expectedChunkSize = (int)Math.Min((long)request.RequestLimit, request.FileTotalSize - request.ChunkOffset);
            if (expectedChunkSize <= 0) expectedChunkSize = request.RequestLimit;

            // 1. Проверяем, не появился ли чанк в ОЗУ пока задача стояла в очереди
            if (_existingChunkProvider != null)
            {
                var existingBytes = _existingChunkProvider(request.MessageId, request.ChunkOffset);
                if (existingBytes != null && existingBytes.Length == expectedChunkSize)
                {
                    request.OnChunkReceived?.Invoke(existingBytes, request.ChunkOffset);
                    request.Completion.TrySetResult(existingBytes);
                    return;
                }
            }

            Client? activeClient = null;
            try
            {
                activeClient = _clientProvider != null
                    ? await _clientProvider(workerId, request.Document.dc_id)
                    : (request.Document.dc_id != 0 ? await _mainClient.GetClientForDC(request.Document.dc_id) : _mainClient);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка подключения к DC {request.Document.dc_id}: {ex.Message}");
                request.Completion.TrySetResult(null);
                return;
            }

            var location = request.Document.ToFileLocation();
            bool completedSuccessfully = false;

            while (!completedSuccessfully && !token.IsCancellationRequested)
            {
                byte[]? partialExisting = null;
                if (_existingChunkProvider != null)
                {
                    var existingBytes = _existingChunkProvider(request.MessageId, request.ChunkOffset);
                    if (existingBytes != null && existingBytes.Length > 0 && existingBytes.Length < expectedChunkSize)
                    {
                        partialExisting = existingBytes;
                    }
                }

                int displayChunkIdx = request.ChunkIndex + 1;

                try
                {
                    await PaceRequestAsync(workerId, token);

                    long reqOffset = request.ChunkOffset;
                    int reqLimit = request.RequestLimit;

                    if (partialExisting != null && partialExisting.Length > 0)
                    {
                        reqOffset = request.ChunkOffset + partialExisting.Length;
                        reqLimit = request.RequestLimit - partialExisting.Length;
                    }

                    // Требование Telegram MTProto API: лимит должен быть строго кратен 4096 байтам
                    int alignedLimit = (int)(Math.Ceiling(reqLimit / 4096.0) * 4096);
                    if (alignedLimit < 4096) alignedLimit = 4096;
                    if (alignedLimit > 1048576) alignedLimit = 1048576;

                    string fileTag = !string.IsNullOrEmpty(request.FileName) ? $" для '{request.FileName}'" : "";
                    string requestTag = alignedLimit >= 1048576
                        ? " [Полный чанк]"
                        : (alignedLimit == 262144 ? " [Зонд 256 КБ]" : $" [Зонд {(double)alignedLimit / 1024.0:0.00} КБ]");
                    AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Запрос чанка #{displayChunkIdx}/{totalFileChunks}{fileTag} (смещение {reqOffset:N0} б, размер {alignedLimit:N0} б){requestTag}...");
                    var sw = Stopwatch.StartNew();

                    var fileBase = await activeClient.Upload_GetFile(location, reqOffset, alignedLimit, precise: true);
                    sw.Stop();
                    TelegramService.NotifyRequestCompleted();

                    if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                    {
                        byte[] finalChunkBytes;
                        if (partialExisting != null && partialExisting.Length > 0)
                        {
                            finalChunkBytes = new byte[partialExisting.Length + uploadFile.bytes.Length];
                            Buffer.BlockCopy(partialExisting, 0, finalChunkBytes, 0, partialExisting.Length);
                            Buffer.BlockCopy(uploadFile.bytes, 0, finalChunkBytes, partialExisting.Length, uploadFile.bytes.Length);
                        }
                        else
                        {
                            finalChunkBytes = uploadFile.bytes;
                        }

                        int receivedLen = finalChunkBytes.Length;
                        string chunkTag;
                        if (receivedLen >= 1048576)
                        {
                            chunkTag = " [Полный чанк]";
                        }
                        else if (request.ChunkOffset + receivedLen >= request.FileTotalSize)
                        {
                            chunkTag = $" [Остаток {(double)receivedLen / 1024.0:0.00} КБ]";
                        }
                        else if (receivedLen == 262144)
                        {
                            chunkTag = " [Зонд 256 КБ]";
                        }
                        else
                        {
                            chunkTag = $" [Зонд {(double)receivedLen / 1024.0:0.00} КБ]";
                        }

                        AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Получен чанк #{displayChunkIdx}/{totalFileChunks}{fileTag} ({receivedLen:N0} б за {sw.ElapsedMilliseconds} мс){chunkTag}.");

                        request.OnChunkReceived?.Invoke(finalChunkBytes, request.ChunkOffset);
                        request.OnProgress?.Invoke(receivedLen, request.FileTotalSize);
                        request.Completion.TrySetResult(finalChunkBytes);
                        completedSuccessfully = true;
                    }
                    else if (request.RetryCount < 3)
                    {
                        request.RetryCount++;
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Пустой ответ для чанка #{displayChunkIdx}{fileTag}. Повтор {request.RetryCount}/3...");
                        await Task.Delay(200, token);
                    }
                    else
                    {
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Исчерпаны попытки для Чанка #{displayChunkIdx}/{totalFileChunks}{fileTag}.");
                        break;
                    }
                }
                catch (RpcException rpcEx) when (rpcEx.Code == 303) // FILE_MIGRATE_X
                {
                    try
                    {
                        activeClient = await _mainClient.GetClientForDC(rpcEx.X);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка миграции на DC {rpcEx.X}: {ex.Message}");
                        break;
                    }
                }
                catch (RpcException rpcEx) when (rpcEx.Code == 400 && rpcEx.Message.Contains("FILE_REFERENCE_EXPIRED"))
                {
                    if (_documentRefresher != null)
                    {
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] FILE_REFERENCE_EXPIRED для '{request.FileName}'. Обновление дескриптора документа...");
                        var refreshedDoc = await _documentRefresher(request.MessageId);
                        if (refreshedDoc != null)
                        {
                            request.Document = refreshedDoc;
                            location = refreshedDoc.ToFileLocation();
                            activeClient = refreshedDoc.dc_id != 0 ? await _mainClient.GetClientForDC(refreshedDoc.dc_id) : _mainClient;
                            continue;
                        }
                    }
                    break;
                }
                catch (RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                {
                    int waitSec = rpcEx.X > 0 ? rpcEx.X : 3;
                    TelegramService.TriggerGlobalFloodWait(waitSec);
                    AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] FLOOD_WAIT {waitSec} сек! Сквозная пауза. Все воркеры приостановлены.");
                    await Task.Delay(waitSec * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка чанка #{displayChunkIdx}/{totalFileChunks} (смещение {request.ChunkOffset:N0} б): {ex.Message}");
                    if (request.RetryCount < 3)
                    {
                        request.RetryCount++;
                        await Task.Delay(300, token);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (!completedSuccessfully)
            {
                request.Completion.TrySetResult(null);
            }
        }

        /// <summary>
        /// Скачивание файла в локальный дисковый кэш (используется только если включена опция EnableDiskReadCache).
        /// </summary>
        public async Task<bool> DownloadFileAsync(
            Document document,
            string destinationFilePath,
            Action<long, long>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            long totalSize = document.size;
            if (totalSize <= 0) return false;

            int chunkSize = 1048576; // 1 МБ на чанк
            var chunkQueue = new ConcurrentQueue<long>();

            long offset = 0;
            while (offset < totalSize)
            {
                chunkQueue.Enqueue(offset);
                offset += chunkSize;
            }

            long totalDownloadedBytes = 0;
            string tempFilePath = destinationFilePath + ".tmp";

            string? parentDir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true))
            using (SafeFileHandle handle = fileStream.SafeFileHandle)
            {
                fileStream.SetLength(totalSize);

                int actualWorkers = Math.Min(_workerCount, chunkQueue.Count);
                Task[] diskWorkerTasks = new Task[actualWorkers];

                AppLogger.Info("MtprotoWorkerPool", $"Старт фоновой скачки на диск ID {document.id} ({totalSize:N0} байт) через {actualWorkers} воркеров MTProto...");

                for (int w = 0; w < actualWorkers; w++)
                {
                    int workerId = w + 1;
                    diskWorkerTasks[w] = Task.Run(async () =>
                    {
                        var workerClient = _clientProvider != null
                            ? await _clientProvider(workerId, document.dc_id)
                            : (document.dc_id != 0 ? await _mainClient.GetClientForDC(document.dc_id) : _mainClient);
                        var location = document.ToFileLocation();

                        while (chunkQueue.TryDequeue(out var chunkOffset))
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            int chunkIdx = (int)(chunkOffset / chunkSize);
                            int reqLimit = (int)Math.Min((long)chunkSize, totalSize - chunkOffset);

                            try
                            {
                                await PaceRequestAsync(workerId, cancellationToken);

                                AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] [Диск] Запрос чанка #{chunkIdx} ({reqLimit / 1024} КБ)...");
                                var sw = Stopwatch.StartNew();

                                var fileBase = await workerClient.Upload_GetFile(location, chunkOffset, reqLimit, precise: true);
                                sw.Stop();
                                TelegramService.NotifyRequestCompleted();

                                if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, uploadFile.bytes, chunkOffset, cancellationToken);
                                    long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                    onProgress?.Invoke(currentTotal, totalSize);
                                    AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] [Диск] Сохранен чанк #{chunkIdx} ({uploadFile.bytes.Length / 1024} КБ за {sw.ElapsedMilliseconds} мс).");
                                }
                            }
                            catch (RpcException rpcEx) when (rpcEx.Code == 420)
                            {
                                int waitSec = rpcEx.X > 0 ? rpcEx.X : 3;
                                TelegramService.TriggerGlobalFloodWait(waitSec);
                                AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] FLOOD_WAIT {waitSec} сек! Сквозная пауза. Все воркеры приостановлены.");
                                await Task.Delay(waitSec * 1000, cancellationToken);
                                chunkQueue.Enqueue(chunkOffset);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка чанка #{chunkIdx}: {ex.Message}");
                                await Task.Delay(300, cancellationToken);
                            }
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(diskWorkerTasks);
            }

            if (totalDownloadedBytes >= totalSize && File.Exists(tempFilePath))
            {
                File.Move(tempFilePath, destinationFilePath, overwrite: true);
                AppLogger.Info("MtprotoWorkerPool", $"Файл ID {document.id} ({totalSize:N0} байт) успешно скачан в кэш.");
                return true;
            }

            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { }
            }
            return false;
        }

        public void Dispose()
        {
            try
            {
                _poolCts.Cancel();
                _poolCts.Dispose();
                _workSignal.Dispose();
                lock (_queueLock)
                {
                    foreach (var req in _queue)
                    {
                        req.Completion.TrySetResult(null);
                    }
                    _queue.Clear();
                    _waitingRequests.Clear();
                    _inProgressRequests.Clear();
                }
            }
            catch { }
        }
    }
}
