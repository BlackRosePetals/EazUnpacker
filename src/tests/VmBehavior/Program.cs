using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using dnlib.DotNet;

internal static class Program
{
    // Samples may create foreground helper threads during module initialization.
    // This disposable probe must exit once its result has been reported.
    [STAThread]
    public static void Main(string[] args)
    {
        try { Environment.Exit(Unpacker.WorkerDesktop.Run(args,Run)); }
        catch(Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
    }

    private static int Run(string[] args)
    {
        try
        {
            if(args[0]=="smoke-start")return StartupProbe.Run(args[1],args[2],args.Skip(3).ToArray());
            if(args[0]=="async-ui")return AsyncUiProbe.Run(args[1],args[2]);
            if(args[0]=="--smoke-fixture")
            {
                System.Windows.Forms.Application.Run(new System.Windows.Forms.Form {Text="Unpacker startup fixture"});return 0;
            }
            if(args[0]=="check-field-data")
            {
                using(var source=ModuleDefMD.Load(args[1]))
                {
                    var rebuilt=Assembly.LoadFile(Path.GetFullPath(args[2]));
                    var exported=JArray.Parse(File.ReadAllText(args[3])).Where(f=>(bool)f["Exported"]!).ToArray();
                    int bytesChecked=0;
                    foreach(var item in exported)
                    {
                        var field=(FieldDef)source.ResolveToken(Convert.ToUInt32(((string)item["Token"]!).Substring(2),16));
                        var type=rebuilt.GetType(field.DeclaringType.ReflectionFullName,true)!;
                        var actualField=type.GetField(field.Name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly)
                            ?? throw new Exception("Lost field: "+field.FullName);
                        object value=actualField.GetValue(null)!;
                        if(System.Runtime.InteropServices.Marshal.SizeOf(actualField.FieldType)!=field.InitialValue.Length)
                            throw new Exception("Field layout mismatch: "+field.FullName);
                        var pinned=System.Runtime.InteropServices.GCHandle.Alloc(value,System.Runtime.InteropServices.GCHandleType.Pinned);
                        try
                        {
                            var bytes=new byte[field.InitialValue.Length];
                            System.Runtime.InteropServices.Marshal.Copy(pinned.AddrOfPinnedObject(),bytes,0,bytes.Length);
                            if(!bytes.SequenceEqual(field.InitialValue))throw new Exception("Rebuilt field bytes differ: "+field.FullName);
                            bytesChecked+=bytes.Length;
                        }
                        finally {pinned.Free();}
                    }
                    File.WriteAllText(args[4],new Newtonsoft.Json.Linq.JObject { ["Fields"]=exported.Length,["Bytes"]=bytesChecked,["Status"]="success" }.ToString());
                    Console.WriteLine("PASS: rebuilt C# initializes "+exported.Length+" exported RVA fields with "+bytesChecked+" original bytes.");
                }
                return 0;
            }
            if(args[0]=="check-initialization")
            {
                using(var original=dnlib.DotNet.ModuleDefMD.Load(args[1]))
                using(var rebuilt=dnlib.DotNet.ModuleDefMD.Load(args[2]))
                {
                    var before=Calls(original.GlobalType.FindStaticConstructor());
                    var initializer=rebuilt.GlobalType.FindStaticConstructor();
                    var target=initializer?.Body.Instructions.Select(i=>i.Operand).OfType<dnlib.DotNet.IMethod>()
                        .FirstOrDefault(m=>m.DeclaringType.FullName=="UnpackModuleInitializer" && m.Name=="Initialize");
                    var after=target==null?Calls(initializer):Calls(target.ResolveMethodDef());
                    if(!before.SequenceEqual(after))throw new Exception("Module initialization calls were lost or reordered.");
                    Console.WriteLine("PASS: recompiled module retains "+before.Length+" ordered initialization calls.");
                }
                return 0;
            }
            if(args[0]=="verify-strings")
            {
                var assembly=Assembly.LoadFile(Path.GetFullPath(args[1]));
                string line=File.ReadLines(args[2]).Last(l=>l.Contains("[060007A1] Guard "));
                var match=System.Text.RegularExpressions.Regex.Match(line,@"Guard ([0-9A-F]+); salt=(\d+); flag=(\d+); RVA=(.+)$");
                var verifier=ResolveMethod(assembly,Convert.ToInt32(match.Groups[1].Value,16));
                byte[] bytes=Convert.FromBase64String(match.Groups[4].Value);
                ulong salt=ulong.Parse(match.Groups[2].Value);int flag=int.Parse(match.Groups[3].Value);
                foreach(string value in File.ReadAllLines(args[3]))
                {
                    bool valid;
                    try{valid=verifier.Invoke(null,new object[]{value,bytes,salt,flag}) is true;}
                    catch(TargetInvocationException ex) when(ex.InnerException is ArgumentException){continue;}
                    if(!valid)continue;
                    using(var sha=System.Security.Cryptography.SHA256.Create())
                    {
                        string profile=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant()+"|"+salt+"|"+flag+"|s:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
                        File.WriteAllText(args[4],profile+Environment.NewLine);Console.WriteLine("Verified original guard: "+value);
                    }
                    return 0;
                }
                return 2;
            }
            if (args[0] == "jit")
            {
                var assembly = Assembly.LoadFile(Path.GetFullPath(args[1]));
                var report = JArray.Parse(File.ReadAllText(args[2]));
                int checkedMethods = 0, skipped = 0, failed = 0;
                foreach (var entry in report.Where(e => (bool)e["Restored"]!))
                {
                    int token = Convert.ToInt32(((string)entry["Token"]!).Substring(2), 16);
                    var method = assembly.ManifestModule.ResolveMethod(token) ?? throw new MissingMethodException("Method token "+token);
                    if (method.ContainsGenericParameters) { skipped++; continue; }
                    try { RuntimeHelpers.PrepareMethod(method.MethodHandle); checkedMethods++; }
                    catch (Exception ex) { failed++; Console.Error.WriteLine("JIT rejected " + entry["Token"] + " " + method + ": " + ex.GetType().Name); }
                }
                Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")}: JIT prepared {checkedMethods} recovered methods; {skipped} open generic definitions skipped; {failed} rejected.");
                return failed == 0 ? 0 : 1;
            }
            if (args[0] == "compare-hash")
            {
                var original = Assembly.Load(File.ReadAllBytes(args[1]));
                var output = Assembly.LoadFile(Path.GetFullPath(args[2]));
                int token = Convert.ToInt32(args[3], 16);
                var before = ResolveMethod(original,token);
                var after = ResolveMethod(output,token);
                var rebuilt=args.Length>4?FindRebuilt(args[4],after):null;
                var values = new System.Collections.Generic.List<string?> { null, "", "a", "abc", "1234", "hello", "中文", "abcdefghijklmnopqrstuvwxyz",
                    "\0", "a\0b", "\r\n\t", "😀", "\uD800", "\uDC00", "e\u0301", "é", new string('x',65536) };
                var random=new Random(20261016);
                for(int sample=0;sample<256;sample++)
                {
                    var chars=new char[random.Next(0,1025)];
                    for(int c=0;c<chars.Length;c++)chars[c]=(char)random.Next(0,65536);
                    values.Add(new string(chars));
                }
                foreach (string? value in values)
                {
                    var expected = InvokeOutcome(before, value);
                    var actual = InvokeOutcome(after, value);
                    if (!Equals(expected, actual)) throw new Exception("Hash behavior mismatch at case " + values.IndexOf(value));
                    if(rebuilt!=null && !Equals(expected,InvokeOutcome(rebuilt,value)))throw new Exception("Rebuilt C# hash behavior mismatch at case "+values.IndexOf(value));
                }
                Console.WriteLine($"PASS: original VM, recovered assembly{(rebuilt==null?"":", rebuilt C#")} hash method agree on {values.Count} inputs (including null/exception, UTF-16 edge cases and seeded random strings).");
                return 0;
            }
            if (args[0] == "compare-out")
            {
                var original = Assembly.Load(File.ReadAllBytes(args[1]));
                var output = Assembly.LoadFile(Path.GetFullPath(args[2]));
                string? rebuiltFile=args.Skip(3).FirstOrDefault(a=>File.Exists(a));
                foreach (string tokenText in args.Skip(3).Where(a=>!File.Exists(a)))
                {
                    int token = Convert.ToInt32(tokenText, 16);
                    var before = ResolveMethod(original,token);
                    var after = ResolveMethod(output,token);
                    object[] expected = { null! }, actual = { null! };
                    before.Invoke(null, expected); after.Invoke(null, actual);
                    var left = ((IEnumerable)expected[0]).Cast<object>().ToArray();
                    var right = ((IEnumerable)actual[0]).Cast<object>().ToArray();
                    if (!left.SequenceEqual(right)) throw new Exception("Out-parameter behavior mismatch: " + tokenText);
                    if(rebuiltFile!=null)
                    {
                        object[] rebuiltArguments={null!};FindRebuilt(rebuiltFile,after).Invoke(null,rebuiltArguments);
                        if(!left.SequenceEqual(((IEnumerable)rebuiltArguments[0]).Cast<object>()))throw new Exception("Rebuilt C# out-parameter behavior mismatch: "+tokenText);
                    }
                    Console.WriteLine($"PASS: original VM, recovered assembly{(rebuiltFile==null?"":", rebuilt C#")} out-list helper {tokenText} agree on {left.Length} elements.");
                }
                return 0;
            }
            throw new ArgumentException("Unknown fixture mode.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static MethodInfo ResolveMethod(Assembly assembly,int token)=>assembly.ManifestModule.ResolveMethod(token) as MethodInfo
        ?? throw new MissingMethodException("Expected method token "+token.ToString("X8"));

    private static object? InvokeOutcome(MethodInfo method,string? value)
    {
        try {return ("return",method.Invoke(null,new object?[]{value}));}
        catch(TargetInvocationException ex) {return ("throw",(object?)ex.InnerException!.GetType().FullName);}
    }

    private static string[] Calls(dnlib.DotNet.MethodDef? method)=>method?.Body?.Instructions
        .Where(i=>i.OpCode.Code==dnlib.DotNet.Emit.Code.Call).Select(i=>(dnlib.DotNet.IMethod)i.Operand)
        .Select(m=>m.DeclaringType.FullName+"::"+m.Name).ToArray()??Array.Empty<string>();

    private static MethodInfo FindRebuilt(string file,MethodInfo reference)
    {
        var assembly=Assembly.LoadFile(Path.GetFullPath(file));
        string directory=Path.GetDirectoryName(Path.GetFullPath(file))!;
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>
        {
            string dependency=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");
            return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
        };
        return assembly.GetType(reference.DeclaringType!.FullName!,true)!.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)
            .Single(m=>m.Name==reference.Name && m.GetParameters().Select(p=>p.ParameterType.FullName).SequenceEqual(reference.GetParameters().Select(p=>p.ParameterType.FullName)));
    }
}
