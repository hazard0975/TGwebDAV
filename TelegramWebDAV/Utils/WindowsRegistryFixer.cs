using System;
using Microsoft.Win32;

namespace TelegramWebDAV.Utils
{
    public static class WindowsRegistryFixer
    {
        private const string WebClientParamsKey = @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters";
        private const string BasicAuthLevelName = "BasicAuthLevel";
        private const string FileSizeLimitName = "FileSizeLimitInBytes";
        
        // 4 ГБ в байтах (максимальный предел для 32-битного DWORD службы WebClient: 0xFFFFFFFF = 4 294 967 295)
        private const int MaxFileSizeLimitDword = unchecked((int)0xFFFFFFFF);

        /// <summary>
        /// Проверяет, применены ли все патчи реестра (BasicAuthLevel = 2 и снятие лимита 50 МБ)
        /// </summary>
        public static bool IsAllFixesApplied()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey))
                {
                    if (key != null)
                    {
                        var authVal = key.GetValue(BasicAuthLevelName);
                        var sizeVal = key.GetValue(FileSizeLimitName);

                        bool authOk = authVal != null && (int)authVal == 2;
                        bool sizeOk = sizeVal != null && (int)sizeVal == MaxFileSizeLimitDword;

                        return authOk && sizeOk;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при чтении реестра: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Проверяет, применен ли патч реестра (BasicAuthLevel = 2)
        /// </summary>
        public static bool IsBasicAuthLevelApplied()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey))
                {
                    if (key != null)
                    {
                        var value = key.GetValue(BasicAuthLevelName);
                        if (value != null && (int)value == 2)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при чтении реестра: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Применяет комплексный патч реестра:
        /// 1. BasicAuthLevel = 2 (разрешение WebDAV по HTTP в локальной сети без SSL).
        /// 2. FileSizeLimitInBytes = 4GB (снятие системного ограничения Windows в 50 МБ на файл).
        /// Требует прав администратора.
        /// </summary>
        public static bool ApplyAllFixes()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey, writable: true))
                {
                    if (key != null)
                    {
                        key.SetValue(BasicAuthLevelName, 2, RegistryValueKind.DWord);
                        key.SetValue(FileSizeLimitName, MaxFileSizeLimitDword, RegistryValueKind.DWord);
                        
                        Console.WriteLine("Патчи реестра успешно применены:");
                        Console.WriteLine(" - BasicAuthLevel = 2 (HTTP WebDAV разрешен)");
                        Console.WriteLine(" - FileSizeLimitInBytes = 4 GB (лимит 50 МБ снят)");
                        Console.WriteLine("Требуется перезапуск службы WebClient (net stop webclient && net start webclient) или перезагрузка ПК.");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("Раздел реестра WebClient не найден. Служба не установлена?");
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Ошибка доступа. Для изменения реестра необходимо запустить программу от имени Администратора.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Неожиданная ошибка при записи в реестр: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Устаревший метод для обратной совместимости
        /// </summary>
        public static bool ApplyBasicAuthLevelFix()
        {
            return ApplyAllFixes();
        }
    }
}
