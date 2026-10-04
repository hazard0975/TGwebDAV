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
        private readonly Database.NodeRepository _repository;
        private AppSettings _settings;

        public event Action? SettingsSaved;

        // UI Controls
        private TabControl _tabControl = null!;
        private TabPage _tabGeneral = null!;
        private TabPage _tabTelegram = null!;
        private TabPage _tabRegistry = null!;
        private TabPage _tabLogs = null!;

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
        private CheckBox _chkGalleryPreview = null!;
        private CheckBox _chkEnableDiskCache = null!;
        private NumericUpDown _numDownloadWorkers = null!;
        private NumericUpDown _numPacingDelayMs = null!;
        private NumericUpDown _numMemoryCacheMb = null!;
        private NumericUpDown _numChunkTtlMinutes = null!;
        private NumericUpDown _numStreamingActivationMb = null!;
        private NumericUpDown _numAudioWindowMb = null!;
        private NumericUpDown _numStreamingWindowMb = null!;

        // Telegram Tab
        private Label _lblStatus = null!;
        private TableLayoutPanel _pnlUserDetails = null!;
        private Label _lblUserName = null!;
        private Label _lblUserTag = null!;
        private Label _lblUserId = null!;
        private Label _lblUserPhone = null!;
        private Label _lblUserPremium = null!;
        private Label _lblUserDc = null!;
        private Label _lblInstruction = null!;
        private TextBox _txtApiId = null!;
        private TextBox _txtApiHash = null!;
        private TextBox _txtChannelTitle = null!;
        private TextBox _txtInput = null!;
        private Button _btnAction = null!;
        private Button _btnLogout = null!;

        // Registry Tab
        private Label _lblRegStatus = null!;
        private Label _lblRegCurrentState = null!;
        private Button _btnApplyRegFix = null!;

        // Logs Tab
        private CheckBox _chkLogDebug = null!;
        private CheckBox _chkLogInfo = null!;
        private CheckBox _chkLogWarn = null!;
        private CheckBox _chkLogError = null!;
        private NumericUpDown _numMaxLogMb = null!;
        private NumericUpDown _numMaxLogFiles = null!;
        private Label _lblLogStats = null!;

        public AuthSettingsForm(ConfigManager configManager, TelegramService telegramService, Database.NodeRepository repository)
        {
            _configManager = configManager;
            _telegramService = telegramService;
            _repository = repository;
            _settings = _configManager.Load();

            InitializeComponents();
            UpdateUiState();
        }

        private void InitializeComponents()
        {
            this.Text = "Telegram WebDAV Service - Настройки";
            this.Size = new Size(570, 640);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = UITheme.BaseFont;

            // Нижняя фиксированная панель действий формы со стандартными кнопками
            var btnSaveAll = UIFactory.CreateButton("Сохранить", (s, e) => SaveAllSettings());
            var btnClose = UIFactory.CreateButton("Закрыть", (s, e) => this.Close());
            var pnlBottomBar = UIFactory.CreateBottomBar(btnSaveAll, btnClose);

            _tabControl = new TabControl { Dock = DockStyle.Fill };

            // =========================================================================
            // === Вкладка 1: Основные настройки (Сервер и Диск) =======================
            // =========================================================================
            _tabGeneral = new TabPage("Сервер и Диск");
            var pnlGeneral = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12),
                AutoScroll = true
            };

            // 1. Рамка: Сетевой диск и служба
            var grpServerDrive = UIFactory.CreateGroupBox("Сетевой диск и служба");
            var pnlServerInner = UIFactory.CreateVerticalContainer();

            _chkWebDavEnabled = UIFactory.CreateCheckBox("Включить встроенный WebDAV сервер", _settings.Server.WebDavEnabled);
            _numPort = UIFactory.CreateNumericInput(1024, 65535, _settings.Server.Port, 80);
            var rowPort = UIFactory.CreateSettingRow("HTTP Порт:", _numPort, 140);

            _chkMountDrive = UIFactory.CreateCheckBox("Автоматически монтировать сетевой диск в Windows", _settings.Server.MountDrive);

            _cmbEngine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = UITheme.BaseFont };
            _cmbEngine.Items.Add("WinFsp (Прямой стриминг в ОЗУ)");
            _cmbEngine.Items.Add("WebDAV (Служба Windows WebClient)");
            _cmbEngine.SelectedIndex = _settings.Server.Engine == DriveEngine.WinFsp ? 0 : 1;
            var rowEngine = UIFactory.CreateSettingRow("Драйвер диска:", _cmbEngine, 140);

            _txtVolumeName = new TextBox { Text = _settings.Server.DriveName ?? "Telegram Drive", Width = 260, Font = UITheme.BaseFont };
            var rowVolumeName = UIFactory.CreateSettingRow("Имя тома:", _txtVolumeName, 140);

            _cmbDriveLetter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Font = UITheme.BaseFont };
            _cmbDriveLetter.Items.AddRange(new object[] { "Z:", "T:", "Y:", "X:", "W:", "AUTO" });
            _cmbDriveLetter.SelectedItem = _settings.Server.DriveLetter ?? "Z:";
            var rowDriveLetter = UIFactory.CreateSettingRow("Буква диска:", _cmbDriveLetter, 140);

            _numCapacityGb = UIFactory.CreateNumericInput(10, 1048576, _settings.Server.VirtualDiskCapacityGb > 0 ? _settings.Server.VirtualDiskCapacityGb : 1024, 80);
            var rowCapacity = UIFactory.CreateSettingRow("Размер диска (ГБ):", _numCapacityGb, 140);

            _chkAutoExpand = UIFactory.CreateCheckBox("Автоматически расширять диск при заполнении > 70%", _settings.Server.AutoExpandDiskCapacity);
            _chkIncludeTrashInSpace = UIFactory.CreateCheckBox("Учитывать файлы в корзине (.Trash) в занятом месте", _settings.Server.IncludeTrashInUsedSpace);

            pnlServerInner.Controls.AddRange(new Control[] {
                _chkWebDavEnabled, rowPort, _chkMountDrive, rowEngine, rowVolumeName, rowDriveLetter, rowCapacity, _chkAutoExpand, _chkIncludeTrashInSpace
            });
            grpServerDrive.Controls.Add(pnlServerInner);

            // 2. Рамка: Интеграция с Windows и проводником
            var grpIntegration = UIFactory.CreateGroupBox("Интеграция с Windows и проводником");
            var pnlIntegrationInner = UIFactory.CreateVerticalContainer();

            _chkAutoStart = UIFactory.CreateCheckBox("Автозапуск сервиса при входе в Windows", _settings.Server.AutoStartWithWindows);
            _chkHideTrash = UIFactory.CreateCheckBox("Скрывать папку .Trash из корня диска (чистый корень)", _settings.Server.HideTrashFromRoot);
            _chkContextMenu = UIFactory.CreateCheckBox("Пункт «Открыть корзину WebDAV» в контекстном меню Windows", _settings.Server.AddTrashToContextMenu);
            _chkAutoShowPopup = UIFactory.CreateCheckBox("Автоматически открывать карточку прогресса при отправке файлов", _settings.Server.AutoShowUploadPopup);
            _chkGalleryPreview = UIFactory.CreateCheckBox("Создавать фото-превью для галереи Telegram", _settings.Server.CreatePhotoGalleryPreview);

            pnlIntegrationInner.Controls.AddRange(new Control[] {
                _chkAutoStart, _chkHideTrash, _chkContextMenu, _chkAutoShowPopup, _chkGalleryPreview
            });
            grpIntegration.Controls.Add(pnlIntegrationInner);

            // 3. Рамка: Кэширование и стриминг
            var grpCache = UIFactory.CreateGroupBox("Кэширование, стриминг и скорость MTProto");
            var pnlCacheInner = UIFactory.CreateVerticalContainer();

            _chkEnableDiskCache = UIFactory.CreateCheckBox("Сохранять прочитанные файлы в дисковый кэш (%TEMP%)", _settings.Server.EnableDiskReadCache);

            _numDownloadWorkers = UIFactory.CreateNumericInput(
                ServerSettings.MinDownloadWorkerCount, 
                ServerSettings.MaxDownloadWorkerCount, 
                Math.Clamp(_settings.Server.DownloadWorkerCount, ServerSettings.MinDownloadWorkerCount, ServerSettings.MaxDownloadWorkerCount), 
                80);
            var rowDownloadWorkers = UIFactory.CreateSettingRow($"Параллельных воркеров MTProto ({ServerSettings.MinDownloadWorkerCount}-{ServerSettings.MaxDownloadWorkerCount}):", _numDownloadWorkers, 270);

            _numPacingDelayMs = UIFactory.CreateNumericInput(
                ServerSettings.MinPacingDelayMs, 
                ServerSettings.MaxPacingDelayMs, 
                Math.Clamp(_settings.Server.PacingDelayMs, ServerSettings.MinPacingDelayMs, ServerSettings.MaxPacingDelayMs), 
                80);
            var rowPacingDelay = UIFactory.CreateSettingRow("Задержка между запросами (мс):", _numPacingDelayMs, 270);

            _numMemoryCacheMb = UIFactory.CreateNumericInput(32, 4096, _settings.Server.MemoryCacheSizeMb > 0 ? _settings.Server.MemoryCacheSizeMb : 128, 80);
            var rowMemCache = UIFactory.CreateSettingRow("Буфер кэша в ОЗУ (МБ):", _numMemoryCacheMb, 270);

            _numChunkTtlMinutes = UIFactory.CreateNumericInput(1, 120, _settings.Server.ChunkMemoryCacheTtlMinutes > 0 ? _settings.Server.ChunkMemoryCacheTtlMinutes : 10, 80);
            var rowChunkTtl = UIFactory.CreateSettingRow("Время жизни кэша в ОЗУ (мин):", _numChunkTtlMinutes, 270);

            _numStreamingActivationMb = UIFactory.CreateNumericInput(1, 50, _settings.Server.StreamingActivationThresholdMb > 0 ? _settings.Server.StreamingActivationThresholdMb : 1, 80);
            var rowStreamingActivation = UIFactory.CreateSettingRow("Старт упреждения после (МБ):", _numStreamingActivationMb, 270);

            _numAudioWindowMb = UIFactory.CreateNumericInput(1, 100, _settings.Server.AudioPrefetchWindowMb > 0 ? _settings.Server.AudioPrefetchWindowMb : 2, 80);
            var rowAudioWindow = UIFactory.CreateSettingRow("Буфер упреждения для аудио (МБ):", _numAudioWindowMb, 270);

            _numStreamingWindowMb = UIFactory.CreateNumericInput(5, 200, _settings.Server.StreamingPrefetchWindowMb > 0 ? _settings.Server.StreamingPrefetchWindowMb : 20, 80);
            var rowStreamingWindow = UIFactory.CreateSettingRow("Буфер упреждения для видео/файлов (МБ):", _numStreamingWindowMb, 270);

            pnlCacheInner.Controls.AddRange(new Control[] {
                _chkEnableDiskCache, rowDownloadWorkers, rowPacingDelay, rowMemCache, rowChunkTtl, rowStreamingActivation, rowAudioWindow, rowStreamingWindow
            });
            grpCache.Controls.Add(pnlCacheInner);

            // 4. Рамка: База данных SQLite (base.db)
            var grpDb = UIFactory.CreateGroupBox("База данных SQLite (base.db)");
            var pnlDbInner = UIFactory.CreateVerticalContainer();

            var lblDbStats = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 4),
                Font = UITheme.SmallFont
            };

            Action updateDbStats = () =>
            {
                try
                {
                    string dbPath = _repository.DatabasePath;
                    if (File.Exists(dbPath))
                    {
                        var fi = new FileInfo(dbPath);
                        double sizeMb = (double)fi.Length / (1024 * 1024);
                        lblDbStats.Text = $"Размер базы на диске: {sizeMb:F2} МБ   |   Файл: {Path.GetFileName(dbPath)}";
                    }
                    else
                    {
                        lblDbStats.Text = $"Файл базы данных: {dbPath} (еще не создан)";
                    }
                }
                catch (Exception ex)
                {
                    lblDbStats.Text = $"Ошибка получения статуса БД: {ex.Message}";
                }
            };
            updateDbStats();

            var btnVacuum = UIFactory.CreateButton("🗜 Сжать базу (VACUUM)");
            btnVacuum.Click += async (s, e) =>
            {
                btnVacuum.Enabled = false;
                btnVacuum.Text = "Сжатие базы...";
                try
                {
                    await System.Threading.Tasks.Task.Run(() => _repository.VacuumDatabase());
                    updateDbStats();
                    MessageBox.Show("Оптимизация базы данных (VACUUM) успешно завершена!\nНеиспользуемое дисковое пространство освобождено.", "База данных", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка сжатия базы данных:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    btnVacuum.Text = "🗜 Сжать базу (VACUUM)";
                    btnVacuum.Enabled = true;
                }
            };

            var btnOpenDbFolder = UIFactory.CreateButton("📁 Папка с базой данных");
            btnOpenDbFolder.Click += (s, e) =>
            {
                try
                {
                    string fullPath = Path.GetFullPath(_repository.DatabasePath);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                    }
                    else
                    {
                        System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var rowDbButtons = UIFactory.CreateActionRow(btnVacuum, btnOpenDbFolder);
            pnlDbInner.Controls.AddRange(new Control[] { lblDbStats, rowDbButtons });
            grpDb.Controls.Add(pnlDbInner);

            pnlGeneral.Controls.AddRange(new Control[] { 
                grpServerDrive, 
                grpIntegration, 
                grpCache, 
                grpDb, 
                new Panel { Height = 10, Width = 10, Margin = Padding.Empty } 
            });
            _tabGeneral.Controls.Add(pnlGeneral);

            // =========================================================================
            // === Вкладка 2: Авторизация Telegram =====================================
            // =========================================================================
            _tabTelegram = new TabPage("Авторизация Telegram");
            var pnlTg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12),
                AutoScroll = true
            };

            // 1. Рамка: Параметры приложения (my.telegram.org)
            var grpTelegramApi = UIFactory.CreateGroupBox("Параметры приложения (my.telegram.org)");
            var pnlTgApiInner = UIFactory.CreateVerticalContainer();

            _txtApiId = new TextBox { Text = _settings.Telegram.ApiId > 0 ? _settings.Telegram.ApiId.ToString() : "", Width = 150, Font = UITheme.BaseFont };
            var rowApiId = UIFactory.CreateSettingRow("API ID:", _txtApiId, 90, 240);

            _txtApiHash = new TextBox { Text = _settings.Telegram.ApiHash ?? "", Width = 150, UseSystemPasswordChar = true, Font = UITheme.BaseFont };
            var rowApiHash = UIFactory.CreateSettingRow("API Hash:", _txtApiHash, 90, 240);

            _txtChannelTitle = new TextBox { Text = string.IsNullOrWhiteSpace(_settings.Telegram.StorageChannelTitle) ? "Telegram WebDAV Drive" : _settings.Telegram.StorageChannelTitle, Width = 150, Font = UITheme.BaseFont };
            var rowChannel = UIFactory.CreateSettingRow("Имя канала:", _txtChannelTitle, 90, 240);

            var btnSaveApi = UIFactory.CreateButton("Сохранить параметры Telegram", (s, e) => SaveTelegramApiKeys(), width: 240);

            pnlTgApiInner.Controls.AddRange(new Control[] { rowApiId, rowApiHash, rowChannel, btnSaveApi });
            grpTelegramApi.Controls.Add(pnlTgApiInner);

            // 2. Рамка: Текущая сессия Telegram
            var grpTelegramSession = UIFactory.CreateGroupBox("Текущая сессия Telegram");
            var pnlTgSessionInner = UIFactory.CreateVerticalContainer();

            _lblStatus = new Label
            {
                Text = "Статус: Проверка сессии...",
                AutoSize = true,
                Font = UITheme.HeaderFont,
                Margin = new Padding(0, 0, 0, 8)
            };

            // Информационная карточка профиля пользователя Telegram
            _pnlUserDetails = new TableLayoutPanel
            {
                Width = UIFactory.DefaultInnerWidth,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 6,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(8, 6, 8, 6),
                BackColor = UITheme.BackgroundLight
            };
            _pnlUserDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _pnlUserDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label createDetailHeader(string title) => new Label
            {
                Text = title,
                AutoSize = true,
                Font = UITheme.BoldFont,
                ForeColor = UITheme.TextMain,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 3, 4, 3)
            };

            Label createDetailValue() => new Label
            {
                Text = "—",
                AutoSize = true,
                Font = UITheme.BaseFont,
                ForeColor = UITheme.TextMain,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 3, 0, 3)
            };

            _lblUserName = createDetailValue();
            _lblUserTag = createDetailValue();
            _lblUserId = createDetailValue();
            _lblUserPhone = createDetailValue();
            _lblUserPremium = createDetailValue();
            _lblUserDc = createDetailValue();

            _pnlUserDetails.Controls.Add(createDetailHeader("👤 Имя и фамилия:"), 0, 0);
            _pnlUserDetails.Controls.Add(_lblUserName, 1, 0);

            _pnlUserDetails.Controls.Add(createDetailHeader("🏷 Никнейм:"), 0, 1);
            _pnlUserDetails.Controls.Add(_lblUserTag, 1, 1);

            _pnlUserDetails.Controls.Add(createDetailHeader("🆔 Telegram User ID:"), 0, 2);
            _pnlUserDetails.Controls.Add(_lblUserId, 1, 2);

            _pnlUserDetails.Controls.Add(createDetailHeader("📱 Номер телефона:"), 0, 3);
            _pnlUserDetails.Controls.Add(_lblUserPhone, 1, 3);

            _pnlUserDetails.Controls.Add(createDetailHeader("⭐ Подписка:"), 0, 4);
            _pnlUserDetails.Controls.Add(_lblUserPremium, 1, 4);

            _pnlUserDetails.Controls.Add(createDetailHeader("🌐 Дата-центр (DC):"), 0, 5);
            _pnlUserDetails.Controls.Add(_lblUserDc, 1, 5);

            _lblInstruction = new Label
            {
                Text = "Введите номер телефона в международном формате (+7...):",
                AutoSize = true,
                Font = UITheme.BaseFont,
                Margin = new Padding(0, 0, 0, 6)
            };

            _txtInput = new TextBox { Width = UIFactory.DefaultInnerWidth, Font = UITheme.BaseFont, Margin = new Padding(0, 0, 0, 8) };

            _btnAction = UIFactory.CreateButton("Отправить", async (s, e) => await HandleTelegramActionAsync(), width: 180);
            _btnLogout = UIFactory.CreateButton("Выйти из аккаунта", (s, e) => HandleTelegramLogout(), width: 160, foreColor: UITheme.TextDanger);

            var pnlTgButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            pnlTgButtons.Controls.AddRange(new Control[] { _btnAction, _btnLogout });

            pnlTgSessionInner.Controls.AddRange(new Control[] { _lblStatus, _pnlUserDetails, _lblInstruction, _txtInput, pnlTgButtons });
            grpTelegramSession.Controls.Add(pnlTgSessionInner);

            pnlTg.Controls.AddRange(new Control[] { 
                grpTelegramApi, 
                grpTelegramSession, 
                new Panel { Height = 10, Width = 10, Margin = Padding.Empty } 
            });
            _tabTelegram.Controls.Add(pnlTg);

            // =========================================================================
            // === Вкладка 3: Патч реестра Windows =====================================
            // =========================================================================
            _tabRegistry = new TabPage("Патч реестра Windows");
            var pnlReg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12),
                AutoScroll = true
            };

            var grpRegistry = UIFactory.CreateGroupBox("Системные твики службы Windows WebClient и UAC");
            var pnlRegInner = UIFactory.CreateVerticalContainer();

            _lblRegStatus = new Label
            {
                Text = "Комплексный фикс реестра Windows для служб WebDAV и виртуальных дисков:\n\n" +
                       "1. BasicAuthLevel = 2 — разрешение подключения WebDAV по HTTP в локальной сети.\n" +
                       "2. FileSizeLimitInBytes = 4 GB — снятие системного лимита в 50 МБ на файл (ошибка 0x800700DF).\n" +
                       "3. EnableLinkedConnections = 1 — сквозная видимость дисков между сессиями пользователя и Администратора.\n" +
                       "4. ZoneMap (Местная интрасеть) — устранение системных предупреждений безопасности при копировании файлов.",
                AutoSize = true,
                Width = UIFactory.DefaultInnerWidth,
                MaximumSize = new Size(UIFactory.DefaultInnerWidth, 0),
                Font = UITheme.BaseFont,
                Margin = new Padding(0, 2, 0, 10)
            };

            _lblRegCurrentState = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(UIFactory.DefaultInnerWidth, 0),
                Font = UITheme.HeaderFont,
                Margin = new Padding(0, 0, 0, 10)
            };

            Action refreshRegState = () =>
            {
                bool isApplied = WindowsRegistryFixer.IsAllFixesApplied();
                if (isApplied)
                {
                    _lblRegCurrentState.Text = "Статус: Все системные патчи успешно применены ✔";
                    _lblRegCurrentState.ForeColor = UITheme.TextSuccess;
                }
                else
                {
                    _lblRegCurrentState.Text = "Статус: Рекомендуется применить системные патчи реестра ⚠";
                    _lblRegCurrentState.ForeColor = Color.DarkOrange;
                }
            };
            refreshRegState();

            _btnApplyRegFix = UIFactory.CreateButton("🛡 Применить комплексный фикс реестра (с запросом UAC)", null, height: 34, width: UIFactory.DefaultInnerWidth);
            _btnApplyRegFix.Margin = new Padding(0, 6, 0, 8);
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

            pnlRegInner.Controls.AddRange(new Control[] { _lblRegStatus, _lblRegCurrentState, _btnApplyRegFix });
            grpRegistry.Controls.Add(pnlRegInner);

            pnlReg.Controls.AddRange(new Control[] { 
                grpRegistry, 
                new Panel { Height = 10, Width = 10, Margin = Padding.Empty } 
            });
            _tabRegistry.Controls.Add(pnlReg);

            // =========================================================================
            // === Вкладка 4: Логирование ==============================================
            // =========================================================================
            _tabLogs = new TabPage("Логирование");
            var pnlLogs = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12),
                AutoScroll = true
            };

            // 1. Рамка: Уровни логирования
            var grpLogLevels = UIFactory.CreateGroupBox("Уровни логирования");
            var pnlLogLevels = UIFactory.CreateVerticalContainer();

            _chkLogDebug = UIFactory.CreateCheckBox("Debug — подробная техническая отладка (COM-интерфейсы, внутренние вызовы)", _settings.Logging.EnableDebug);
            _chkLogInfo = UIFactory.CreateCheckBox("Info — стандартные информационные события (файлы, загрузки, запуски)", _settings.Logging.EnableInfo);
            _chkLogWarn = UIFactory.CreateCheckBox("Warn — предупреждения и некритичные отклонения", _settings.Logging.EnableWarn);
            _chkLogError = UIFactory.CreateCheckBox("Error — ошибки приложения и сбои операций", _settings.Logging.EnableError);

            pnlLogLevels.Controls.AddRange(new Control[] { _chkLogDebug, _chkLogInfo, _chkLogWarn, _chkLogError });
            grpLogLevels.Controls.Add(pnlLogLevels);

            // 2. Рамка: Параметры хранения и ротации
            var grpRotation = UIFactory.CreateGroupBox("Параметры хранения и ротации лог-файлов");
            var pnlRotation = UIFactory.CreateVerticalContainer();

            _numMaxLogMb = UIFactory.CreateNumericInput(1, 50, Math.Clamp(_settings.Logging.MaxLogFileSizeMb > 0 ? _settings.Logging.MaxLogFileSizeMb : 5, 1, 50), 90);
            var rowMaxMb = UIFactory.CreateSettingRow("Максимальный размер одного файла лога (МБ):", _numMaxLogMb, 330);

            _numMaxLogFiles = UIFactory.CreateNumericInput(1, 10, Math.Clamp(_settings.Logging.MaxArchivedFiles > 0 ? _settings.Logging.MaxArchivedFiles : 3, 1, 10), 90);
            var rowMaxFiles = UIFactory.CreateSettingRow("Количество хранящихся архивных файлов:", _numMaxLogFiles, 330);

            pnlRotation.Controls.AddRange(new Control[] { rowMaxMb, rowMaxFiles });
            grpRotation.Controls.Add(pnlRotation);

            // 3. Рамка: Текущая информация и действия
            var grpStats = UIFactory.CreateGroupBox("Текущая информация и действия");
            var pnlStatsInner = UIFactory.CreateVerticalContainer();

            _lblLogStats = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 4),
                Font = UITheme.SmallFont
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

            var btnOpenLogDir = UIFactory.CreateButton("📁 Папка с логами");
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

            var btnOpenLogFile = UIFactory.CreateButton("📄 Открыть app.log");
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

            var btnClearLogs = UIFactory.CreateButton("🗑 Очистить логи", foreColor: UITheme.TextDanger);
            btnClearLogs.Click += (s, e) =>
            {
                if (MessageBox.Show("Вы действительно хотите очистить текущий лог и все его архивы?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    AppLogger.ClearLogs();
                    updateLogStats();
                    MessageBox.Show("Все логи успешно очищены!", "Логирование", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            var rowLogActions = UIFactory.CreateActionRow(btnOpenLogDir, btnOpenLogFile, btnClearLogs);
            pnlStatsInner.Controls.AddRange(new Control[] { _lblLogStats, rowLogActions });
            grpStats.Controls.Add(pnlStatsInner);

            pnlLogs.Controls.AddRange(new Control[] { 
                grpLogLevels, 
                grpRotation, 
                grpStats, 
                new Panel { Height = 10, Width = 10, Margin = Padding.Empty } 
            });
            _tabLogs.Controls.Add(pnlLogs);

            _tabControl.TabPages.AddRange(new TabPage[] { _tabGeneral, _tabTelegram, _tabRegistry, _tabLogs });
            this.Controls.Add(_tabControl);
            this.Controls.Add(pnlBottomBar);
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
                _lblStatus.Text = "Статус: Авторизован в Telegram ✔";
                _lblStatus.ForeColor = UITheme.TextSuccess;

                if (user != null)
                {
                    _lblUserName.Text = user.FullName;
                    _lblUserTag.Text = user.FormattedUsername;
                    _lblUserId.Text = user.Id.ToString();
                    _lblUserPhone.Text = user.FormattedPhone;
                    _lblUserPremium.Text = user.PremiumDescription;
                    if (user.IsPremium)
                    {
                        _lblUserPremium.ForeColor = Color.FromArgb(170, 95, 0);
                    }
                    else
                    {
                        _lblUserPremium.ForeColor = UITheme.TextMain;
                    }
                    _lblUserDc.Text = user.DcDescription;
                }

                _pnlUserDetails.Visible = true;
                _lblInstruction.Text = "Сессия активна. Мультимедиа файлы и папки доступны через WebDAV.";
                _lblInstruction.ForeColor = UITheme.TextMuted;
                _lblInstruction.Visible = true;
                _txtInput.Visible = false;
                _btnAction.Visible = false;
                _btnLogout.Visible = true;
            }
            else
            {
                _lblStatus.Text = "Статус: Не авторизован ❌";
                _lblStatus.ForeColor = UITheme.TextDanger;
                _pnlUserDetails.Visible = false;
                _txtInput.Visible = hasApiKeys;
                _btnAction.Visible = hasApiKeys;
                _btnLogout.Visible = false;

                if (!hasApiKeys)
                {
                    _lblInstruction.Text = "Сначала укажите и сохраните API ID и API Hash (получить на my.telegram.org).";
                    _lblInstruction.ForeColor = Color.DarkOrange;
                    _lblInstruction.Visible = true;
                }
                else
                {
                    _lblInstruction.ForeColor = UITheme.TextMain;
                    _lblInstruction.Visible = true;
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

        private void SaveAllSettings()
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
            _settings.Server.CreatePhotoGalleryPreview = _chkGalleryPreview.Checked;
            _settings.Server.EnableDiskReadCache = _chkEnableDiskCache.Checked;
            _settings.Server.DownloadWorkerCount = (int)_numDownloadWorkers.Value;
            _settings.Server.PacingDelayMs = (int)_numPacingDelayMs.Value;
            TelegramService.SetPacingDelay(_settings.Server.PacingDelayMs);
            _settings.Server.MemoryCacheSizeMb = (int)_numMemoryCacheMb.Value;
            _settings.Server.ChunkMemoryCacheTtlMinutes = (int)_numChunkTtlMinutes.Value;
            _settings.Server.StreamingActivationThresholdMb = (int)_numStreamingActivationMb.Value;
            _settings.Server.AudioPrefetchWindowMb = (int)_numAudioWindowMb.Value;
            _settings.Server.StreamingPrefetchWindowMb = (int)_numStreamingWindowMb.Value;

            _settings.Logging.EnableDebug = _chkLogDebug.Checked;
            _settings.Logging.EnableInfo = _chkLogInfo.Checked;
            _settings.Logging.EnableWarn = _chkLogWarn.Checked;
            _settings.Logging.EnableError = _chkLogError.Checked;
            _settings.Logging.MaxLogFileSizeMb = (int)_numMaxLogMb.Value;
            _settings.Logging.MaxArchivedFiles = (int)_numMaxLogFiles.Value;

            if (_chkContextMenu.Checked)
            {
                ShellContextMenuHelper.RegisterTrashContextMenu(_settings.Server.DriveLetter);
            }
            else
            {
                ShellContextMenuHelper.UnregisterTrashContextMenu();
            }

            _configManager.Save(_settings);
            _telegramService.UpdateSettings(_settings);
            SettingsSaved?.Invoke();

            string msg = "Все настройки успешно сохранены!";
            if (oldEngine != _settings.Server.Engine)
            {
                msg += $"\n\nДрайвер диска изменен на {_settings.Server.Engine}. Выполните «Переподключить сетевой диск» в трее или перезапустите сервис для применения изменений.";
            }
            MessageBox.Show(msg, "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
