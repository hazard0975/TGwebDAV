using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TelegramWebDAV.Config;
using TelegramWebDAV.Services;
using TelegramWebDAV.Utils;

namespace TelegramWebDAV.UI
{
    /// <summary>
    /// Нативное окно настроек сервиса и пошаговой авторизации в Telegram (WinForms).
    /// </summary>
    public class AuthSettingsForm : Form
    {
        private readonly ConfigManager _configManager;
        private readonly TelegramService _telegramService;
        private AppSettings _settings;

        // UI Controls
        private TabControl _tabControl = null!;
        private TabPage _tabGeneral = null!;
        private TabPage _tabTelegram = null!;
        private TabPage _tabRegistry = null!;

        // General Tab
        private CheckBox _chkWebDavEnabled = null!;
        private NumericUpDown _numPort = null!;
        private ComboBox _cmbEngine = null!;
        private CheckBox _chkMountDrive = null!;
        private ComboBox _cmbDriveLetter = null!;
        private TextBox _txtVolumeName = null!;
        private NumericUpDown _numCapacityGb = null!;
        private CheckBox _chkAutoExpand = null!;
        private CheckBox _chkIncludeTrashInSpace = null!;
        private CheckBox _chkAutoStart = null!;
        private CheckBox _chkHideTrash = null!;
        private CheckBox _chkContextMenu = null!;
        private CheckBox _chkAutoShowPopup = null!;
        private CheckBox _chkEnableDiskCache = null!;
        private NumericUpDown _numMemoryCacheMb = null!;
        private NumericUpDown _numChunkTtlMinutes = null!;
        private NumericUpDown _numFullTrackMaxMb = null!;
        private NumericUpDown _numStreamingWindowMb = null!;

        // Telegram Tab
        private Label _lblStatus = null!;
        private Label _lblInstruction = null!;
        private TextBox _txtApiId = null!;
        private TextBox _txtApiHash = null!;
        private TextBox _txtChannelTitle = null!;
        private Button _btnSaveApi = null!;
        private TextBox _txtInput = null!;
        private Button _btnAction = null!;
        private Button _btnLogout = null!;

        // Registry Tab
        private Label _lblRegStatus = null!;
        private Button _btnApplyRegFix = null!;

        // Logs Tab
        private TabPage _tabLogs = null!;
        private CheckBox _chkLogDebug = null!;
        private CheckBox _chkLogInfo = null!;
        private CheckBox _chkLogWarn = null!;
        private CheckBox _chkLogError = null!;
        private NumericUpDown _numMaxLogMb = null!;
        private NumericUpDown _numMaxLogFiles = null!;
        private Label _lblLogStats = null!;

        public AuthSettingsForm(ConfigManager configManager, TelegramService telegramService)
        {
            _configManager = configManager;
            _telegramService = telegramService;
            _settings = _configManager.Load();

            InitializeComponents();
            UpdateUiState();
        }

        private void InitializeComponents()
        {
            this.Text = "Telegram WebDAV Service - Настройки";
            this.Size = new Size(570, 600);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _tabControl = new TabControl { Dock = DockStyle.Fill };

            // === Вкладка 1: Основные настройки (Сервер и Диск) ===
            _tabGeneral = new TabPage("Сервер и Диск");
            var pnlGeneral = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(15),
                AutoScroll = true
            };

            _chkWebDavEnabled = new CheckBox
            {
                Text = "Включить встроенный WebDAV сервер",
                Checked = _settings.Server.WebDavEnabled,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 10)
            };

            var pnlPort = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            pnlPort.Controls.Add(new Label { Text = "HTTP Порт:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = _settings.Server.Port, Width = 80 };
            pnlPort.Controls.Add(_numPort);

            var pnlEngine = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 5) };
            pnlEngine.Controls.Add(new Label { Text = "Драйвер диска:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _cmbEngine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
            _cmbEngine.Items.Add("WinFsp (Прямой стриминг в память, без кэша на C:)");
            _cmbEngine.Items.Add("WebDAV (Встроенная служба Windows WebClient)");
            _cmbEngine.SelectedIndex = _settings.Server.Engine == DriveEngine.WinFsp ? 0 : 1;
            pnlEngine.Controls.Add(_cmbEngine);

            _chkMountDrive = new CheckBox
            {
                Text = "Автоматически монтировать сетевой диск в Windows",
                Checked = _settings.Server.MountDrive,
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 10)
            };

            var pnlDrive = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            pnlDrive.Controls.Add(new Label { Text = "Буква диска:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _cmbDriveLetter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            _cmbDriveLetter.Items.AddRange(new object[] { "Z:", "T:", "Y:", "X:", "W:", "AUTO" });
            _cmbDriveLetter.SelectedItem = _settings.Server.DriveLetter ?? "Z:";
            pnlDrive.Controls.Add(_cmbDriveLetter);

            var pnlVol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 5) };
            pnlVol.Controls.Add(new Label { Text = "Имя тома:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _txtVolumeName = new TextBox { Text = _settings.Server.DriveName ?? "Telegram Drive", Width = 150 };
            pnlVol.Controls.Add(_txtVolumeName);

            var pnlCapacity = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 0, 5) };
            pnlCapacity.Controls.Add(new Label { Text = "Размер диска (ГБ):", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numCapacityGb = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 1048576, // До 1 ПБ
                Value = Math.Max(10, Math.Min(1048576, _settings.Server.VirtualDiskCapacityGb > 0 ? _settings.Server.VirtualDiskCapacityGb : 1024)),
                Width = 100
            };
            pnlCapacity.Controls.Add(_numCapacityGb);

            _chkAutoExpand = new CheckBox
            {
                Text = "Автоматически расширять диск при заполнении > 70%",
                Checked = _settings.Server.AutoExpandDiskCapacity,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _chkIncludeTrashInSpace = new CheckBox
            {
                Text = "Учитывать файлы в корзине (.Trash) в занятом месте диска",
                Checked = _settings.Server.IncludeTrashInUsedSpace,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _chkAutoStart = new CheckBox
            {
                Text = "Автозапуск сервиса при входе в Windows",
                Checked = _settings.Server.AutoStartWithWindows,
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 5)
            };

            _chkHideTrash = new CheckBox
            {
                Text = "Скрывать папку .Trash из корня диска (чистый корень)",
                Checked = _settings.Server.HideTrashFromRoot,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _chkContextMenu = new CheckBox
            {
                Text = "Пункт «Открыть корзину WebDAV» в контекстном меню Windows",
                Checked = _settings.Server.AddTrashToContextMenu,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _chkAutoShowPopup = new CheckBox
            {
                Text = "Автоматически открывать карточку прогресса при отправке файлов",
                Checked = _settings.Server.AutoShowUploadPopup,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _chkEnableDiskCache = new CheckBox
            {
                Text = "Сохранять прочитанные файлы в дисковый кэш (%TEMP%)",
                Checked = _settings.Server.EnableDiskReadCache,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            var pnlMemCache = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 0, 5) };
            pnlMemCache.Controls.Add(new Label { Text = "Буфер кэша в ОЗУ (МБ):", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numMemoryCacheMb = new NumericUpDown
            {
                Minimum = 32,
                Maximum = 4096, // До 4 ГБ
                Value = Math.Max(32, Math.Min(4096, _settings.Server.MemoryCacheSizeMb > 0 ? _settings.Server.MemoryCacheSizeMb : 128)),
                Width = 100
            };
            pnlMemCache.Controls.Add(_numMemoryCacheMb);

            var pnlChunkTtl = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 0, 5) };
            pnlChunkTtl.Controls.Add(new Label { Text = "Время жизни кэша в ОЗУ (мин):", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numChunkTtlMinutes = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 120, // До 2 часов
                Value = Math.Max(1, Math.Min(120, _settings.Server.ChunkMemoryCacheTtlMinutes > 0 ? _settings.Server.ChunkMemoryCacheTtlMinutes : 10)),
                Width = 100
            };
            pnlChunkTtl.Controls.Add(_numChunkTtlMinutes);

            var pnlFullTrack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 0, 5) };
            pnlFullTrack.Controls.Add(new Label { Text = "Качать трек целиком до (МБ):", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numFullTrackMaxMb = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 500, // До 500 МБ
                Value = Math.Max(10, Math.Min(500, _settings.Server.FullTrackPrefetchMaxFileSizeMb > 0 ? _settings.Server.FullTrackPrefetchMaxFileSizeMb : 70)),
                Width = 100
            };
            pnlFullTrack.Controls.Add(_numFullTrackMaxMb);

            var pnlStreamWindow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 0, 10) };
            pnlStreamWindow.Controls.Add(new Label { Text = "Окно стриминга для больших файлов (МБ):", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _numStreamingWindowMb = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 200, // До 200 МБ
                Value = Math.Max(5, Math.Min(200, _settings.Server.StreamingPrefetchWindowMb > 0 ? _settings.Server.StreamingPrefetchWindowMb : 20)),
                Width = 100
            };
            pnlStreamWindow.Controls.Add(_numStreamingWindowMb);

            var btnSaveGeneral = new Button
            {
                Text = "Сохранить настройки",
                AutoSize = true,
                Padding = new Padding(10, 5, 10, 5),
                Margin = new Padding(0, 15, 0, 0)
            };
            btnSaveGeneral.Click += (s, e) => SaveGeneralSettings();

            pnlGeneral.Controls.AddRange(new Control[] {
                _chkWebDavEnabled, pnlPort, pnlEngine, _chkMountDrive, pnlDrive, pnlVol, pnlCapacity, _chkAutoExpand, _chkIncludeTrashInSpace, _chkAutoStart, _chkHideTrash, _chkContextMenu, _chkAutoShowPopup, _chkEnableDiskCache, pnlMemCache, pnlChunkTtl, pnlFullTrack, pnlStreamWindow, btnSaveGeneral
            });
            _tabGeneral.Controls.Add(pnlGeneral);

            // === Вкладка 2: Авторизация Telegram ===
            _tabTelegram = new TabPage("Авторизация Telegram");
            var pnlTg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(15),
                AutoScroll = true
            };

            var lblApiTitle = new Label
            {
                Text = "Параметры приложения Telegram (my.telegram.org):",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };

            var pnlApiId = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            pnlApiId.Controls.Add(new Label { Text = "API ID:  ", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _txtApiId = new TextBox
            {
                Text = _settings.Telegram.ApiId > 0 ? _settings.Telegram.ApiId.ToString() : "",
                Width = 240
            };
            pnlApiId.Controls.Add(_txtApiId);

            var pnlApiHash = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            pnlApiHash.Controls.Add(new Label { Text = "API Hash:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _txtApiHash = new TextBox
            {
                Text = _settings.Telegram.ApiHash ?? "",
                Width = 240,
                UseSystemPasswordChar = true
            };
            pnlApiHash.Controls.Add(_txtApiHash);

            var pnlChannel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            pnlChannel.Controls.Add(new Label { Text = "Имя канала:", AutoSize = true, Margin = new Padding(0, 5, 2, 0) });
            _txtChannelTitle = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(_settings.Telegram.StorageChannelTitle) ? "Telegram WebDAV Drive" : _settings.Telegram.StorageChannelTitle,
                Width = 240
            };
            pnlChannel.Controls.Add(_txtChannelTitle);

            _btnSaveApi = new Button
            {
                Text = "Сохранить настройки Telegram",
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(0, 0, 0, 15)
            };
            _btnSaveApi.Click += (s, e) => SaveTelegramApiKeys();

            var lblSeparator = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Height = 2,
                Width = 510,
                Margin = new Padding(0, 0, 0, 15)
            };

            _lblStatus = new Label
            {
                Text = "Статус: Проверка сессии...",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10)
            };

            _lblInstruction = new Label
            {
                Text = "Введите номер телефона в международном формате (+7...):",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 5)
            };

            _txtInput = new TextBox { Width = 320, Margin = new Padding(0, 0, 0, 10) };

            var pnlTgButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 5, 0, 10) };
            _btnAction = new Button { Text = "Отправить", AutoSize = true, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(0, 0, 10, 0) };
            _btnAction.Click += async (s, e) => await HandleTelegramActionAsync();

            _btnLogout = new Button { Text = "Выйти из аккаунта", AutoSize = true, Padding = new Padding(10, 5, 10, 5), ForeColor = Color.DarkRed };
            _btnLogout.Click += (s, e) => HandleTelegramLogout();

            pnlTgButtons.Controls.AddRange(new Control[] { _btnAction, _btnLogout });

            pnlTg.Controls.AddRange(new Control[] {
                lblApiTitle, pnlApiId, pnlApiHash, pnlChannel, _btnSaveApi, lblSeparator, _lblStatus, _lblInstruction, _txtInput, pnlTgButtons
            });
            _tabTelegram.Controls.Add(pnlTg);

            // === Вкладка 3: Патч реестра Windows ===
            _tabRegistry = new TabPage("Патч реестра Windows");
            var pnlReg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(15),
                AutoScroll = true
            };

            _lblRegStatus = new Label
            {
                Text = "Комплексный фикс реестра Windows для служб WebDAV и виртуальных дисков:\n\n" +
                       "1. BasicAuthLevel = 2 — разрешение подключения WebDAV по HTTP в локальной сети.\n" +
                       "2. FileSizeLimitInBytes = 4 GB — снятие системного лимита в 50 МБ на файл (ошибка 0x800700DF).\n" +
                       "3. EnableLinkedConnections = 1 — сквозная видимость дисков между сессиями пользователя и Администратора.\n" +
                       "4. ZoneMap (Местная интрасеть) — устранение системных предупреждений безопасности при копировании файлов.",
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Margin = new Padding(0, 5, 0, 15)
            };

            var lblRegCurrentState = new Label
            {
                AutoSize = true,
                Font = new Font(this.Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 15)
            };

            Action refreshRegState = () =>
            {
                bool isApplied = WindowsRegistryFixer.IsAllFixesApplied();
                if (isApplied)
                {
                    lblRegCurrentState.Text = "Статус: Все системные патчи успешно применены ✔";
                    lblRegCurrentState.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblRegCurrentState.Text = "Статус: Рекомендуется применить системные патчи реестра ⚠";
                    lblRegCurrentState.ForeColor = Color.DarkOrange;
                }
            };
            refreshRegState();

            _btnApplyRegFix = new Button
            {
                Text = "🛡 Применить комплексный фикс реестра (с запросом UAC)",
                AutoSize = true,
                Padding = new Padding(12, 10, 12, 10),
                Font = new Font(this.Font.FontFamily, 9.5f, FontStyle.Bold)
            };
            _btnApplyRegFix.Click += (s, e) =>
            {
                _btnApplyRegFix.Enabled = false;
                try
                {
                    bool success = WindowsRegistryFixer.ApplyAllFixesWithElevation(out string message);
                    refreshRegState();
                    if (success)
                    {
                        MessageBox.Show(
                            "Патчи реестра Windows успешно применены:\n\n" +
                            "• BasicAuthLevel = 2 (HTTP WebDAV разрешен)\n" +
                            "• FileSizeLimitInBytes = 4 GB (лимит 50 МБ снят)\n" +
                            "• EnableLinkedConnections = 1 (диск виден и от Администратора)\n" +
                            "• Местная интрасеть для localhost/дисков настроена\n\n" +
                            "Для полного вступления изменений в силу рекомендуется перезапустить службу WebClient или перезагрузить ПК.",
                            "Патч реестра Windows",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(message, "Патч реестра Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                finally
                {
                    _btnApplyRegFix.Enabled = true;
                }
            };

            pnlReg.Controls.AddRange(new Control[] { _lblRegStatus, lblRegCurrentState, _btnApplyRegFix });
            _tabRegistry.Controls.Add(pnlReg);

            // === Вкладка 4: Логирование ===
            _tabLogs = new TabPage("Логирование");

            // Нижняя панель для кнопки «Сохранить» в правом нижнем углу окна
            var pnlSaveBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(0, 6, 15, 10)
            };

            var btnSaveLogs = new Button
            {
                Text = "Сохранить",
                AutoSize = true,
                Padding = new Padding(20, 6, 20, 6),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Dock = DockStyle.Right
            };

            pnlSaveBar.Controls.Add(btnSaveLogs);

            var pnlLogs = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(15),
                AutoScroll = true
            };

            // 1. Рамка: Уровни логирования (уменьшенные отступы)
            var grpLogLevels = new GroupBox
            {
                Text = "Уровни логирования",
                Width = 510,
                MinimumSize = new Size(510, 110),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(8)
            };

            var pnlLogLevels = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            _chkLogDebug = new CheckBox
            {
                Text = "Debug — подробная техническая отладка (COM-интерфейсы, внутренние вызовы)",
                Checked = _settings.Logging.EnableDebug,
                AutoSize = true,
                Margin = new Padding(0, 1, 0, 2)
            };

            _chkLogInfo = new CheckBox
            {
                Text = "Info — стандартные информационные события (файлы, загрузки, запуски)",
                Checked = _settings.Logging.EnableInfo,
                AutoSize = true,
                Margin = new Padding(0, 1, 0, 2)
            };

            _chkLogWarn = new CheckBox
            {
                Text = "Warn — предупреждения и некритичные отклонения",
                Checked = _settings.Logging.EnableWarn,
                AutoSize = true,
                Margin = new Padding(0, 1, 0, 2)
            };

            _chkLogError = new CheckBox
            {
                Text = "Error — ошибки приложения и сбои операций",
                Checked = _settings.Logging.EnableError,
                AutoSize = true,
                Margin = new Padding(0, 1, 0, 2)
            };

            pnlLogLevels.Controls.AddRange(new Control[] { _chkLogDebug, _chkLogInfo, _chkLogWarn, _chkLogError });
            grpLogLevels.Controls.Add(pnlLogLevels);

            // 2. Рамка: Параметры хранения и ротации (уменьшенные отступы)
            var grpRotation = new GroupBox
            {
                Text = "Параметры хранения и ротации лог-файлов",
                Width = 510,
                MinimumSize = new Size(510, 80),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(8)
            };

            var tblRotation = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0)
            };
            tblRotation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72f));
            tblRotation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28f));

            var lblMaxMb = new Label
            {
                Text = "Максимальный размер одного файла лога (МБ):",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 10, 4)
            };

            _numMaxLogMb = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 50,
                Value = Math.Clamp(_settings.Logging.MaxLogFileSizeMb > 0 ? _settings.Logging.MaxLogFileSizeMb : 5, 1, 50),
                Width = 90,
                Anchor = AnchorStyles.Right
            };

            var lblMaxFiles = new Label
            {
                Text = "Количество хранящихся архивных файлов:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 10, 4)
            };

            _numMaxLogFiles = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10,
                Value = Math.Clamp(_settings.Logging.MaxArchivedFiles > 0 ? _settings.Logging.MaxArchivedFiles : 3, 1, 10),
                Width = 90,
                Anchor = AnchorStyles.Right
            };

            tblRotation.Controls.Add(lblMaxMb, 0, 0);
            tblRotation.Controls.Add(_numMaxLogMb, 1, 0);
            tblRotation.Controls.Add(lblMaxFiles, 0, 1);
            tblRotation.Controls.Add(_numMaxLogFiles, 1, 1);
            grpRotation.Controls.Add(tblRotation);

            // 3. Рамка: Текущая информация и действия (увеличенные отступы "воздух")
            var grpStats = new GroupBox
            {
                Text = "Текущая информация и действия",
                Width = 510,
                MinimumSize = new Size(510, 140),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(12, 12, 12, 12)
            };

            var pnlStatsInner = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            _lblLogStats = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 14),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            };

            Action updateLogStats = () =>
            {
                try
                {
                    string logPath = AppLogger.CurrentLogFilePath;
                    long mainSize = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                    long archiveTotal = 0;
                    int archiveCount = 0;
                    for (int i = 1; i <= 10; i++)
                    {
                        string arch = Path.Combine(AppLogger.LogDirectory, $"app.{i}.log");
                        if (File.Exists(arch))
                        {
                            archiveCount++;
                            archiveTotal += new FileInfo(arch).Length;
                        }
                    }
                    double mainMb = mainSize / (1024.0 * 1024.0);
                    double totalMb = (mainSize + archiveTotal) / (1024.0 * 1024.0);
                    _lblLogStats.Text = $"Текущий файл: {Path.GetFileName(logPath)} ({mainMb:F2} МБ)\n" +
                                       $"Архивы ротации: {archiveCount} файлов (общий объём {totalMb:F2} МБ)\n" +
                                       $"Папка: {AppLogger.LogDirectory}";
                }
                catch
                {
                    _lblLogStats.Text = $"Папка логов: {AppLogger.LogDirectory}";
                }
            };
            updateLogStats();

            // Табличное ровное размещение 3 кнопок действия
            var tblLogActions = new TableLayoutPanel
            {
                Width = 485,
                Height = 36,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0)
            };
            tblLogActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tblLogActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tblLogActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

            var btnOpenLogDir = new Button
            {
                Text = "📁 Папка с логами",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 3, 0)
            };
            btnOpenLogDir.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(AppLogger.LogDirectory);
                    System.Diagnostics.Process.Start("explorer.exe", AppLogger.LogDirectory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var btnOpenLogFile = new Button
            {
                Text = "📄 Открыть app.log",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 0, 3, 0)
            };
            btnOpenLogFile.Click += (s, e) =>
            {
                try
                {
                    if (File.Exists(AppLogger.CurrentLogFilePath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = AppLogger.CurrentLogFilePath,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        MessageBox.Show("Файл app.log пока еще не создан.", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось открыть файл лога: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var btnClearLogs = new Button
            {
                Text = "🗑 Очистить логи",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 0, 0, 0),
                ForeColor = Color.DarkRed
            };
            btnClearLogs.Click += (s, e) =>
            {
                if (MessageBox.Show("Вы действительно хотите очистить текущий лог и все его архивы?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    AppLogger.ClearLogs();
                    updateLogStats();
                    MessageBox.Show("Все логи успешно очищены!", "Логирование", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            tblLogActions.Controls.Add(btnOpenLogDir, 0, 0);
            tblLogActions.Controls.Add(btnOpenLogFile, 1, 0);
            tblLogActions.Controls.Add(btnClearLogs, 2, 0);

            pnlStatsInner.Controls.AddRange(new Control[] { _lblLogStats, tblLogActions });
            grpStats.Controls.Add(pnlStatsInner);

            btnSaveLogs.Click += (s, e) =>
            {
                _settings.Logging.EnableDebug = _chkLogDebug.Checked;
                _settings.Logging.EnableInfo = _chkLogInfo.Checked;
                _settings.Logging.EnableWarn = _chkLogWarn.Checked;
                _settings.Logging.EnableError = _chkLogError.Checked;
                _settings.Logging.MaxLogFileSizeMb = (int)_numMaxLogMb.Value;
                _settings.Logging.MaxArchivedFiles = (int)_numMaxLogFiles.Value;

                _configManager.Save(_settings);
                updateLogStats();
                MessageBox.Show("Настройки логирования успешно сохранены!", "Логирование", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            pnlLogs.Controls.AddRange(new Control[] { grpLogLevels, grpRotation, grpStats });
            _tabLogs.Controls.AddRange(new Control[] { pnlLogs, pnlSaveBar });

            _tabControl.TabPages.AddRange(new TabPage[] { _tabGeneral, _tabTelegram, _tabRegistry, _tabLogs });
            this.Controls.Add(_tabControl);
        }

        private void SaveTelegramApiKeys()
        {
            if (!int.TryParse(_txtApiId.Text.Trim(), out int apiId) || apiId <= 0)
            {
                MessageBox.Show("Введите корректный числовой API ID (например, 12345678).", "Ошибка валидации", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string apiHash = _txtApiHash.Text.Trim();
            if (string.IsNullOrEmpty(apiHash) || apiHash.Length < 10)
            {
                MessageBox.Show("Введите корректный API Hash (строка из 32 шестнадцатеричных символов).", "Ошибка валидации", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string channelTitle = _txtChannelTitle.Text.Trim();
            if (string.IsNullOrEmpty(channelTitle))
            {
                channelTitle = "Telegram WebDAV Drive";
                _txtChannelTitle.Text = channelTitle;
            }

            _settings.Telegram.ApiId = apiId;
            _settings.Telegram.ApiHash = apiHash;
            _settings.Telegram.StorageChannelTitle = channelTitle;
            _configManager.Save(_settings);

            _telegramService.UpdateApiCredentials(apiId, apiHash);
            _telegramService.UpdateStorageChannelTitle(channelTitle);
            UpdateUiState();

            MessageBox.Show("Настройки Telegram (API ID, Hash и имя канала) успешно сохранены!", "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void UpdateUiState()
        {
            bool hasApiKeys = _settings.Telegram.ApiId > 0 && !string.IsNullOrEmpty(_settings.Telegram.ApiHash);

            if (_telegramService.IsAuthorized)
            {
                var user = _telegramService.CurrentUser;
                string userInfo = user != null ? $" ({user.FirstName} {user.LastName} | @{user.Username})" : "";
                _lblStatus.Text = $"Статус: Авторизован в Telegram ✔{userInfo}";
                _lblStatus.ForeColor = Color.DarkGreen;
                _lblInstruction.Text = "Сессия активна. Мультимедиа файлы и папки доступны через WebDAV.";
                _txtInput.Visible = false;
                _btnAction.Visible = false;
                _btnLogout.Visible = true;
            }
            else
            {
                _lblStatus.Text = "Статус: Не авторизован ❌";
                _lblStatus.ForeColor = Color.DarkRed;
                _txtInput.Visible = hasApiKeys;
                _btnAction.Visible = hasApiKeys;
                _btnLogout.Visible = false;

                if (!hasApiKeys)
                {
                    _lblInstruction.Text = "Сначала укажите и сохраните API ID и API Hash (получить на my.telegram.org).";
                    _lblInstruction.ForeColor = Color.DarkOrange;
                }
                else
                {
                    _lblInstruction.ForeColor = Color.Black;
                    switch (_telegramService.CurrentStep)
                    {
                        case AuthStep.NeedsPhone:
                            _lblInstruction.Text = "Введите номер телефона в международном формате (+7...):";
                            _btnAction.Text = "Получить код в Telegram";
                            _txtInput.UseSystemPasswordChar = false;
                            break;
                        case AuthStep.NeedsCode:
                            _lblInstruction.Text = "Введите проверочный код из Telegram (пришел в приложение Telegram):";
                            _btnAction.Text = "Подтвердить код";
                            _txtInput.UseSystemPasswordChar = false;
                            break;
                        case AuthStep.Needs2FA:
                            _lblInstruction.Text = "Введите облачный пароль 2FA (двухфакторной аутентификации):";
                            _btnAction.Text = "Войти";
                            _txtInput.UseSystemPasswordChar = true;
                            break;
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task HandleTelegramActionAsync()
        {
            string input = _txtInput.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            _btnAction.Enabled = false;
            try
            {
                string? nextRequirement = await _telegramService.LoginStepAsync(input);
                _txtInput.Clear();
                UpdateUiState();

                if (_telegramService.IsAuthorized)
                {
                    MessageBox.Show("Авторизация в Telegram успешно завершена!", "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка авторизации: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnAction.Enabled = true;
            }
        }

        private void HandleTelegramLogout()
        {
            if (MessageBox.Show("Вы уверены, что хотите сбросить сессию Telegram?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _telegramService.Logout();
                UpdateUiState();
            }
        }

        private void SaveGeneralSettings()
        {
            var oldEngine = _settings.Server.Engine;
            _settings.Server.Engine = _cmbEngine.SelectedIndex == 0 ? DriveEngine.WinFsp : DriveEngine.WebDav;
            _settings.Server.WebDavEnabled = _chkWebDavEnabled.Checked;
            _settings.Server.Port = (int)_numPort.Value;
            _settings.Server.MountDrive = _chkMountDrive.Checked;
            _settings.Server.DriveLetter = _cmbDriveLetter.SelectedItem?.ToString() ?? "Z:";
            _settings.Server.DriveName = _txtVolumeName.Text.Trim();
            _settings.Server.VirtualDiskCapacityGb = (long)_numCapacityGb.Value;
            _settings.Server.AutoExpandDiskCapacity = _chkAutoExpand.Checked;
            _settings.Server.IncludeTrashInUsedSpace = _chkIncludeTrashInSpace.Checked;
            _settings.Server.AutoStartWithWindows = _chkAutoStart.Checked;
            _settings.Server.HideTrashFromRoot = _chkHideTrash.Checked;
            _settings.Server.AddTrashToContextMenu = _chkContextMenu.Checked;
            _settings.Server.AutoShowUploadPopup = _chkAutoShowPopup.Checked;
            _settings.Server.EnableDiskReadCache = _chkEnableDiskCache.Checked;
            _settings.Server.MemoryCacheSizeMb = (int)_numMemoryCacheMb.Value;
            _settings.Server.ChunkMemoryCacheTtlMinutes = (int)_numChunkTtlMinutes.Value;
            _settings.Server.FullTrackPrefetchMaxFileSizeMb = (int)_numFullTrackMaxMb.Value;
            _settings.Server.StreamingPrefetchWindowMb = (int)_numStreamingWindowMb.Value;

            if (_chkContextMenu.Checked)
            {
                ShellContextMenuHelper.RegisterTrashContextMenu(_settings.Server.DriveLetter);
            }
            else
            {
                ShellContextMenuHelper.UnregisterTrashContextMenu();
            }

            _configManager.Save(_settings);

            string msg = "Настройки успешно сохранены в appsettings.json!";
            if (oldEngine != _settings.Server.Engine)
            {
                msg += $"\n\nДрайвер диска изменен на {_settings.Server.Engine}. Выполните «Переподключить сетевой диск» в трее или перезапустите сервис для применения изменений.";
            }
            MessageBox.Show(msg, "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
