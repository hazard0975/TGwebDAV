using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Utils
{
    /// <summary>
    /// Утилита для интеграции корзины WebDAV и перехода к сообщению в Telegram в контекстное меню Проводника Windows (HKCU).
    /// Позволяет открывать корзину и сообщения Telegram по правому клику мыши на сетевой диск и файлы без прав администратора.
    /// </summary>
    public static class ShellContextMenuHelper
    {
        private const string TrashMenuKeyName = "TelegramWebDAVTrash";
        private const string OpenInTgMenuKeyName = "TelegramWebDAVOpenInTg";
        
        private const string DriveKeyPath = @"Software\Classes\Drive\shell\" + TrashMenuKeyName;
        private const string DriveBackgroundKeyPath = @"Software\Classes\Drive\Background\shell\" + TrashMenuKeyName;
        private const string BackgroundKeyPath = @"Software\Classes\Directory\Background\shell\" + TrashMenuKeyName;
        private const string DirectoryKeyPath = @"Software\Classes\Directory\shell\" + TrashMenuKeyName;
        private const string FileOpenInTgKeyPath = @"Software\Classes\*\shell\" + OpenInTgMenuKeyName;

        /// <summary>
        /// Проверяет, зарегистрирован ли пункт контекстного меню в реестре Windows.
        /// </summary>
        public static bool IsContextMenuRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DriveKeyPath);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Регистрирует пункты «Открыть корзину WebDAV» и «Открыть в Telegram» в контекстном меню Windows.
        /// </summary>
        public static bool RegisterTrashContextMenu(string driveLetter)
        {
            try
            {
                string cleanDrive = (driveLetter ?? "Z:").Trim();
                if (cleanDrive.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) cleanDrive = "Z:";
                if (!cleanDrive.EndsWith("\\")) cleanDrive += "\\";

                string driveWithoutSlash = cleanDrive.TrimEnd('\\');
                string trashLocalPath = Path.Combine(cleanDrive, ".Trash");
                string menuText = "Открыть корзину WebDAV";
                string explorerCommand = $"explorer.exe \"{trashLocalPath}\"";
                string exePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TelegramWebDAV.exe");
                string appliesToCondition = $"System.ItemPathDisplay:~<\"{driveWithoutSlash}\" OR System.ParsingPath:~<\"{driveWithoutSlash}\" OR System.ItemFolderPathDisplay:~<\"{driveWithoutSlash}\" OR System.ItemPathDisplay:=\"{cleanDrive}\" OR System.ParsingPath:=\"{cleanDrive}\"";

                // 1. Контекстное меню для диска (ПКМ по диску в "Этот компьютер")
                using (var driveKey = Registry.CurrentUser.CreateSubKey(DriveKeyPath))
                {
                    if (driveKey != null)
                    {
                        driveKey.SetValue("", menuText);
                        driveKey.SetValue("Icon", "shell32.dll,31");
                        driveKey.SetValue("AppliesTo", appliesToCondition);
                        using var cmdKey = driveKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                // 2. Контекстное меню для фона корня диска (ПКМ в пустом месте корня диска)
                using (var driveBgKey = Registry.CurrentUser.CreateSubKey(DriveBackgroundKeyPath))
                {
                    if (driveBgKey != null)
                    {
                        driveBgKey.SetValue("", menuText);
                        driveBgKey.SetValue("Icon", "shell32.dll,31");
                        driveBgKey.SetValue("AppliesTo", appliesToCondition);
                        using var cmdKey = driveBgKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                // 3. Контекстное меню для фона папки (ПКМ в пустом месте внутри папки диска)
                using (var bgKey = Registry.CurrentUser.CreateSubKey(BackgroundKeyPath))
                {
                    if (bgKey != null)
                    {
                        bgKey.SetValue("", menuText);
                        bgKey.SetValue("Icon", "shell32.dll,31");
                        bgKey.SetValue("AppliesTo", appliesToCondition);
                        using var cmdKey = bgKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                // 4. Контекстное меню для папок на диске (ПКМ по любой папке диска)
                using (var dirKey = Registry.CurrentUser.CreateSubKey(DirectoryKeyPath))
                {
                    if (dirKey != null)
                    {
                        dirKey.SetValue("", menuText);
                        dirKey.SetValue("Icon", "shell32.dll,31");
                        dirKey.SetValue("AppliesTo", appliesToCondition);
                        using var cmdKey = dirKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                // 5. Контекстное меню для файлов на диске: «Открыть в Telegram»
                using (var tgKey = Registry.CurrentUser.CreateSubKey(FileOpenInTgKeyPath))
                {
                    if (tgKey != null)
                    {
                        tgKey.SetValue("", "Открыть в Telegram");
                        tgKey.SetValue("Icon", File.Exists(exePath) ? $"\"{exePath}\",0" : "shell32.dll,14");
                        tgKey.SetValue("AppliesTo", appliesToCondition);
                        using var cmdKey = tgKey.CreateSubKey("command");
                        cmdKey?.SetValue("", $"\"{exePath}\" --open-in-tg \"%1\"");
                    }
                }

                AppLogger.Info("Shell", $"Контекстное меню WebDAV и 'Открыть в Telegram' успешно зарегистрировано в реестре для {cleanDrive}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shell", $"Не удалось зарегистрировать контекстное меню: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Удаляет пункты WebDAV из контекстного меню Windows.
        /// </summary>
        public static bool UnregisterTrashContextMenu()
        {
            try
            {
                using (var driveShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true))
                {
                    driveShellKey?.DeleteSubKeyTree(TrashMenuKeyName, false);
                }

                using (var driveBgShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\Background\shell", true))
                {
                    driveBgShellKey?.DeleteSubKeyTree(TrashMenuKeyName, false);
                }

                using (var bgShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true))
                {
                    bgShellKey?.DeleteSubKeyTree(TrashMenuKeyName, false);
                }

                using (var dirShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true))
                {
                    dirShellKey?.DeleteSubKeyTree(TrashMenuKeyName, false);
                }

                using (var fileShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell", true))
                {
                    fileShellKey?.DeleteSubKeyTree(OpenInTgMenuKeyName, false);
                }

                AppLogger.Info("Shell", "Контекстное меню WebDAV и 'Открыть в Telegram' удалено из реестра.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shell", $"Не удалось удалить контекстное меню: {ex.Message}", ex);
                return false;
            }
        }
    }
}
