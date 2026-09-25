using System;
using System.IO;
using System.Text.Json;

namespace TelegramWebDAV.Config
{
    public class ConfigManager
    {
        private readonly string _configPath;
        private readonly object _lock = new object();

        public ConfigManager(string configPath = "appsettings.json")
        {
            _configPath = configPath;
        }

        public AppSettings Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_configPath))
                {
                    var defaultSettings = new AppSettings();
                    SaveNoLock(defaultSettings);
                    return defaultSettings;
                }

                try
                {
                    string json = File.ReadAllText(_configPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка чтения конфигурации: {ex.Message}");
                    return new AppSettings();
                }
            }
        }

        public AppSettings CurrentSettings => Load();

        public void Save(AppSettings settings)
        {
            lock (_lock)
            {
                SaveNoLock(settings);
            }
        }

        private void SaveNoLock(AppSettings settings)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка сохранения конфигурации: {ex.Message}");
            }
        }
    }
}
