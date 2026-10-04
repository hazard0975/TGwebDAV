using System;
using System.Text.Json.Serialization;

namespace TelegramWebDAV.Config
{
    public enum DriveEngine
    {
        WinFsp,
        WebDav
    }

    public class AppSettings
    {
        public TelegramSettings Telegram { get; set; } = new TelegramSettings();
        public ServerSettings Server { get; set; } = new ServerSettings();
        public DatabaseSettings Database { get; set; } = new DatabaseSettings();
        public LoggingSettings Logging { get; set; } = new LoggingSettings();

        // Обратная совместимость
        public WebDavSettings WebDav => new WebDavSettings
        {
            Port = Server.Port,
            DriveLetter = Server.DriveLetter,
            DriveName = Server.DriveName,
            AutoMountOnStartup = Server.MountDrive
        };
    }

    public class TelegramSettings
    {
        public int ApiId { get; set; } = 0;
        public string ApiHash { get; set; } = "";
        public string SessionPath { get; set; } = "user.session";
        public string StorageChannelTitle { get; set; } = "Telegram WebDAV Drive";
        public long StorageChannelId { get; set; } = 0;
        public long StorageChannelAccessHash { get; set; } = 0;
        public string? PhoneNumber { get; set; }
    }

    public class ServerSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DriveEngine Engine { get; set; } = DriveEngine.WinFsp;

        public bool WebDavEnabled { get; set; } = true;
        public int Port { get; set; } = 37000;
        public bool MountDrive { get; set; } = true;
        public string DriveLetter { get; set; } = "Z:";
        public string DriveName { get; set; } = "Telegram Drive";
        public bool AutoStartWithWindows { get; set; } = true;
        public bool HideTrashFromRoot { get; set; } = true;
        public bool AddTrashToContextMenu { get; set; } = true;
        public bool AutoShowUploadPopup { get; set; } = true;

        // Настройки емкости виртуального диска
        public long VirtualDiskCapacityGb { get; set; } = 1024;
        public bool AutoExpandDiskCapacity { get; set; } = true;
        public bool IncludeTrashInUsedSpace { get; set; } = true;

        // Публикация изображений
        public bool CreatePhotoGalleryPreview { get; set; } = true; // Создавать фото-превью для нативной галереи Telegram

        // Настройки кэширования
        public bool EnableDiskReadCache { get; set; } = false; // По умолчанию 100% через ОЗУ
        public int MemoryCacheSizeMb { get; set; } = 128; // Динамический RAM-буфер 128 МБ
        public int ChunkMemoryCacheTtlMinutes { get; set; } = 10; // Время жизни чанков в ОЗУ (минут)
        public int AudioPrefetchWindowMb { get; set; } = 2; // Буфер претча для аудиофайлов (.mp3, .flac, .ogg и т.д.) - по умолчанию 2 МБ
        public int StreamingActivationThresholdMb { get; set; } = 1; // Объем вычитанных данных (МБ) для старта упреждения/префетча
        public int StreamingPrefetchWindowMb { get; set; } = 20; // Окно упреждения для видео и крупных файлов
    }

    public class DatabaseSettings
    {
        public string Path { get; set; } = "base.db";
    }

    public class WebDavSettings
    {
        public int Port { get; set; } = 37000;
        public string DriveLetter { get; set; } = "Z:";
        public string DriveName { get; set; } = "Telegram Drive";
        public bool AutoMountOnStartup { get; set; } = true;
    }

    public class LoggingSettings
    {
        public bool EnableDebug { get; set; } = false;
        public bool EnableInfo { get; set; } = true;
        public bool EnableWarn { get; set; } = true;
        public bool EnableError { get; set; } = true;

        public int MaxLogFileSizeMb { get; set; } = 5;
        public int MaxArchivedFiles { get; set; } = 3;
    }
}
