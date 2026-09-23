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
        
        // Versioning & Trash
        public int Version { get; set; }
        public bool IsDeleted { get; set; }
        public int? OriginalNodeId { get; set; }

        // Audio Metadata
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public int? TrackNumber { get; set; }
        public int? DurationSeconds { get; set; }
        public int? Bitrate { get; set; }

        // Cache
        public byte[]? HeaderCacheBytes { get; set; }
        public byte[]? AlbumCoverBytes { get; set; }
    }
}
