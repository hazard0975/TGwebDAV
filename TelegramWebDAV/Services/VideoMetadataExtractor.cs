using System;
using System.IO;
using TelegramWebDAV.Models;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Парсер метаданных видео (длительность, ширина, высота) и генератор превью-кадров (thumbnails)
    /// с использованием чистого C# MP4 Box парсера, ATL.NET и безопасной нормализации превью.
    /// </summary>
    public static class VideoMetadataExtractor
    {
        public static bool IsVideoFile(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or 
                ".flv" or ".m4v" or ".ts" or ".3gp" or ".mpeg" or ".mpg" or ".vob" => true,
                _ => false
            };
        }

        public static bool IsPotentialVideo(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            if (IsVideoFile(fileName)) return true;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".tmp" || ext == ".temp" || ext == ".part" || ext == ".crdownload";
        }

        public static string GetVideoMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".ts" => "video/mp2t",
                ".3gp" => "video/3gpp",
                _ => "video/mp4"
            };
        }

        /// <summary>
        /// Извлекает метаданные видео (длительность, ширина, высота, превью) из локального файла.
        /// </summary>
        public static VideoMetadataResult ExtractFromFile(string filePath, string? originalFileName = null)
        {
            var result = new VideoMetadataResult();

            if (!File.Exists(filePath))
            {
                return result;
            }

            // Шаг 1: Полный парсинг MP4/MOV структуры по всему файлу через прямой бинарный парсер (без COM, без падений)
            try
            {
                ParseMp4Full(filePath, result);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("VideoMetadataExtractor", $"MP4 parser ошибка '{filePath}': {ex.Message}");
            }

            // Шаг 2: Чтение тегов и вшитых обложек/постеров через ATL.NET
            try
            {
                var track = new ATL.Track(filePath);
                if (result.DurationSeconds <= 0 && track.Duration > 0)
                {
                    result.DurationSeconds = track.Duration;
                }

                if (track.EmbeddedPictures != null && track.EmbeddedPictures.Count > 0)
                {
                    var pic = track.EmbeddedPictures[0];
                    if (pic.PictureData != null && pic.PictureData.Length > 0)
                    {
                        result.Thumbnail = NormalizeThumbnailForTelegram(pic.PictureData);
                        if (result.Thumbnail != null)
                        {
                            AppLogger.Info("VideoMetadataExtractor", $"Найдена встроенная обложка/постер в '{Path.GetFileName(filePath)}' ({result.Thumbnail.Length} байт)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("VideoMetadataExtractor", $"ATL чтение '{filePath}': {ex.Message}");
            }

            // Шаг 3: Безопасные значения по умолчанию для плеера Telegram, если метаданные не найдены
            if (result.Width <= 0 || result.Height <= 0)
            {
                result.Width = 1280;
                result.Height = 720;
            }

            if (result.DurationSeconds < 0)
            {
                result.DurationSeconds = 0;
            }

            AppLogger.Info("VideoMetadataExtractor", $"Метаданные видео '{Path.GetFileName(filePath)}': {result.Width}x{result.Height}, {result.DurationSeconds} сек, превью: {(result.Thumbnail != null ? $"{result.Thumbnail.Length} байт" : "нет")}");

            return result;
        }

        #region Thumbnail Processing for Telegram

        private static byte[]? NormalizeThumbnailForTelegram(byte[] rawBytes)
        {
            try
            {
                using var ms = new MemoryStream(rawBytes);
                using var originalBmp = System.Drawing.Image.FromStream(ms);
                return ResizeBitmapToTelegramJpeg(originalBmp, 320, 320);
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ResizeBitmapToTelegramJpeg(System.Drawing.Image img, int maxW, int maxH)
        {
            try
            {
                int origW = img.Width;
                int origH = img.Height;

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
                    g.DrawImage(img, 0, 0, newW, newH);
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
                AppLogger.Debug("VideoMetadataExtractor", $"Ошибка сжатия превью: {ex.Message}");
                return null;
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo? GetEncoder(System.Drawing.Imaging.ImageFormat format)
        {
            var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        #endregion

        #region Full MP4 Box Parser (Поддерживает moov в начале и в конце файла)

        private static void ParseMp4Full(string filePath, VideoMetadataResult result)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLength = fs.Length;
            byte[] boxHeader = new byte[8];

            long pos = 0;
            while (pos + 8 <= fileLength)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (fs.Read(boxHeader, 0, 8) < 8) break;

                uint boxSize = ReadUInt32BE(boxHeader, 0);
                string boxType = System.Text.Encoding.ASCII.GetString(boxHeader, 4, 4);

                long actualBoxSize = boxSize;
                int headerSize = 8;

                if (boxSize == 1) // 64-bit box
                {
                    byte[] largeSizeBuf = new byte[8];
                    if (fs.Read(largeSizeBuf, 0, 8) < 8) break;
                    actualBoxSize = (long)ReadUInt64BE(largeSizeBuf, 0);
                    headerSize = 16;
                }
                else if (boxSize == 0)
                {
                    actualBoxSize = fileLength - pos;
                }

                if (actualBoxSize <= 0) break;

                if (boxType == "moov")
                {
                    // Найден контейнер moov! Парсим его содержимое
                    ParseMoovBox(fs, pos + headerSize, actualBoxSize - headerSize, result);
                    break;
                }

                pos += actualBoxSize;
            }
        }

        private static void ParseMoovBox(FileStream fs, long startPos, long length, VideoMetadataResult result)
        {
            long endPos = startPos + length;
            long curPos = startPos;
            byte[] header = new byte[8];

            while (curPos + 8 <= endPos)
            {
                fs.Seek(curPos, SeekOrigin.Begin);
                if (fs.Read(header, 0, 8) < 8) break;

                uint size = ReadUInt32BE(header, 0);
                string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (size <= 0) break;

                if (type == "mvhd" && result.DurationSeconds <= 0)
                {
                    int mvhdSize = (int)Math.Min((long)size, 64);
                    byte[] mvhdBuf = new byte[mvhdSize];
                    fs.Seek(curPos, SeekOrigin.Begin);
                    fs.Read(mvhdBuf, 0, mvhdSize);

                    byte version = mvhdBuf[8];
                    int timeOffset = (version == 1) ? 28 : 20;
                    if (timeOffset + 8 <= mvhdSize)
                    {
                        uint timescale = ReadUInt32BE(mvhdBuf, timeOffset);
                        ulong duration = (version == 1) 
                            ? ReadUInt64BE(mvhdBuf, timeOffset + 4) 
                            : ReadUInt32BE(mvhdBuf, timeOffset + 4);

                        if (timescale > 0 && duration > 0)
                        {
                            result.DurationSeconds = (int)(duration / timescale);
                        }
                    }
                }
                else if (type == "trak")
                {
                    ParseTrakBox(fs, curPos + 8, size - 8, result);
                }

                curPos += size;
            }
        }

        private static void ParseTrakBox(FileStream fs, long startPos, long length, VideoMetadataResult result)
        {
            long endPos = startPos + length;
            long curPos = startPos;
            byte[] header = new byte[8];

            while (curPos + 8 <= endPos)
            {
                fs.Seek(curPos, SeekOrigin.Begin);
                if (fs.Read(header, 0, 8) < 8) break;

                uint size = ReadUInt32BE(header, 0);
                string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (size <= 0) break;

                if (type == "tkhd" && (result.Width <= 0 || result.Height <= 0))
                {
                    int tkhdSize = (int)Math.Min((long)size, 128);
                    byte[] tkhdBuf = new byte[tkhdSize];
                    fs.Seek(curPos, SeekOrigin.Begin);
                    fs.Read(tkhdBuf, 0, tkhdSize);

                    if (tkhdSize >= 84)
                    {
                        int wOffset = tkhdSize - 8;
                        int hOffset = tkhdSize - 4;

                        uint wFixed = ReadUInt32BE(tkhdBuf, wOffset);
                        uint hFixed = ReadUInt32BE(tkhdBuf, hOffset);

                        int width = (int)(wFixed >> 16);
                        int height = (int)(hFixed >> 16);

                        if (width > 0 && height > 0)
                        {
                            result.Width = width;
                            result.Height = height;
                            AppLogger.Info("VideoMetadataExtractor", $"MP4 Box parser определил габариты: {width}x{height}");
                        }
                    }
                }

                curPos += size;
            }
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   (uint)buffer[offset + 3];
        }

        private static ulong ReadUInt64BE(byte[] buffer, int offset)
        {
            return ((ulong)ReadUInt32BE(buffer, offset) << 32) | ReadUInt32BE(buffer, offset + 4);
        }

        #endregion
    }

    public class VideoMetadataResult
    {
        public int DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[]? Thumbnail { get; set; }
    }
}
