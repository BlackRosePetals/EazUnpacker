using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class ClrValidator
{
    // Run in a separate process so target identity, loaded helpers and dependencies
    // cannot hide broken references in the final output.
    internal static int Run(string assemblyPath, string vmReportPath, string outputPath,int stage=5)
    {
        ProgressEvents.Send(stage,"info","加载 CLR 检查程序集");
        var results = new List<object>();
        var bindings = new List<object>();
        var embedded = EmbeddedIdentities(assemblyPath);
        ResolveEventHandler resolver = (_, request) =>
        {
            var expected = new AssemblyName(request.Name);
            if (!embedded.Any(name => name.Version == null && SameIdentity(name, expected))) return null;
            string path = Path.Combine(Path.GetDirectoryName(assemblyPath)!, expected.Name + ".dll");
            if (!File.Exists(path)) return null;
            var actual = AssemblyName.GetAssemblyName(path);
            if (!SameIdentity(expected, actual)) return null;
            var dependency = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == actual.FullName) ?? Assembly.LoadFile(path);
            bindings.Add(new { Requested = request.Name, Loaded = dependency.FullName, Basis = "Original version-independent embedded assembly manifest" });
            return dependency;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        int prepared = 0, skipped = 0, failed = 0;
        var assembly = Assembly.LoadFile(Path.GetFullPath(assemblyPath));
        var entries=JArray.Parse(File.ReadAllText(vmReportPath)).Where(e => (bool)e["Restored"]!).ToArray();
        var progress=new ProgressEvents.Counter(stage);
        ProgressEvents.Send(stage,"info","准备 "+entries.Length+" 个 CLR 方法");
        foreach (var entry in entries)
        {
            string token = (string)entry["Token"]!;
            string status, detail;
            try
            {
                var method = assembly.ManifestModule.ResolveMethod(Convert.ToInt32(token.Substring(2), 16));
                if (method.ContainsGenericParameters)
                {
                    method = CloseMethod(method, assembly);
                }
                var genericArguments = (method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : Array.Empty<Type>())
                    .Concat(method is MethodInfo { IsGenericMethod: true } info ? info.GetGenericArguments() : Array.Empty<Type>()).ToArray();
                if (genericArguments.Length == 0) RuntimeHelpers.PrepareMethod(method.MethodHandle);
                else RuntimeHelpers.PrepareMethod(method.MethodHandle, genericArguments.Select(t => t.TypeHandle).ToArray());
                prepared++; status = "success";
                detail = "CLR prepared " + method + "; business code was not invoked. Generic definitions use one constraint-valid instantiation.";
            }
            catch (Exception ex)
            {
                failed++; status = "failed"; detail = ex.GetType().Name + ": " + ex.Message;
            }
            results.Add(new { Token = token, Status = status, Detail = detail });
            progress.Report("CLR 已检查；成功 "+prepared+"，失败 "+failed,results.Count,entries.Length);
        }
        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(new { Prepared = prepared, Skipped = skipped, Failed = failed, DependencyBindings = bindings, Methods = results }, Formatting.Indented));
        Console.WriteLine($"CLR validation: prepared={prepared}, skipped={skipped}, failed={failed}");
        return failed > 0 ? 1 : skipped > 0 ? 2 : 0;
    }

    private static bool SameIdentity(AssemblyName left, AssemblyName right) =>
        string.Equals(left.Name,right.Name,StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.CultureName??"",right.CultureName??"",StringComparison.OrdinalIgnoreCase)
        && Enumerable.SequenceEqual(left.GetPublicKeyToken()??Array.Empty<byte>(),right.GetPublicKeyToken()??Array.Empty<byte>());

    private static List<AssemblyName> EmbeddedIdentities(string path)
    {
        using var module = dnlib.DotNet.ModuleDefMD.Load(path);
        var names = new List<AssemblyName>();
        foreach (var text in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody)
            .SelectMany(m=>m.Body.Instructions).Where(i=>i.OpCode.Code==dnlib.DotNet.Emit.Code.Ldstr).Select(i=>(string)i.Operand))
        {
            var parts = text.Split(',');
            if (parts.Length < 8) continue;
            for (int index=parts.Length%4;index+3<parts.Length;index+=4)
            {
                try
                {
                    string resource = parts[index+1];
                    int separator = resource.IndexOf('|');
                    if(separator>=0)resource=resource.Substring(separator+1);
                    if(!module.Resources.Any(r=>r.Name==resource))continue;
                    string filename=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[index+2]));
                    if(Path.GetFileName(filename)!=filename || !filename.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                    var identity=new AssemblyName(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[index])));
                    if(string.Equals(Path.GetFileNameWithoutExtension(filename),identity.Name,StringComparison.OrdinalIgnoreCase))names.Add(identity);
                }
                catch(FormatException) { }
                catch(ArgumentException) { }
                catch(FileLoadException) { }
            }
        }
        return names;
    }

    private static MethodBase CloseMethod(MethodBase definition, Assembly assembly)
    {
        var candidates = new[] { typeof(object), typeof(int), typeof(string), typeof(System.IO.MemoryStream), typeof(Action) }
            .Concat(assembly.GetTypes().Where(t => !t.ContainsGenericParameters && !t.IsAbstract && !t.IsInterface))
            .Distinct().Take(64).ToArray();
        var declaringType = definition.DeclaringType!;
        var typeChoices = declaringType.ContainsGenericParameters
            ? Arguments(declaringType.GetGenericArguments().Length, candidates)
            : new[] { Array.Empty<Type>() };
        foreach (var typeArguments in typeChoices)
        {
            Type closedType;
            try { closedType = typeArguments.Length == 0 ? declaringType : declaringType.MakeGenericType(typeArguments); }
            catch (ArgumentException) { continue; }
            var method = closedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(closedType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                .FirstOrDefault(m => m.MetadataToken == definition.MetadataToken);
            if (method == null) continue;
            if (!method.ContainsGenericParameters) return method;
            if (method is not MethodInfo genericMethod || !genericMethod.IsGenericMethodDefinition) continue;
            foreach (var methodArguments in Arguments(genericMethod.GetGenericArguments().Length, candidates))
            {
                try { return genericMethod.MakeGenericMethod(methodArguments); }
                catch (ArgumentException) { }
            }
        }
        throw new InvalidOperationException("Could not construct a constraint-valid generic instantiation for " + definition);
    }

    private static IEnumerable<Type[]> Arguments(int count, Type[] candidates)
    {
        // Cap the search to keep unusual constraint sets within the worker timeout.
        int produced = 0;
        var indices = new int[count];
        while (produced++ < 4096)
        {
            yield return indices.Select(i => candidates[i]).ToArray();
            int position = count - 1;
            while (position >= 0 && ++indices[position] == candidates.Length) indices[position--] = 0;
            if (position < 0) yield break;
        }
    }
}
