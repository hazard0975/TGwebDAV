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

        // Очереди задач: синхронное чтение плеером имеет приоритет над фоновым упреждением
        private readonly ConcurrentQueue<ChunkDownloadRequest> _highPriorityQueue = new();
        private readonly ConcurrentQueue<ChunkDownloadRequest> _normalPriorityQueue = new();

        // Единая таблица активных задач на скачивание чанков для 100% дедупликации: ключ = "{messageId}:{chunkOffset}"
        private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlightChunks = new();

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
            public volatile bool IsCancelled;
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
            AppLogger.Info("MtprotoWorkerPool", $"Пул постоянных воркеров запущен: {_workerCount} воркеров MTProto готовы к обработке очереди.");
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

            // Проверяем, есть ли чанк уже в очереди или в процессе загрузки
            if (_inFlightChunks.TryGetValue(key, out var existingTask))
            {
                return existingTask;
            }

            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_inFlightChunks.TryAdd(key, tcs.Task))
            {
                int chunkIdx = (int)(chunkOffset / 1048576);
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

                if (isHighPriority)
                {
                    _highPriorityQueue.Enqueue(request);
                }
                else
                {
                    _normalPriorityQueue.Enqueue(request);
                }

                _workSignal.Release();
                return tcs.Task;
            }

            // Если параллельный поток успел добавить задачу на этой микросекунде
            return _inFlightChunks.TryGetValue(key, out var concurrentTask) ? concurrentTask : tcs.Task;
        }

        /// <summary>
        /// Отменяет все ожидающие в очереди низкоприоритетные чанки упреждения для указанного файла (например, при перемотке в плеере).
        /// </summary>
        public void CancelPendingChunksForFile(int messageId)
        {
            foreach (var req in _normalPriorityQueue)
            {
                if (req.MessageId == messageId && !req.IsCancelled)
                {
                    req.IsCancelled = true;
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

                // Сначала забираем срочные чанки плеера (High Priority), затем фоновые упреждающие (Normal Priority)
                if (!_highPriorityQueue.TryDequeue(out request))
                {
                    _normalPriorityQueue.TryDequeue(out request);
                }

                if (request == null) continue;

                string key = $"{request.MessageId}:{request.ChunkOffset}";

                if (request.IsCancelled || token.IsCancellationRequested)
                {
                    _inFlightChunks.TryRemove(key, out _);
                    request.Completion.TrySetResult(null);
                    continue;
                }

                await ProcessChunkRequestAsync(workerId, request, key, token);
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
                    _inFlightChunks.TryRemove(key, out _);
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
                _inFlightChunks.TryRemove(key, out _);
                request.Completion.TrySetResult(null);
                return;
            }

            var location = request.Document.ToFileLocation();
            bool completedSuccessfully = false;

            while (!completedSuccessfully && !token.IsCancellationRequested && !request.IsCancelled)
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

                    string probeTag = reqLimit < 1048576 ? " [Докачка остатка/Зонд]" : "";
                    AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Запрос чанка #{request.ChunkIndex}/{totalFileChunks} (смещение {reqOffset:N0} б, размер {reqLimit:N0} б){probeTag}...");
                    var sw = Stopwatch.StartNew();

                    var fileBase = await activeClient.Upload_GetFile(location, reqOffset, reqLimit, precise: true);
                    sw.Stop();

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
                        string chunkTag = receivedLen < expectedChunkSize
                            ? $" [{receivedLen:N0} б из {expectedChunkSize:N0} б, Хвост EOF]"
                            : " [Полный]";

                        AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Получен чанк #{request.ChunkIndex}/{totalFileChunks} ({receivedLen:N0} б за {sw.ElapsedMilliseconds} мс){chunkTag}.");

                        _inFlightChunks.TryRemove(key, out _);
                        request.OnChunkReceived?.Invoke(finalChunkBytes, request.ChunkOffset);
                        request.OnProgress?.Invoke(receivedLen, request.FileTotalSize);
                        request.Completion.TrySetResult(finalChunkBytes);
                        completedSuccessfully = true;
                    }
                    else if (request.RetryCount < 3)
                    {
                        request.RetryCount++;
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Пустой ответ для чанка #{request.ChunkIndex}. Повтор {request.RetryCount}/3...");
                        await Task.Delay(200, token);
                    }
                    else
                    {
                        AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Исчерпаны попытки для Чанка #{request.ChunkIndex}/{totalFileChunks}.");
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
                    AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка чанка #{request.ChunkIndex}/{totalFileChunks} (смещение {request.ChunkOffset:N0} б): {ex.Message}");
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
                _inFlightChunks.TryRemove(key, out _);
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
            }
            catch { }
        }
    }
}
