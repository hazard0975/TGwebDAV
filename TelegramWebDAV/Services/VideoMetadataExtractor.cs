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

            // Шаг 4: Если Media Foundation не смог декодировать кодек (например, AV1, MKV, VP9), извлекаем кадр через Shell Thumbnail Provider (K-Lite / Icaros / Windows Shell)
            if (result.Thumbnail == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractThumbnailViaShellItem(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"Shell Thumbnail (K-Lite/Icaros) ошибка '{filePath}': {ex.Message}");
                }
            }

            // Шаг 5: Безопасные значения по умолчанию для плеера Telegram, если метаданные не найдены
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
        private const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE); // -2
        private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC); // -4
        private const int MF_SOURCE_READER_FLAG_ENDOFSTREAM = 0x00000001;

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f827-4970-b477-5da46946468f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-2202217e0959");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644fd020-497d-4e44-ab84-dc729a4e4dc1");
        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_NV12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_YUY2 = new Guid("32595559-0000-0010-8000-00aa00389b71");
        private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFGetStrideForBitmapInfoHeader(uint format, int dwWidth, out int pStride);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType([Out] out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes([Out] out IntPtr ppMFAttributes, [In] uint cInitialSize);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
            [In] IntPtr pAttributes,
            [Out] out IMFSourceReader ppSourceReader);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AttributesSetUINT32Delegate(IntPtr pThis, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, uint unValue);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConvertToContiguousBufferDelegate(IntPtr pThis, out IntPtr ppBuffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MediaBufferLockDelegate(IntPtr pThis, out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MediaBufferUnlockDelegate(IntPtr pThis);

        [ComImport]
        [Guid("2cd2d921-b4e6-4a3b-9915-88a391e9e045")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
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
        }

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
            [PreserveSig] int ReadSample([In] int dwStreamIndex, [In] int dwControlFlags, [Out] out int pdwActualStreamIndex, [Out] out int pdwStreamFlags, [Out] out long pllTimestamp, [Out] out IntPtr ppSample);
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
            IntPtr pAttributes = IntPtr.Zero;
            IMFSourceReader? reader = null;
            IMFMediaType? mediaType = null;
            IMFMediaType? currentType = null;
            IntPtr pSample = IntPtr.Zero;
            IntPtr pBuffer = IntPtr.Zero;

            int hrMf = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
            if (hrMf != 0)
            {
                AppLogger.Warn("VideoMetadataExtractor", $"MFStartup вернул hr = 0x{hrMf:X8}");
                return;
            }

            try
            {
                // Включаем встроенный видеопроцессор преобразования форматов цвета (Windows Video Processor)
                int hrAttr = MFCreateAttributes(out pAttributes, 1);
                if (hrAttr == 0 && pAttributes != IntPtr.Zero)
                {
                    try
                    {
                        var attrObj = Marshal.GetObjectForIUnknown(pAttributes) as IMFAttributes;
                        if (attrObj != null)
                        {
                            int hrSetAttr = attrObj.SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                            if (hrSetAttr != 0)
                            {
                                AppLogger.Debug("VideoMetadataExtractor", $"IMFAttributes.SetUINT32 вернул hr = 0x{hrSetAttr:X8}");
                            }
                        }
                    }
                    catch (Exception exAttr)
                    {
                        AppLogger.Debug("VideoMetadataExtractor", $"Не удалось установить MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING: {exAttr.Message}");
                    }
                }

                int hrReader = MFCreateSourceReaderFromURL(filePath, pAttributes, out reader);
                if (hrReader != 0 || reader == null)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"MFCreateSourceReaderFromURL вернул hr = 0x{hrReader:X8} для '{Path.GetFileName(filePath)}'");
                    return;
                }

                // 1. Включаем первый видеопоток
                reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

                // 2. Ищем явный индекс видеопотока (0..5)
                int videoStreamIndex = -1;
                for (int s = 0; s < 6; s++)
                {
                    int hrNat = reader.GetNativeMediaType(s, 0, out IMFMediaType natType);
                    if (hrNat == 0 && natType != null)
                    {
                        if (natType.GetGUID(MF_MT_MAJOR_TYPE, out Guid major) == 0 && major == MFMediaType_Video)
                        {
                            videoStreamIndex = s;
                            Marshal.ReleaseComObject(natType);
                            break;
                        }
                        Marshal.ReleaseComObject(natType);
                    }
                }

                int targetStreamIndex = videoStreamIndex >= 0 ? videoStreamIndex : 0;
                reader.SetStreamSelection(targetStreamIndex, true);

                // Пробуем целевые медиатипы:
                // 1. NV12 — нативный аппаратный формат декодеров GPU (DirectX Video Acceleration)
                // 2. RGB32 — формат несжатого кадра
                // 3. YUY2 — запасной формат
                Guid chosenSubtype = MFVideoFormat_NV12;
                bool isFormatSet = false;

                int[] streamIndices = videoStreamIndex >= 0 
                    ? (videoStreamIndex == 0 ? new[] { 0, 1 } : new[] { videoStreamIndex, 0, 1 })
                    : new[] { 0, 1 };

                Guid[] candidateSubtypes = new[] { MFVideoFormat_NV12, MFVideoFormat_RGB32, MFVideoFormat_YUY2 };

                foreach (var sIdx in streamIndices)
                {
                    reader.SetStreamSelection(sIdx, true);

                    // Шаг 1: Перебираем все нативные выходные медиатипы, предлагаемые декодером для данного потока
                    for (int m = 0; m < 50; m++)
                    {
                        IMFMediaType? offeredType = null;
                        int hrOffered = reader.GetNativeMediaType(sIdx, m, out offeredType);
                        if (hrOffered != 0 || offeredType == null) break;

                        try
                        {
                            if (offeredType.GetGUID(MF_MT_SUBTYPE, out Guid offeredSubtype) == 0)
                            {
                                foreach (var prefSubtype in candidateSubtypes)
                                {
                                    if (offeredSubtype == prefSubtype)
                                    {
                                        int hrSetOffered = reader.SetCurrentMediaType(sIdx, IntPtr.Zero, offeredType);
                                        if (hrSetOffered == 0)
                                        {
                                            chosenSubtype = prefSubtype;
                                            targetStreamIndex = sIdx;
                                            isFormatSet = true;
                                            AppLogger.Debug("VideoMetadataExtractor", $"Согласован нативный медиатип декодера: {prefSubtype} (stream={sIdx}, typeIndex={m})");
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(offeredType);
                        }

                        if (isFormatSet) break;
                    }

                    if (isFormatSet) break;

                    // Шаг 2: Если прямой нативный тип не выбран, пробуем чистый медиатип
                    foreach (var subtype in candidateSubtypes)
                    {
                        int hrCreate = MFCreateMediaType(out mediaType);
                        if (hrCreate == 0 && mediaType != null)
                        {
                            mediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                            mediaType.SetGUID(MF_MT_SUBTYPE, subtype);

                            int hrSetType = reader.SetCurrentMediaType(sIdx, IntPtr.Zero, mediaType);
                            if (hrSetType == 0)
                            {
                                chosenSubtype = subtype;
                                targetStreamIndex = sIdx;
                                isFormatSet = true;
                                break;
                            }
                            else
                            {
                                AppLogger.Debug("VideoMetadataExtractor", $"SetCurrentMediaType(clean, stream={sIdx}, subtype={subtype}) вернул hr = 0x{hrSetType:X8}");
                            }

                            Marshal.ReleaseComObject(mediaType);
                            mediaType = null;
                        }

                        // Шаг 3: Пробуем базовый нативный тип
                        IMFMediaType? baseType = null;
                        int hrGetNative = reader.GetNativeMediaType(sIdx, 0, out baseType);
                        if (hrGetNative == 0 && baseType != null)
                        {
                            baseType.SetGUID(MF_MT_SUBTYPE, subtype);
                            int hrSetNative = reader.SetCurrentMediaType(sIdx, IntPtr.Zero, baseType);
                            if (hrSetNative == 0)
                            {
                                chosenSubtype = subtype;
                                targetStreamIndex = sIdx;
                                isFormatSet = true;
                                Marshal.ReleaseComObject(baseType);
                                break;
                            }
                            else
                            {
                                AppLogger.Debug("VideoMetadataExtractor", $"SetCurrentMediaType(native, stream={sIdx}, subtype={subtype}) вернул hr = 0x{hrSetNative:X8}");
                            }
                            Marshal.ReleaseComObject(baseType);
                        }
                    }

                    if (isFormatSet) break;
                }

                if (!isFormatSet)
                {
                    // Если явный тип не согласован, пробуем использовать текущий тип декодера
                    int hrCurr = reader.GetCurrentMediaType(targetStreamIndex, out currentType);
                    if (hrCurr == 0 && currentType != null)
                    {
                        if (currentType.GetGUID(MF_MT_SUBTYPE, out Guid sub) == 0)
                        {
                            // Если текущий подтип является несжатым видео
                            if (sub == MFVideoFormat_NV12 || sub == MFVideoFormat_RGB32 || sub == MFVideoFormat_YUY2)
                            {
                                chosenSubtype = sub;
                                isFormatSet = true;
                                AppLogger.Debug("VideoMetadataExtractor", $"Используется текущий медиатип ридера: {chosenSubtype}");
                            }
                        }
                    }
                }

                if (!isFormatSet)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"Не удалось согласовать видеоформат (RGB32/NV12/YUY2) для '{Path.GetFileName(filePath)}'");
                    return;
                }

                // Читаем фактический медиатип, чтобы узнать разрешение кадра
                if (currentType == null)
                {
                    int hrGetType = reader.GetCurrentMediaType(targetStreamIndex, out currentType);
                    if (hrGetType != 0 || currentType == null)
                    {
                        AppLogger.Warn("VideoMetadataExtractor", $"GetCurrentMediaType вернул hr = 0x{hrGetType:X8}");
                        return;
                    }
                }

                int hrSize = currentType.GetUINT64(MF_MT_FRAME_SIZE, out ulong packedSize);
                int frameWidth = hrSize == 0 ? (int)(packedSize >> 32) : result.Width;
                int frameHeight = hrSize == 0 ? (int)(packedSize & 0xFFFFFFFF) : result.Height;

                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    frameWidth = result.Width > 0 ? result.Width : 640;
                    frameHeight = result.Height > 0 ? result.Height : 360;
                }

                // Перематываем на 15 секунд вперед (150 000 000 * 100ns), чтобы взять активный кадр ролика вместо заставки / черного экрана
                try
                {
                    long targetSeekTime = 150_000_000L; // 15.0 сек
                    if (result.DurationSeconds > 0 && result.DurationSeconds < 20)
                    {
                        // Если видео короче 20 сек, перематываем на середину видео
                        targetSeekTime = (long)(result.DurationSeconds / 2.0 * 10_000_000L);
                    }

                    var varPos = new PropVariant { vt = 20 /* VT_I8 */, hVal = targetSeekTime };
                    int hrPos = reader.SetCurrentPosition(Guid.Empty, ref varPos);
                    if (hrPos != 0)
                    {
                        // Если видео короткое, пробуем перемотать на 1 секунду
                        varPos.hVal = 10_000_000L;
                        reader.SetCurrentPosition(Guid.Empty, ref varPos);
                    }
                }
                catch { }

                // Читаем видеокадр
                for (int attempt = 0; attempt < 25; attempt++)
                {
                    int hrSample = reader.ReadSample(
                        targetStreamIndex,
                        0,
                        out int streamIndex,
                        out int streamFlags,
                        out long timestamp,
                        out pSample);

                    if (hrSample != 0)
                    {
                        AppLogger.Warn("VideoMetadataExtractor", $"ReadSample вернул hr = 0x{hrSample:X8}");
                        break;
                    }

                    if ((streamFlags & MF_SOURCE_READER_FLAG_ENDOFSTREAM) != 0)
                    {
                        break;
                    }

                    if (pSample != IntPtr.Zero)
                    {
                        break;
                    }
                }

                IntPtr pMediaBuffer = IntPtr.Zero;
                if (pSample != IntPtr.Zero)
                {
                    try
                    {
                        // В COM-интерфейсе IMFSample (наследует IMFAttributes: 3 IUnknown + 30 IMFAttributes = 33):
                        // ConvertToContiguousBuffer находится по индексу 41 в VTable
                        IntPtr sampleVTable = Marshal.ReadIntPtr(pSample);
                        IntPtr pConvertToContiguousBuffer = Marshal.ReadIntPtr(sampleVTable, 41 * IntPtr.Size);
                        var convertFunc = Marshal.GetDelegateForFunctionPointer<ConvertToContiguousBufferDelegate>(pConvertToContiguousBuffer);
                        int hrBuf = convertFunc(pSample, out pMediaBuffer);

                        if (hrBuf != 0 || pMediaBuffer == IntPtr.Zero)
                        {
                            AppLogger.Debug("VideoMetadataExtractor", $"ConvertToContiguousBuffer вернул hr = 0x{hrBuf:X8}");
                        }
                    }
                    catch (Exception exBuf)
                    {
                        AppLogger.Debug("VideoMetadataExtractor", $"Ошибка вызова ConvertToContiguousBuffer: {exBuf.Message}");
                    }
                }

                if (pMediaBuffer == IntPtr.Zero)
                {
                    AppLogger.Warn("VideoMetadataExtractor", $"Media Foundation не вернул буфер кадра для '{Path.GetFileName(filePath)}'");
                    return;
                }

                try
                {
                    // В COM-интерфейсе IMFMediaBuffer (наследует IUnknown: 3 метода):
                    // Lock находится по индексу 3 в VTable, Unlock по индексу 4
                    IntPtr bufferVTable = Marshal.ReadIntPtr(pMediaBuffer);
                    IntPtr pLock = Marshal.ReadIntPtr(bufferVTable, 3 * IntPtr.Size);
                    IntPtr pUnlock = Marshal.ReadIntPtr(bufferVTable, 4 * IntPtr.Size);
                    var lockFunc = Marshal.GetDelegateForFunctionPointer<MediaBufferLockDelegate>(pLock);
                    var unlockFunc = Marshal.GetDelegateForFunctionPointer<MediaBufferUnlockDelegate>(pUnlock);

                    int hrLock = lockFunc(pMediaBuffer, out pBuffer, out uint maxLength, out uint curLength);
                    if (hrLock == 0 && pBuffer != IntPtr.Zero)
                    {
                        try
                        {
                            // Определяем точный шаг строки (Stride) видеокарты
                            int stride = 0;
                            if (currentType != null)
                            {
                                currentType.GetUINT32(MF_MT_DEFAULT_STRIDE, out uint uStride);
                                stride = (int)uStride;
                            }

                            if (chosenSubtype == MFVideoFormat_NV12)
                            {
                                // Аппаратный декодер NV12 всегда выравнивает ширину строки минимум по 16 байтам (854 -> 864, 640 -> 640)
                                int aligned16 = (frameWidth + 15) & ~15;
                                if (stride <= 0 || stride < frameWidth || (stride == frameWidth && (frameWidth & 15) != 0))
                                {
                                    stride = aligned16;
                                }
                            }
                            else if (chosenSubtype == MFVideoFormat_RGB32)
                            {
                                if (stride == 0) stride = frameWidth * 4;
                            }
                            else // YUY2
                            {
                                if (stride <= 0) stride = (frameWidth * 2 + 3) & ~3;
                            }

                            using System.Drawing.Bitmap? bmp = CreateBitmapFromBuffer(pBuffer, (int)curLength, frameWidth, frameHeight, stride, chosenSubtype);
                            if (bmp != null)
                            {
                                result.Thumbnail = ResizeBitmapToTelegramJpeg(bmp, 320, 320);
                                if (result.Thumbnail != null)
                                {
                                    AppLogger.Info("VideoMetadataExtractor", $"Успешно сгенерирован стоп-кадр через Windows Media Foundation ({result.Thumbnail.Length} байт, {frameWidth}x{frameHeight}, stride={stride}, формат: {(chosenSubtype == MFVideoFormat_RGB32 ? "RGB32" : chosenSubtype == MFVideoFormat_NV12 ? "NV12" : "YUY2")})");

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
                        }
                        finally
                        {
                            unlockFunc(pMediaBuffer);
                        }
                    }
                    else
                    {
                        AppLogger.Warn("VideoMetadataExtractor", $"IMFMediaBuffer.Lock вернул hr = 0x{hrLock:X8}");
                    }
                }
                finally
                {
                    Marshal.Release(pMediaBuffer);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("VideoMetadataExtractor", $"ExtractThumbnailViaMediaFoundation ошибка: {ex}");
            }
            finally
            {
                if (pSample != IntPtr.Zero) Marshal.Release(pSample);
                if (currentType != null && Marshal.IsComObject(currentType)) Marshal.ReleaseComObject(currentType);
                if (mediaType != null && Marshal.IsComObject(mediaType)) Marshal.ReleaseComObject(mediaType);
                if (reader != null && Marshal.IsComObject(reader)) Marshal.ReleaseComObject(reader);
                if (pAttributes != IntPtr.Zero) Marshal.Release(pAttributes);

                try { MFShutdown(); } catch { }
            }
        }

        private static unsafe System.Drawing.Bitmap? CreateBitmapFromBuffer(IntPtr pBuffer, int length, int width, int height, int srcStride, Guid subtype)
        {
            if (pBuffer == IntPtr.Zero || width <= 0 || height <= 0) return null;

            if (subtype == MFVideoFormat_RGB32)
            {
                int absStride = Math.Abs(srcStride > 0 ? srcStride : width * 4);
                var bmp = new System.Drawing.Bitmap(
                    width,
                    height,
                    absStride,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb,
                    pBuffer);
                // В Media Foundation отрицательный шаг означает bottom-up (DIB), положительный — top-down
                if (srcStride < 0)
                {
                    bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
                }
                return bmp;
            }

            if (subtype == MFVideoFormat_NV12)
            {
                // NV12: Y-плоскость (stride * sliceHeight байт), затем UV-плоскость (stride * sliceHeight / 2 байт)
                int stride = srcStride > 0 ? srcStride : ((width + 15) & ~15);
                int sliceHeight = height;
                if (length > 0 && stride > 0)
                {
                    int derivedSlice = (int)(length / (stride * 1.5));
                    if (derivedSlice >= height)
                    {
                        sliceHeight = derivedSlice;
                    }
                }

                int expectedLen = stride * height * 3 / 2;
                if (length < expectedLen) return null;

                var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bmpData = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                try
                {
                    byte* pSrc = (byte*)pBuffer.ToPointer();
                    byte* pY = pSrc;
                    byte* pUV = pSrc + (stride * sliceHeight);
                    byte* pDst = (byte*)bmpData.Scan0.ToPointer();
                    int dstStride = bmpData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        byte* rowDst = pDst + (y * dstStride);
                        byte* rowY = pY + (y * stride);
                        byte* rowUV = pUV + ((y / 2) * stride);

                        for (int x = 0; x < width; x++)
                        {
                            int yVal = rowY[x];
                            int uvIdx = (x & ~1);
                            int uVal = rowUV[uvIdx] - 128;
                            int vVal = rowUV[uvIdx + 1] - 128;

                            int r = yVal + (int)(1.402f * vVal);
                            int g = yVal - (int)(0.344136f * uVal + 0.714136f * vVal);
                            int b = yVal + (int)(1.772f * uVal);

                            rowDst[x * 4 + 0] = (byte)Math.Clamp(b, 0, 255); // Blue
                            rowDst[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255); // Green
                            rowDst[x * 4 + 2] = (byte)Math.Clamp(r, 0, 255); // Red
                            rowDst[x * 4 + 3] = 255;                         // Alpha
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                return bmp;
            }

            if (subtype == MFVideoFormat_YUY2)
            {
                // YUY2: каждые 4 байта содержат 2 пикселя (Y0, U0, Y1, V0)
                int stride = srcStride > 0 ? srcStride : width * 2;
                int expectedLen = stride * height;
                if (length < expectedLen) return null;

                var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bmpData = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                try
                {
                    byte* pSrc = (byte*)pBuffer.ToPointer();
                    byte* pDst = (byte*)bmpData.Scan0.ToPointer();
                    int dstStride = bmpData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        byte* rowSrc = pSrc + (y * stride);
                        byte* rowDst = pDst + (y * dstStride);

                        for (int x = 0; x < width; x += 2)
                        {
                            int y0 = rowSrc[x * 2 + 0];
                            int u0 = rowSrc[x * 2 + 1] - 128;
                            int y1 = rowSrc[x * 2 + 2];
                            int v0 = rowSrc[x * 2 + 3] - 128;

                            // Pixel 1
                            int r0 = y0 + (int)(1.402f * v0);
                            int g0 = y0 - (int)(0.344136f * u0 + 0.714136f * v0);
                            int b0 = y0 + (int)(1.772f * u0);

                            rowDst[x * 4 + 0] = (byte)Math.Clamp(b0, 0, 255);
                            rowDst[x * 4 + 1] = (byte)Math.Clamp(g0, 0, 255);
                            rowDst[x * 4 + 2] = (byte)Math.Clamp(r0, 0, 255);
                            rowDst[x * 4 + 3] = 255;

                            // Pixel 2
                            if (x + 1 < width)
                            {
                                int r1 = y1 + (int)(1.402f * v0);
                                int g1 = y1 - (int)(0.344136f * u0 + 0.714136f * v0);
                                int b1 = y1 + (int)(1.772f * u0);

                                rowDst[(x + 1) * 4 + 0] = (byte)Math.Clamp(b1, 0, 255);
                                rowDst[(x + 1) * 4 + 1] = (byte)Math.Clamp(g1, 0, 255);
                                rowDst[(x + 1) * 4 + 2] = (byte)Math.Clamp(r1, 0, 255);
                                rowDst[(x + 1) * 4 + 3] = 255;
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                return bmp;
            }

            return null;
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

        #region Windows Shell Thumbnail Provider Interop (K-Lite / Icaros / Windows Shell)

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10,
            SIIGBF_CROPTOSQUARE = 0x20,
            SIIGBF_WIDESTHUMBNAIL = 0x40,
            SIIGBF_ICONBACKGROUND = 0x80,
            SIIGBF_SCALEUP = 0x100
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler([In] IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
            [PreserveSig] int GetParent([Out] out IShellItem ppsi);
            [PreserveSig] int GetDisplayName([In] uint sigdnName, [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            [PreserveSig] int GetAttributes([In] uint sfgaoMask, [Out] out uint psfgaoAttribs);
            [PreserveSig] int Compare([In] IShellItem psi, [In] uint hint, [Out] out int piOrder);
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c07c425e")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(
                [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
                [In] SIIGBF flags,
                [Out] out IntPtr phbm);
        }

        [ComImport]
        [Guid("b7d14566-0509-4cce-a714-da8a879bb607")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithFile
        {
            [PreserveSig] int Initialize([In, MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, [In] uint grfMode);
        }

        [ComImport]
        [Guid("7f738814-ad42-11d9-ba98-005056c00008")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithItem
        {
            [PreserveSig] int Initialize([In] IntPtr psi, [In] uint grfMode);
        }

        [ComImport]
        [Guid("e357fccd-a995-4576-b01f-234630154e96")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IThumbnailProvider
        {
            [PreserveSig]
            int GetThumbnail([In] uint cx, [Out] out IntPtr phbmp, [Out] out uint pdwAlpha);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In] ref Guid riid,
            [Out] out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll")]
        private static extern void OleUninitialize();

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c07c425e");
        private static readonly Guid BHID_ThumbnailHandler = new Guid("7b0e7d7a-156c-4000-95d9-47860433e508");
        private static readonly Guid IID_IThumbnailProvider = new Guid("e357fccd-a995-4576-b01f-234630154e96");

        private static string? GetThumbnailHandlerClsidForExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return null;
            if (!ext.StartsWith(".")) ext = "." + ext;

            try
            {
                // 1. HKCR\SystemFileAssociations\<ext>\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"SystemFileAssociations\{ext}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}"))
                {
                    var val = key?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(val)) return val;
                }

                // 2. HKCR\<ext>\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{ext}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}"))
                {
                    var val = key?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(val)) return val;
                }

                // 3. HKCR\<ProgID>\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}
                using (var extKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext))
                {
                    var progId = extKey?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(progId))
                    {
                        using var progKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}");
                        var val = progKey?.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                }
            }
            catch { }

            return null;
        }

        private static void ExtractThumbnailViaShellItem(string filePath, VideoMetadataResult result)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var thread = new Thread(() =>
            {
                int hrOle = OleInitialize(IntPtr.Zero);
                IntPtr hBitmap = IntPtr.Zero;
                try
                {
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();
                    string? clsidStr = GetThumbnailHandlerClsidForExtension(ext);
                    if (string.IsNullOrEmpty(clsidStr))
                    {
                        // Стандартный CLSID Icaros Thumbnail Provider (K-Lite Codec Pack)
                        clsidStr = "{49E17978-2C26-4444-9FA8-1F19F2BE1D52}";
                    }

                    // Вариант 1 (Приоритетный): Прямое создание зарегистрированного IThumbnailProvider из реестра (K-Lite / Icaros)
                    if (!string.IsNullOrEmpty(clsidStr) && Guid.TryParse(clsidStr, out Guid handlerClsid))
                    {
                        try
                        {
                            var comType = Type.GetTypeFromCLSID(handlerClsid);
                            if (comType != null)
                            {
                                object? instance = Activator.CreateInstance(comType);
                                if (instance != null)
                                {
                                    try
                                    {
                                        int hrInit = -1;
                                        if (instance is IInitializeWithFile initFile)
                                        {
                                            hrInit = initFile.Initialize(filePath, 0); // STGM_READ
                                        }
                                        else if (instance is IInitializeWithItem initItem)
                                        {
                                            Guid iidItem = IID_IShellItem;
                                            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iidItem, out IntPtr pItem) == 0 && pItem != IntPtr.Zero)
                                            {
                                                try { hrInit = initItem.Initialize(pItem, 0); }
                                                finally { Marshal.Release(pItem); }
                                            }
                                        }

                                        if (hrInit == 0 && instance is IThumbnailProvider thumbProv)
                                        {
                                            int hrThumb = thumbProv.GetThumbnail(320, out hBitmap, out _);
                                            if (hrThumb == 0 && hBitmap != IntPtr.Zero)
                                            {
                                                AppLogger.Info("VideoMetadataExtractor", $"Успешно получен стоп-кадр через IThumbnailProvider (K-Lite {clsidStr})");
                                            }
                                            else
                                            {
                                                AppLogger.Debug("VideoMetadataExtractor", $"IThumbnailProvider.GetThumbnail вернул hr = 0x{hrThumb:X8} ({clsidStr})");
                                            }
                                        }
                                        else
                                        {
                                            AppLogger.Debug("VideoMetadataExtractor", $"IInitializeWithFile.Initialize вернул hr = 0x{hrInit:X8} ({clsidStr})");
                                        }
                                    }
                                    finally
                                    {
                                        try { Marshal.ReleaseComObject(instance); } catch { }
                                    }
                                }
                            }
                        }
                        catch (Exception exDirect)
                        {
                            AppLogger.Debug("VideoMetadataExtractor", $"Ошибка прямого вызова IThumbnailProvider ({clsidStr}): {exDirect.Message}");
                        }
                    }

                    // Вариант 2: Если прямой вызов не вернул hBitmap, запрашиваем через SHCreateItemFromParsingName
                    if (hBitmap == IntPtr.Zero)
                    {
                        IntPtr pShellItem = IntPtr.Zero;
                        IntPtr pFactory = IntPtr.Zero;
                        try
                        {
                            Guid iidItem = IID_IShellItem;
                            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iidItem, out pShellItem);
                            if (hr == 0 && pShellItem != IntPtr.Zero)
                            {
                                Guid iidFactory = IID_IShellItemImageFactory;
                                int hrQi = Marshal.QueryInterface(pShellItem, ref iidFactory, out pFactory);
                                if (hrQi == 0 && pFactory != IntPtr.Zero)
                                {
                                    var factory = Marshal.GetObjectForIUnknown(pFactory) as IShellItemImageFactory;
                                    if (factory != null)
                                    {
                                        var size = new SIZE(320, 320);
                                        int hrImg = factory.GetImage(size, SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out hBitmap);
                                        if (hrImg != 0 || hBitmap == IntPtr.Zero)
                                        {
                                            factory.GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_SCALEUP, out hBitmap);
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (pFactory != IntPtr.Zero) { try { Marshal.Release(pFactory); } catch { } }
                            if (pShellItem != IntPtr.Zero) { try { Marshal.Release(pShellItem); } catch { } }
                        }
                    }

                    if (hBitmap != IntPtr.Zero)
                    {
                        using (var bmp = System.Drawing.Image.FromHbitmap(hBitmap))
                        {
                            result.Thumbnail = ResizeBitmapToTelegramJpeg(bmp, 320, 320);
                            if (result.Thumbnail != null)
                            {
                                AppLogger.Info("VideoMetadataExtractor", $"Успешно сгенерирован стоп-кадр через Windows Shell (K-Lite/Icaros) ({result.Thumbnail.Length} байт, {bmp.Width}x{bmp.Height})");

                                try
                                {
                                    string thumbPath = Path.Combine(Path.GetDirectoryName(filePath) ?? Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(filePath)}_preview.jpg");
                                    File.WriteAllBytes(thumbPath, result.Thumbnail);
                                    AppLogger.Info("VideoMetadataExtractor", $"Превью сохранено на диск: {thumbPath}");
                                }
                                catch { }
                            }

                            if (result.Width <= 0 || result.Height <= 0)
                            {
                                result.Width = bmp.Width;
                                result.Height = bmp.Height;
                            }
                        }
                    }
                    else
                    {
                        AppLogger.Debug("VideoMetadataExtractor", $"Не удалось получить hBitmap через Shell/K-Lite для '{Path.GetFileName(filePath)}'");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"Ошибка Shell Thumbnail: {ex.Message}");
                }
                finally
                {
                    if (hBitmap != IntPtr.Zero)
                    {
                        try { DeleteObject(hBitmap); } catch { }
                    }
                    if (hrOle == 0 || hrOle == 1) // S_OK or S_FALSE
                    {
                        try { OleUninitialize(); } catch { }
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join(4000);
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
