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
    /// Пул параллельных MTProto воркеров для скачивания больших файлов из Telegram (TDLib / Telegram Desktop style).
    /// Поддерживает автоматический пейсинг вызовов (профилактику FLOOD_WAIT), нумерацию воркеров в логах 
    /// и детализацию производительности каждого чанка.
    /// </summary>
    public class MtprotoDownloadWorkerPool
    {
        private readonly Client _mainClient;
        private readonly Func<int, int, Task<Client>>? _clientProvider;
        private readonly int _workerCount;
        private DateTime _poolFloodWaitUntil = DateTime.MinValue;

        // Глобальный семафор и динамическая задержка пейсинга вызовов Upload_GetFile для предотвращения FLOOD_WAIT
        private static readonly SemaphoreSlim _pacingLock = new SemaphoreSlim(1, 1);
        private static DateTime _lastRequestUtc = DateTime.MinValue;
        private static int _pacingDelayMs = 200; // Начинаем с плавного темпа 200 мс (~5 МБ/с без блокировок)

        public MtprotoDownloadWorkerPool(Client mainClient, int workerCount = 3, Func<int, int, Task<Client>>? clientProvider = null)
        {
            _mainClient = mainClient ?? throw new ArgumentNullException(nameof(mainClient));
            _workerCount = Math.Max(1, workerCount);
            _clientProvider = clientProvider;
        }

        public class DownloadChunkTask
        {
            public int ChunkIndex { get; set; }
            public long ChunkOffset { get; set; }
            public int RequestLimit { get; set; }
            public int RetryCount { get; set; }
        }

        /// <summary>
        /// Гарантирует микро-интервал между запросами Upload_GetFile (пейсинг 200 мс)
        /// и соблюдает единую паузу пула при возникновении FLOOD_WAIT.
        /// </summary>
        private async Task PaceRequestAsync(int workerId, CancellationToken cancellationToken)
        {
            // 1. Если активна общесистемная пауза FLOOD_WAIT — выжидаем её
            if (_poolFloodWaitUntil > DateTime.UtcNow)
            {
                var waitTime = _poolFloodWaitUntil - DateTime.UtcNow;
                if (waitTime.TotalMilliseconds > 0)
                {
                    AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Пауза из-за активного FLOOD_WAIT ({waitTime.TotalSeconds:F1} сек)...");
                    await Task.Delay(waitTime, cancellationToken);
                }
            }

            // 2. Гарантируем минимальный интервал между запусками вызовов к Telegram API
            await _pacingLock.WaitAsync(cancellationToken);
            try
            {
                var elapsed = (DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
                if (elapsed < _pacingDelayMs)
                {
                    await Task.Delay((int)(_pacingDelayMs - elapsed), cancellationToken);
                }
                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                _pacingLock.Release();
            }
        }

        /// <summary>
        /// Потоковое скачивание диапазона байт строго через ОЗУ (Pure RAM Mode) без записи на дисковый накопитель.
        /// Воркеры качают чанки параллельно в оперативную память, заполняя кольцевой RAM-кэш и сразу отдавая данные в поток.
        /// </summary>
        public async Task<bool> DownloadToStreamAsync(
            Document document,
            Stream destinationStream,
            long offset,
            long length,
            Action<byte[], long>? onChunkReceived = null,
            Action<long, long>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            long totalSize = document.size > 0 ? document.size : offset + length;
            if (length <= 0) return true;

            int chunkSize = 1048576; // 1 МБ чанк
            var chunkTasks = new List<DownloadChunkTask>();

            long currentOffset = offset;
            long remaining = length;
            int index = 0;
            while (remaining > 0)
            {
                int limit = (int)Math.Min(chunkSize, remaining);
                chunkTasks.Add(new DownloadChunkTask
                {
                    ChunkIndex = index++,
                    ChunkOffset = currentOffset,
                    RequestLimit = limit
                });
                currentOffset += limit;
                remaining -= limit;
            }

            var chunkQueue = new ConcurrentQueue<DownloadChunkTask>(chunkTasks);
            var downloadedChunks = new ConcurrentDictionary<long, byte[]>();

            int actualWorkers = Math.Min(_workerCount, chunkQueue.Count);
            Task[] workerTasks = new Task[actualWorkers];

            long totalDownloadedBytes = 0;

            AppLogger.Info("MtprotoWorkerPool", $"[RAM Streaming] Старт скачивания {length:N0} байт (со смещения {offset:N0}) через {actualWorkers} воркеров MTProto...");

            for (int w = 0; w < actualWorkers; w++)
            {
                int workerId = w + 1;
                workerTasks[w] = Task.Run(async () =>
                {
                    var workerClient = _clientProvider != null
                        ? await _clientProvider(workerId, document.dc_id)
                        : (document.dc_id != 0 ? await _mainClient.GetClientForDC(document.dc_id) : _mainClient);
                    var location = document.ToFileLocation();

                    while (chunkQueue.TryDequeue(out var chunk))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            await PaceRequestAsync(workerId, cancellationToken);

                            AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Запрос чанка #{chunk.ChunkIndex} (смещение {chunk.ChunkOffset:N0}, размер {chunk.RequestLimit / 1024} КБ)...");
                            var sw = Stopwatch.StartNew();

                            var fileBase = await workerClient.Upload_GetFile(location, chunk.ChunkOffset, chunk.RequestLimit, precise: true);
                            sw.Stop();

                            if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                            {
                                downloadedChunks[chunk.ChunkOffset] = uploadFile.bytes;
                                onChunkReceived?.Invoke(uploadFile.bytes, chunk.ChunkOffset);

                                long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                onProgress?.Invoke(currentTotal, length);

                                AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] Успешно получен чанк #{chunk.ChunkIndex} ({uploadFile.bytes.Length / 1024} КБ за {sw.ElapsedMilliseconds} мс).");
                            }
                            else if (chunk.RetryCount < 3)
                            {
                                chunk.RetryCount++;
                                chunkQueue.Enqueue(chunk);
                            }
                        }
                        catch (RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                        {
                            int waitSec = rpcEx.X > 0 ? rpcEx.X : 3;
                            _poolFloodWaitUntil = DateTime.UtcNow.AddSeconds(waitSec);
                            Interlocked.Exchange(ref _pacingDelayMs, Math.Min(500, _pacingDelayMs + 50));
                            AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] FLOOD_WAIT {waitSec} сек! Авто-адаптация пейсинга до {_pacingDelayMs} мс. Все воркеры приостановлены.");
                            await Task.Delay(waitSec * 1000, cancellationToken);
                            chunkQueue.Enqueue(chunk);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка чанка #{chunk.ChunkIndex} (смещение {chunk.ChunkOffset:N0}): {ex.Message}");
                            if (chunk.RetryCount < 3)
                            {
                                chunk.RetryCount++;
                                chunkQueue.Enqueue(chunk);
                            }
                            await Task.Delay(300, cancellationToken);
                        }
                    }
                }, cancellationToken);
            }

            // Последовательный вывод скачанных чанков из RAM в destinationStream
            foreach (var task in chunkTasks)
            {
                byte[]? chunkBytes = null;
                while (!downloadedChunks.TryGetValue(task.ChunkOffset, out chunkBytes))
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    await Task.Delay(10, cancellationToken);
                }

                if (chunkBytes == null) continue;

                try
                {
                    await destinationStream.WriteAsync(chunkBytes, 0, chunkBytes.Length, cancellationToken);
                    await destinationStream.FlushAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("MtprotoWorkerPool", $"Клиент прервал RAM-стриминг: {ex.Message}");
                    return false;
                }
            }

            return true;
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
            var chunkQueue = new ConcurrentQueue<DownloadChunkTask>();

            long offset = 0;
            int index = 0;
            while (offset < totalSize)
            {
                int limit = (int)Math.Min(chunkSize, totalSize - offset);
                chunkQueue.Enqueue(new DownloadChunkTask
                {
                    ChunkIndex = index++,
                    ChunkOffset = offset,
                    RequestLimit = limit
                });
                offset += limit;
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
                Task[] workerTasks = new Task[actualWorkers];

                AppLogger.Info("MtprotoWorkerPool", $"Старт фоновой скачки на диск ID {document.id} ({totalSize:N0} байт) через {actualWorkers} воркеров MTProto...");

                for (int w = 0; w < actualWorkers; w++)
                {
                    int workerId = w + 1;
                    workerTasks[w] = Task.Run(async () =>
                    {
                        var workerClient = _clientProvider != null
                            ? await _clientProvider(workerId, document.dc_id)
                            : (document.dc_id != 0 ? await _mainClient.GetClientForDC(document.dc_id) : _mainClient);
                        var location = document.ToFileLocation();

                        while (chunkQueue.TryDequeue(out var chunk))
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            try
                            {
                                await PaceRequestAsync(workerId, cancellationToken);

                                AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] [Диск] Запрос чанка #{chunk.ChunkIndex} ({chunk.RequestLimit / 1024} КБ)...");
                                var sw = Stopwatch.StartNew();

                                var fileBase = await workerClient.Upload_GetFile(location, chunk.ChunkOffset, chunk.RequestLimit, precise: true);
                                sw.Stop();

                                if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, uploadFile.bytes, chunk.ChunkOffset, cancellationToken);

                                    long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                    onProgress?.Invoke(currentTotal, totalSize);

                                    AppLogger.Info("MtprotoWorkerPool", $"[Воркер #{workerId}] [Диск] Сохранен чанк #{chunk.ChunkIndex} ({uploadFile.bytes.Length / 1024} КБ за {sw.ElapsedMilliseconds} мс).");
                                }
                                else if (chunk.RetryCount < 3)
                                {
                                    chunk.RetryCount++;
                                    chunkQueue.Enqueue(chunk);
                                }
                            }
                            catch (RpcException rpcEx) when (rpcEx.Code == 420)
                            {
                                int waitSec = rpcEx.X > 0 ? rpcEx.X : 3;
                                _poolFloodWaitUntil = DateTime.UtcNow.AddSeconds(waitSec);
                                Interlocked.Exchange(ref _pacingDelayMs, Math.Min(500, _pacingDelayMs + 50));
                                AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] FLOOD_WAIT {waitSec} сек! Авто-адаптация пейсинга до {_pacingDelayMs} мс. Все воркеры приостановлены.");
                                await Task.Delay(waitSec * 1000, cancellationToken);
                                chunkQueue.Enqueue(chunk);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("MtprotoWorkerPool", $"[Воркер #{workerId}] Ошибка чанка #{chunk.ChunkIndex}: {ex.Message}");
                                if (chunk.RetryCount < 3)
                                {
                                    chunk.RetryCount++;
                                    chunkQueue.Enqueue(chunk);
                                }
                                await Task.Delay(300, cancellationToken);
                            }
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(workerTasks);
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
    }
}
