using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.UI
{
    public enum TransferDirection
    {
        Upload,
        Download
    }

    /// <summary>
    /// Интерактивный всплывающий HUD-виджет для отображения живого прогресса передачи данных
    /// (выгрузка в Telegram или скачивание/кэширование из Telegram) при активности или наведении на трей.
    /// </summary>
    public class TrayProgressOverlay : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        private readonly System.Windows.Forms.Timer _updateTimer;
        private readonly System.Windows.Forms.Timer _hideCheckTimer;
        private readonly System.Windows.Forms.Timer _completionTimer;
        private readonly SynchronizationContext? _syncContext;

        public bool AutoShowOnUpload { get; set; } = true;

        private TransferDirection _direction = TransferDirection.Upload;
        private string _currentFileName = string.Empty;
        private long _currentBytes = 0;
        private long _totalBytes = 0;
        private int _queueCount = 0;
        private bool _isTransferring = false;
        private bool _isFinalizing = false;
        private bool _isCompleted = false;
        private DateTime _lastSpeedCalcTime = DateTime.UtcNow;
        private long _lastSpeedBytes = 0;
        private double _bytesPerSecond = 0;
        private DateTime _lastHoverTime = DateTime.MinValue;
        private DateTime _lastProgressUpdateTime = DateTime.UtcNow;

        public TrayProgressOverlay()
        {
            _syncContext = SynchronizationContext.Current;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(290, 96);
            BackColor = Color.FromArgb(30, 41, 59); // Slate 800

            // Принудительно создаем дескриптор Win32 HWND
            CreateControl();

            _updateTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _updateTimer.Tick += (s, e) => Invalidate();

            _hideCheckTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _hideCheckTimer.Tick += HideCheckTimer_Tick;

            _completionTimer = new System.Windows.Forms.Timer { Interval = 1400 };
            _completionTimer.Tick += (s, e) =>
            {
                AppLogger.Info("TrayProgressOverlay", $"_completionTimer Tick! _isTransferring={_isTransferring}, _isCompleted={_isCompleted}, Visible={Visible}");
                _completionTimer.Stop();
                if (!_isTransferring)
                {
                    _updateTimer.Stop();
                    _hideCheckTimer.Stop();
                    _isCompleted = false;
                    _isFinalizing = false;
                    _currentFileName = string.Empty;
                    Hide();
                    AppLogger.Info("TrayProgressOverlay", "Оверлей успешно скрыт по завершению таймаута.");
                }
            };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Подавляем системную заливку фона Windows, чтобы не было черного экрана
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void UpdateProgress(string fileName, long current, long total, TransferDirection direction = TransferDirection.Upload, int queueCount = 0)
        {
            if (_syncContext != null && SynchronizationContext.Current != _syncContext)
            {
                _syncContext.Post(_ => UpdateProgress(fileName, current, total, direction, queueCount), null);
                return;
            }

            _queueCount = queueCount;

            // Отменяем любой таймер закрытия от предыдущих файлов
            if (_completionTimer.Enabled)
            {
                AppLogger.Info("TrayProgressOverlay", $"Остановлен активный _completionTimer для файла {fileName}");
                _completionTimer.Stop();
            }

            _direction = direction;

            // При смене файла сбрасываем счетчик, иначе гарантируем монотонный рост (защита от сетевого джиттера)
            if (_currentFileName != fileName)
            {
                _currentFileName = fileName;
                _currentBytes = current;
                _lastSpeedBytes = 0;
                _lastSpeedCalcTime = DateTime.UtcNow;
                _bytesPerSecond = 0;
            }
            else
            {
                _currentBytes = Math.Max(_currentBytes, current);
            }
            _totalBytes = total;
            _isTransferring = true;
            _isCompleted = false;
            _lastProgressUpdateTime = DateTime.UtcNow;

            if (total > 0 && current >= total)
            {
                _isFinalizing = true;
                _bytesPerSecond = 0;
            }
            else
            {
                _isFinalizing = false;
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastSpeedCalcTime).TotalSeconds;
                if (elapsed >= 0.5)
                {
                    long bytesDiff = current - _lastSpeedBytes;
                    if (bytesDiff >= 0)
                    {
                        double instantSpeed = bytesDiff / elapsed;
                        _bytesPerSecond = _bytesPerSecond == 0 ? instantSpeed : (_bytesPerSecond * 0.7 + instantSpeed * 0.3);
                    }
                    _lastSpeedBytes = current;
                    _lastSpeedCalcTime = now;
                }
            }

            // Если окно скрыто — позиционируем и показываем
            if (!Visible)
            {
                AppLogger.Info("TrayProgressOverlay", $"Показ окна прогресса [{_direction}] для {fileName} ({current}/{total})");
                PositionNearTray(useMouse: false);
                Show();
                _updateTimer.Start();
                _hideCheckTimer.Start();
            }

            Invalidate();
            Update(); // Принудительно запускаем перерисовку очереди WM_PAINT
        }

        public void CompleteTransfer(string fileName, TransferDirection direction = TransferDirection.Upload)
        {
            if (_syncContext != null && SynchronizationContext.Current != _syncContext)
            {
                _syncContext.Post(_ => CompleteTransfer(fileName, direction), null);
                return;
            }

            AppLogger.Info("TrayProgressOverlay", $"CompleteTransfer [{direction}] вызван для '{fileName}'. Запуск _completionTimer...");
            _direction = direction;
            _isTransferring = false;
            _isFinalizing = false;
            _isCompleted = true;
            _bytesPerSecond = 0;
            Invalidate();
            Update();

            // Запускаем гарантированный таймер скрытия
            _completionTimer.Stop();
            _completionTimer.Start();
        }

        // Для обратной совместимости
        public void CompleteUpload(string fileName) => CompleteTransfer(fileName, TransferDirection.Upload);

        public void NotifyTrayHover()
        {
            if (_syncContext != null && SynchronizationContext.Current != _syncContext)
            {
                _syncContext.Post(_ => NotifyTrayHover(), null);
                return;
            }

            _lastHoverTime = DateTime.UtcNow;

            // Показываем окно по наведению на трей только если идет реальная передача
            if (!Visible && (_isTransferring || _isFinalizing))
            {
                PositionNearTray(useMouse: true);
                Show();
                Refresh();
                _updateTimer.Start();
                _hideCheckTimer.Start();
            }
        }

        private void PositionNearTray(bool useMouse)
        {
            Rectangle workingArea;
            int x, y;

            if (useMouse)
            {
                var mousePos = Cursor.Position;
                workingArea = Screen.GetWorkingArea(mousePos);

                x = mousePos.X - (Width / 2);
                y = mousePos.Y - Height - 16;

                if (x + Width > workingArea.Right - 8)
                    x = workingArea.Right - Width - 8;
                if (x < workingArea.Left + 8)
                    x = workingArea.Left + 8;

                if (y < workingArea.Top + 8)
                    y = mousePos.Y + 24;
            }
            else
            {
                // Автоматическое позиционирование в правом нижнем углу над панелью задач
                workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
                x = workingArea.Right - Width - 16;
                y = workingArea.Bottom - Height - 16;
            }

            Location = new Point(x, y);
        }

        private void HideCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (!Visible) return;

            // Если прошло более 1.5 сек с момента последнего обновления данных при отсутствии активности,
            // запускаем штатный таймер финализации и скрытия (защита от зависания окна при обрыве потока/завершении чтения)
            var secondsSinceProgress = (DateTime.UtcNow - _lastProgressUpdateTime).TotalSeconds;
            if ((_isTransferring || _isFinalizing) && secondsSinceProgress >= 1.5)
            {
                if (!_completionTimer.Enabled)
                {
                    AppLogger.Info("TrayProgressOverlay", $"Таймаут неактивности передачи ({secondsSinceProgress:F1} сек). Запуск скрытия оверлея...");
                    CompleteTransfer(_currentFileName, _direction);
                }
                return;
            }

            // Если окно всплыло по автоматическому показу и идет активная загрузка — оно отображается
            if (AutoShowOnUpload && (_isTransferring || _isFinalizing || _isCompleted))
            {
                return;
            }

            var mousePos = Cursor.Position;
            var mouseOverOverlay = Bounds.Contains(mousePos);
            var secondsSinceLastHover = (DateTime.UtcNow - _lastHoverTime).TotalSeconds;

            // В режиме показа только при наведении скрываем окно, если курсор ушел
            if (!mouseOverOverlay && secondsSinceLastHover > 0.6)
            {
                AppLogger.Info("TrayProgressOverlay", $"Скрытие окна по HideCheckTimer (mouseOver={mouseOverOverlay}, secondsSinceLastHover={secondsSinceLastHover:F1})");
                _updateTimer.Stop();
                _hideCheckTimer.Stop();
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 1. Скругленный контур и фон
            using (var path = GetRoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (var brush = new SolidBrush(Color.FromArgb(30, 41, 59))) // Slate 800
                {
                    g.FillPath(brush, path);
                }
                using (var pen = new Pen(Color.FromArgb(71, 85, 105), 1)) // Slate 600
                {
                    g.DrawPath(pen, path);
                }
            }

            // 2. Заголовок
            using (var titleFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.FromArgb(148, 163, 184))) // Slate 400
            {
                string header;
                if (_isCompleted)
                {
                    header = _direction == TransferDirection.Download ? "СКАЧИВАНИЕ ЗАВЕРШЕНО" : "ЗАГРУЗКА ЗАВЕРШЕНА";
                }
                else if (_queueCount > 1)
                {
                    header = _direction == TransferDirection.Download 
                        ? $"СКАЧИВАНИЕ ИЗ TELEGRAM (в очереди: {_queueCount})" 
                        : $"ОТПРАВКА В TELEGRAM (в очереди: {_queueCount})";
                }
                else if (_isFinalizing)
                {
                    header = _direction == TransferDirection.Download ? "СОХРАНЕНИЕ В КЭШ" : "СОХРАНЕНИЕ В ОБЛАКЕ";
                }
                else if (_isTransferring)
                {
                    header = _direction == TransferDirection.Download ? "СКАЧИВАНИЕ ИЗ TELEGRAM" : "ОТПРАВКА В TELEGRAM";
                }
                else
                {
                    header = "TELEGRAM WEBDAV";
                }
                g.DrawString(header, titleFont, titleBrush, new PointF(12, 10));
            }

            // 3. Имя файла
            using (var nameFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var nameBrush = new SolidBrush(Color.White))
            {
                string displayName = string.IsNullOrEmpty(_currentFileName) ? "Готов к передаче" : _currentFileName;
                if (displayName.Length > 32) displayName = displayName.Substring(0, 30) + "...";
                g.DrawString(displayName, nameFont, nameBrush, new PointF(12, 28));
            }

            // 4. Шкала прогресса
            int barX = 12;
            int barY = 52;
            int barWidth = Width - 24;
            int barHeight = 8;
            int percent = 0;

            if (_isCompleted)
            {
                percent = 100;
            }
            else if (_totalBytes > 0)
            {
                percent = (int)Math.Min(100, Math.Max(0, (_currentBytes * 100) / _totalBytes));
            }

            using (var barBgPath = GetRoundedRect(new Rectangle(barX, barY, barWidth, barHeight), 4))
            {
                using (var bgBrush = new SolidBrush(Color.FromArgb(51, 65, 85))) // Slate 700
                {
                    g.FillPath(bgBrush, barBgPath);
                }
            }

            if (percent > 0)
            {
                int fillWidth = Math.Max(6, (barWidth * percent) / 100);
                using (var fillPath = GetRoundedRect(new Rectangle(barX, barY, fillWidth, barHeight), 4))
                {
                    Color startColor = _direction == TransferDirection.Download 
                        ? Color.FromArgb(245, 158, 11)  // Amber 500
                        : Color.FromArgb(34, 197, 94);   // Green Emerald 500

                    Color endColor = _direction == TransferDirection.Download
                        ? Color.FromArgb(251, 191, 36)  // Amber 400
                        : Color.FromArgb(56, 189, 248);  // Sky 400

                    using (var fillBrush = new LinearGradientBrush(
                        new Rectangle(barX, barY, barWidth, barHeight),
                        startColor,
                        endColor,
                        0f))
                    {
                        g.FillPath(fillBrush, fillPath);
                    }
                }
            }

            // 5. Детальная статистика (МБ, процент, скорость)
            using (var statsFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
            using (var statsBrush = new SolidBrush(Color.FromArgb(203, 213, 225))) // Slate 300
            {
                string stats;
                if (_isCompleted)
                {
                    stats = _direction == TransferDirection.Download 
                        ? "Файл успешно получен из Telegram ✔" 
                        : "Файл успешно сохранен в Telegram ✔";
                }
                else if (_isFinalizing)
                {
                    stats = _direction == TransferDirection.Download
                        ? "Завершение кэширования... ⏳"
                        : "Финализация сообщения в канале... ⏳";
                }
                else if (_isTransferring && _totalBytes > 0)
                {
                    string currStr = FormatBytes(_currentBytes);
                    string totalStr = FormatBytes(_totalBytes);
                    string speedStr = _bytesPerSecond > 1024 ? $"{FormatBytes((long)_bytesPerSecond)}/с" : "вычисление...";
                    stats = $"{currStr} из {totalStr} ({percent}%) • {speedStr}";
                }
                else if (_isTransferring)
                {
                    stats = $"Передано: {FormatBytes(_currentBytes)} • передача...";
                }
                else
                {
                    stats = "Все файлы синхронизированы ✔";
                }

                g.DrawString(stats, statsFont, statsBrush, new PointF(12, 68));
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} Б";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} КБ";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} МБ";
            return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} ГБ";
        }

        private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _updateTimer.Dispose();
                _hideCheckTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
