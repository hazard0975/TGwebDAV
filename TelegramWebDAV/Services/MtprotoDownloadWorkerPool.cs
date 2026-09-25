using System;
using System.Collections.Concurrent;
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
    /// Распределяет скачивание 1-мегабайтных чанков между параллельными потоками со встроенной записью по смещению
    /// через RandomAccess и поддержкой отказоустойчивых повторов при FLOOD_WAIT.
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

                AppLogger.Info("MtprotoWorkerPool", $"Старт параллельного скачивания файла ID {document.id} ({totalSize:N0} байт) через {actualWorkers} воркеров MTProto...");

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
                                    // Асинхронная запись блока по строгому смещению через RandomAccess
                                    await RandomAccess.WriteAsync(handle, uploadFile.bytes, chunk.ChunkOffset, cancellationToken);

                                    long currentTotal = Interlocked.Add(ref totalDownloadedBytes, uploadFile.bytes.Length);
                                    onProgress?.Invoke(currentTotal, totalSize);
                                }
                                else
                                {
                                    if (chunk.RetryCount < 3)
                                    {
                                        chunk.RetryCount++;
                                        chunkQueue.Enqueue(chunk);
                                    }
                                }
                            }
                            catch (RpcException rpcEx) when (rpcEx.Code == 420) // FLOOD_WAIT_X
                            {
                                int waitSec = rpcEx.X > 0 ? rpcEx.X : 3;
                                AppLogger.Warn("MtprotoWorkerPool", $"[Worker #{workerId}] FLOOD_WAIT {waitSec} сек. Автоматическая пауза воркера...");
                                await Task.Delay(waitSec * 1000, cancellationToken);
                                chunkQueue.Enqueue(chunk);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("MtprotoWorkerPool", $"[Worker #{workerId}] Ошибка скачивания чанка {chunk.ChunkOffset}: {ex.Message}");
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
                AppLogger.Info("MtprotoWorkerPool", $"Файл ID {document.id} ({totalSize:N0} байт) успешно скачан через пул воркеров и сохранен.");
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
