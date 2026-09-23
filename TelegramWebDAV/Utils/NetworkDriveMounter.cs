using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TelegramWebDAV.Utils
{
    /// <summary>
    /// Утилита для автомонтирования WebDAV-папки как виртуального сетевого диска в Windows (Z:, T: и т.д.)
    /// Использует функции Windows API из mpr.dll (WNetAddConnection2W, WNetCancelConnection2W).
    /// </summary>
    public static class NetworkDriveMounter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct NETRESOURCE
        {
            public uint dwScope;
            public uint dwType;
            public uint dwDisplayType;
            public uint dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        private const uint RESOURCETYPE_DISK = 0x00000001;
        private const uint CONNECT_UPDATE_PROFILE = 0x00000001;
        private const int NO_ERROR = 0;
        private const int ERROR_ALREADY_ASSIGNED = 85;
        private const int ERROR_DEVICE_ALREADY_REMEMBERED = 1202;

        [DllImport("mpr.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int WNetAddConnection2(
            ref NETRESOURCE lpNetResource,
            string? lpPassword,
            string? lpUserName,
            uint dwFlags);

        [DllImport("mpr.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int WNetCancelConnection2(
            string lpName,
            uint dwFlags,
            bool fForce);

        /// <summary>
        /// Монтирует локальный WebDAV URL на указанную букву диска в Windows.
        /// </summary>
        public static bool Mount(string driveLetter, string webDavUrl, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(driveLetter))
            {
                errorMessage = "Буква диска не указана";
                return false;
            }

            // Форматируем букву (например, "Z:" или "Z")
            string formattedLetter = driveLetter.Trim().ToUpper();
            if (!formattedLetter.EndsWith(":")) formattedLetter += ":";

            // Сначала пробуем аккуратно отмонтировать, если буква была занята старым сеансом
            Unmount(formattedLetter, true);

            var resource = new NETRESOURCE
            {
                dwType = RESOURCETYPE_DISK,
                lpLocalName = formattedLetter,
                lpRemoteName = webDavUrl.TrimEnd('/')
            };

            int result = WNetAddConnection2(ref resource, null, null, CONNECT_UPDATE_PROFILE);

            if (result == NO_ERROR || result == ERROR_ALREADY_ASSIGNED || result == ERROR_DEVICE_ALREADY_REMEMBERED)
            {
                Services.AppLogger.Info("NetworkDriveMounter", $"Диск {formattedLetter} успешно смонтирован на {webDavUrl}");
                return true;
            }

            errorMessage = $"Код ошибки WinAPI: {result}";
            Services.AppLogger.Warn("NetworkDriveMounter", $"Ошибка монтирования диска {formattedLetter}: {errorMessage}");
            return false;
        }

        /// <summary>
        /// Размонтирует сетевой диск из системы.
        /// </summary>
        public static bool Unmount(string driveLetter, bool force = false)
        {
            string formattedLetter = driveLetter.Trim().ToUpper();
            if (!formattedLetter.EndsWith(":")) formattedLetter += ":";

            int result = WNetCancelConnection2(formattedLetter, CONNECT_UPDATE_PROFILE, force);
            return result == NO_ERROR;
        }

        /// <summary>
        /// Находит первую свободную букву диска в Windows от Z до D.
        /// </summary>
        public static string GetFirstAvailableDriveLetter()
        {
            string[] occupiedDrives = Environment.GetLogicalDrives();
            for (char c = 'Z'; c >= 'D'; c--)
            {
                string candidate = $"{c}:\\";
                bool isOccupied = false;
                foreach (string occ in occupiedDrives)
                {
                    if (string.Equals(occ, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        isOccupied = true;
                        break;
                    }
                }
                if (!isOccupied)
                {
                    return $"{c}:";
                }
            }
            return "Z:";
        }
    }
}
