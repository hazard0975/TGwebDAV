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
        [STAThread]
        private static async Task Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Console.WriteLine("=================================================");
            Console.WriteLine(" Telegram WebDAV & Network Drive Service v2.0");
            Console.WriteLine("=================================================");

            // 1. Инициализация конфигурации (appsettings.json)
            var configManager = new ConfigManager();
            var settings = configManager.Load();

            // 2. Инициализация базы данных SQLite с нуля (base.db + WAL)
            var dbManager = new DatabaseManager(settings.Database.Path);
            dbManager.InitializeDatabase();
            var repository = new NodeRepository(dbManager);

            // 3. Инициализация сервиса Telegram (WTelegramClient)
            var telegramService = new TelegramService(configManager);
            await telegramService.ConnectAsync();

            // 4. Запуск встроенного WebDAV сервера на порту 37000 (или из конфига)
            var webDavServer = new WebDavServer(configManager, repository, telegramService);
            if (settings.Server.WebDavEnabled)
            {
                webDavServer.Start();
            }

            // 5. Запуск приложения в системном трее Windows
            Application.Run(new TrayContext(configManager, repository, telegramService, webDavServer));
        }
    }
}
