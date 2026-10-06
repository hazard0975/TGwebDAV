using System;

namespace TelegramWebDAV.Models
{
    public class Node
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // Telegram Data
        public int? TgMessageId { get; set; }
        public int? TgPreviewMessageId { get; set; }
        
        // Versioning & Trash
        public int Version { get; set; } = 1;
        public bool InTrash { get; set; }
        public bool IsDeleted { get => InTrash; set => InTrash = value; }
        public int? OriginalNodeId { get; set; }

        // Local payload for small files (<= 1 byte or unuploaded placeholders)
        public byte[]? InlineData { get; set; }
    }
}
