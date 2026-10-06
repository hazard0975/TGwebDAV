using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Прямой потоковый канал передачи данных (Memory Pipe) между файловой системой WinFsp и Telegram MTProto.
    /// 1. Zero-Temp: данные передаются напрямую из системного вызова Write в сетевой сокет Telegram без временных файлов на диске C:.
    /// 2. Буфер 512 КБ (размер одной части MTProto): минимальное потребление ОЗУ, данные всегда под рукой.
    /// 3. Плавное обратное давление (Backpressure): метод Write притормаживает Проводник ровно со скоростью сетевой отдачи,
    ///    благодаря чему график скорости Windows Explorer в реальном времени отображает честную скорость интернет-канала.
    /// 4. Совместимость с WTelegramClient (CanSeek = true, фиксированный Length).
    /// </summary>
    public class StreamingPipeStream : Stream
    {
        private const int MaxBufferBytes = 512 * 1024; // Ровно 512 КБ

        private readonly long _length;
        private long _position;
        private readonly Queue<byte[]> _queue = new();
        private byte[]? _currentChunk;
        private int _currentChunkOffset;
        private long _bufferedBytes;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<long, long>? _onProgress;
        private volatile bool _isCompleted;

        public StreamingPipeStream(long length, Action<long, long>? onProgress = null)
        {
            _length = length;
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
        /// Если в буфере уже накопилось >= 512 КБ, вызов синхронно ожидает отправки в сеть (Backpressure).
        /// </summary>
        public void PushData(byte[] data, int offset, int count)
        {
            if (count <= 0)
                return;

            byte[] chunk = new byte[count];
            Buffer.BlockCopy(data, offset, chunk, 0, count);

            lock (_lock)
            {
                while (_bufferedBytes >= MaxBufferBytes && !_isCompleted && !_cts.IsCancellationRequested)
                {
                    Monitor.Wait(_lock, 50);
                }

                if (_isCompleted || _cts.IsCancellationRequested)
                    throw new IOException("Передача данных прервана пользователем или отменена.");

                _queue.Enqueue(chunk);
                _bufferedBytes += count;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>
        /// Сигнализирует потребителю (Telegram), что все данные файла успешно записаны.
        /// </summary>
        public void CompleteWriting()
        {
            lock (_lock)
            {
                _isCompleted = true;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>
        /// Принудительно прерывает передачу при отмене копирования пользователем в Проводнике.
        /// </summary>
        public void Abort()
        {
            lock (_lock)
            {
                _isCompleted = true;
                try { _cts.Cancel(); } catch { }
                Monitor.PulseAll(_lock);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            int totalRead = 0;

            while (totalRead < count)
            {
                lock (_lock)
                {
                    // Если текущий чанк исчерпан, берем следующий из очереди
                    if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
                    {
                        _currentChunk = null;
                        _currentChunkOffset = 0;

                        while (_queue.Count == 0 && !_isCompleted && !_cts.IsCancellationRequested)
                        {
                            Monitor.Wait(_lock, 50);
                        }

                        if (_queue.Count > 0)
                        {
                            _currentChunk = _queue.Dequeue();
                        }
                        else if (_isCompleted || _cts.IsCancellationRequested)
                        {
                            // Очередь пуста и запись завершена -> конец потока (EOF)
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
                        _bufferedBytes -= toCopy;

                        Monitor.PulseAll(_lock);
                    }
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
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
