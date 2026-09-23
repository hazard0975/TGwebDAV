using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TelegramWebDAV.Config;
using TelegramWebDAV.Database;
using TelegramWebDAV.Server;
using TelegramWebDAV.Services;
using TelegramWebDAV.Utils;

namespace TelegramWebDAV.UI
{
    /// <summary>
    /// Контекст приложения в системном трее Windows (NotifyIcon).
    /// Управляет жизненным циклом фонового WebDAV-сервера, иконкой в трее и вызовом меню.
    /// </summary>
    public class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ConfigManager _configManager;
        private readonly NodeRepository _repository;
        private readonly TelegramService _telegramService;
        private readonly WebDavServer _webDavServer;
        private AppSettings _settings;
        private AuthSettingsForm? _settingsForm;

        public TrayContext(
            ConfigManager configManager,
            NodeRepository repository,
            TelegramService telegramService,
            WebDavServer webDavServer)
        {
            _configManager = configManager;
            _repository = repository;
            _telegramService = telegramService;
            _webDavServer = webDavServer;
            _settings = _configManager.Load();

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Telegram WebDAV & Network Drive",
                Visible = true
            };
            _notifyIcon.DoubleClick += (s, e) => ShowSettingsDialog();

            BuildContextMenu();
            CheckDriveMounting();
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            // Статус подключения
            string driveLetter = _settings.Server.DriveLetter ?? "Z:";
            var itemStatus = new ToolStripMenuItem($"Статус: {(_telegramService.IsAuthorized ? "Telegram В сети ✔" : "Требуется авторизация ❌")}")
            {
                Enabled = false,
                Font = new Font(menu.Font, FontStyle.Bold)
            };
            menu.Items.Add(itemStatus);

            // Открыть сетевой диск в Проводнике
            var itemOpenDrive = new ToolStripMenuItem($"Открыть диск ({driveLetter}) в Проводнике");
            itemOpenDrive.Click += (s, e) => OpenDriveInExplorer();
            menu.Items.Add(itemOpenDrive);

            menu.Items.Add(new ToolStripSeparator());

            // Настройки и авторизация
            var itemSettings = new ToolStripMenuItem("Настройки и Авторизация...");
            itemSettings.Click += (s, e) => ShowSettingsDialog();
            menu.Items.Add(itemSettings);

            // Переподключить диск
            var itemRemount = new ToolStripMenuItem("Переподключить сетевой диск");
            itemRemount.Click += (s, e) => RemountDrive();
            menu.Items.Add(itemRemount);

            menu.Items.Add(new ToolStripSeparator());

            // Выход
            var itemExit = new ToolStripMenuItem("Выход");
            itemExit.Click += (s, e) => ExitApplication();
            menu.Items.Add(itemExit);

            _notifyIcon.ContextMenuStrip = menu;
        }

        private void ShowSettingsDialog()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized)
                {
                    _settingsForm.WindowState = FormWindowState.Normal;
                }
                _settingsForm.BringToFront();
                _settingsForm.Activate();
                return;
            }

            _settingsForm = new AuthSettingsForm(_configManager, _telegramService);
            _settingsForm.FormClosed += (s, e) =>
            {
                _settingsForm = null;
                _settings = _configManager.Load();
                BuildContextMenu();
            };
            _settingsForm.Show();
            _settingsForm.BringToFront();
            _settingsForm.Activate();
        }

        private void OpenDriveInExplorer()
        {
            string drive = _settings.Server.DriveLetter ?? "Z:";
            if (drive == "AUTO") drive = "Z:";
            if (!drive.EndsWith("\\")) drive += "\\";

            try
            {
                if (Directory.Exists(drive))
                {
                    Process.Start("explorer.exe", drive);
                }
                else
                {
                    // Если диск не смонтирован как буква, открываем WebDAV URL
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"http://localhost:{_settings.Server.Port}/",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CheckDriveMounting()
        {
            if (_settings.Server.MountDrive)
            {
                string letter = _settings.Server.DriveLetter == "AUTO"
                    ? NetworkDriveMounter.GetFirstAvailableDriveLetter()
                    : _settings.Server.DriveLetter ?? "Z:";

                string url = $"http://localhost:{_settings.Server.Port}/";
                NetworkDriveMounter.Mount(letter, url, out _);
            }
        }

        private void RemountDrive()
        {
            string letter = _settings.Server.DriveLetter == "AUTO"
                ? NetworkDriveMounter.GetFirstAvailableDriveLetter()
                : _settings.Server.DriveLetter ?? "Z:";

            NetworkDriveMounter.Unmount(letter, true);
            string url = $"http://localhost:{_settings.Server.Port}/";
            if (NetworkDriveMounter.Mount(letter, url, out string error))
            {
                _notifyIcon.ShowBalloonTip(2000, "Telegram WebDAV", $"Сетевой диск {letter} успешно переподключен", ToolTipIcon.Info);
            }
            else
            {
                _notifyIcon.ShowBalloonTip(3000, "Telegram WebDAV", $"Ошибка подключения: {error}", ToolTipIcon.Warning);
            }
        }

        private void ExitApplication()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _webDavServer?.Stop();
            AppLogger.Shutdown();
            Application.Exit();
        }
    }
}
