using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class ClrValidator
{
    internal sealed class ValidationTarget
    {
        internal string Token="",Role="",HostToken="";
    }

    internal static IReadOnlyList<ValidationTarget> ReadTargets(string vmReportPath)
    {
        var targets=new Dictionary<string,ValidationTarget>(StringComparer.OrdinalIgnoreCase);
        void Add(string token,string role,string host)
        {
            if(string.IsNullOrWhiteSpace(token))throw new InvalidDataException("Recovered method has no final metadata token.");
            if(!targets.ContainsKey(token))targets.Add(token,new ValidationTarget{Token=token,Role=role,HostToken=host});
        }
        foreach(var entry in JArray.Parse(File.ReadAllText(vmReportPath)).Where(e=>(bool?)e["Restored"]==true))
        {
            string token=(string?)entry["Token"]??"";
            Add(token,"recovered-host",token);
            string? adapter=(string?)entry["RecoveredAdapterToken"];
            if((bool?)entry["Inlined"]==true && string.IsNullOrWhiteSpace(adapter))
                throw new InvalidDataException("Inlined recovered VM body has no final adapter token: "+token);
            if(!string.IsNullOrWhiteSpace(adapter))Add(adapter!,"recovered-inline-adapter",token);
            if(entry["AccessBridgeTokens"] is JArray bridges)
                foreach(var bridge in bridges)Add((string?)bridge??"","recovered-access-bridge",token);
        }
        return targets.Values.ToArray();
    }
    // Run in a separate process so target identity, loaded helpers and dependencies
    // cannot hide broken references in the final output.
    internal static int Run(string assemblyPath, string vmReportPath, string outputPath,int stage=5,string? originalInput=null)
    {
        ProgressEvents.Send(stage,"info","加载 CLR 检查程序集");
        using var dependencyScope=new DependencyLoadScope(originalInput??assemblyPath,assemblyPath);
        var results = new List<object>();
        var bindings = new List<object>();
        var missingDependencies=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(outputPath+".active.txt","Reading embedded dependency identities");
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
        var entries=ReadTargets(vmReportPath);
        void SaveReport(bool complete)
        {
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(new { Complete=complete,Expected=entries.Count,Prepared=prepared,Skipped=skipped,Failed=failed,
                MissingDependencies=missingDependencies.OrderBy(n=>n,StringComparer.OrdinalIgnoreCase),DependencyBindings=bindings.Concat(dependencyScope.Bindings),Methods=results,
                Limitations="CLR preparation is not execution equivalence. Missing dependencies remain validation failures, not proof of invalid recovered IL. Original names and source are not established by this check." }, Formatting.Indented));
        }
        SaveReport(false);
        Assembly? assembly=null;
        Exception? assemblyLoadFailure=null;
        File.WriteAllText(outputPath+".active.txt","Loading recovered assembly: "+assemblyPath);
        try {assembly=Assembly.LoadFile(Path.GetFullPath(assemblyPath));}
        catch(Exception ex) {assemblyLoadFailure=ex;}
        var progress=new ProgressEvents.Counter(stage);
        ProgressEvents.Send(stage,"info","准备 "+entries.Count+" 个 CLR 方法（含内联适配器）");
        foreach (var entry in entries)
        {
            string token = entry.Token;
            string status, detail, failureKind="",rootCause="",missingAssembly="";
            try
            {
                File.WriteAllText(outputPath+".active.txt","Resolving "+token);
                if(assemblyLoadFailure!=null)throw new InvalidOperationException("The recovered assembly could not be loaded.",assemblyLoadFailure);
                var method = assembly!.ManifestModule.ResolveMethod(Convert.ToInt32(token.Substring(2), 16));
                if (method.ContainsGenericParameters)
                {
                    method = CloseMethod(method, assembly);
                }
                var genericArguments = (method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : Array.Empty<Type>())
                    .Concat(method is MethodInfo { IsGenericMethod: true } info ? info.GetGenericArguments() : Array.Empty<Type>()).ToArray();
                File.WriteAllText(outputPath+".active.txt","Preparing "+token+" "+method);
                if (genericArguments.Length == 0) RuntimeHelpers.PrepareMethod(method.MethodHandle);
                else RuntimeHelpers.PrepareMethod(method.MethodHandle, genericArguments.Select(t => t.TypeHandle).ToArray());
                prepared++; status = "success";
                detail = "CLR prepared " + method + "; business methods were not explicitly invoked. Generic definitions use one constraint-valid instantiation; CLR loading/JIT may initialize runtime helpers.";
            }
            catch (Exception ex)
            {
                failed++; status = "failed"; detail = ex.GetType().Name + ": " + ex.Message;
                var causes=ExceptionChain(ex).ToArray();
                rootCause=string.Join(" -> ",causes.Select(e=>e.GetType().Name+": "+e.Message));
                var missing=causes.OfType<FileNotFoundException>().Select(e=>e.FileName)
                    .Concat(causes.OfType<FileLoadException>().Select(e=>e.FileName))
                    .Where(n=>!string.IsNullOrEmpty(n)).Distinct().ToArray();
                foreach(string name in missing)missingDependencies.Add(name);
                missingAssembly=string.Join("; ",missing);
                failureKind=missing.Length>0?"missing-dependency"
                    :causes.Any(e=>e is InvalidProgramException or BadImageFormatException)?"invalid-il-or-image"
                    :causes.Any(e=>e is TypeInitializationException)?"type-initialization"
                    :causes.Any(e=>e is TypeLoadException or MissingMethodException or MissingFieldException)?"metadata-resolution"
                    :"clr-preparation";
            }
            results.Add(new { Token = token,entry.Role,entry.HostToken, Status = status, Detail = detail,FailureKind=failureKind,RootCause=rootCause,MissingAssembly=missingAssembly });
            SaveReport(false);
            progress.Report("CLR 已检查；成功 "+prepared+"，失败 "+failed,results.Count,entries.Count);
        }
        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        File.WriteAllText(outputPath+".active.txt","Finished "+entries.Count+" targets");
        SaveReport(true);
        Console.WriteLine($"CLR validation: prepared={prepared}, skipped={skipped}, failed={failed}");
        return failed > 0 ? 1 : skipped > 0 ? 2 : 0;
    }

    private static IEnumerable<Exception> ExceptionChain(Exception error)
    {
        yield return error;
        if(error.InnerException!=null)
            foreach(var inner in ExceptionChain(error.InnerException))yield return inner;
        if(error is ReflectionTypeLoadException types)
            foreach(var loader in types.LoaderExceptions.Where(e=>e!=null))
                foreach(var inner in ExceptionChain(loader!))yield return inner;
    }

    // An exit code alone cannot establish that every restored method was checked.
    internal static bool HasCompleteReport(string path,int expected)
    {
        if(expected<1 || !File.Exists(path))return false;
        try
        {
            var report=JObject.Parse(File.ReadAllText(path));
            var methods=report["Methods"] as JArray;
            return (bool?)report["Complete"]!=false && (int?)report["Prepared"]==expected && (int?)report["Failed"]==0 && (int?)report["Skipped"]==0
                && methods?.Count==expected && methods.All(m=>(string?)m["Status"]=="success")
                && methods.Select(m=>(string?)m["Token"]).All(t=>!string.IsNullOrEmpty(t))
                && methods.Select(m=>(string?)m["Token"]).Distinct(StringComparer.OrdinalIgnoreCase).Count()==expected;
        }
        catch(Exception ex) when(ex is JsonException or IOException or InvalidCastException or ArgumentException or FormatException or OverflowException)
        {return false;}
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

    internal static MethodBase CloseMethod(MethodBase definition, Assembly assembly)
    {
        Type[] availableTypes;
        try {availableTypes=assembly.GetTypes();}
        catch(ReflectionTypeLoadException ex) {availableTypes=ex.Types.Where(t=>t!=null).Cast<Type>().ToArray();}
        var candidates = new[] { typeof(object), typeof(int), typeof(string), typeof(System.IO.MemoryStream), typeof(Action) }
            .Concat(availableTypes.Where(t => !t.ContainsGenericParameters && !t.IsAbstract && !t.IsInterface))
            .Distinct().ToArray();
        var declaringType = definition.DeclaringType!;
        var typeChoices = declaringType.ContainsGenericParameters
            ? Arguments(declaringType.GetGenericArguments(), candidates)
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
            foreach (var methodArguments in Arguments(genericMethod.GetGenericArguments(), candidates))
            {
                try { return genericMethod.MakeGenericMethod(methodArguments); }
                catch (ArgumentException) { }
            }
        }
        throw new InvalidOperationException("Could not construct a constraint-valid generic instantiation for " + definition);
    }

    private static IEnumerable<Type[]> Arguments(Type[] parameters, Type[] candidates)
    {
        // Filter by each parameter's constraints before bounding the search. A
        // compatible type late in a large assembly must not be excluded merely
        // because 64 unrelated types preceded it in the metadata table.
        bool Fits(Type parameter,Type candidate)
        {
            var flags=parameter.GenericParameterAttributes;
            if((flags&GenericParameterAttributes.ReferenceTypeConstraint)!=0 && candidate.IsValueType)return false;
            if((flags&GenericParameterAttributes.NotNullableValueTypeConstraint)!=0
                && (!candidate.IsValueType || Nullable.GetUnderlyingType(candidate)!=null))return false;
            if((flags&GenericParameterAttributes.DefaultConstructorConstraint)!=0
                && !candidate.IsValueType && candidate.GetConstructor(Type.EmptyTypes)==null)return false;
            return parameter.GetGenericParameterConstraints().Where(t=>!t.ContainsGenericParameters).All(t=>t.IsAssignableFrom(candidate));
        }
        var choices=parameters.Select(p=>p.GetGenericParameterConstraints().Where(t=>!t.ContainsGenericParameters && !t.IsAbstract && !t.IsInterface)
            .Concat(candidates).Distinct().Where(t=>Fits(p,t)).Take(64).ToArray()).ToArray();
        if(choices.Any(c=>c.Length==0))yield break;
        int count=parameters.Length;
        // Cap the search to keep unusual constraint sets within the worker timeout.
        int produced = 0;
        var indices = new int[count];
        while (produced++ < 4096)
        {
            yield return indices.Select((index,position)=>choices[position][index]).ToArray();
            int position = count - 1;
            while (position >= 0 && ++indices[position] == choices[position].Length) indices[position--] = 0;
            if (position < 0) yield break;
        }
    }
}
