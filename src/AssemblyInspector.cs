using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

internal static class AssemblyInspector
{
    internal static AssemblyInfo Inspect(string path)
    {
        using var module = ModuleDefMD.Load(path);
        var types = module.GetTypes().ToArray();
        var methods = types.SelectMany(t => t.Methods).ToArray();
        var result = new AssemblyInfo
        {
            Runtime = module.RuntimeVersion,
            Types = types.Length,
            Methods = methods.Length,
            Resources = module.Resources.Count,
            ModernRuntime = module.GetAssemblyRefs().Any(a => a.Name == "System.Private.CoreLib" || a.Name == "System.Runtime" && a.Version.Major >= 5)
        };
        foreach (var method in methods.Where(m => m.HasBody))
        {
            if (method.IsStatic && method.MethodSig?.ToString() == "System.String (System.Int32)")
                result.StringInt32Candidates.Add(method.MDToken.Raw);
            var ins = method.Body.Instructions;
            result.StringLiterals += ins.Count(i => i.OpCode.Code == Code.Ldstr);
            result.StringInt32Calls += ins.Count(i => i.Operand is IMethod target && target.MethodSig?.ToString() == "System.String (System.Int32)");
            // Calls into a VM executor use Stream as first argument and either an encoded string or decoded position as second.
            if (ins.Any(i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) && i.Operand is IMethod target &&
                target.MethodSig?.Params.Count >= 2 && target.MethodSig.Params[0].FullName == "System.IO.Stream" &&
                (target.MethodSig.Params[1].FullName == "System.String" || target.MethodSig.Params[1].FullName == "System.Int64") &&
                target.MethodSig.Params.Last().FullName == "System.Object[]" &&
                method.MethodSig?.Params.Any(p => p.FullName == "System.IO.Stream") == false))
                result.VirtualizedMethods.Add(method.MDToken.Raw);
        }
        return result;
    }
}
