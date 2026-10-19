using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;

namespace Unpacker;

internal static class SafeProxyPass
{
    private sealed class Forwarder
    {
        internal OpCode Code=OpCodes.Call;
        internal MethodDef Target=null!;
    }

    private sealed class Plan
    {
        internal OpCode Code=OpCodes.Call;
        internal MethodDef Target=null!;
        internal int Depth;
        internal HashSet<TypeDef> InitializedTypes=new();
    }

    internal static int Process(ModuleDef module,string report)
    {
        var changes=new List<object>();
        var comparer=new SigComparer();
        var methods=module.GetTypes().SelectMany(t=>t.Methods).ToArray();
        var forwarders=new Dictionary<MethodDef,Forwarder>();
        foreach(var method in methods)
        {
            // Retain methods and tokens. Only bypass a static, exact argument-order
            // forwarder, including a compiler-generated store/reload of its result.
            if(!method.IsStatic || method.IsConstructor || method.HasGenericParameters || method.DeclaringType.HasGenericParameters || !method.HasBody
                || !method.MethodSig.IsDefault || method.MethodSig.HasThis || method.MethodSig.ExplicitThis
                || method.IsSynchronized || method.HasDeclSecurities || method.HasImplMap
                || method.DeclaringType.HasDeclSecurities || method.Body.ExceptionHandlers.Count!=0)continue;
            var il=method.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
            int count=method.MethodSig.Params.Count;
            bool direct=il.Length==count+2 && method.Body.Variables.Count==0 && il[count+1].OpCode.Code==Code.Ret;
            bool spilled=il.Length==count+4 && method.Body.Variables.Count==1 && method.ReturnType.ElementType!=ElementType.Void
                && method.ReturnType.RemovePinnedAndModifiers().ElementType is not (ElementType.R4 or ElementType.R8)
                && il[count+1].IsStloc() && il[count+2].IsLdloc() && il[count+3].OpCode.Code==Code.Ret
                && il[count+1].GetLocal(method.Body.Variables)==method.Body.Variables[0]
                && il[count+2].GetLocal(method.Body.Variables)==method.Body.Variables[0]
                && comparer.Equals(method.Body.Variables[0].Type,method.ReturnType);
            if((!direct && !spilled)
                || il[count].OpCode.Code is not (Code.Call or Code.Callvirt)
                || ResolveLocalMethod(il[count].Operand,module) is not MethodDef target || target==method
                || target.IsConstructor || target.HasGenericParameters || target.DeclaringType.HasGenericParameters || target.IsVirtual || !target.HasBody
                || !target.MethodSig.IsDefault || target.MethodSig.ExplicitThis || target.MethodSig.HasThis==target.IsStatic
                || target.IsSynchronized || target.HasDeclSecurities || target.HasImplMap || target.DeclaringType.HasDeclSecurities
                || target.Body.ExceptionHandlers.Count!=0 || (target.IsStatic && il[count].OpCode.Code!=Code.Call))continue;
            TypeSig receiver=target.DeclaringType.ToTypeSig();
            if(target.DeclaringType.IsValueType)receiver=new ByRefSig(receiver);
            var expected=target.IsStatic?target.MethodSig.Params.ToArray():new[]{receiver}.Concat(target.MethodSig.Params).ToArray();
            if(expected.Length!=count || !SameStackType(method.ReturnType,target.ReturnType,comparer)
                || expected.Where((p,i)=>!SameStackType(p,method.MethodSig.Params[i],comparer)).Any())continue;
            if(Enumerable.Range(0,count).Any(i=>!il[i].IsLdarg() || il[i].GetParameter(method.Parameters)?.MethodSigIndex!=i))continue;
            forwarders.Add(method,new Forwarder{Code=il[count].OpCode,Target=target});
        }
        var proxies=new Dictionary<MethodDef,Plan>();
        foreach(var proxy in forwarders.Keys)
        {
            var plan=new Plan();
            var visited=new HashSet<MethodDef>();
            var current=proxy;
            // A cycle is not a leaf. Bound unusually deep graphs without recursion.
            while(plan.Depth<1024 && visited.Add(current) && forwarders.TryGetValue(current,out var next))
            {
                RequireInitialization(current.DeclaringType,plan);
                plan.Depth++;plan.Code=next.Code;plan.Target=next.Target;
                current=next.Target;
            }
            if(plan.Target==null || forwarders.ContainsKey(current))continue;
            var target=plan.Target;
            // Caller-sensitive APIs and external/native behavior remain excluded:
            // only transparent local chains ending at a call-free IL leaf qualify.
            if(target.Body.Instructions.Any(i=>i.OpCode.Code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj
                or Code.Jmp or Code.Ldftn or Code.Ldvirtftn))continue;
            RequireInitialization(target.DeclaringType,plan);
            bool unknownFieldOwner=false;
            foreach(var field in target.Body.Instructions.Select(i=>i.Operand).OfType<IField>())
            {
                var owner=field.DeclaringType.ResolveTypeDef();
                if(owner==null || owner.Module!=module){unknownFieldOwner=true;break;}
                RequireInitialization(owner,plan);
            }
            if(!unknownFieldOwner)proxies.Add(proxy,plan);
        }
        foreach(var caller in methods.Where(m=>m.HasBody))
        for(int index=0;index<caller.Body.Instructions.Count;index++)
        {
            var instruction=caller.Body.Instructions[index];
            if(instruction.OpCode.Code!=Code.Call || ResolveLocalMethod(instruction.Operand,module) is not MethodDef proxy
                || !proxies.TryGetValue(proxy,out var plan))continue;
            if(caller.DeclaringType!=plan.Target.DeclaringType
                && (!(plan.Target.IsPublic || plan.Target.IsAssembly) || plan.Target.DeclaringType.DeclaringType!=null))continue;
            // A precise-initialization static caller has entered its own cctor (or
            // completed it). BeforeFieldInit and instance methods do not prove this.
            // Never remove a trigger for any other type's cctor.
            if(plan.InitializedTypes.Any(t=>t!=caller.DeclaringType
                || (!caller.IsStaticConstructor && (!caller.IsStatic || t.IsBeforeFieldInit))))continue;
            // Prefixes (notably tail./constrained.) require separate proof.
            if(index>0 && caller.Body.Instructions[index-1].OpCode.OpCodeType==OpCodeType.Prefix)continue;
            changes.Add(new{Caller="0x"+caller.MDToken.Raw.ToString("X8"),Offset=instruction.Offset,
                Proxy="0x"+proxy.MDToken.Raw.ToString("X8"),Target="0x"+plan.Target.MDToken.Raw.ToString("X8"),ChainDepth=plan.Depth,
                Evidence="Exact argument-order static chain; identical CLI stack types; no EH/synchronization/security or removed type-initializer trigger; local nonvirtual call-free leaf; definitions retained."});
            instruction.OpCode=plan.Code;instruction.Operand=plan.Target;
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(changes,Formatting.Indented));
        return changes.Count;
    }

    private static void RequireInitialization(TypeDef type,Plan plan)
    {
        if(type.FindStaticConstructor()!=null)plan.InitializedTypes.Add(type);
    }

    private static MethodDef? ResolveLocalMethod(object? operand,ModuleDef module)
    {
        if(operand is MethodDef definition)return definition.Module==module?definition:null;
        // Do not resolve a MethodSpec or a reference to another assembly: generic
        // substitution and external execution need a separate proof.
        if(operand is not MemberRef reference || !reference.IsMethodRef || reference.DeclaringType==null || reference.DeclaringType is TypeSpec
            || !AssemblyNameComparer.CompareAll.Equals(reference.DeclaringType.DefinitionAssembly,module.Assembly))return null;
        // Lookup in this module also works for ModuleDefUser fixtures and does not
        // ask an assembly resolver to load a same-identity external assembly.
        var owner=reference.DeclaringType as TypeDef ?? module.Find(reference.DeclaringType.FullName,false);
        var resolved=owner?.FindMethod(reference.Name,reference.MethodSig);
        return resolved?.Module==module?resolved:null;
    }

    private static bool SameStackType(TypeSig left,TypeSig right,SigComparer comparer)
    {
        if(comparer.Equals(left,right))return true;
        // I4/U4 and I8/U8 have exactly the same CLI evaluation-stack width and
        // bit pattern. Do NOT extend this to small integers (sign extension),
        // floating point (rounding), enums, byrefs, modifiers or native integers.
        return (left.ElementType is ElementType.I4 or ElementType.U4) && (right.ElementType is ElementType.I4 or ElementType.U4)
            || (left.ElementType is ElementType.I8 or ElementType.U8) && (right.ElementType is ElementType.I8 or ElementType.U8);
    }
}
