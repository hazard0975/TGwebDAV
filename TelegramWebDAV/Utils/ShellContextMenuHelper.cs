using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Utils
{
    /// <summary>
    /// Утилита для интеграции корзины WebDAV в контекстное меню Проводника Windows (HKCU).
    /// Позволяет открывать корзину по правому клику мыши на сетевой диск или пустую область папки без прав администратора.
    /// </summary>
    public static class ShellContextMenuHelper
    {
        private const string MenuKeyName = "TelegramWebDAVTrash";
        private const string DriveKeyPath = @"Software\Classes\Drive\shell\" + MenuKeyName;
        private const string BackgroundKeyPath = @"Software\Classes\Directory\Background\shell\" + MenuKeyName;

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
        /// Регистрирует пункт «Открыть корзину WebDAV» в контекстном меню Windows.
        /// </summary>
        public static bool RegisterTrashContextMenu(string driveLetter)
        {
            try
            {
                string cleanDrive = (driveLetter ?? "Z:").Trim();
                if (cleanDrive.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) cleanDrive = "Z:";
                if (!cleanDrive.EndsWith("\\")) cleanDrive += "\\";

                string trashLocalPath = Path.Combine(cleanDrive, ".Trash");
                string menuText = "Открыть корзину WebDAV";
                string explorerCommand = $"explorer.exe \"{trashLocalPath}\"";

                // 1. Контекстное меню для диска (ПКМ по диску Z: в Компьютере)
                using (var driveKey = Registry.CurrentUser.CreateSubKey(DriveKeyPath))
                {
                    if (driveKey != null)
                    {
                        driveKey.SetValue("", menuText);
                        // Используем стандартную системную иконку корзины Windows
                        driveKey.SetValue("Icon", "shell32.dll,31");
                        using var cmdKey = driveKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                // 2. Контекстное меню для фона папки (ПКМ в пустом месте Проводника)
                using (var bgKey = Registry.CurrentUser.CreateSubKey(BackgroundKeyPath))
                {
                    if (bgKey != null)
                    {
                        bgKey.SetValue("", $"{menuText} ({cleanDrive.TrimEnd('\\')})");
                        bgKey.SetValue("Icon", "shell32.dll,31");
                        using var cmdKey = bgKey.CreateSubKey("command");
                        cmdKey?.SetValue("", explorerCommand);
                    }
                }

                AppLogger.Info("Shell", $"Контекстное меню корзины WebDAV успешно зарегистрировано в реестре для {cleanDrive}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shell", $"Не удалось зарегистрировать контекстное меню корзины: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Удаляет пункт корзины WebDAV из контекстного меню Windows.
        /// </summary>
        public static bool UnregisterTrashContextMenu()
        {
            try
            {
                using (var driveShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true))
                {
                    driveShellKey?.DeleteSubKeyTree(MenuKeyName, false);
                }

                using (var bgShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true))
                {
                    bgShellKey?.DeleteSubKeyTree(MenuKeyName, false);
                }

                AppLogger.Info("Shell", "Контекстное меню корзины WebDAV удалено из реестра.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shell", $"Не удалось удалить контекстное меню корзины: {ex.Message}", ex);
                return false;
            }
        }
    }
}
