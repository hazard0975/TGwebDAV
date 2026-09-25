using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Поддерживает как прямую работу через оперативную память (Pure RAM Stream), так и запись в локальный кэш при включении.
    /// </summary>
    public class MtprotoDownloadWorkerPool
    {
        private readonly Client _mainClient;
        private readonly int _workerCount;

        public MtprotoDownloadWorkerPool(Client mainClient, int workerCount = 3)
        {
            _mainClient = mainClient ?? throw new ArgumentNullException(nameof(mainClient));
            _workerCount = Math.Max(1, workerCount);
        }

        public class DownloadChunkTask
        {
            public long ChunkOffset { get; set; }
            public int RequestLimit { get; set; }
            public int RetryCount { get; set; }
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
            while (remaining > 0)
            {
                int limit = (int)Math.Min(chunkSize, remaining);
                chunkTasks.Add(new DownloadChunkTask
                {
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

            AppLogger.Info("MtprotoWorkerPool", $"[RAM Streaming] Параллельное скачивание {length:N0} байт (со смещения {offset:N0}) через {actualWorkers} воркеров в ОЗУ...");

            for (int w = 0; w < actualWorkers; w++)
            {
                int workerId = w + 1;
                workerTasks[w] = Task.Run(async () =>
                {
                    var workerClient = document.dc_id != 0 ? await _mainClient.GetClientForDC(document.dc_id) : _mainClient;
                    var location = document.ToFileLocation();

                    while (chunkQueue.TryDequeue(out var chunk))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var fileBase = await workerClient.Upload_GetFile(location, chunk.ChunkOffset, chunk.RequestLimit, precise: true);
                            if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                            {
                                downloadedChunks[chunk.ChunkOffset] = uploadFile.bytes;
                                onChunkReceived?.Invoke(uploadFile.bytes, chunk.ChunkOffset);

                                long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                onProgress?.Invoke(currentTotal, length);
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
                            AppLogger.Warn("MtprotoWorkerPool", $"[Stream Worker #{workerId}] FLOOD_WAIT {waitSec} сек...");
                            await Task.Delay(waitSec * 1000, cancellationToken);
                            chunkQueue.Enqueue(chunk);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("MtprotoWorkerPool", $"[Stream Worker #{workerId}] Ошибка чанка {chunk.ChunkOffset}: {ex.Message}");
                            if (chunk.RetryCount < 3)
                            {
                                chunk.RetryCount++;
                                chunkQueue.Enqueue(chunk);
                            }
                            await Task.Delay(500, cancellationToken);
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

            int chunkSize = 1048576; // 1 МБ на чанк (официальный размер блока TDLib)
            var chunkQueue = new ConcurrentQueue<DownloadChunkTask>();

            long offset = 0;
            while (offset < totalSize)
            {
                int limit = (int)Math.Min(chunkSize, totalSize - offset);
                chunkQueue.Enqueue(new DownloadChunkTask
                {
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
                // Заранее резервируем размер файла на диске для исключения фрагментации
                fileStream.SetLength(totalSize);

                int actualWorkers = Math.Min(_workerCount, chunkQueue.Count);
                Task[] workerTasks = new Task[actualWorkers];

                AppLogger.Info("MtprotoWorkerPool", $"Старт фоновой скачки на диск ID {document.id} ({totalSize:N0} байт) через {actualWorkers} воркеров MTProto...");

                for (int w = 0; w < actualWorkers; w++)
                {
                    int workerId = w + 1;
                    workerTasks[w] = Task.Run(async () =>
                    {
                        var workerClient = document.dc_id != 0 ? await _mainClient.GetClientForDC(document.dc_id) : _mainClient;
                        var location = document.ToFileLocation();

                        while (chunkQueue.TryDequeue(out var chunk))
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            try
                            {
                                var fileBase = await workerClient.Upload_GetFile(location, chunk.ChunkOffset, chunk.RequestLimit, precise: true);
                                if (fileBase is Upload_File uploadFile && uploadFile.bytes != null && uploadFile.bytes.Length > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, uploadFile.bytes, chunk.ChunkOffset, cancellationToken);

                                    long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                    onProgress?.Invoke(currentTotal, totalSize);
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
                                AppLogger.Warn("MtprotoWorkerPool", $"[Disk Worker #{workerId}] FLOOD_WAIT {waitSec} сек...");
                                await Task.Delay(waitSec * 1000, cancellationToken);
                                chunkQueue.Enqueue(chunk);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("MtprotoWorkerPool", $"[Disk Worker #{workerId}] Ошибка чанка {chunk.ChunkOffset}: {ex.Message}");
                                if (chunk.RetryCount < 3)
                                {
                                    chunk.RetryCount++;
                                    chunkQueue.Enqueue(chunk);
                                }
                                await Task.Delay(500, cancellationToken);
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
