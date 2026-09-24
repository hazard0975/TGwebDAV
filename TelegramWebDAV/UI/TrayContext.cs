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
        private readonly TrayProgressOverlay _progressOverlay;
        private AppSettings _settings;
        private AuthSettingsForm? _settingsForm;
        private string? _activeUploadFileName;
        private int _activeUploadPercent;

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
            _progressOverlay = new TrayProgressOverlay
            {
                AutoShowOnUpload = _settings.Server.AutoShowUploadPopup
            };

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Telegram WebDAV & Network Drive",
                Visible = true
            };
            _notifyIcon.DoubleClick += (s, e) => ShowSettingsDialog();
            _notifyIcon.MouseMove += (s, e) => _progressOverlay.NotifyTrayHover();

            BuildContextMenu();
            CheckDriveMounting();

            if (_settings.Server.AddTrashToContextMenu)
            {
                ShellContextMenuHelper.RegisterTrashContextMenu(_settings.Server.DriveLetter);
            }

            _telegramService.OnUploadProgress += (fileName, current, total) =>
            {
                _activeUploadFileName = fileName;
                if (total > 0)
                {
                    _activeUploadPercent = (int)(current * 100 / total);
                    string text = $"Загрузка: {fileName} ({_activeUploadPercent}%)";
                    if (text.Length > 63) text = text.Substring(0, 60) + "...";
                    try
                    {
                        _notifyIcon.Text = text;
                    }
                    catch { }
                }

                _progressOverlay.UpdateProgress(fileName, current, total);
            };

            _telegramService.OnUploadCompleted += (fileName) =>
            {
                _activeUploadFileName = null;
                _activeUploadPercent = 0;
                try
                {
                    _notifyIcon.Text = "Telegram WebDAV & Network Drive";
                }
                catch { }

                _progressOverlay.CompleteUpload(fileName);
            };
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            // Статус подключения
            string driveLetter = _settings.Server.DriveLetter ?? "Z:";
            var itemStatus = new ToolStripLabel
            {
                Text = _telegramService.IsAuthorized ? "Статус: Авторизовано ✔" : "Статус: Не авторизовано ❌"
            };
            menu.Items.Add(itemStatus);

            // Разделительная линия под статусом
            menu.Items.Add(new ToolStripSeparator());

            menu.Renderer = new StatusMenuRenderer(itemStatus, () => _telegramService.IsAuthorized);
            menu.Opening += (s, e) =>
            {
                itemStatus.Text = _telegramService.IsAuthorized ? "Статус: Авторизовано ✔" : "Статус: Не авторизовано ❌";
            };

            // Открыть сетевой диск в Проводнике
            var itemOpenDrive = new ToolStripMenuItem($"Открыть диск ({driveLetter})");
            itemOpenDrive.Click += (s, e) => OpenDriveInExplorer();
            menu.Items.Add(itemOpenDrive);

            // Открыть корзину в Проводнике
            var itemOpenTrash = new ToolStripMenuItem($"Открыть корзину ({driveLetter}\\.Trash)");
            itemOpenTrash.Click += (s, e) => OpenTrashInExplorer();
            menu.Items.Add(itemOpenTrash);

            menu.Items.Add(new ToolStripSeparator());

            // Настройки и авторизация
            var itemSettings = new ToolStripMenuItem("Настройки");
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
                _progressOverlay.AutoShowOnUpload = _settings.Server.AutoShowUploadPopup;
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

        private void OpenTrashInExplorer()
        {
            string drive = _settings.Server.DriveLetter ?? "Z:";
            if (drive == "AUTO") drive = "Z:";
            if (!drive.EndsWith("\\")) drive += "\\";

            string trashPath = Path.Combine(drive, ".Trash");
            try
            {
                if (Directory.Exists(trashPath))
                {
                    Process.Start("explorer.exe", $"\"{trashPath}\"");
                }
                else
                {
                    // Если диск не смонтирован как буква, открываем WebDAV URL корзины
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"http://localhost:{_settings.Server.Port}/.Trash",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть корзину: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _progressOverlay.Dispose();
            _webDavServer?.Stop();
            AppLogger.Shutdown();
            Application.Exit();
        }

        private class StatusMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly ToolStripItem _statusItem;
            private readonly Func<bool> _isAuthorized;

            public StatusMenuRenderer(ToolStripItem statusItem, Func<bool> isAuthorized)
            {
                _statusItem = statusItem;
                _isAuthorized = isAuthorized;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item == _statusItem)
                {
                    bool isAuth = _isAuthorized();
                    string prefix = "Статус: ";
                    string statusText = isAuth ? "Авторизовано ✔" : "Не авторизовано ❌";
                    Color statusColor = isAuth ? Color.ForestGreen : Color.Firebrick;

                    var baseFont = e.TextFont ?? SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
                    using var regFont = new Font(baseFont.FontFamily, baseFont.Size, FontStyle.Regular);
                    using var boldFont = new Font(baseFont.FontFamily, baseFont.Size, FontStyle.Bold);

                    Size prefixSize = TextRenderer.MeasureText(e.Graphics, prefix, regFont, Size.Empty, TextFormatFlags.NoPadding);
                    int y = e.TextRectangle.Top + (e.TextRectangle.Height - boldFont.Height) / 2;

                    TextRenderer.DrawText(e.Graphics, prefix, regFont, new Point(e.TextRectangle.Left, y), SystemColors.ControlText, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, statusText, boldFont, new Point(e.TextRectangle.Left + prefixSize.Width + 2, y), statusColor, TextFormatFlags.NoPadding);
                    return;
                }

                base.OnRenderItemText(e);
            }
        }
    }
}
