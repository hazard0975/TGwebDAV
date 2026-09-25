using System;
using TelegramWebDAV.Config;
using TelegramWebDAV.Utils;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Менеджер виртуального диска Telegram.
    /// Управляет подключением диска через WinFsp (прямой доступ в ОЗУ) или WebDAV (встроенная служба Windows)
    /// в зависимости от настроек пользователя.
    /// </summary>
    public class VirtualDriveManager : IDisposable
    {
        private readonly ConfigManager _configManager;
        private readonly WinFspServer _winFspServer;
        private string? _activeMountedLetter;
        private DriveEngine _activeEngine;

        public bool IsMounted => !string.IsNullOrEmpty(_activeMountedLetter);
        public string? MountedLetter => _activeMountedLetter;
        public DriveEngine ActiveEngine => _activeEngine;

        public VirtualDriveManager(ConfigManager configManager, WinFspServer winFspServer)
        {
            _configManager = configManager;
            _winFspServer = winFspServer;
        }

        /// <summary>
        /// Монтирует виртуальный диск согласно текущей конфигурации приложения.
        /// </summary>
        public bool Mount(string? targetDriveLetter, out string errorMessage)
        {
            errorMessage = string.Empty;
            var settings = _configManager.Load();

            string letter = string.IsNullOrWhiteSpace(targetDriveLetter) || targetDriveLetter == "AUTO"
                ? (settings.Server.DriveLetter == "AUTO" ? NetworkDriveMounter.GetFirstAvailableDriveLetter() : settings.Server.DriveLetter ?? "Z:")
                : targetDriveLetter;

            // Сначала размонтируем старое подключение, если оно было
            Unmount();

            if (settings.Server.Engine == DriveEngine.WinFsp)
            {
                if (WinFspServer.IsAvailable())
                {
                    AppLogger.Info("VirtualDriveManager", $"Выбран режим диска WinFsp. Подключение {letter}...");
                    if (_winFspServer.Start(letter, out errorMessage))
                    {
                        _activeMountedLetter = letter;
                        _activeEngine = DriveEngine.WinFsp;
                        return true;
                    }

                    AppLogger.Warn("VirtualDriveManager", $"Не удалось запустить WinFsp ({errorMessage}). Переключаемся на резервный WebDAV.");
                }
                else
                {
                    AppLogger.Warn("VirtualDriveManager", "Драйвер WinFsp не обнаружен в системе. Автоматический переход на режим WebDAV.");
                }
            }

            // WebDAV режим (или fallback)
            AppLogger.Info("VirtualDriveManager", $"Выбран режим диска WebDAV. Подключение {letter}...");
            string webDavUrl = $"http://localhost:{settings.Server.Port}/";
            if (NetworkDriveMounter.Mount(letter, webDavUrl, out errorMessage))
            {
                _activeMountedLetter = letter;
                _activeEngine = DriveEngine.WebDav;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Размонтирует диск из системы для обоих движков (WinFsp и WebDAV).
        /// </summary>
        public void Unmount()
        {
            if (_winFspServer.IsMounted)
            {
                _winFspServer.Stop();
            }

            if (!string.IsNullOrEmpty(_activeMountedLetter))
            {
                NetworkDriveMounter.Unmount(_activeMountedLetter, true);
                _activeMountedLetter = null;
            }
            else
            {
                var settings = _configManager.Load();
                string candidate = settings.Server.DriveLetter ?? "Z:";
                if (candidate != "AUTO")
                {
                    NetworkDriveMounter.Unmount(candidate, true);
                }
            }
        }

        /// <summary>
        /// Переподключает диск с актуальными настройками.
        /// </summary>
        public bool Remount(out string errorMessage)
        {
            Unmount();
            var settings = _configManager.Load();
            return Mount(settings.Server.DriveLetter, out errorMessage);
        }

        public void Dispose()
        {
            Unmount();
        }
    }
}
