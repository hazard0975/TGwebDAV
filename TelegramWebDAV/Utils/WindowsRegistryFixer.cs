using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TelegramWebDAV.Utils
{
    public static class WindowsRegistryFixer
    {
        private const string WebClientParamsKey = @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters";
        private const string PoliciesSystemKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string BasicAuthLevelName = "BasicAuthLevel";
        private const string FileSizeLimitName = "FileSizeLimitInBytes";
        private const string EnableLinkedConnectionsName = "EnableLinkedConnections";
        
        // 4 ГБ в байтах (максимальный предел для 32-битного DWORD службы WebClient: 0xFFFFFFFF = 4 294 967 295)
        private const int MaxFileSizeLimitDword = unchecked((int)0xFFFFFFFF);

        /// <summary>
        /// Проверяет, применены ли все патчи реестра (BasicAuthLevel = 2, снятие лимита 50 МБ и EnableLinkedConnections = 1)
        /// </summary>
        public static bool IsAllFixesApplied()
        {
            try
            {
                bool webClientOk = false;
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey))
                {
                    if (key != null)
                    {
                        var authVal = key.GetValue(BasicAuthLevelName);
                        var sizeVal = key.GetValue(FileSizeLimitName);

                        bool authOk = authVal != null && (int)authVal == 2;
                        bool sizeOk = sizeVal != null && (int)sizeVal == MaxFileSizeLimitDword;

                        webClientOk = authOk && sizeOk;
                    }
                }

                bool linkedOk = false;
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(PoliciesSystemKey))
                {
                    if (key != null)
                    {
                        var linkedVal = key.GetValue(EnableLinkedConnectionsName);
                        linkedOk = linkedVal != null && (int)linkedVal == 1;
                    }
                }

                return webClientOk && linkedOk;
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
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey))
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
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(WebClientParamsKey, writable: true))
                {
                    if (key != null)
                    {
                        key.SetValue(BasicAuthLevelName, 2, RegistryValueKind.DWord);
                        key.SetValue(FileSizeLimitName, MaxFileSizeLimitDword, RegistryValueKind.DWord);
                        
                        Console.WriteLine("Патчи реестра успешно применены:");
                        Console.WriteLine(" - BasicAuthLevel = 2 (HTTP WebDAV разрешен)");
                        Console.WriteLine(" - FileSizeLimitInBytes = 4 GB (лимит 50 МБ снят)");
                    }
                    else
                    {
                        Console.WriteLine("Раздел реестра WebClient не найден. Служба не установлена?");
                    }
                }

                // Включаем совместный доступ к дискам между сессиями администратора и обычного пользователя (UAC Linked Connections)
                try
                {
                    using (RegistryKey? sysKey = Registry.LocalMachine.CreateSubKey(PoliciesSystemKey))
                    {
                        if (sysKey != null)
                        {
                            sysKey.SetValue(EnableLinkedConnectionsName, 1, RegistryValueKind.DWord);
                            Console.WriteLine(" - EnableLinkedConnections = 1 (диск виден и от имени администратора, и в обычных программах)");
                        }
                    }
                }
                catch (Exception sysEx)
                {
                    Console.WriteLine($"Предупреждение при настройке EnableLinkedConnections: {sysEx.Message}");
                }

                // Добавляем доверие к сетевым дискам и 127.0.0.1 в зону "Местная интрасеть" и отключаем предупреждения безопасности
                try
                {
                    // 1. Настройка ZoneMap (Местная интрасеть)
                    using (var zoneKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap"))
                    {
                        if (zoneKey != null)
                        {
                            zoneKey.SetValue("UNCAsIntranet", 1, RegistryValueKind.DWord);
                            zoneKey.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
                            zoneKey.SetValue("IntranetName", 1, RegistryValueKind.DWord);
                            zoneKey.SetValue("ProxyBypass", 1, RegistryValueKind.DWord);
                        }
                    }

                    // 2. Добавление localhost в доверенную зону (все протоколы: file, http, https, wildcard *)
                    using (var domainKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Domains\localhost"))
                    {
                        if (domainKey != null)
                        {
                            domainKey.SetValue("*", 1, RegistryValueKind.DWord);
                            domainKey.SetValue("http", 1, RegistryValueKind.DWord);
                            domainKey.SetValue("https", 1, RegistryValueKind.DWord);
                            domainKey.SetValue("file", 1, RegistryValueKind.DWord);
                        }
                    }

                    // 3. Добавление 127.0.0.1 в доверенные IP-диапазоны
                    using (var rangeKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1"))
                    {
                        if (rangeKey != null)
                        {
                            rangeKey.SetValue(":Range", "127.0.0.1", RegistryValueKind.String);
                            rangeKey.SetValue("*", 1, RegistryValueKind.DWord);
                            rangeKey.SetValue("http", 1, RegistryValueKind.DWord);
                            rangeKey.SetValue("https", 1, RegistryValueKind.DWord);
                            rangeKey.SetValue("file", 1, RegistryValueKind.DWord);
                        }
                    }

                    // 4. Добавление IPv6 ::1
                    using (var range2Key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range2"))
                    {
                        if (range2Key != null)
                        {
                            range2Key.SetValue(":Range", "::1", RegistryValueKind.String);
                            range2Key.SetValue("*", 1, RegistryValueKind.DWord);
                            range2Key.SetValue("http", 1, RegistryValueKind.DWord);
                            range2Key.SetValue("file", 1, RegistryValueKind.DWord);
                        }
                    }

                    // 5. Отключение диалога предупреждения безопасности при копировании/запуске файлов из зоны Интрасети (Zone 1)
                    // 1806 = Launching applications and unsafe files (0 = Enable / Разрешить без окна предупреждения)
                    // 1609 = Promote data binding (0 = Enable)
                    // 1803 = File Download (0 = Enable)
                    using (var zone1Key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\1"))
                    {
                        if (zone1Key != null)
                        {
                            zone1Key.SetValue("1806", 0, RegistryValueKind.DWord);
                            zone1Key.SetValue("1609", 0, RegistryValueKind.DWord);
                            zone1Key.SetValue("1803", 0, RegistryValueKind.DWord);
                        }
                    }

                    // 6. Политика безопасных типов файлов (LowRiskFileTypes) для проводника Windows
                    const string lowRiskExtensions = ".exe;.bat;.cmd;.mp3;.flac;.wav;.7z;.zip;.rar;.iso;.mkv;.mp4;.avi;.txt;.pdf;.doc;.docx;.xls;.xlsx;.bin;.cue;.ogg;.m4a;.ape;.mov;.wmv;.ts;.m3u;.m3u8;.jpg;.jpeg;.png;.gif;";

                    using (var assocCu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Associations"))
                    {
                        if (assocCu != null)
                        {
                            assocCu.SetValue("LowRiskFileTypes", lowRiskExtensions, RegistryValueKind.String);
                            assocCu.SetValue("DefaultFileTypeRisk", 6151, RegistryValueKind.DWord);
                        }
                    }

                    try
                    {
                        using (var assocLm = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Associations"))
                        {
                            if (assocLm != null)
                            {
                                assocLm.SetValue("LowRiskFileTypes", lowRiskExtensions, RegistryValueKind.String);
                                assocLm.SetValue("DefaultFileTypeRisk", 6151, RegistryValueKind.DWord);
                            }
                        }
                    }
                    catch { }

                    Console.WriteLine(" - Местная интрасеть и LowRiskFileTypes настроены (окна предупреждений безопасности отключены)");
                }
                catch (Exception zoneEx)
                {
                    Console.WriteLine($"Предупреждение при настройке зоны доверия: {zoneEx.Message}");
                }

                Console.WriteLine("Требуется перезапуск службы WebClient (net stop webclient && net start webclient) или перезагрузка ПК.");
                return true;
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
        /// Проверяет, запущен ли текущий процесс с правами Администратора
        /// </summary>
        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Применяет комплексный патч реестра. Если текущий процесс запущен без прав администратора,
        /// автоматически запрашивает системное окно повышения привилегий UAC.
        /// </summary>
        public static bool ApplyAllFixesWithElevation(out string statusMessage)
        {
            statusMessage = string.Empty;

            if (IsAdministrator())
            {
                bool directSuccess = ApplyAllFixes();
                statusMessage = directSuccess ? "Патчи реестра успешно применены!" : "Не удалось применить патчи реестра.";
                return directSuccess;
            }

            try
            {
                string? exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    statusMessage = "Не удалось определить путь к исполняемому файлу приложения.";
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--apply-registry-fix",
                    Verb = "runas", // Системный вызов UAC Windows
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0 && IsAllFixesApplied())
                    {
                        statusMessage = "Патчи реестра успешно применены через повышенные привилегии!";
                        return true;
                    }
                    else
                    {
                        statusMessage = "Процесс настройки реестра завершился с ошибкой.";
                        return false;
                    }
                }
                else
                {
                    statusMessage = "Не удалось запустить процесс запроса прав администратора.";
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (пользователь нажал 'Нет' в UAC)
            {
                statusMessage = "Запрос прав администратора (UAC) был отменен пользователем.";
                return false;
            }
            catch (Exception ex)
            {
                statusMessage = $"Ошибка вызова UAC: {ex.Message}";
                return false;
            }
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
