using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TelegramWebDAV.Models;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Парсер метаданных видео (длительность, ширина, высота) и генератор превью-кадров (thumbnails)
    /// с использованием чистого C# MP4 Box парсера, ATL.NET и Windows Shell IThumbnailCache.
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

            // Шаг 3: Если превью нет, извлекаем его через системный Windows Media Foundation (IMFSourceReader)
            if (result.Thumbnail == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractThumbnailViaMediaFoundation(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"Media Foundation превью ошибка '{filePath}': {ex.Message}");
                }
            }

            // Шаг 4: Безопасные значения по умолчанию для плеера Telegram, если метаданные не найдены
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

        #region Windows Media Foundation Interop

        private const uint MF_VERSION = 0x00020070;
        private const uint MFSTARTUP_NOSOCKET = 0x1;
        private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = -2; // 0xFFFFFFFE
        private const int MF_SOURCE_READER_FLAG_ENDOFSTREAM = 0x00000001;

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f827-4970-b477-5da46946468f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-2202217e0959");
        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType([Out] out IMFMediaType ppMFType);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
            [In] IntPtr pAttributes,
            [Out] out IMFSourceReader ppSourceReader);

        [ComImport]
        [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection([In] int dwStreamIndex, [Out, MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
            [PreserveSig] int SetStreamSelection([In] int dwStreamIndex, [In, MarshalAs(UnmanagedType.Bool)] bool fSelected);
            [PreserveSig] int GetNativeMediaType([In] int dwStreamIndex, [In] int dwMediaTypeIndex, [Out] out IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType([In] int dwStreamIndex, [Out] out IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType([In] int dwStreamIndex, [In] IntPtr pdwReserved, [In] IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, [In] ref PropVariant varPosition);
            [PreserveSig] int ReadSample([In] int dwStreamIndex, [In] int dwControlFlags, [Out] out int pdwActualStreamIndex, [Out] out int pdwStreamFlags, [Out] out long pllTimestamp, [Out] out IMFSample ppSample);
            [PreserveSig] int Flush([In] int dwStreamIndex);
            [PreserveSig] int GetServiceForStream([In] int dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute([In] int dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidAttribute, [Out] out PropVariant pvarAttribute);
        }

        [ComImport]
        [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out int pType);
            [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare([In] IntPtr pTheirs, [In] int MatchType, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint punValue);
            [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
            [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out double pfValue);
            [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out Guid pguidValue);
            [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcchLength);
            [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, [In] uint cchBufSize, [Out] out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, [Out] out uint pcchLength);
            [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] byte[] pBuf, [In] uint cbBufSize, [Out] out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppBuf, [Out] out uint pcbSize);
            [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
            [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value);
            [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
            [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] double fValue);
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
            [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] byte[] pBuf, [In] uint cbBufSize);
            [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount([Out] out uint pcItems);
            [PreserveSig] int GetItemByIndex([In] uint unIndex, [Out] out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems([In] IntPtr pDest);
            // IMFMediaType specific
            [PreserveSig] int GetMajorType([Out] out Guid pguidMajorType);
            [PreserveSig] int IsCompressedFormat([Out, MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
            [PreserveSig] int IsEqual([In] IMFMediaType pIMediaType, [Out] out uint pdwFlags);
            [PreserveSig] int GetRepresentation([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidRepresentation, [Out] out IntPtr ppvRepresentation);
            [PreserveSig] int FreeRepresentation([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidRepresentation, [In] IntPtr pvRepresentation);
        }

        [ComImport]
        [Guid("c40a0074-b93a-4d80-ae8c-5a1c634f58e4")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out int pType);
            [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare([In] IntPtr pTheirs, [In] int MatchType, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint punValue);
            [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
            [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out double pfValue);
            [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out Guid pguidValue);
            [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcchLength);
            [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, [In] uint cchBufSize, [Out] out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, [Out] out uint pcchLength);
            [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] byte[] pBuf, [In] uint cbBufSize, [Out] out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppBuf, [Out] out uint pcbSize);
            [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
            [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value);
            [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
            [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] double fValue);
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
            [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] byte[] pBuf, [In] uint cbBufSize);
            [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount([Out] out uint pcItems);
            [PreserveSig] int GetItemByIndex([In] uint unIndex, [Out] out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems([In] IntPtr pDest);
            // IMFSample specific
            [PreserveSig] int GetSampleFlags([Out] out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags([In] uint dwSampleFlags);
            [PreserveSig] int GetSampleTime([Out] out long phnsSampleTime);
            [PreserveSig] int SetSampleTime([In] long hnsSampleTime);
            [PreserveSig] int GetSampleDuration([Out] out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration([In] long hnsSampleDuration);
            [PreserveSig] int GetBufferCount([Out] out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex([In] uint dwIndex, [Out] out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer([Out] out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer([In] IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex([In] uint dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength([Out] out uint pcbTotalLength);
            [PreserveSig] int CopyToBuffer([In] IMFMediaBuffer pBuffer);
        }

        [ComImport]
        [Guid("045db593-0721-4d87-bb4e-67832e01e0d8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock([Out] out IntPtr ppbBuffer, [Out] out uint pcbMaxLength, [Out] out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength([Out] out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength([In] uint cbCurrentLength);
            [PreserveSig] int GetMaxLength([Out] out uint pcbMaxLength);
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public long hVal;
        }

        private static void ExtractThumbnailViaMediaFoundation(string filePath, VideoMetadataResult result)
        {
            IMFSourceReader? reader = null;
            IMFMediaType? mediaType = null;
            IMFMediaType? currentType = null;
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            IntPtr pBuffer = IntPtr.Zero;

            int hrMf = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
            if (hrMf != 0)
            {
                AppLogger.Warn("VideoMetadataExtractor", $"MFStartup вернул hr = 0x{hrMf:X8}");
                return;
            }

            try
            {
                int hrReader = MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out reader);
                if (hrReader != 0 || reader == null)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"MFCreateSourceReaderFromURL вернул hr = 0x{hrReader:X8} для '{Path.GetFileName(filePath)}'");
                    return;
                }

                // Создаем целевой медиатип: RGB32
                int hrCreateType = MFCreateMediaType(out mediaType);
                if (hrCreateType != 0 || mediaType == null)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"MFCreateMediaType вернул hr = 0x{hrCreateType:X8}");
                    return;
                }

                mediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                mediaType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);

                int hrSetType = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, mediaType);
                if (hrSetType != 0)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"SetCurrentMediaType(RGB32) вернул hr = 0x{hrSetType:X8}");
                    return;
                }

                // Читаем фактический медиатип, чтобы узнать разрешение кадра
                int hrGetType = reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, out currentType);
                if (hrGetType != 0 || currentType == null)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"GetCurrentMediaType вернул hr = 0x{hrGetType:X8}");
                    return;
                }

                int hrSize = currentType.GetUINT64(MF_MT_FRAME_SIZE, out ulong packedSize);
                int frameWidth = hrSize == 0 ? (int)(packedSize >> 32) : result.Width;
                int frameHeight = hrSize == 0 ? (int)(packedSize & 0xFFFFFFFF) : result.Height;

                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    frameWidth = result.Width > 0 ? result.Width : 640;
                    frameHeight = result.Height > 0 ? result.Height : 360;
                }

                // Читаем первый видеокадр
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    int hrSample = reader.ReadSample(
                        MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                        0,
                        out int streamIndex,
                        out int streamFlags,
                        out long timestamp,
                        out sample);

                    if (hrSample != 0)
                    {
                        AppLogger.Warn("VideoMetadataExtractor", $"ReadSample вернул hr = 0x{hrSample:X8}");
                        break;
                    }

                    if ((streamFlags & MF_SOURCE_READER_FLAG_ENDOFSTREAM) != 0)
                    {
                        break;
                    }

                    if (sample != null)
                    {
                        break;
                    }
                }

                if (sample == null)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"Media Foundation не вернул семпл кадра для '{Path.GetFileName(filePath)}'");
                    return;
                }

                int hrContig = sample.ConvertToContiguousBuffer(out buffer);
                if (hrContig != 0 || buffer == null)
                {
                    sample.GetBufferByIndex(0, out buffer);
                }

                if (buffer != null)
                {
                    int hrLock = buffer.Lock(out pBuffer, out uint maxLength, out uint curLength);
                    if (hrLock == 0 && pBuffer != IntPtr.Zero)
                    {
                        try
                        {
                            int stride = frameWidth * 4;
                            using var bmp = new System.Drawing.Bitmap(
                                frameWidth,
                                frameHeight,
                                stride,
                                System.Drawing.Imaging.PixelFormat.Format32bppRgb,
                                pBuffer);

                            // В Windows DIB буферы растра часто идут снизу вверх (bottom-up),
                            // поэтому проверяем и переворачиваем изображение
                            bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);

                            result.Thumbnail = ResizeBitmapToTelegramJpeg(bmp, 320, 320);
                            if (result.Thumbnail != null)
                            {
                                AppLogger.Info("VideoMetadataExtractor", $"Успешно сгенерирован стоп-кадр через Windows Media Foundation ({result.Thumbnail.Length} байт, {frameWidth}x{frameHeight})");

                                try
                                {
                                    string thumbPath = Path.Combine(Path.GetDirectoryName(filePath) ?? Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(filePath)}_preview.jpg");
                                    File.WriteAllBytes(thumbPath, result.Thumbnail);
                                    AppLogger.Info("VideoMetadataExtractor", $"Превью сохранено на диск: {thumbPath}");
                                }
                                catch (Exception saveEx)
                                {
                                    AppLogger.Debug("VideoMetadataExtractor", $"Не удалось сохранить превью на диск: {saveEx.Message}");
                                }
                            }
                        }
                        finally
                        {
                            buffer.Unlock();
                        }
                    }
                    else
                    {
                        AppLogger.Warn("VideoMetadataExtractor", $"IMFMediaBuffer.Lock вернул hr = 0x{hrLock:X8}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("VideoMetadataExtractor", $"ExtractThumbnailViaMediaFoundation ошибка: {ex.Message}");
            }
            finally
            {
                if (buffer != null && Marshal.IsComObject(buffer)) Marshal.ReleaseComObject(buffer);
                if (sample != null && Marshal.IsComObject(sample)) Marshal.ReleaseComObject(sample);
                if (currentType != null && Marshal.IsComObject(currentType)) Marshal.ReleaseComObject(currentType);
                if (mediaType != null && Marshal.IsComObject(mediaType)) Marshal.ReleaseComObject(mediaType);
                if (reader != null && Marshal.IsComObject(reader)) Marshal.ReleaseComObject(reader);

                try { MFShutdown(); } catch { }
            }
        }

        #endregion

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
