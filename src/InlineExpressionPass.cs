using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;

namespace Unpacker;

/// <summary>Inlines bounded, total integer IL leaves, never arbitrary runtime code.</summary>
internal static class InlineExpressionPass
{
    internal static int Process(ModuleDef module,string report)
    {
        var changes=new List<object>();
        var candidates=module.GetTypes().SelectMany(t=>t.Methods).Where(IsCandidate).ToDictionary(m=>m,m=>m.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).TakeWhile(i=>i.OpCode.Code!=Code.Ret).ToArray());
        foreach(var caller in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray())
        {
            int budget=256;var body=caller.Body;
            // Only original call sites are visited: never recursively grow newly emitted IL.
            foreach(var call in body.Instructions.Where(i=>i.OpCode.Code==Code.Call).ToArray())
            {
                if(call.Operand is not MethodDef target||target==caller||!candidates.TryGetValue(target,out var expression))continue;
                int index=body.Instructions.IndexOf(call);
                if(index>0&&body.Instructions[index-1].OpCode.OpCodeType==OpCodeType.Prefix)continue;
                // Removing a static call must not remove or advance another type's cctor.
                if(target.DeclaringType.FindStaticConstructor()!=null &&
                    (target.DeclaringType!=caller.DeclaringType||!caller.IsStatic||caller.DeclaringType.IsBeforeFieldInit))continue;
                if(caller.DeclaringType!=target.DeclaringType&&!(target.IsPublic||target.IsAssembly))continue;
                int count=target.MethodSig.Params.Count,cost=expression.Length+count-1;
                if(cost>budget||body.Variables.Count+count>ushort.MaxValue)continue;
                var locals=target.MethodSig.Params.Select(p=>new Local(p)).ToArray();
                var emitted=new List<Instruction>();
                // Spill every evaluated argument exactly once, in reverse stack order.
                // This preserves call-site side effects, unused args and repeated reads.
                for(int p=count-1;p>=0;p--)emitted.Add(OpCodes.Stloc.ToInstruction(locals[p]));
                foreach(var instruction in expression)
                    emitted.Add(instruction.IsLdarg()?OpCodes.Ldloc.ToInstruction(locals[instruction.GetParameter(target.Parameters)!.MethodSigIndex]):new Instruction(instruction.OpCode,instruction.Operand));
                if(emitted.Count==0)continue;
                foreach(var local in locals)body.Variables.Add(local);
                call.OpCode=emitted[0].OpCode;call.Operand=emitted[0].Operand;
                for(int n=1;n<emitted.Count;n++)body.Instructions.Insert(index+n,emitted[n]);
                body.MaxStack=(ushort)Math.Min(ushort.MaxValue,Math.Max(8,(int)body.MaxStack)+Math.Max(8,(int)target.Body.MaxStack));body.KeepOldMaxStack=true;
                budget-=cost;
                changes.Add(new{Caller="0x"+caller.MDToken.Raw.ToString("X8"),Target="0x"+target.MDToken.Raw.ToString("X8"),TargetName=target.FullName,
                    OriginalOffset=call.Offset,Instructions=expression.Length,SpilledArguments=count,
                    Evidence="Bounded static total I4/I8 leaf; exact-once argument evaluation; no calls/fields/EH/prefixes/generics/division; no removed cctor trigger; definitions retained."});
            }
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(changes,Formatting.Indented));return changes.Count;
    }

    private static bool Integral(TypeSig type)=>type.ElementType is ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8;
    private static bool IsCandidate(MethodDef method)
    {
        if(!method.IsStatic||method.IsConstructor||!method.HasBody||method.HasGenericParameters||method.DeclaringType.HasGenericParameters||
            method.IsSynchronized||method.HasImplMap||method.HasDeclSecurities||method.DeclaringType.HasDeclSecurities||
            method.CustomAttributes.Any(a=>a.TypeFullName.StartsWith("System.Security.",StringComparison.Ordinal))||method.DeclaringType.CustomAttributes.Any(a=>a.TypeFullName.StartsWith("System.Security.",StringComparison.Ordinal))||
            (method.ImplAttributes&MethodImplAttributes.NoInlining)!=0||method.Body.ExceptionHandlers.Count!=0||method.Body.Variables.Count!=0||
            !method.MethodSig.IsDefault||method.MethodSig.HasThis||method.MethodSig.ExplicitThis||!Integral(method.ReturnType)||method.MethodSig.Params.Count>8||!method.MethodSig.Params.All(Integral))return false;
        var code=method.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
        if(code.Length<2||code.Length>32||code[code.Length-1].OpCode.Code!=Code.Ret)return false;
        // Validate evaluation-stack widths, not merely opcode membership. No
        // invalid/incompatible CLI arithmetic becomes a supposedly safe inline.
        var stack=new List<bool>(); // false=I4, true=I8
        foreach(var i in code.Take(code.Length-1))
        {
            if(i.IsLdcI4()){stack.Add(false);continue;}
            if(i.OpCode.Code==Code.Ldc_I8){stack.Add(true);continue;}
            if(i.IsLdarg())
            {
                var p=i.GetParameter(method.Parameters);if(p==null||p.MethodSigIndex<0||p.MethodSigIndex>=method.MethodSig.Params.Count)return false;
                stack.Add(p.Type.ElementType is ElementType.I8 or ElementType.U8);continue;
            }
            if(stack.Count==0)return false;
            bool right=stack[stack.Count-1];stack.RemoveAt(stack.Count-1);
            switch(i.OpCode.Code)
            {
                case Code.Neg:case Code.Not:stack.Add(right);break;
                case Code.Conv_I1:case Code.Conv_U1:case Code.Conv_I2:case Code.Conv_U2:case Code.Conv_I4:case Code.Conv_U4:stack.Add(false);break;
                case Code.Conv_I8:case Code.Conv_U8:stack.Add(true);break;
                case Code.Add:case Code.Sub:case Code.Mul:case Code.And:case Code.Or:case Code.Xor:
                case Code.Shl:case Code.Shr:case Code.Shr_Un:case Code.Ceq:case Code.Cgt:case Code.Cgt_Un:case Code.Clt:case Code.Clt_Un:
                    if(stack.Count==0)return false;
                    bool left=stack[stack.Count-1];stack.RemoveAt(stack.Count-1);
                    bool shift=i.OpCode.Code is Code.Shl or Code.Shr or Code.Shr_Un;
                    if(shift?right:left!=right)return false;
                    stack.Add(i.OpCode.Code is Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un?false:left);break;
                default:return false;
            }
        }
        return stack.Count==1&&stack[0]==(method.ReturnType.ElementType is ElementType.I8 or ElementType.U8);
    }
}
