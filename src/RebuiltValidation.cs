using dnlib.DotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class RebuiltValidation
{
    internal static void VerifyResources(string recovered,string rebuilt,string report)
    {
        using var source=ModuleDefMD.Load(recovered);using var target=ModuleDefMD.Load(rebuilt);
        var checks=source.Resources.OfType<EmbeddedResource>().GroupBy(r=>r.Name.String).Select(g=>g.First()).Select(resource=> {
            var candidates=target.Resources.OfType<EmbeddedResource>().Where(r=>r.Name==resource.Name).ToArray();
            return new {Name=resource.Name.String,Bytes=resource.CreateReader().Length,
                Equal=candidates.Length==1 && Enumerable.SequenceEqual(resource.CreateReader().ToArray(),candidates[0].CreateReader().ToArray())};
        }).ToArray();
        File.WriteAllText(report,JsonConvert.SerializeObject(checks,Formatting.Indented));
        if(checks.Any(c=>!c.Equal))throw new InvalidDataException("Recompiled C# lost or changed embedded resource bytes; see "+report);
    }

    // C# recompilation changes metadata row IDs. Never reuse input VM tokens.
    internal static int MapMethods(string recovered, string rebuilt, string vmReport, string output)
    {
        using var source = ModuleDefMD.Load(recovered);
        using var target = ModuleDefMD.Load(rebuilt);
        var index = target.GetTypes().SelectMany(t => t.Methods)
            .GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.ToArray());
        var methods = new List<object>();
        int missing = 0;
        foreach (var entry in ClrValidator.ReadTargets(vmReport))
        {
            string originalToken = entry.Token;
            var definition = source.ResolveToken(Convert.ToUInt32(originalToken.Substring(2), 16)) as MethodDef;
            MethodDef? match = null;
            string kind="signature";string? asyncOwner=null;
            if (definition != null && index.TryGetValue(definition.FullName, out var candidates) && candidates.Length == 1)
                match = candidates[0];
            if(definition!=null && MapAsyncStateMethod(source,target,definition,out var active,out asyncOwner))
            {match=active;kind="async-state-machine-by-owner";}
            if (match == null || !match.HasBody) missing++;
            methods.Add(new { OriginalToken = originalToken, Signature = definition?.FullName,
                Token = match == null ? "" : "0x" + match.MDToken.Raw.ToString("X8"),
                Restored = match?.HasBody == true,MappingKind=kind,AsyncOwner=asyncOwner,RebuiltSignature=match?.FullName });
        }
        File.WriteAllText(output, JsonConvert.SerializeObject(methods, Formatting.Indented));
        if (missing != 0) throw new InvalidDataException("Rebuilt C# lost or ambiguously maps " + missing + " recovered VM methods; see " + output);
        return methods.Count;
    }

    // Roslyn legitimately regenerates state types when the source is async.
    // Follow the unique owner attribute in both assemblies, not generated names,
    // token proximity or a same-named unrelated MoveNext method.
    private static bool MapAsyncStateMethod(ModuleDef source,ModuleDef target,MethodDef definition,out MethodDef? match,out string? ownerName)
    {
        match=null;ownerName=null;
        if(definition.IsStatic || definition.ReturnType.ElementType!=ElementType.Void
            || !definition.DeclaringType.Interfaces.Any(i=>i.Interface.FullName=="System.Runtime.CompilerServices.IAsyncStateMachine"))return false;
        string role=definition.Name.String.Split('.').Last();
        if(role=="MoveNext" && definition.MethodSig.Params.Count!=0)return false;
        if(role=="SetStateMachine" && (definition.MethodSig.Params.Count!=1 || definition.MethodSig.Params[0].FullName!="System.Runtime.CompilerServices.IAsyncStateMachine"))return false;
        if(role!="MoveNext" && role!="SetStateMachine")return false;
        string? StateName(MethodDef method)=>method.CustomAttributes.Where(a=>a.TypeFullName=="System.Runtime.CompilerServices.AsyncStateMachineAttribute")
            .Select(a=>a.ConstructorArguments.Count==1?(a.ConstructorArguments[0].Value as TypeSig)?.FullName:null).SingleOrDefault();
        var owners=source.GetTypes().SelectMany(t=>t.Methods).Where(m=>StateName(m)==definition.DeclaringType.FullName).ToArray();
        if(owners.Length!=1)return false;
        ownerName=owners[0].FullName;
        var rebuiltOwners=target.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.FullName==owners[0].FullName).ToArray();
        if(rebuiltOwners.Length!=1)return false;
        string? stateName=StateName(rebuiltOwners[0]);
        if(stateName==null)return false;
        var states=target.GetTypes().Where(t=>t.FullName==stateName && t.Interfaces.Any(i=>i.Interface.FullName=="System.Runtime.CompilerServices.IAsyncStateMachine")).ToArray();
        if(states.Length!=1)return false;
        var methods=states[0].Methods.Where(m=>!m.IsStatic && m.Name.String.Split('.').Last()==role
            && m.MethodSig.Params.Count==definition.MethodSig.Params.Count && m.ReturnType.ElementType==ElementType.Void).ToArray();
        if(methods.Length!=1)return false;
        match=methods[0];return true;
    }
}
