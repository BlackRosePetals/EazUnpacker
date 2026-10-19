using System.Security.Cryptography;
using dnlib.DotNet;
using Newtonsoft.Json;

namespace Unpacker;

internal static class Analyzer
{
    internal static int Run(string input)
    {
        input=Path.GetFullPath(input);
        byte[] bytes=File.ReadAllBytes(input);
        string temporary=WorkCache.CreateTemporary("analysis");
        try
        {
            bool bundle=BundleExtractor.IsBundle(bytes,out long header);
            var payloads=bundle?BundleExtractor.Extract(bytes,header,temporary):new List<string>{input};
            var assemblies=new List<object>();
            foreach(string file in payloads.Where(f=>Path.GetExtension(f).Equals(".dll",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(f).Equals(".exe",StringComparison.OrdinalIgnoreCase)))
            {
                using var module=ModuleDefMD.Load(file);
                var types=module.GetTypes().Where(t=>!t.IsGlobalModuleType).ToArray();
                assemblies.Add(new {File=Path.GetFileName(file),Info=AssemblyInspector.Inspect(file),
                    ObfuscatedTypes=types.Count(t=>NameRecoveryPass.NeedsName(t.Name,true)),
                    ObfuscatedMethods=types.SelectMany(t=>t.Methods).Count(m=>!m.IsConstructor&&NameRecoveryPass.NeedsName(m.Name)
                        && !m.Overrides.Any(o=>m.Name.String==o.MethodDeclaration.DeclaringType.FullName+"."+o.MethodDeclaration.Name)),
                    ObfuscatedFields=types.SelectMany(t=>t.Fields).Count(f=>NameRecoveryPass.NeedsName(f.Name)),
                    LocalVariables=types.SelectMany(t=>t.Methods).Where(m=>m.HasBody).Sum(m=>m.Body.Variables.Count),
                    TargetFramework=module.Assembly.CustomAttributes.FirstOrDefault(a=>a.TypeFullName=="System.Runtime.Versioning.TargetFrameworkAttribute")?.ConstructorArguments.FirstOrDefault().Value?.ToString()});
            }
            using var sha=SHA256.Create();
            string report=Path.Combine(WorkCache.ForOutput(input),"latest.json");
            var recovery=File.Exists(report)?JsonConvert.DeserializeObject<SampleResult>(File.ReadAllText(report)):null;
            Console.WriteLine(JsonConvert.SerializeObject(new {Input=input,Sha256=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(),Bundle=bundle,Payloads=payloads.Select(Path.GetFileName),Assemblies=assemblies,
                SourceProject=recovery?.SourceProject,RebuiltAssembly=recovery?.RebuiltAssembly,RuntimeConfig=recovery?.RuntimeConfig,StringIndex=recovery?.StringIndex,DiagnosticsDirectory=recovery?.DiagnosticsDirectory},Formatting.Indented));
            return 0;
        }
        finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
}
