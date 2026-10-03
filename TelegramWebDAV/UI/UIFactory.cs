using System;
using System.Drawing;
using System.Windows.Forms;

namespace TelegramWebDAV.UI
{
    /// <summary>
    /// Фабрика компонентов интерфейса Windows Forms.
    /// Гарантирует соблюдение единого стиля, защиту от схлопывания в FlowLayoutPanel и единообразные размеры.
    /// </summary>
    public static class UIFactory
    {
        /// <summary>
        /// Создает рамку GroupBox с защитой от сжатия (MinimumSize) и компактными отступами.
        /// </summary>
        public static GroupBox CreateGroupBox(string title, int width = UITheme.ContentWidth, int minHeight = 0)
        {
            var grp = new GroupBox
            {
                Text = title,
                Width = width,
                MinimumSize = new Size(width, minHeight),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = UITheme.BaseFont,
                Margin = UITheme.GroupBoxMargin,
                Padding = new Padding(8, 6, 8, 8)
            };
            return grp;
        }

        /// <summary>
        /// Создает внутренний контейнер FlowLayoutPanel для GroupBox с направлением сверху вниз.
        /// </summary>
        public static FlowLayoutPanel CreateVerticalContainer(int width = 474)
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Width = width,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
        }

        /// <summary>
        /// Создает строку параметра: компактная колонка названия слева + контрол сразу справа.
        /// </summary>
        public static TableLayoutPanel CreateSettingRow(string labelText, Control control, int labelWidth = 140, int width = 474)
        {
            var tbl = new TableLayoutPanel
            {
                Width = width,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = UITheme.RowMargin,
                Padding = Padding.Empty
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Font = UITheme.BaseFont,
                ForeColor = UITheme.TextMain,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 4, 4)
            };

            control.Margin = new Padding(0, 2, 0, 2);
            control.Anchor = AnchorStyles.Left;

            tbl.Controls.Add(lbl, 0, 0);
            tbl.Controls.Add(control, 1, 0);

            return tbl;
        }

        /// <summary>
        /// Создает строку для текстового поля ввода на всю оставшуюся ширину.
        /// </summary>
        public static TableLayoutPanel CreateInputRow(string labelText, Control control, int labelWidth = 90, int width = 474)
        {
            var tbl = new TableLayoutPanel
            {
                Width = width,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = UITheme.RowMargin,
                Padding = Padding.Empty
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Font = UITheme.BaseFont,
                ForeColor = UITheme.TextMain,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 4, 4)
            };

            control.Margin = new Padding(0, 2, 0, 2);
            control.Dock = DockStyle.Fill;

            tbl.Controls.Add(lbl, 0, 0);
            tbl.Controls.Add(control, 1, 0);

            return tbl;
        }

        /// <summary>
        /// Создает числовое поле NumericUpDown с компактной шириной и единым шрифтом.
        /// </summary>
        public static NumericUpDown CreateNumericInput(decimal min, decimal max, decimal val, int width = UITheme.InputNumberWidth, int decimalPlaces = 0)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                Width = width,
                DecimalPlaces = decimalPlaces,
                Font = UITheme.BaseFont,
                Margin = UITheme.ControlMargin
            };
        }

        /// <summary>
        /// Создает стандартную кнопку действия через единую фабрику с защитой от сплющивания (MinimumSize).
        /// </summary>
        public static Button CreateButton(
            string text, 
            EventHandler? onClick = null, 
            bool isPrimary = false, 
            int height = UITheme.ButtonHeightDefault, 
            int? width = null, 
            bool autoSize = false,
            DockStyle dock = DockStyle.None,
            Color? foreColor = null)
        {
            var btn = new Button
            {
                Text = text,
                Height = height,
                MinimumSize = new Size(width ?? 0, height),
                Font = isPrimary ? UITheme.BoldFont : UITheme.BaseFont,
                UseVisualStyleBackColor = true,
                AutoSize = autoSize,
                Dock = dock,
                Padding = autoSize ? new Padding(12, 4, 12, 4) : Padding.Empty,
                Margin = new Padding(0, 2, 0, 2)
            };
            if (foreColor.HasValue)
            {
                btn.ForeColor = foreColor.Value;
            }
            if (width.HasValue)
            {
                btn.Width = width.Value;
            }
            if (onClick != null)
            {
                btn.Click += onClick;
            }
            return btn;
        }

        /// <summary>
        /// Создает чекбокс со стандартным шрифтом и отступами.
        /// </summary>
        public static CheckBox CreateCheckBox(string text, bool isChecked, EventHandler? onCheckedChanged = null)
        {
            var chk = new CheckBox
            {
                Text = text,
                Checked = isChecked,
                AutoSize = true,
                Font = UITheme.BaseFont,
                ForeColor = UITheme.TextMain,
                Margin = new Padding(0, 3, 0, 5)
            };
            if (onCheckedChanged != null)
            {
                chk.CheckedChanged += onCheckedChanged;
            }
            return chk;
        }

        /// <summary>
        /// Создает информационную подпись или подсказку мелким приглушенным шрифтом.
        /// </summary>
        public static Label CreateHintLabel(string text, int width = 474)
        {
            return new Label
            {
                Text = text,
                Width = width,
                MaximumSize = new Size(width, 0),
                AutoSize = true,
                Font = UITheme.SmallFont,
                ForeColor = UITheme.TextMuted,
                Margin = new Padding(0, 1, 0, 6)
            };
        }

        /// <summary>
        /// Создает панель кнопок с равномерным распределением колонок.
        /// </summary>
        public static TableLayoutPanel CreateActionRow(params Control[] buttons)
        {
            var tbl = new TableLayoutPanel
            {
                Width = 474,
                Height = UITheme.ButtonHeightDefault + 4,
                MinimumSize = new Size(474, UITheme.ButtonHeightDefault + 4),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = buttons.Length,
                RowCount = 1,
                Margin = new Padding(0, 2, 0, 2)
            };
            float percentPerCol = 100f / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percentPerCol));
                buttons[i].Dock = DockStyle.Fill;
                tbl.Controls.Add(buttons[i], i, 0);
            }
            return tbl;
        }

        /// <summary>
        /// Создает нижнюю фиксированную панель окна со строго одинаковыми кнопками стандартного размера (88x27).
        /// </summary>
        public static TableLayoutPanel CreateBottomBar(Button saveBtn, Button? closeBtn = null)
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(12, 9, 22, 9),
                BackColor = SystemColors.Control
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
            if (closeBtn != null)
            {
                pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
                closeBtn.Dock = DockStyle.Fill;
                closeBtn.Font = UITheme.BaseFont;
                closeBtn.Height = 27;
                closeBtn.MinimumSize = new Size(88, 27);
                closeBtn.Margin = new Padding(4, 0, 0, 0);
                pnl.Controls.Add(closeBtn, 2, 0);
            }
            else
            {
                pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
            }

            saveBtn.Dock = DockStyle.Fill;
            saveBtn.Font = UITheme.BaseFont;
            saveBtn.Height = 27;
            saveBtn.MinimumSize = new Size(88, 27);
            saveBtn.Margin = new Padding(0, 0, 4, 0);
            pnl.Controls.Add(saveBtn, 1, 0);

            return pnl;
        }
    }
}
