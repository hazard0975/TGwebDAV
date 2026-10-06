using System;

namespace TelegramWebDAV.Models
{
    /// <summary>
    /// Профиль и учетные данные аккаунта Telegram (my.telegram.org + сессия).
    /// </summary>
    public class TelegramAccount
    {
        public int Id { get; set; }
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
        public string SessionPath { get; set; } = "user.session";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
