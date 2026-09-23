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
                var telegramService = new TelegramService(configManager);
                await telegramService.ConnectAsync();

                // 4. Запуск встроенного WebDAV сервера на порту 37000 (или из конфига)
                var webDavServer = new WebDavServer(configManager, repository, telegramService);
                if (settings.Server.WebDavEnabled)
                {
                    webDavServer.Start();
                    AppLogger.Info("Program", $"Встроенный WebDAV сервер запущен: http://localhost:{settings.Server.Port}/");
                }

                // 5. Запуск приложения в системном трее Windows
                Application.Run(new TrayContext(configManager, repository, telegramService, webDavServer));
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
    }
}
