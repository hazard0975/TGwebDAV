using System;

namespace TelegramWebDAV.Models
{
    /// <summary>
    /// Канал-хранилище Telegram для файлов и папок виртуального диска.
    /// </summary>
    public class TelegramChannel
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
        public long ChannelId { get; set; }
        public long AccessHash { get; set; }
        public string Title { get; set; } = "Telegram WebDAV Drive";
        public bool IsPrimary { get; set; } = true;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
