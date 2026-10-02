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
        /// Создает рамку GroupBox с защитой от сжатия (MinimumSize) и правильными отступами.
        /// </summary>
        public static GroupBox CreateGroupBox(string title, int width = UITheme.ContentWidth, int minHeight = 0)
        {
            var grp = new GroupBox
            {
                Text = title,
                Width = width,
                MinimumSize = new Size(width, minHeight),
                AutoSize = true,
                Font = UITheme.BaseFont,
                Margin = UITheme.GroupBoxMargin,
                Padding = UITheme.GroupBoxPadding
            };
            return grp;
        }

        /// <summary>
        /// Создает внутренний контейнер FlowLayoutPanel для GroupBox с направлением сверху вниз.
        /// </summary>
        public static FlowLayoutPanel CreateVerticalContainer(int width = UITheme.ContentWidth - 20)
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Width = width,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
        }

        /// <summary>
        /// Создает строку параметра: слева метка с описанием, справа поле ввода/переключатель, прижатый к правому краю.
        /// </summary>
        public static TableLayoutPanel CreateSettingRow(string labelText, Control control, string? hintText = null, int width = UITheme.ContentWidth - 25)
        {
            var tbl = new TableLayoutPanel
            {
                Width = width,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = UITheme.RowMargin,
                Padding = Padding.Empty
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Font = UITheme.BaseFont,
                ForeColor = UITheme.TextMain,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 8, 4)
            };

            control.Margin = new Padding(0, 2, 0, 2);
            control.Anchor = AnchorStyles.Right;

            tbl.Controls.Add(lbl, 0, 0);
            tbl.Controls.Add(control, 1, 0);

            if (!string.IsNullOrWhiteSpace(hintText))
            {
                var lblHint = new Label
                {
                    Text = hintText,
                    AutoSize = true,
                    Font = UITheme.SmallFont,
                    ForeColor = UITheme.TextMuted,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0, 0, 0, 4)
                };
                
                var container = new FlowLayoutPanel
                {
                    Width = width,
                    AutoSize = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    Margin = UITheme.RowMargin
                };
                container.Controls.Add(tbl);
                container.Controls.Add(lblHint);
                
                var wrapperTbl = new TableLayoutPanel
                {
                    Width = width,
                    AutoSize = true,
                    ColumnCount = 1,
                    RowCount = 1,
                    Margin = Padding.Empty
                };
                wrapperTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                wrapperTbl.Controls.Add(container, 0, 0);
                return wrapperTbl;
            }

            return tbl;
        }

        /// <summary>
        /// Создает числовое поле NumericUpDown с выравниванием по правому краю и единым шрифтом.
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
                TextAlign = HorizontalAlignment.Right,
                Font = UITheme.BaseFont,
                Margin = UITheme.ControlMargin
            };
        }

        /// <summary>
        /// Создает стандартную кнопку действия.
        /// </summary>
        public static Button CreateButton(string text, EventHandler? onClick = null, bool isPrimary = false, int height = UITheme.ButtonHeightDefault, int? width = null)
        {
            var btn = new Button
            {
                Text = text,
                Height = height,
                Font = isPrimary ? UITheme.BoldFont : UITheme.BaseFont,
                UseVisualStyleBackColor = true,
                Margin = new Padding(0, 2, 4, 2)
            };
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
                Margin = new Padding(0, 4, 0, 6)
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
        public static Label CreateHintLabel(string text, int width = UITheme.ContentWidth - 25)
        {
            return new Label
            {
                Text = text,
                Width = width,
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
                Width = UITheme.ContentWidth - 25,
                Height = UITheme.ButtonHeightDefault + 6,
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
    }
}
