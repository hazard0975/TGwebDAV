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
}
