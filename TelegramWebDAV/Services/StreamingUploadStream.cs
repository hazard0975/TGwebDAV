using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Сквозной потоковый адаптер (Direct Streaming Wrapper) для передачи входящего сетевого потока WebDAV 
    /// напрямую в MTProto-клиент Telegram (WTelegramClient) без буферизации файла на диск.
    /// Предоставляет фиксированную длину (Content-Length), последовательное чтение и обратное давление (TCP Backpressure),
    /// благодаря чему Проводник Windows отображает реальную скорость отдачи и плавный прогресс 0-100%.
    /// </summary>
    public class StreamingUploadStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly byte[]? _prefixBuffer;
        private int _prefixOffset;
        private readonly long _length;
        private long _position;
        private readonly Action<long, long>? _onProgress;

        public StreamingUploadStream(Stream innerStream, long length, byte[]? prefixBuffer = null, Action<long, long>? onProgress = null)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _length = length;
            _prefixBuffer = prefixBuffer;
            _prefixOffset = 0;
            _position = 0;
            _onProgress = onProgress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value == _position) return;
                if (value == 0 && _position == 0) return;
                throw new NotSupportedException($"StreamingUploadStream не поддерживает произвольное позиционирование (запрошено: {value}, текущее: {_position}).");
            }
        }

        public override void Flush()
        {
            _innerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            int totalRead = 0;

            // 1. Сначала отдаем байты из предварительного буфера заголовка (если был прочитан)
            if (_prefixBuffer != null && _prefixOffset < _prefixBuffer.Length)
            {
                int prefixAvailable = _prefixBuffer.Length - _prefixOffset;
                int toCopy = Math.Min(count, prefixAvailable);
                Buffer.BlockCopy(_prefixBuffer, _prefixOffset, buffer, offset, toCopy);
                _prefixOffset += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
                _position += toCopy;
            }

            // 2. Дочитываем недостающие байты напрямую из входящего сокета WebDAV
            if (count > 0)
            {
                int bytesRead = _innerStream.Read(buffer, offset, count);
                if (bytesRead > 0)
                {
                    totalRead += bytesRead;
                    _position += bytesRead;
                }
            }

            if (totalRead > 0)
            {
                _onProgress?.Invoke(_position, _length);
            }

            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            int totalRead = 0;

            if (_prefixBuffer != null && _prefixOffset < _prefixBuffer.Length)
            {
                int prefixAvailable = _prefixBuffer.Length - _prefixOffset;
                int toCopy = Math.Min(count, prefixAvailable);
                Buffer.BlockCopy(_prefixBuffer, _prefixOffset, buffer, offset, toCopy);
                _prefixOffset += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
                _position += toCopy;
            }

            if (count > 0)
            {
                int bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
                if (bytesRead > 0)
                {
                    totalRead += bytesRead;
                    _position += bytesRead;
                }
            }

            if (totalRead > 0)
            {
                _onProgress?.Invoke(_position, _length);
            }

            return totalRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int totalRead = 0;

            if (_prefixBuffer != null && _prefixOffset < _prefixBuffer.Length)
            {
                int prefixAvailable = _prefixBuffer.Length - _prefixOffset;
                int toCopy = Math.Min(buffer.Length, prefixAvailable);
                _prefixBuffer.AsMemory(_prefixOffset, toCopy).CopyTo(buffer);
                _prefixOffset += toCopy;
                buffer = buffer.Slice(toCopy);
                totalRead += toCopy;
                _position += toCopy;
            }

            if (!buffer.IsEmpty)
            {
                int bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead > 0)
                {
                    totalRead += bytesRead;
                    _position += bytesRead;
                }
            }

            if (totalRead > 0)
            {
                _onProgress?.Invoke(_position, _length);
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long targetPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (targetPosition == _position)
            {
                return _position;
            }

            if (origin == SeekOrigin.Begin && offset == 0 && _position == 0)
            {
                return 0;
            }

            throw new NotSupportedException($"StreamingUploadStream поддерживает только последовательное прямое чтение (попытка Seek с {_position} на {targetPosition}).");
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
