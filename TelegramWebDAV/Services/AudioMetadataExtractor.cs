using System;
using System.IO;
using System.Text;
using TelegramWebDAV.Models;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Парсер аудио-метаданных (ID3v1, ID3v2, FLAC, M4A) на основе легковесного чтения первых байт потока (Header Cache).
    /// Позволяет извлекать реальные названия треков, исполнителей и формат даже из временных файлов (.tmp).
    /// </summary>
    public static class AudioMetadataExtractor
    {
        public const int HeaderCacheSize = 262144; // 256 KB для надежного чтения ID3v2 тегов и обложки альбома (APIC)

        public static bool IsAudioFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".ogg" || ext == ".wav" || ext == ".aac" || ext == ".opus" || ext == ".wma";
        }

        public static bool IsPotentialAudio(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            if (IsAudioFile(fileName)) return true;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".tmp" || ext == ".temp" || ext == ".part" || ext == ".crdownload";
        }

        public static string? DetectAudioFormat(byte[] buffer, int length)
        {
            if (buffer == null || length < 4) return null;

            // ID3v2 заголовок: 'ID3' (MP3)
            if (length >= 3 && buffer[0] == 0x49 && buffer[1] == 0x44 && buffer[2] == 0x33)
                return ".mp3";

            // MPEG Frame Sync: 11 бит единиц (0xFF, 0xE0+)
            if (length >= 2 && buffer[0] == 0xFF && (buffer[1] & 0xE0) == 0xE0)
                return ".mp3";

            // FLAC: 'fLaC'
            if (length >= 4 && buffer[0] == 0x66 && buffer[1] == 0x4C && buffer[2] == 0x61 && buffer[3] == 0x43)
                return ".flac";

            // OGG: 'OggS'
            if (length >= 4 && buffer[0] == 0x4F && buffer[1] == 0x67 && buffer[2] == 0x67 && buffer[3] == 0x53)
                return ".ogg";

            // M4A / AAC: 'ftyp' на смещении 4-7
            if (length >= 8 && buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70)
                return ".m4a";

            // RIFF WAVE: 'RIFF' .... 'WAVE'
            if (length >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                buffer[8] == 0x57 && buffer[9] == 0x41 && buffer[10] == 0x56 && buffer[11] == 0x45)
                return ".wav";

            return null;
        }

        /// <summary>
        /// Извлекает метаданные и обложку из локального файла с помощью библиотеки ATL.
        /// </summary>
        public static AudioMetadataResult ExtractFromFile(string filePath, string? originalFileName = null)
        {
            var result = new AudioMetadataResult();
            try
            {
                if (File.Exists(filePath))
                {
                    var track = new ATL.Track(filePath);
                    result.Title = !string.IsNullOrWhiteSpace(track.Title) ? track.Title.Trim() : null;
                    result.Artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist.Trim() : null;
                    result.Album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album.Trim() : null;
                    result.Year = track.Year > 0 ? track.Year : null;
                    result.DurationSeconds = track.Duration > 0 ? track.Duration : null;
                    result.Bitrate = track.Bitrate > 0 ? track.Bitrate : null;
                    string effectiveFileName = !string.IsNullOrWhiteSpace(originalFileName) ? originalFileName : filePath;
                    result.AudioFormat = Path.GetExtension(effectiveFileName)?.ToLowerInvariant();

                    // Если ATL подставил имя локального файла вместо реального тега ID3
                    string fileNoExt = Path.GetFileNameWithoutExtension(filePath);
                    if (!string.IsNullOrEmpty(result.Title) && 
                        (result.Title.Equals(fileNoExt, StringComparison.OrdinalIgnoreCase) || 
                         result.Title.Equals(Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Title = null;
                    }

                    if (track.EmbeddedPictures != null && track.EmbeddedPictures.Count > 0)
                    {
                        var pic = track.EmbeddedPictures[0];
                        if (pic.PictureData != null && pic.PictureData.Length > 0)
                        {
                            result.AlbumCover = pic.PictureData;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AudioMetadataExtractor", $"ATL чтение файла '{filePath}': {ex.Message}");
            }

            if (string.IsNullOrEmpty(result.Title))
            {
                string nameForInference = !string.IsNullOrWhiteSpace(originalFileName) 
                    ? originalFileName 
                    : Path.GetFileName(filePath);
                InferFromFileName(nameForInference, result);
            }

            return result;
        }

        /// <summary>
        /// Извлекает кэш первых 256 КБ заголовка и парсит аудио-теги
        /// </summary>
        public static AudioMetadataResult ExtractFromStream(Stream stream, string fileName)
        {
            var result = new AudioMetadataResult();
            byte[] headerBuffer = new byte[HeaderCacheSize];
            int readBytes = 0;

            try
            {
                long originalPos = stream.CanSeek ? stream.Position : 0;
                readBytes = stream.Read(headerBuffer, 0, headerBuffer.Length);
                
                if (stream.CanSeek)
                {
                    stream.Position = originalPos; // Возвращаем позицию потока для дальнейшей записи в TG
                }

                if (readBytes > 0)
                {
                    result.AudioFormat = DetectAudioFormat(headerBuffer, readBytes);

                    // Базовый парсинг ID3v2 (первые 3 байта 'ID3')
                    if (readBytes >= 10 && headerBuffer[0] == 0x49 && headerBuffer[1] == 0x44 && headerBuffer[2] == 0x33)
                    {
                        ParseId3v2Header(headerBuffer, readBytes, result, fileName);
                    }
                    else
                    {
                        // Если тегов нет, берем имя файла без расширения
                        InferFromFileName(fileName, result);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioMetadataExtractor] Ошибка парсинга тегов для {fileName}: {ex.Message}");
                InferFromFileName(fileName, result);
            }

            return result;
        }

        private static void ParseId3v2Header(byte[] buffer, int length, AudioMetadataResult result, string fileName)
        {
            result.Bitrate = 320; // kbps default for HQ
            result.DurationSeconds = 0; // Определяется точнее или оставляется 0 для нативного подсчета клиентом
            result.AudioFormat = ".mp3";

            try
            {
                int version = buffer[3]; // 3 = ID3v2.3, 4 = ID3v2.4
                int tagSize = ((buffer[6] & 0x7F) << 21) |
                              ((buffer[7] & 0x7F) << 14) |
                              ((buffer[8] & 0x7F) << 7) |
                              (buffer[9] & 0x7F);

                int maxPos = Math.Min(length, 10 + tagSize);
                int pos = 10;

                while (pos + 10 <= maxPos)
                {
                    // Проверяем 4-байтный идентификатор фрейма
                    if (buffer[pos] == 0) break; // Заполнитель нулями в конце тега

                    string frameId = Encoding.ASCII.GetString(buffer, pos, 4);
                    int frameSize;
                    if (version == 4)
                    {
                        frameSize = ((buffer[pos + 4] & 0x7F) << 21) |
                                    ((buffer[pos + 5] & 0x7F) << 14) |
                                    ((buffer[pos + 6] & 0x7F) << 7) |
                                    (buffer[pos + 7] & 0x7F);
                    }
                    else
                    {
                        frameSize = (buffer[pos + 4] << 24) |
                                    (buffer[pos + 5] << 16) |
                                    (buffer[pos + 6] << 8) |
                                    buffer[pos + 7];
                    }

                    if (frameSize <= 0 || pos + 10 + frameSize > maxPos)
                    {
                        break;
                    }

                    int dataOffset = pos + 10;
                    if (frameId == "APIC" && result.AlbumCover == null && frameSize > 10)
                    {
                        try
                        {
                            byte apicEncoding = buffer[dataOffset];
                            int p = dataOffset + 1;
                            while (p < dataOffset + frameSize && buffer[p] != 0) p++;
                            if (p < dataOffset + frameSize)
                            {
                                p++; // пропускаем 0 байт mime типа
                                if (p < dataOffset + frameSize)
                                {
                                    p++; // пропускаем байт типа картинки (0x03 front cover и т.д.)
                                    if (apicEncoding == 1 || apicEncoding == 2)
                                    {
                                        while (p + 1 < dataOffset + frameSize && !(buffer[p] == 0 && buffer[p + 1] == 0)) p += 2;
                                        p += 2;
                                    }
                                    else
                                    {
                                        while (p < dataOffset + frameSize && buffer[p] != 0) p++;
                                        p++;
                                    }

                                    int imgLen = (dataOffset + frameSize) - p;
                                    if (imgLen > 100 && p + imgLen <= length)
                                    {
                                        byte[] cover = new byte[imgLen];
                                        Buffer.BlockCopy(buffer, p, cover, 0, imgLen);
                                        result.AlbumCover = cover;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    else if (frameSize > 1)
                    {
                        byte encodingByte = buffer[dataOffset];
                        string textVal = DecodeId3Text(buffer, dataOffset + 1, frameSize - 1, encodingByte);

                        if (!string.IsNullOrWhiteSpace(textVal))
                        {
                            switch (frameId)
                            {
                                case "TIT2":
                                    result.Title = textVal;
                                    break;
                                case "TPE1":
                                    result.Artist = textVal;
                                    break;
                                case "TALB":
                                    result.Album = textVal;
                                    break;
                                case "TYER":
                                case "TDRC":
                                    if (int.TryParse(textVal.Length >= 4 ? textVal.Substring(0, 4) : textVal, out int yr))
                                        result.Year = yr;
                                    break;
                                case "TRCK":
                                    var trackPart = textVal.Split('/')[0];
                                    if (int.TryParse(trackPart, out int trk))
                                        result.TrackNumber = trk;
                                    break;
                                case "TCON":
                                    result.Genre = textVal;
                                    break;
                                case "TLEN":
                                    if (int.TryParse(textVal, out int ms) && ms > 0)
                                        result.DurationSeconds = ms / 1000;
                                    break;
                            }
                        }
                    }

                    pos += 10 + frameSize;
                }
            }
            catch
            {
                // При ошибке парсинга фреймов оставляем то, что успели распарсить
            }

            // Если название не прочиталось из тегов, берем имя файла
            if (string.IsNullOrEmpty(result.Title))
            {
                InferFromFileName(fileName, result);
            }
        }

        private static string DecodeId3Text(byte[] buffer, int offset, int length, byte encodingByte)
        {
            try
            {
                Encoding enc;
                switch (encodingByte)
                {
                    case 1:
                        enc = Encoding.Unicode; // UTF-16 with BOM
                        break;
                    case 2:
                        enc = Encoding.BigEndianUnicode; // UTF-16BE
                        break;
                    case 3:
                        enc = Encoding.UTF8;
                        break;
                    default:
                        enc = Encoding.Latin1; // ISO-8859-1
                        break;
                }

                string str = enc.GetString(buffer, offset, length);
                return str.Trim('\0', ' ', '\r', '\n');
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void InferFromFileName(string fileName, AudioMetadataResult result)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(nameWithoutExt) || 
                nameWithoutExt.StartsWith("allw", StringComparison.OrdinalIgnoreCase) ||
                nameWithoutExt.StartsWith("sync", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
            {
                // Не используем мусорные имена временных файлов Allway Sync
                return;
            }

            // Если теги в файле отсутствуют, используем чистое имя файла без расширения
            result.Title = nameWithoutExt;
            result.Artist = null;
            result.Album = null;
        }
    }

    public class AudioMetadataResult
    {
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public int? TrackNumber { get; set; }
        public int? DurationSeconds { get; set; }
        public int? Bitrate { get; set; }
        public string? AudioFormat { get; set; } // .mp3, .flac, .m4a
        public byte[]? HeaderCache { get; set; }
        public byte[]? AlbumCover { get; set; }
    }
}
