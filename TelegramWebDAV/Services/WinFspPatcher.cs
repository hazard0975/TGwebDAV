using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Автоматический патчер легаси-библиотеки winfsp-msil.dll для .NET 8.
    /// Устраняет устаревший метод CheckVersion() (Assembly.GetExecutingAssembly().Location),
    /// гарантируя 100% стабильную инициализацию WinFsp в современных средах .NET 8 x64.
    /// </summary>
    public static class WinFspPatcher
    {
        public static void PatchIfNeeded()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllPath = Path.Combine(baseDir, "winfsp-msil.dll");

                if (!File.Exists(dllPath))
                {
                    return;
                }

                // Читаем сборку через Mono.Cecil
                using (var asm = AssemblyDefinition.ReadAssembly(dllPath))
                {
                    bool patched = false;
                    foreach (var type in asm.MainModule.Types)
                    {
                        if (type.FullName == "Fsp.Interop.Api")
                        {
                            foreach (var method in type.Methods)
                            {
                                if (method.Name == "CheckVersion")
                                {
                                    // Если метод уже содержит ровно 1 инструкцию RET, он уже пропатчен
                                    if (method.HasBody && method.Body.Instructions.Count == 1 &&
                                        method.Body.Instructions[0].OpCode == OpCodes.Ret)
                                    {
                                        return;
                                    }

                                    // Заменяем тело метода CheckVersion на безопасную заглушку return
                                    method.Body.Instructions.Clear();
                                    method.Body.Variables.Clear();
                                    method.Body.ExceptionHandlers.Clear();
                                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                                    patched = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (patched)
                    {
                        string tempPath = dllPath + ".tmp";
                        asm.Write(tempPath);
                        asm.Dispose();

                        File.Delete(dllPath);
                        File.Move(tempPath, dllPath);
                        AppLogger.Info("WinFspPatcher", "Файл winfsp-msil.dll успешно адаптирован для .NET 8 (CheckVersion -> RET).");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WinFspPatcher", $"Предупреждение WinFspPatcher: {ex.Message}");
            }
        }
    }
}
