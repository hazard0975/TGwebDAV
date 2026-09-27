using System;
using System.IO;
using System.Runtime.InteropServices;
using TelegramWebDAV.Models;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Парсер метаданных видео (длительность, ширина, высота) и генератор превью-кадров (thumbnails)
    /// с использованием ATL.NET, Windows Shell API (IShellItemImageFactory) и встроенного парсера MP4 атомов.
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

            // Шаг 1: Чтение встроенных тегов и постеров через ATL.NET
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
                        result.Thumbnail = pic.PictureData;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("VideoMetadataExtractor", $"ATL чтение '{filePath}': {ex.Message}");
            }

            // Шаг 2: Windows Shell API (IShellItemImageFactory для превью и IShellItem2 для разрешения)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractWindowsShellMetadata(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"Windows Shell чтение '{filePath}': {ex.Message}");
                }
            }

            // Шаг 3: Fallback парсинг заголовков MP4/MOV (если ширина или длительность не найдены)
            if (result.Width <= 0 || result.Height <= 0 || result.DurationSeconds <= 0)
            {
                try
                {
                    ParseMp4HeaderFallback(filePath, result);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("VideoMetadataExtractor", $"MP4 fallback чтение '{filePath}': {ex.Message}");
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

        #region Windows Shell Interop

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out] out IntPtr ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c07c4ffc")]
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
        [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem2
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IntPtr ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStoreWithCredentials(int flags, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetProperty(ref PROPERTYKEY key, out IntPtr pv);
            [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
            [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out System.Runtime.InteropServices.ComTypes.FILETIME pft);
            [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
            [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
            [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
            [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
            [PreserveSig] int GetBool(ref PROPERTYKEY key, out bool pf);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
            public PROPERTYKEY(Guid guid, uint id) { fmtid = guid; pid = id; }
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
            SIIGBF_WIDETHUMBNAILS = 0x40,
            SIIGBF_ICONBACKGROUND = 0x80,
            SIIGBF_SCALEUP = 0x100
        }

        private static void ExtractWindowsShellMetadata(string filePath, VideoMetadataResult result)
        {
            // 1. Попытка чтения свойств видео через IShellItem2
            try
            {
                var shellItem2Guid = new Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
                SHCreateItemFromParsingName(filePath, IntPtr.Zero, shellItem2Guid, out IntPtr shellItemPtr);
                if (shellItemPtr != IntPtr.Zero)
                {
                    try
                    {
                        var shellItem = (IShellItem2)Marshal.GetObjectForIUnknown(shellItemPtr);

                        // PKEY_Video_FrameWidth: {64440490-4C87-11D1-A264-00A0C91FED73}, 3
                        var keyWidth = new PROPERTYKEY(new Guid("64440490-4C87-11D1-A264-00A0C91FED73"), 3);
                        if (shellItem.GetUInt32(ref keyWidth, out uint w) == 0 && w > 0)
                        {
                            result.Width = (int)w;
                        }

                        // PKEY_Video_FrameHeight: {64440490-4C87-11D1-A264-00A0C91FED73}, 4
                        var keyHeight = new PROPERTYKEY(new Guid("64440490-4C87-11D1-A264-00A0C91FED73"), 4);
                        if (shellItem.GetUInt32(ref keyHeight, out uint h) == 0 && h > 0)
                        {
                            result.Height = (int)h;
                        }

                        // PKEY_Media_Duration: {64440490-4C87-11D1-A264-00A0C91FED73}, 3 (100-нс единицы)
                        if (result.DurationSeconds <= 0)
                        {
                            var keyDuration = new PROPERTYKEY(new Guid("64440490-4C87-11D1-A264-00A0C91FED73"), 3);
                            if (shellItem.GetUInt64(ref keyDuration, out ulong dur100ns) == 0 && dur100ns > 0)
                            {
                                result.DurationSeconds = (int)(dur100ns / 10000000UL);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(shellItemPtr);
                    }
                }
            }
            catch { }

            // 2. Генерация превью-кадра через IShellItemImageFactory
            if (result.Thumbnail == null)
            {
                try
                {
                    var factoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c07c4ffc");
                    SHCreateItemFromParsingName(filePath, IntPtr.Zero, factoryGuid, out IntPtr factoryPtr);
                    if (factoryPtr != IntPtr.Zero)
                    {
                        try
                        {
                            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(factoryPtr);
                            // Запрашиваем превью 640x360
                            int hr = factory.GetImage(
                                new SIZE(640, 360), 
                                SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_THUMBNAILONLY, 
                                out IntPtr hBitmap
                            );

                            if (hr == 0 && hBitmap != IntPtr.Zero)
                            {
                                try
                                {
                                    using var bmp = System.Drawing.Image.FromHbitmap(hBitmap);
                                    if (result.Width <= 0 || result.Height <= 0)
                                    {
                                        result.Width = bmp.Width;
                                        result.Height = bmp.Height;
                                    }

                                    using var ms = new MemoryStream();
                                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                                    result.Thumbnail = ms.ToArray();
                                }
                                finally
                                {
                                    DeleteObject(hBitmap);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(factoryPtr);
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        #region MP4 Header Fallback Parser

        private static void ParseMp4HeaderFallback(string filePath, VideoMetadataResult result)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[65536];
            int read = fs.Read(header, 0, header.Length);
            if (read < 16) return;

            int pos = 0;
            while (pos + 8 <= read)
            {
                uint boxSize = ReadUInt32BE(header, pos);
                string boxType = System.Text.Encoding.ASCII.GetString(header, pos + 4, 4);

                if (boxSize == 0) break;
                if (boxSize == 1) // 64-bit box
                {
                    if (pos + 16 > read) break;
                    pos += 16;
                    continue;
                }

                if (boxType == "moov" || boxType == "trak" || boxType == "mdia")
                {
                    // Контейнерный бокс: заходим внутрь
                    pos += 8;
                    continue;
                }

                if (boxType == "mvhd" && result.DurationSeconds <= 0 && pos + 32 <= read)
                {
                    byte version = header[pos + 8];
                    int timeOffset = (version == 1) ? 28 : 20;
                    if (pos + timeOffset + 8 <= read)
                    {
                        uint timescale = ReadUInt32BE(header, pos + timeOffset);
                        ulong duration = (version == 1) 
                            ? ReadUInt64BE(header, pos + timeOffset + 4) 
                            : ReadUInt32BE(header, pos + timeOffset + 4);

                        if (timescale > 0 && duration > 0)
                        {
                            result.DurationSeconds = (int)(duration / timescale);
                        }
                    }
                }
                else if (boxType == "tkhd" && (result.Width <= 0 || result.Height <= 0))
                {
                    byte version = header[pos + 8];
                    int tkhdLength = (int)boxSize;
                    if (pos + tkhdLength <= read && tkhdLength >= 84)
                    {
                        // Ширина и высота находятся в последних 8 байтах атома tkhd (fixed point 16.16)
                        int wOffset = pos + tkhdLength - 8;
                        int hOffset = pos + tkhdLength - 4;

                        uint wFixed = ReadUInt32BE(header, wOffset);
                        uint hFixed = ReadUInt32BE(header, hOffset);

                        int width = (int)(wFixed >> 16);
                        int height = (int)(hFixed >> 16);

                        if (width > 0 && height > 0)
                        {
                            result.Width = width;
                            result.Height = height;
                        }
                    }
                }

                pos += (int)boxSize;
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
