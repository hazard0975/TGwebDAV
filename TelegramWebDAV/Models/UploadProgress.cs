using System;

namespace TelegramWebDAV.Models
{
    public class UploadProgress
    {
        public int Id { get; set; }
        public int NodeId { get; set; }
        public long ChunkPosition { get; set; }
        public string FileHash { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
