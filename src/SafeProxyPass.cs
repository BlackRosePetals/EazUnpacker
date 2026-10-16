using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;

namespace Unpacker;

internal static class SafeProxyPass
{
    internal static int Process(ModuleDef module,string report)
    {
        var changes=new List<object>();
        var comparer=new SigComparer();
        var proxies=new Dictionary<MethodDef,(OpCode Code,MethodDef Target)>();
        foreach(var method in module.GetTypes().SelectMany(t=>t.Methods))
        {
            // Retain methods and tokens. Only bypass a static, exact argument-order
            // forwarder with no initialization, synchronization, EH or generic state.
            if(!method.IsStatic || method.IsConstructor || method.HasGenericParameters || method.DeclaringType.HasGenericParameters || !method.HasBody
                || method.IsSynchronized || method.HasDeclSecurities || method.HasImplMap
                || method.DeclaringType.FindStaticConstructor()!=null || method.Body.ExceptionHandlers.Count!=0
                || method.Body.Variables.Count!=0)continue;
            var il=method.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
            int count=method.MethodSig.Params.Count;
            if(il.Length!=count+2 || il[count+1].OpCode.Code!=Code.Ret
                || il[count].OpCode.Code is not (Code.Call or Code.Callvirt)
                || il[count].Operand is not MethodDef target || target.Module!=module || target==method
                || target.IsConstructor || target.HasGenericParameters || target.DeclaringType.HasGenericParameters || target.IsVirtual || !target.HasBody
                || target.DeclaringType.FindStaticConstructor()!=null
                || target.IsSynchronized || target.HasDeclSecurities || target.Body.ExceptionHandlers.Count!=0)continue;
            // Caller-sensitive APIs and external/native behavior are deliberately
            // excluded: the terminal method must be a local call-free IL leaf.
            if(target.Body.Instructions.Any(i=>i.OpCode.Code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj
                or Code.Jmp or Code.Ldftn or Code.Ldvirtftn))continue;
            if(target.Body.Instructions.Select(i=>i.Operand).OfType<IField>().Any(f=>f.DeclaringType.ResolveTypeDef()?.FindStaticConstructor()!=null))continue;
            var expected=target.IsStatic?target.MethodSig.Params.ToArray():new[]{target.DeclaringType.ToTypeSig()}.Concat(target.MethodSig.Params).ToArray();
            if(expected.Length!=count || !comparer.Equals(method.ReturnType,target.ReturnType)
                || expected.Where((p,i)=>!comparer.Equals(p,method.MethodSig.Params[i])).Any())continue;
            if(Enumerable.Range(0,count).Any(i=>!il[i].IsLdarg() || il[i].GetParameter(method.Parameters)?.MethodSigIndex!=i))continue;
            proxies.Add(method,(il[count].OpCode,target));
        }
        foreach(var caller in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody))
        foreach(var instruction in caller.Body.Instructions)
        {
            if(instruction.OpCode.Code!=Code.Call || instruction.Operand is not MethodDef proxy || !proxies.TryGetValue(proxy,out var plan))continue;
            if(caller.DeclaringType!=plan.Target.DeclaringType
                && (!(plan.Target.IsPublic || plan.Target.IsAssembly) || plan.Target.DeclaringType.DeclaringType!=null))continue;
            // Prefixes (notably tail./constrained.) require separate proof.
            int index=caller.Body.Instructions.IndexOf(instruction);
            if(index>0 && caller.Body.Instructions[index-1].OpCode.OpCodeType==OpCodeType.Prefix)continue;
            changes.Add(new{Caller="0x"+caller.MDToken.Raw.ToString("X8"),Offset=instruction.Offset,
                Proxy="0x"+proxy.MDToken.Raw.ToString("X8"),Target="0x"+plan.Target.MDToken.Raw.ToString("X8"),
                Evidence="Exact static forwarding signature/argument order; no proxy cctor/EH/synchronization; local nonvirtual call-free leaf; definitions retained."});
            instruction.OpCode=plan.Code;instruction.Operand=plan.Target;
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(changes,Formatting.Indented));
        return changes.Count;
    }
}
