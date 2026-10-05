using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Потокобезопасный мост (Producer-Consumer Streaming Pipe) между ядром Windows / драйвером WinFsp
    /// (который синхронно выталкивает байты через метод Write) и MTProto-клиентом Telegram
    /// (который асинхронно вычитывает байты порциями по 512 КБ для Upload_SaveBigFilePart).
    /// 
    /// Предоставляет:
    /// 1. Zero-Temp streaming: данные передаются напрямую из буфера ОС в сокет Telegram без записи на диск C:.
    /// 2. Ограниченный кольцевой буфер (Bounded Queue ~2-4 МБ): предотвращает рост потребления памяти.
    /// 3. Обратное давление (Backpressure): если скорость отдачи в сеть ниже скорости диска,
    ///    метод Write притормаживает Проводник Windows, благодаря чему полоса копирования отражает
    ///    реальную скорость загрузки в облако.
    /// 4. Совместимость с WTelegramClient (CanSeek = true, фиксированный Length).
    /// </summary>
    public class StreamingPipeStream : Stream
    {
        private readonly long _length;
        private long _position;
        private readonly BlockingCollection<byte[]> _queue;
        private byte[]? _currentChunk;
        private int _currentChunkOffset;
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<long, long>? _onProgress;
        private volatile bool _isCompleted;

        /// <summary>
        /// Инициализирует потоковый пайп.
        /// </summary>
        /// <param name="length">Ожидаемая общая длина файла в байтах.</param>
        /// <param name="maxBufferedChunks">Максимальное количество чанков в очереди (по умолчанию 32 шт по 64-128 КБ ~2-4 МБ).</param>
        /// <param name="onProgress">Опциональный колбэк прогресса.</param>
        public StreamingPipeStream(long length, int maxBufferedChunks = 32, Action<long, long>? onProgress = null)
        {
            _length = length;
            _queue = new BlockingCollection<byte[]>(maxBufferedChunks);
            _onProgress = onProgress;
        }

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => true; // Для совместимости с WTelegramClient
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value == _position || (value == 0 && _position == 0))
                    return;

                throw new NotSupportedException($"StreamingPipeStream не поддерживает произвольное позиционирование (запрошено: {value}, текущее: {_position}).");
            }
        }

        public override void Flush()
        {
            // Прямой пайп в ОЗУ, flush не требуется
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin && (offset == _position || (offset == 0 && _position == 0)))
                return _position;

            if (origin == SeekOrigin.Current && offset == 0)
                return _position;

            throw new NotSupportedException($"StreamingPipeStream поддерживает только последовательное чтение (origin: {origin}, offset: {offset}, pos: {_position}).");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Длина StreamingPipeStream фиксируется при инициализации.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Для записи используйте PushData(byte[], int, int).");
        }

        /// <summary>
        /// Толкает порцию байтов из системного вызова WinFsp Write в очередь потребителя.
        /// Если очередь заполнена (сеть не успевает передавать), вызов блокируется до освобождения места (Backpressure).
        /// </summary>
        public void PushData(byte[] data, int offset, int count)
        {
            if (_isCompleted || _cts.IsCancellationRequested)
                throw new InvalidOperationException("Поток уже завершен или отменен.");

            if (count <= 0)
                return;

            byte[] chunk = new byte[count];
            Buffer.BlockCopy(data, offset, chunk, 0, count);

            try
            {
                _queue.Add(chunk, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new IOException("Передача данных прервана пользователем или отменена.");
            }
        }

        /// <summary>
        /// Сигнализирует потребителю (Telegram), что все данные файла успешно записаны.
        /// </summary>
        public void CompleteWriting()
        {
            if (!_isCompleted)
            {
                _isCompleted = true;
                _queue.CompleteAdding();
            }
        }

        /// <summary>
        /// Принудительно прерывает передачу при отмене копирования пользователем в Проводнике.
        /// </summary>
        public void Abort()
        {
            _isCompleted = true;
            try
            {
                _cts.Cancel();
            }
            catch { }

            try
            {
                _queue.CompleteAdding();
            }
            catch { }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            int totalRead = 0;

            while (totalRead < count)
            {
                // Если текущий чанк исчерпан, берем следующий из очереди
                if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
                {
                    _currentChunk = null;
                    _currentChunkOffset = 0;

                    try
                    {
                        if (!_queue.TryTake(out _currentChunk, Timeout.Infinite, _cts.Token))
                        {
                            // Очередь закрыта и пуста — конец потока (EOF)
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                if (_currentChunk != null)
                {
                    int available = _currentChunk.Length - _currentChunkOffset;
                    int toCopy = Math.Min(count - totalRead, available);
                    Buffer.BlockCopy(_currentChunk, _currentChunkOffset, buffer, offset + totalRead, toCopy);
                    _currentChunkOffset += toCopy;
                    totalRead += toCopy;
                    _position += toCopy;
                }
            }

            if (totalRead > 0)
            {
                _onProgress?.Invoke(_position, _length);
            }

            return totalRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Выполняем чтение через фоновый Task, чтобы не блокировать асинхронный контекст MTProto
            return Task.Run(() =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                return Read(buffer, offset, count);
            }, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Abort();
                _queue.Dispose();
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
