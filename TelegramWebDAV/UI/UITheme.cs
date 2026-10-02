using System.Drawing;
using System.Windows.Forms;

namespace TelegramWebDAV.UI
{
    /// <summary>
    /// Единые константы оформления, шрифтов, цветов и размеров (UI Design System).
    /// </summary>
    public static class UITheme
    {
        // === Шрифты ===
        public static readonly Font BaseFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BoldFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font HeaderFont = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font SmallFont = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font MonoFont = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // === Цвета ===
        public static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        public static readonly Color TextMuted = Color.FromArgb(110, 110, 110);
        public static readonly Color TextDanger = Color.FromArgb(190, 40, 40);
        public static readonly Color TextSuccess = Color.FromArgb(35, 140, 35);
        public static readonly Color BorderLight = Color.FromArgb(215, 215, 215);
        public static readonly Color BackgroundLight = Color.FromArgb(248, 249, 250);

        // === Размеры контролов (px) ===
        public const int ContentWidth = 510;
        public const int ButtonHeightDefault = 30;
        public const int ButtonHeightPrimary = 34;
        public const int ButtonHeightSmall = 26;
        public const int InputNumberWidth = 90;
        public const int InputPortWidth = 90;
        public const int DropDownWidthDefault = 380;

        // === Отступы (Margins & Paddings) ===
        public static readonly Padding GroupBoxMargin = new Padding(0, 0, 0, 10);
        public static readonly Padding GroupBoxPadding = new Padding(10, 8, 10, 8);
        public static readonly Padding RowMargin = new Padding(0, 2, 0, 4);
        public static readonly Padding ControlMargin = new Padding(0, 2, 0, 2);
    }
}
