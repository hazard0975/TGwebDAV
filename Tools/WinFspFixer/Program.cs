using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WinFspFixer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("WinFspFixer: No target directory specified.");
                return;
            }

            string targetDir = args[0];
            string dllPath = Path.Combine(targetDir, "winfsp-msil.dll");

            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"WinFspFixer: winfsp-msil.dll not found in {targetDir}. Skipping.");
                return;
            }

            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(dllPath);
                bool patched = false;

                foreach (var type in asm.MainModule.Types)
                {
                    if (type.FullName == "Fsp.Interop.Api")
                    {
                        foreach (var method in type.Methods)
                        {
                            if (method.Name == "CheckVersion")
                            {
                                if (method.HasBody && method.Body.Instructions.Count == 1 &&
                                    method.Body.Instructions[0].OpCode == OpCodes.Ret)
                                {
                                    Console.WriteLine("WinFspFixer: winfsp-msil.dll is already patched.");
                                    return;
                                }

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
                    Console.WriteLine($"WinFspFixer: Successfully patched winfsp-msil.dll for .NET 8 in {targetDir}.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WinFspFixer Error: {ex.Message}");
            }
        }
    }
}
