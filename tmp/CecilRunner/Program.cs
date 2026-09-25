using System;
using Mono.Cecil;

class Program
{
    static void Main()
    {
        var asm = AssemblyDefinition.ReadAssembly("/app/applet/TelegramWebDAV/bin/Debug/net8.0-windows/winfsp-msil.dll");
        foreach (var type in asm.MainModule.Types)
        {
            if (type.Name == "Api")
            {
                foreach (var m in type.Methods)
                {
                    if (m.Name == ".cctor" || m.Name == "CheckVersion")
                    {
                        Console.WriteLine($"=== METHOD: {m.Name} ===");
                        if (m.HasBody)
                        {
                            foreach (var ins in m.Body.Instructions)
                            {
                                Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode} {ins.Operand}");
                            }
                        }
                    }
                }
            }
        }
    }
}
