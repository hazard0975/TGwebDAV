using System;
using System.Reflection;
using System.Linq;

class Program
{
    static void Main()
    {
        var clientType = typeof(WTelegram.Client);
        Console.WriteLine("--- Methods containing 'GetMessages' or 'getMessages' in WTelegram.Client ---");
        var methods = clientType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("GetMessages", StringComparison.OrdinalIgnoreCase));
        
        foreach (var m in methods)
        {
            Console.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }

        Console.WriteLine("\n--- Looking for TL message types ---");
        var tlAssembly = clientType.Assembly;
        var types = tlAssembly.GetTypes()
            .Where(t => t.Name.Contains("GetMessages", StringComparison.OrdinalIgnoreCase))
            .Take(20);

        foreach (var t in types)
        {
            Console.WriteLine($"{t.FullName}");
        }
    }
}
