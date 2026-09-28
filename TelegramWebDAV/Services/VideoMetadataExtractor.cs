using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TelegramWebDAV.Models;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Парсер метаданных видео (длительность, ширина, высота) и генератор превью-кадров (thumbnails)
    /// с использованием нативного Windows Media Foundation (IMFSourceReader), Shell API, ATL.NET и полного MP4 Box парсера.
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

            // Шаг 1: Чтение встроенных тегов и вшитых постеров через ATL.NET
            try
            {
                var track = new ATL.Track(filePath);
                if (track.Duration > 0)
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

            // Шаг 2: Windows Media Foundation (нативное извлечение стоп-кадра и точных размеров)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractWithMediaFoundation(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"MediaFoundation ошибка для '{filePath}': {ex.Message}");
                }
            }

            // Шаг 3: Полный парсинг MP4/MOV структуры по всему файлу (если ширина/высота не были прочитаны)
            if (result.Width <= 0 || result.Height <= 0 || result.DurationSeconds <= 0)
            {
                try
                {
                    ParseMp4Full(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"MP4 parser ошибка '{filePath}': {ex.Message}");
                }
            }

            // Шаг 4: Безопасные значения по умолчанию для плеера Telegram
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

        #region Windows Media Foundation Engine

        private const uint MF_VERSION = 0x00020070;
        private const uint MFSTARTUP_NOSOCKET = 0x1;
        private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
        private const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f827-4970-b450-482a4d455d3f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_PD_DURATION = new Guid("6c9e0f0f-e4c8-4158-b117-1bbda606594e");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IntPtr ppMFType);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
            [In] IntPtr pAttributes,
            [Out] out IntPtr ppSourceReader);

        [ComImport]
        [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint dwStreamIndex, out bool pfSelected);
            [PreserveSig] int SetStreamSelection(uint dwStreamIndex, bool fSelected);
            [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IntPtr ppMediaType);
            [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IntPtr ppMediaType);
            [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IntPtr pMediaType);
            [PreserveSig] int SetStreamPosition(ref Guid pguidTimeFormat, ref PROPVARIANT pvarStartPosition);
            [PreserveSig] int ReadSample(
                uint dwStreamIndex,
                uint dwControlFlags,
                out uint pdwActualStreamIndex,
                out uint pdwStreamFlags,
                out long pllTimestamp,
                out IntPtr ppSample);
            [PreserveSig] int Flush(uint dwStreamIndex);
            [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid pguidService, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, ref Guid pguidAttribute, out PROPVARIANT pvarAttribute);
        }

        [ComImport]
        [Guid("2CD2D921-C447-44A7-A13C-4ADAB5110F12")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IntPtr pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IntPtr pDest);
        }

        [ComImport]
        [Guid("c40a0074-b93a-4d80-ae8c-5a1c634f58e4")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // Упрощенная сигнатура для взятия буфера
            [PreserveSig] int GetItem(); // dummy
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32();
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32();
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID();
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();

            // IMFSample methods
            [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(uint dwIndex, out IntPtr ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IntPtr ppBuffer);
            [PreserveSig] int AddBuffer(IntPtr pBuffer);
            [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IntPtr pBuffer);
        }

        [ComImport]
        [Guid("045db593-0721-4d53-bc4e-31f24093f40a")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
            [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
        }

        private static void ExtractWithMediaFoundation(string filePath, VideoMetadataResult result)
        {
            var thread = new Thread(() =>
            {
                int hrInit = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
                if (hrInit != 0) return;

                IntPtr pReader = IntPtr.Zero;
                IntPtr pMediaType = IntPtr.Zero;
                try
                {
                    int hr = MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out pReader);
                    if (hr != 0 || pReader == IntPtr.Zero)
                    {
                        AppLogger.Debug("VideoMetadataExtractor", $"MFCreateSourceReaderFromURL вернул 0x{hr:X8}");
                        return;
                    }

                    var reader = (IMFSourceReader)Marshal.GetObjectForIUnknown(pReader);

                    // 1. Длительность из атрибутов презентации
                    if (result.DurationSeconds <= 0)
                    {
                        var keyDuration = MF_PD_DURATION;
                        if (reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, ref keyDuration, out var varDuration) == 0)
                        {
                            ulong dur100ns = varDuration.ulVal;
                            if (dur100ns > 0)
                            {
                                result.DurationSeconds = (int)(dur100ns / 10000000UL);
                            }
                            PropVariantClear(ref varDuration);
                        }
                    }

                    // 2. Читаем исходный формат видеопотока (габариты кадра)
                    if (reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, out IntPtr pNativeType) == 0 && pNativeType != IntPtr.Zero)
                    {
                        try
                        {
                            var attrs = (IMFAttributes)Marshal.GetObjectForIUnknown(pNativeType);
                            var keyFrameSize = MF_MT_FRAME_SIZE;
                            if (attrs.GetUINT64(ref keyFrameSize, out ulong sizeVal) == 0 && sizeVal > 0)
                            {
                                int w = (int)(sizeVal >> 32);
                                int h = (int)(sizeVal & 0xFFFFFFFF);
                                if (w > 0 && h > 0)
                                {
                                    result.Width = w;
                                    result.Height = h;
                                    AppLogger.Info("VideoMetadataExtractor", $"MF исходные габариты: {w}x{h}");
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(pNativeType);
                        }
                    }

                    // 3. Если превью ещё нет, настраиваем ридер на декодирование первого кадра в RGB32
                    if (result.Thumbnail == null)
                    {
                        if (MFCreateMediaType(out pMediaType) == 0 && pMediaType != IntPtr.Zero)
                        {
                            var mediaType = (IMFAttributes)Marshal.GetObjectForIUnknown(pMediaType);
                            var keyMajor = MF_MT_MAJOR_TYPE;
                            var keySub = MF_MT_SUBTYPE;
                            var valMajor = MFMediaType_Video;
                            var valSub = MFVideoFormat_RGB32;

                            mediaType.SetGUID(ref keyMajor, ref valMajor);
                            mediaType.SetGUID(ref keySub, ref valSub);

                            int hrSet = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, pMediaType);
                            if (hrSet == 0)
                            {
                                // Читаем первый видеокадр
                                int hrRead = reader.ReadSample(
                                    MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                                    0,
                                    out uint streamIdx,
                                    out uint flags,
                                    out long timestamp,
                                    out IntPtr pSample
                                );

                                if (hrRead == 0 && pSample != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var sample = (IMFSample)Marshal.GetObjectForIUnknown(pSample);
                                        if (sample.ConvertToContiguousBuffer(out IntPtr pBuffer) == 0 && pBuffer != IntPtr.Zero)
                                        {
                                            try
                                            {
                                                var mediaBuffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(pBuffer);
                                                if (mediaBuffer.Lock(out IntPtr pData, out uint maxLen, out uint curLen) == 0)
                                                {
                                                    try
                                                    {
                                                        int w = result.Width > 0 ? result.Width : 640;
                                                        int h = result.Height > 0 ? result.Height : 360;

                                                        if (curLen >= w * h * 4)
                                                        {
                                                            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                                                            var bmpData = bmp.LockBits(
                                                                new System.Drawing.Rectangle(0, 0, w, h),
                                                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                                                System.Drawing.Imaging.PixelFormat.Format32bppRgb
                                                            );

                                                            // MF RGB32 кадры обычно идут снизу вверх (bottom-up), поэтому копируем построчно
                                                            int stride = bmpData.Stride;
                                                            int rowBytes = w * 4;
                                                            for (int y = 0; y < h; y++)
                                                            {
                                                                int srcY = h - 1 - y; // инвертируем строки
                                                                IntPtr srcRow = IntPtr.Add(pData, srcY * rowBytes);
                                                                IntPtr dstRow = IntPtr.Add(bmpData.Scan0, y * stride);
                                                                CopyMemory(dstRow, srcRow, (uint)rowBytes);
                                                            }

                                                            bmp.UnlockBits(bmpData);
                                                            result.Thumbnail = ResizeBitmapToTelegramJpeg(bmp, 320, 320);
                                                            AppLogger.Info("VideoMetadataExtractor", $"Успешно сгенерирован стоп-кадр через Windows Media Foundation ({result.Thumbnail?.Length} байт)");
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        mediaBuffer.Unlock();
                                                    }
                                                }
                                            }
                                            finally
                                            {
                                                Marshal.Release(pBuffer);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.Release(pSample);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (pMediaType != IntPtr.Zero) Marshal.Release(pMediaType);
                    if (pReader != IntPtr.Zero) Marshal.Release(pReader);
                    MFShutdown();
                }
            });

            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(5000))
            {
                AppLogger.Warn("VideoMetadataExtractor", $"Таймаут Media Foundation для '{Path.GetFileName(filePath)}'");
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        #endregion

        #region Shell Property Store Structs & Ole32

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public byte bVal;
            [FieldOffset(8)] public sbyte cVal;
            [FieldOffset(8)] public ushort uiVal;
            [FieldOffset(8)] public short iVal;
            [FieldOffset(8)] public uint uintVal;
            [FieldOffset(8)] public int intVal;
            [FieldOffset(8)] public ulong ulVal;
            [FieldOffset(8)] public long lVal;
            [FieldOffset(8)] public float fltVal;
            [FieldOffset(8)] public double dblVal;
            [FieldOffset(8)] public IntPtr ptrVal;
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
