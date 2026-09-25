using System;
using Mono.Cecil;
using Mono.Cecil.Rocks;

class Program
{
    static void Main()
    {
        var asm = AssemblyDefinition.ReadAssembly("/app/applet/TelegramWebDAV/bin/Debug/net8.0-windows/winfsp-msil.dll");
        foreach (var type in asm.MainModule.GetAllTypes())
        {
            if (type.Name.Contains("Api"))
            {
                Console.WriteLine("==============================");
                Console.WriteLine($"Type: {type.FullName}");
                foreach (var field in type.Fields)
                {
                    Console.WriteLine($"  Field: {field.Name} ({field.FieldType.FullName})");
                }
                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == "CheckVersion")
                    {
                        Console.WriteLine($"  Method: {method.Name}");
                        if (method.HasBody)
                        {
                            foreach (var instr in method.Body.Instructions)
                            {
                                Console.WriteLine($"    {instr.OpCode} {instr.Operand}");
                            }
                        }
                    }
                }
            }
        }
    }
}
