using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TelegramWebDAV.Services
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Высокопроизводительный асинхронный файловый логгер с ротацией до 5 МБ.
    /// Использует неблокирующую очередь (Channel) в фоновом потоке.
    /// </summary>
    public static class AppLogger
    {
        public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
        public const int MaxArchivedFiles = 3;
        
        public static string LogDirectory { get; }
        public static string CurrentLogFilePath { get; }

        private static readonly Channel<string> _channel;
        private static readonly Task _workerTask;
        private static readonly CancellationTokenSource _cts = new();

        static AppLogger()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            LogDirectory = Path.Combine(baseDir, "logs");
            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch { }

            CurrentLogFilePath = Path.Combine(LogDirectory, "app.log");

            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true
            });

            _workerTask = Task.Run(ProcessQueueAsync);

            // Визуальный разделитель между сеансами запуска в файле лога (ровно 1 пустая строка перед баннером)
            _channel.Writer.TryWrite("");
            _channel.Writer.TryWrite("====================================================================================================");
            _channel.Writer.TryWrite($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Telegram WebDAV & Network Drive Service ===");
            _channel.Writer.TryWrite("====================================================================================================");

            // Интеграция с внутренним логированием WTelegramClient
            WTelegram.Helpers.Log = (level, str) =>
            {
                switch (level)
                {
                    case 1:
                        Debug("WTelegram", str);
                        break;
                    case 2:
                        Info("WTelegram", str);
                        break;
                    case 3:
                        Warn("WTelegram", str);
                        break;
                    case 4:
                    default:
                        Error("WTelegram", str);
                        break;
                }
            };

            Info("AppLogger", $"Инициализирован файловый логгер. Путь: {CurrentLogFilePath} (лимит {MaxFileSizeBytes / 1024 / 1024} МБ, ротация до {MaxArchivedFiles} файлов).");
        }

        public static void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Log(LogLevel.Info, category, message);
        public static void Warn(string category, string message, Exception? ex = null) => 
            Log(LogLevel.Warn, category, ex != null ? $"{message} | Ex: {ex.Message}" : message);
        public static void Error(string category, string message, Exception? ex = null) => 
            Log(LogLevel.Error, category, ex != null ? $"{message} | Ex: {ex.Message}\n{ex.StackTrace}" : message);

        public static void Log(LogLevel level, string category, string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string sanitized = Sanitize(message);
            string formatted = $"[{timestamp}] [{level.ToString().ToUpperInvariant(),-5}] [{category}] {sanitized}";

            // Вывод в консоль
            Console.WriteLine(formatted);

            // Запись в неблокирующую очередь
            _channel.Writer.TryWrite(formatted);
        }

        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            // Маскируем 32-символьные шестнадцатеричные хеши (api_hash)
            string result = Regex.Replace(input, @"\b[a-fA-F0-9]{32}\b", "***HASH***");
            
            return result;
        }

        private static async Task ProcessQueueAsync()
        {
            FileStream? fileStream = null;
            StreamWriter? writer = null;

            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_channel.Reader.TryRead(out var line))
                    {
                        try
                        {
                            EnsureWriter(ref fileStream, ref writer);
                            if (writer != null)
                            {
                                await writer.WriteLineAsync(line);
                                if (_channel.Reader.Count == 0)
                                {
                                    await writer.FlushAsync();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AppLogger] Ошибка записи лога на диск: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                try
                {
                    if (writer != null)
                    {
                        while (_channel.Reader.TryRead(out var line))
                        {
                            writer.WriteLine(line);
                        }
                        writer.Flush();
                        writer.Dispose();
                    }
                    fileStream?.Dispose();
                }
                catch { }
            }
        }

        private static void EnsureWriter(ref FileStream? fileStream, ref StreamWriter? writer)
        {
            if (fileStream == null || writer == null)
            {
                RotateIfNeeded();
                fileStream = new FileStream(CurrentLogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
                return;
            }

            if (fileStream.Length >= MaxFileSizeBytes)
            {
                writer.Flush();
                writer.Dispose();
                fileStream.Dispose();
                writer = null;
                fileStream = null;

                RotateIfNeeded();

                fileStream = new FileStream(CurrentLogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(CurrentLogFilePath)) return;

                var fi = new FileInfo(CurrentLogFilePath);
                if (fi.Length < MaxFileSizeBytes) return;

                // Удаляем самый старый архив если существует
                string oldest = Path.Combine(LogDirectory, $"app.{MaxArchivedFiles}.log");
                if (File.Exists(oldest))
                {
                    try { File.Delete(oldest); } catch { }
                }

                // Сдвигаем архивы: app.2.log -> app.3.log, app.1.log -> app.2.log
                for (int i = MaxArchivedFiles - 1; i >= 1; i--)
                {
                    string source = Path.Combine(LogDirectory, $"app.{i}.log");
                    string dest = Path.Combine(LogDirectory, $"app.{i + 1}.log");
                    if (File.Exists(source))
                    {
                        try { File.Move(source, dest, true); } catch { }
                    }
                }

                // Переименовываем текущий app.log -> app.1.log
                string archive1 = Path.Combine(LogDirectory, "app.1.log");
                try { File.Move(CurrentLogFilePath, archive1, true); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppLogger] Ошибка ротации логов: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            try
            {
                Info("AppLogger", "Завершение работы сервиса и сброс буфера логов...");
                _channel.Writer.Complete();
                _cts.Cancel();
                _workerTask.Wait(1500);
            }
            catch { }
        }
    }
}
