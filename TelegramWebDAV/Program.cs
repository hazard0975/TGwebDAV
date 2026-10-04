using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegramWebDAV.Config;
using TelegramWebDAV.Database;
using TelegramWebDAV.Server;
using TelegramWebDAV.Services;
using TelegramWebDAV.UI;

namespace TelegramWebDAV
{
    /// <summary>
    /// Главная точка входа универсального сервиса Telegram WebDAV и сетевого диска.
    /// </summary>
    internal static class Program
    {
        private static System.Threading.Mutex? _singleInstanceMutex;

        [STAThread]
        private static async Task Main(string[] args)
        {
            // Принудительно фиксируем рабочую директорию на папку с исполняемым файлом
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Обработка запроса на применение твиков реестра с повышенными привилегиями (UAC)
            if (args != null && args.Length > 0 && args[0] == "--apply-registry-fix")
            {
                bool success = TelegramWebDAV.Utils.WindowsRegistryFixer.ApplyAllFixes();
                Environment.Exit(success ? 0 : 1);
                return;
            }

            // Обработка запроса из контекстного меню Проводника «Открыть в Telegram»
            if (args != null && args.Length >= 2 && args[0] == "--open-in-tg")
            {
                HandleOpenInTelegram(args[1]);
                return;
            }

            // Включаем системную поддержку Assembly.Location в .NET 8 для корректной работы сторонних библиотек (WinFsp)
            AppContext.SetData("Switch.System.Reflection.Assembly.Location.IncludeInSingleFileApp", true);

            // Глобальные перехватчики неперехваченных ошибок
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                AppLogger.Error("Program", $"UnhandledException: {ex?.Message}", ex);
                AppLogger.Shutdown();
            };

            Application.ThreadException += (s, e) =>
            {
                AppLogger.Error("Program", $"ThreadException: {e.Exception.Message}", e.Exception);
                AppLogger.Shutdown();
            };

            AppLogger.Info("Program", "=================================================");
            AppLogger.Info("Program", " Telegram WebDAV & Network Drive Service v2.0");
            AppLogger.Info("Program", "=================================================");

            const string mutexName = @"Global\TelegramWebDAV_SingleInstance_Mutex";
            bool createdNew = false;
            try
            {
                _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out createdNew);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Program", $"Предупреждение при инициализации Mutex: {ex.Message}");
                createdNew = true; // Если произошла ошибка доступа к Mutex, разрешаем запуск
            }

            if (!createdNew)
            {
                AppLogger.Warn("Program", "Обнаружен уже запущенный экземпляр службы Telegram WebDAV. Завершение работы второго процесса.");
                AppLogger.Shutdown();
                MessageBox.Show(
                    "Служба Telegram WebDAV уже запущенa и работает в системном трее Windows.",
                    "Telegram WebDAV Service",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 1. Инициализация конфигурации (appsettings.json)
                var configManager = new ConfigManager();
                var settings = configManager.Load();
                AppLogger.Info("Program", $"Конфигурация загружена. Порт: {settings.Server.Port}, Диск: {settings.Server.DriveLetter}");

                // 2. Инициализация базы данных SQLite с нуля (base.db + WAL)
                var dbManager = new DatabaseManager(settings.Database.Path);
                dbManager.InitializeDatabase();
                var repository = new NodeRepository(dbManager);
                AppLogger.Info("Program", $"База данных SQLite инициализирована по пути: {settings.Database.Path}");

                // 3. Инициализация сервиса Telegram (WTelegramClient)
                var telegramService = new TelegramService(configManager, repository);
                await telegramService.ConnectAsync();

                // 4. Запуск встроенного WebDAV сервера на порту 37000 (или из конфига)
                var webDavServer = new WebDavServer(configManager, repository, telegramService);
                if (settings.Server.WebDavEnabled)
                {
                    webDavServer.Start();
                    AppLogger.Info("Program", $"Встроенный WebDAV сервер запущен: http://localhost:{settings.Server.Port}/");
                }

                // 5. Инициализация менеджера виртуального диска (WinFsp / WebDAV)
                var winFspServer = new WinFspServer(configManager, repository, telegramService);
                var virtualDriveManager = new VirtualDriveManager(configManager, winFspServer);

                // 6. Запуск приложения в системном трее Windows
                Application.Run(new TrayContext(configManager, repository, telegramService, webDavServer, virtualDriveManager));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Program", $"Критическая ошибка при запуске службы Telegram WebDAV: {ex.Message}", ex);
                MessageBox.Show(
                    $"Не удалось запустить службу Telegram WebDAV:\n\n{ex.Message}\n\nПодробности записаны в файл logs/app.log.",
                    "Критическая ошибка запуска",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                AppLogger.Shutdown();
                if (_singleInstanceMutex != null)
                {
                    try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                    _singleInstanceMutex.Dispose();
                }
            }
        }

        private static void HandleOpenInTelegram(string rawPath)
        {
            try
            {
                var configManager = new ConfigManager();
                var settings = configManager.Load();

                string trimmed = rawPath.Trim();
                int colonIdx = trimmed.IndexOf(':');
                string relPath = colonIdx >= 0 ? trimmed.Substring(colonIdx + 1) : trimmed;
                relPath = relPath.Replace('\\', '/').Trim();
                if (!relPath.StartsWith("/")) relPath = "/" + relPath;

                var dbManager = new DatabaseManager(settings.Database.Path);
                dbManager.InitializeDatabase();
                var repository = new NodeRepository(dbManager);
                var node = repository.GetNodeByPath(relPath);

                if (node != null && node.TgMessageId.HasValue && node.TgMessageId.Value > 1)
                {
                    long channelId = settings.Telegram.StorageChannelId;
                    if (channelId != 0)
                    {
                        string cleanChannel = channelId.ToString().Trim();
                        if (cleanChannel.StartsWith("-100")) cleanChannel = cleanChannel.Substring(4);
                        else if (cleanChannel.StartsWith("-")) cleanChannel = cleanChannel.Substring(1);

                        string url = $"https://t.me/c/{cleanChannel}/{node.TgMessageId.Value}";
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                MessageBox.Show(
                    $"Файл '{relPath}' не имеет сохраненного Message ID в Telegram или хранится локально.",
                    "Telegram WebDAV",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось открыть сообщение в Telegram:\n{ex.Message}",
                    "Telegram WebDAV",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
