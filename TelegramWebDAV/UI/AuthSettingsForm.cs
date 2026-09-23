using System;
using System.Drawing;
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
        private TabControl _tabControl;
        private TabPage _tabGeneral;
        private TabPage _tabTelegram;
        private TabPage _tabRegistry;

        // General Tab
        private CheckBox _chkWebDavEnabled;
        private NumericUpDown _numPort;
        private CheckBox _chkMountDrive;
        private ComboBox _cmbDriveLetter;
        private TextBox _txtVolumeName;
        private CheckBox _chkAutoStart;

        // Telegram Tab
        private Label _lblStatus;
        private TextBox _txtInput;
        private Button _btnAction;
        private Button _btnLogout;
        private Label _lblInstruction;

        // Registry Tab
        private Label _lblRegStatus;
        private Button _btnApplyRegFix;

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
            this.Size = new Size(520, 420);
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

            _chkMountDrive = new CheckBox
            {
                Text = "Автоматически монтировать сетевой диск в Windows",
                Checked = _settings.Server.MountDrive,
                AutoSize = true,
                Margin = new Padding(0, 15, 0, 10)
            };

            var pnlDrive = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            pnlDrive.Controls.Add(new Label { Text = "Буква диска:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _cmbDriveLetter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            _cmbDriveLetter.Items.AddRange(new object[] { "Z:", "T:", "Y:", "X:", "W:", "AUTO" });
            _cmbDriveLetter.SelectedItem = _settings.Server.DriveLetter ?? "Z:";
            pnlDrive.Controls.Add(_cmbDriveLetter);

            var pnlVol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 0) };
            pnlVol.Controls.Add(new Label { Text = "Имя тома:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) });
            _txtVolumeName = new TextBox { Text = _settings.Server.DriveName ?? "Telegram Drive", Width = 150 };
            pnlVol.Controls.Add(_txtVolumeName);

            _chkAutoStart = new CheckBox
            {
                Text = "Автозапуск сервиса при входе в Windows",
                Checked = _settings.Server.AutoStartWithWindows,
                AutoSize = true,
                Margin = new Padding(0, 15, 0, 10)
            };

            var btnSaveGeneral = new Button
            {
                Text = "Сохранить настройки",
                AutoSize = true,
                Padding = new Padding(10, 5, 10, 5),
                Margin = new Padding(0, 15, 0, 0)
            };
            btnSaveGeneral.Click += (s, e) => SaveGeneralSettings();

            pnlGeneral.Controls.AddRange(new Control[] {
                _chkWebDavEnabled, pnlPort, _chkMountDrive, pnlDrive, pnlVol, _chkAutoStart, btnSaveGeneral
            });
            _tabGeneral.Controls.Add(pnlGeneral);

            // === Вкладка 2: Авторизация Telegram ===
            _tabTelegram = new TabPage("Авторизация Telegram");
            var pnlTg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(15)
            };

            _lblStatus = new Label
            {
                Text = "Статус: Проверка сессии...",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 5, 0, 15)
            };

            _lblInstruction = new Label
            {
                Text = "Введите номер телефона в международном формате (+7...):",
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 5)
            };

            _txtInput = new TextBox { Width = 300, Margin = new Padding(0, 0, 0, 10) };

            var pnlTgButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _btnAction = new Button { Text = "Отправить", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
            _btnAction.Click += async (s, e) => await HandleTelegramActionAsync();

            _btnLogout = new Button { Text = "Выйти из аккаунта", AutoSize = true, Padding = new Padding(10, 5, 10, 5), ForeColor = Color.DarkRed };
            _btnLogout.Click += (s, e) => HandleTelegramLogout();

            pnlTgButtons.Controls.AddRange(new Control[] { _btnAction, _btnLogout });

            pnlTg.Controls.AddRange(new Control[] { _lblStatus, _lblInstruction, _txtInput, pnlTgButtons });
            _tabTelegram.Controls.Add(pnlTg);

            // === Вкладка 3: Патч реестра Windows ===
            _tabRegistry = new TabPage("Патч реестра Windows");
            var pnlReg = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(15)
            };

            _lblRegStatus = new Label
            {
                Text = "Комплексный фикс реестра для встроенного WebClient Windows:\n" +
                       "1. BasicAuthLevel = 2 — разрешение подключения по HTTP в локальной сети.\n" +
                       "2. FileSizeLimitInBytes = 4 GB — снятие стандартного ограничения Windows в 50 МБ на файл (ошибка 0x800700DF в Проводнике).",
                AutoSize = true,
                MaximumSize = new Size(450, 0),
                Margin = new Padding(0, 5, 0, 15)
            };

            _btnApplyRegFix = new Button
            {
                Text = "Применить комплексный фикс реестра (HTTP + 4 ГБ)",
                AutoSize = true,
                Padding = new Padding(10, 8, 10, 8)
            };
            _btnApplyRegFix.Click += (s, e) =>
            {
                bool success = WindowsRegistryFixer.ApplyAllFixes();
                if (success)
                {
                    MessageBox.Show("Патчи реестра успешно применены (HTTP разрешен, лимит 50 МБ снят до 4 ГБ)!\n\nРекомендуется перезапустить службу WebClient или перезагрузить компьютер.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Не удалось применить изменения. Запустите приложение от имени Администратора.", "Ошибка доступа", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            pnlReg.Controls.AddRange(new Control[] { _lblRegStatus, _btnApplyRegFix });
            _tabRegistry.Controls.Add(pnlReg);

            _tabControl.TabPages.AddRange(new TabPage[] { _tabGeneral, _tabTelegram, _tabRegistry });
            this.Controls.Add(_tabControl);
        }

        private void UpdateUiState()
        {
            if (_telegramService.IsAuthorized)
            {
                _lblStatus.Text = "Статус: Авторизован в Telegram ✔";
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
                _txtInput.Visible = true;
                _btnAction.Visible = true;
                _btnLogout.Visible = false;

                switch (_telegramService.CurrentStep)
                {
                    case AuthStep.NeedsPhone:
                        _lblInstruction.Text = "Введите номер телефона (+7...):";
                        _btnAction.Text = "Получить код в Telegram";
                        break;
                    case AuthStep.NeedsCode:
                        _lblInstruction.Text = "Введите проверочный 5-значный код из Telegram:";
                        _btnAction.Text = "Подтвердить код";
                        break;
                    case AuthStep.Needs2FA:
                        _lblInstruction.Text = "Введите облачный пароль 2FA:";
                        _btnAction.Text = "Войти";
                        _txtInput.UseSystemPasswordChar = true;
                        break;
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
                string nextRequirement = await _telegramService.LoginStepAsync(input);
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
            _settings.Server.WebDavEnabled = _chkWebDavEnabled.Checked;
            _settings.Server.Port = (int)_numPort.Value;
            _settings.Server.MountDrive = _chkMountDrive.Checked;
            _settings.Server.DriveLetter = _cmbDriveLetter.SelectedItem?.ToString() ?? "Z:";
            _settings.Server.DriveName = _txtVolumeName.Text.Trim();
            _settings.Server.AutoStartWithWindows = _chkAutoStart.Checked;

            _configManager.Save(_settings);
            MessageBox.Show("Настройки успешно сохранены в appsettings.json!", "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
