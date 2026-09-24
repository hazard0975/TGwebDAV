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
        private TabControl _tabControl = null!;
        private TabPage _tabGeneral = null!;
        private TabPage _tabTelegram = null!;
        private TabPage _tabRegistry = null!;

        // General Tab
        private CheckBox _chkWebDavEnabled = null!;
        private NumericUpDown _numPort = null!;
        private CheckBox _chkMountDrive = null!;
        private ComboBox _cmbDriveLetter = null!;
        private TextBox _txtVolumeName = null!;
        private CheckBox _chkAutoStart = null!;
        private CheckBox _chkHideTrash = null!;
        private CheckBox _chkContextMenu = null!;
        private CheckBox _chkAutoShowPopup = null!;

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
            this.Size = new Size(570, 520);
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
                Margin = new Padding(0, 15, 0, 5)
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
                Margin = new Padding(0, 5, 0, 10)
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
                _chkWebDavEnabled, pnlPort, _chkMountDrive, pnlDrive, pnlVol, _chkAutoStart, _chkHideTrash, _chkContextMenu, _chkAutoShowPopup, btnSaveGeneral
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
            _settings.Server.WebDavEnabled = _chkWebDavEnabled.Checked;
            _settings.Server.Port = (int)_numPort.Value;
            _settings.Server.MountDrive = _chkMountDrive.Checked;
            _settings.Server.DriveLetter = _cmbDriveLetter.SelectedItem?.ToString() ?? "Z:";
            _settings.Server.DriveName = _txtVolumeName.Text.Trim();
            _settings.Server.AutoStartWithWindows = _chkAutoStart.Checked;
            _settings.Server.HideTrashFromRoot = _chkHideTrash.Checked;
            _settings.Server.AddTrashToContextMenu = _chkContextMenu.Checked;
            _settings.Server.AutoShowUploadPopup = _chkAutoShowPopup.Checked;

            if (_chkContextMenu.Checked)
            {
                ShellContextMenuHelper.RegisterTrashContextMenu(_settings.Server.DriveLetter);
            }
            else
            {
                ShellContextMenuHelper.UnregisterTrashContextMenu();
            }

            _configManager.Save(_settings);
            MessageBox.Show("Настройки успешно сохранены в appsettings.json!", "Telegram WebDAV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
