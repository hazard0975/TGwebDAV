using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        var asm = Assembly.LoadFrom("TelegramWebDAV/bin/Release/net8.0-windows/win-x64/WTelegramClient.dll");
        var clientType = asm.GetType("WTelegram.Client");
        if (clientType == null) return;

        void DumpMethod(MethodBase? m)
        {
            if (m == null) return;
            Console.WriteLine("=== Method: " + m.DeclaringType?.Name + "." + m.Name + " ===");
            var body = m.GetMethodBody();
            if (body == null) return;
            var il = body.GetILAsByteArray();
            if (il == null) return;
            int i = 0;
            while (i < il.Length)
            {
                byte op = il[i++];
                if (op == 0x72 && i + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try { Console.WriteLine("ldstr: " + asm.ManifestModule.ResolveString(token)); } catch { }
                }
                else if ((op == 0x28 || op == 0x6f) && i + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try
                    {
                        var member = asm.ManifestModule.ResolveMethod(token);
                        Console.WriteLine("call: " + member?.DeclaringType?.Name + "." + member?.Name);
                    }
                    catch { }
                }
            }
        }

        var mLogin = clientType.GetMethod("Login", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
        DumpMethod(mLogin);

        var smLogin = clientType.GetNestedType("<Login>d__114", BindingFlags.NonPublic);
        DumpMethod(smLogin?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance));

        var mRunLogin = clientType.GetMethod("RunLoginAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        DumpMethod(mRunLogin);

        var smRunLogin = clientType.GetNestedType("<RunLoginAsync>d__116", BindingFlags.NonPublic);
        DumpMethod(smRunLogin?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
