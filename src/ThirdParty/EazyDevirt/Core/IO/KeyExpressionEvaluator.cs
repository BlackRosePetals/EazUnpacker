using System.Globalization;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.Core.IO;

/// <summary>Bounded, non-executing evaluation of key-expression candidates; the original guard remains authoritative.</summary>
internal static class KeyExpressionEvaluator
{
    private sealed record TypeValue(ITypeDescriptor Type);

    // Infer the producer's CLR type without evaluating its receiver or calling
    // target code. This is a search-order hint, never a substitute for Verify.
    internal static string? InferKeyType(VMMethod method,int loadIndex)
    {
        string? Known(string? name)=>name is "System.String" or "System.Int32" ? name : null;
        var visited=new HashSet<int>();int budget=4096;
        string? ReadType(int index,int depth)
        {
            if(index<0||index>=method.Instructions.Count||depth>64||!visited.Add(index)||--budget<0)return null;
            var code=method.Instructions;var instruction=code[index];
            if(instruction.IsLdcI4())return "System.Int32";
            if(instruction.OpCode.Code==CilCode.Ldstr)return "System.String";
            if(instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt && instruction.Operand is IMethodDescriptor called)
                return Known(called.Signature?.ReturnType.FullName);
            if(instruction.OpCode.Code is CilCode.Box or CilCode.Unbox_Any or CilCode.Castclass)
                return Known((instruction.Operand as ITypeDescriptor)?.FullName)??ReadType(index-1,depth+1);
            if(instruction.OpCode.Code is CilCode.Dup or CilCode.Nop)return ReadType(index-1,depth+1);
            if(instruction.IsLdloc())
            {
                var local=instruction.GetLocalVariable(method.Locals);
                var declared=Known(local.VariableType.FullName);if(declared!=null)return declared;
                for(int i=index-1;i>=0&&--budget>=0;i--)
                    if(code[i].IsStloc()&&code[i].GetLocalVariable(method.Locals)==local)return ReadType(i-1,depth+1);
            }
            return null;
        }
        try {return ReadType(loadIndex,0);}
        catch(Exception ex) when(ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException){return null;}
    }

    internal static IEnumerable<object> IntrinsicCandidates(VMMethod method)
    {
        // PreserveSig COM methods expose an HRESULT, not just small positive
        // integers. Standard success/error/cancellation outcomes are enabled
        // by metadata shape, never an application name, token or key profile.
        bool hresult=method.Instructions.Any(i=>i.OpCode.Code is CilCode.Call or CilCode.Callvirt && i.Operand is IMethodDescriptor called &&
            called.Resolve() is {PreserveSignature:true} definition && definition.DeclaringType?.IsImport==true &&
            definition.Signature?.ReturnType.FullName=="System.Int32");
        if(!hresult)yield break;
        foreach(uint value in new uint[]{0,1,0x80004001,0x80004002,0x80004003,0x80004004,0x80004005,0x80070005,0x8007000e,0x80070057,0x800704c7})
            yield return unchecked((int)value);
    }

    internal static IEnumerable<object> Candidates(VMMethod method,int loadIndex)
    {
        var code=method.Instructions;
        var starts=new List<int>{loadIndex};
        if(code[loadIndex].IsLdloc())
        {
            var local=code[loadIndex].GetLocalVariable(method.Locals);
            starts.AddRange(Enumerable.Range(0,loadIndex).Reverse().Where(i=>code[i].IsStloc()&&code[i].GetLocalVariable(method.Locals)==local).Take(16).Select(i=>i-1));
        }
        var result=new List<object>();
        foreach(int start in starts)
        {
            try {int cursor=start,budget=4096;object? value=Read(method,ref cursor,0,ref budget);
                if(value is int or string && !result.Contains(value))result.Add(value);}
            catch(Exception ex) when(ex is NotSupportedException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException or DivideByZeroException)
            {
                if(Environment.GetEnvironmentVariable("UNPACKER_VM_KEY_DIAGNOSTICS")=="1")
                    Console.WriteLine("Key-expression candidate rejected: "+ex.Message);
            }
        }
        return result;
    }

    private static object? Read(VMMethod method,ref int cursor,int depth,ref int budget)
    {
        if(depth>80||--budget<0||cursor<0)throw new NotSupportedException("Key-expression budget exhausted.");
        var code=method.Instructions;while(cursor>=0&&code[cursor].OpCode.Code==CilCode.Nop)cursor--;
        if(cursor<0)throw new NotSupportedException("Key expression has no producer.");
        var instruction=code[cursor--];
        if(instruction.IsStloc() || instruction.OpCode.Code==CilCode.Pop)
        {
            // A store/pop consumes an intervening expression, not the value below it.
            Read(method,ref cursor,depth+1,ref budget);
            return Read(method,ref cursor,depth+1,ref budget);
        }
        if(instruction.IsLdcI4())return instruction.GetLdcI4Constant();
        if(instruction.OpCode.Code==CilCode.Ldc_I8)return (long)instruction.Operand!;
        if(instruction.OpCode.Code==CilCode.Ldstr)return (string)instruction.Operand!;
        if(instruction.OpCode.Code==CilCode.Ldnull)return null;
        if(instruction.OpCode.Code==CilCode.Ldtoken&&instruction.Operand is ITypeDescriptor type)return new TypeValue(type);
        if(instruction.IsLdloc())
        {
            var local=instruction.GetLocalVariable(method.Locals);
            for(int i=cursor;i>=0;i--)if(code[i].IsStloc()&&code[i].GetLocalVariable(method.Locals)==local)
            {int at=i-1;return Read(method,ref at,depth+1,ref budget);}
            throw new NotSupportedException("Key local depends on unavailable runtime state.");
        }
        if(instruction.OpCode.Code==CilCode.Ldsfld&&instruction.Operand is IFieldDescriptor field&&field.Resolve()?.Constant is { } constant)
            return constant.InterpretData();
        if(instruction.OpCode.Code is CilCode.Box or CilCode.Unbox_Any or CilCode.Castclass or CilCode.Dup)return Read(method,ref cursor,depth+1,ref budget);
        if(instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt && instruction.Operand is IMethodDescriptor called)
        {
            var signature=called.Signature??throw new NotSupportedException();
            var args=new object?[signature.ParameterTypes.Count+(signature.HasThis?1:0)];
            for(int i=args.Length-1;i>=0;i--)args[i]=Read(method,ref cursor,depth+1,ref budget);
            return Call(called,args,method.Parent.DeclaringModule!,0,ref budget);
        }
        if(IsUnary(instruction.OpCode.Code))return Unary(instruction.OpCode.Code,Read(method,ref cursor,depth+1,ref budget));
        if(IsBinary(instruction.OpCode.Code))
        {
            object? right=Read(method,ref cursor,depth+1,ref budget);
            return Binary(instruction.OpCode.Code,Read(method,ref cursor,depth+1,ref budget),right);
        }
        throw new NotSupportedException("Unsupported key producer: "+instruction.OpCode);
    }

    private static object? Call(IMethodDescriptor called,object?[] args,ModuleDefinition module,int depth,ref int budget)
    {
        string owner=called.DeclaringType?.FullName??"",name=called.Name?.ToString()??"";
        if(owner=="System.Type"&&name=="GetTypeFromHandle"&&args.Length==1&&args[0] is TypeValue)return args[0];
        if(owner is "System.Type" or "System.Reflection.MemberInfo" && args.Length==1&&args[0] is TypeValue type)
            return name switch {"get_FullName"=>type.Type.FullName,"get_Name"=>type.Type.Name?.ToString(),"get_Namespace"=>type.Type.Namespace?.ToString(),_=>throw new NotSupportedException()};
        if(owner=="System.String")
        {
            if(name=="get_Length"&&args.Length==1&&args[0] is string text)return text.Length;
            if(name=="get_Chars"&&args.Length==2&&args[0] is string chars)return (int)chars[Convert.ToInt32(args[1])];
            if(name=="Concat"&&args.All(a=>a is string or null)){string value=string.Concat(args);if(value.Length>65536)throw new NotSupportedException("Key string too large.");return value;}
            if(name is "op_Equality" or "Equals" && args.Length==2&&args.All(a=>a is string or null))return Equals(args[0],args[1])?1:0;
        }
        var definition=called.Resolve();
        if(definition?.DeclaringModule!=module||!definition.IsStatic||definition.CilMethodBody==null||definition.GenericParameters.Count!=0||depth>8)
            throw new NotSupportedException("Key helper is not a supported local pure IL function.");
        return Interpret(definition,args,module,depth+1,ref budget);
    }

    private static object? Interpret(MethodDefinition method,object?[] args,ModuleDefinition module,int depth,ref int budget)
    {
        var body=method.CilMethodBody!;if(body.ExceptionHandlers.Count!=0)throw new NotSupportedException("Key helper has exception regions.");
        var code=body.Instructions;code.CalculateOffsets();var offsets=code.Select((i,n)=>(i.Offset,n)).ToDictionary(p=>p.Offset,p=>p.n);
        var stack=new List<object?>();var locals=new Dictionary<CilLocalVariable,object?>();
        object? Pop(){if(stack.Count==0)throw new NotSupportedException("Key helper stack underflow.");var v=stack[stack.Count-1];stack.RemoveAt(stack.Count-1);return v;}
        int Target(object? operand)=>operand is ICilLabel label&&offsets.TryGetValue(label.Offset,out int index)?index:throw new NotSupportedException("Key helper has an invalid branch.");
        for(int pc=0;pc<code.Count;pc++)
        {
            if(--budget<0)throw new NotSupportedException("Key helper instruction budget exhausted.");
            var i=code[pc];var op=i.OpCode.Code;
            if(i.IsLdcI4()){stack.Add(i.GetLdcI4Constant());continue;}
            if(i.IsLdloc()){var local=i.GetLocalVariable(body.LocalVariables);if(!locals.TryGetValue(local,out var value))throw new NotSupportedException("Uninitialized key local.");stack.Add(value);continue;}
            if(i.IsStloc()){locals[i.GetLocalVariable(body.LocalVariables)]=Pop();continue;}
            if(op is CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1 or CilCode.Ldarg_2 or CilCode.Ldarg_3){stack.Add(args[InlineVmCallAdapter.ArgumentIndex(i)]);continue;}
            if(op==CilCode.Ldc_I8){stack.Add((long)i.Operand!);continue;}
            if(op==CilCode.Ldstr){stack.Add((string)i.Operand!);continue;}
            if(op==CilCode.Ldnull){stack.Add(null);continue;}
            if(op==CilCode.Nop)continue;
            if(op==CilCode.Dup){var value=Pop();stack.Add(value);stack.Add(value);continue;}
            if(op==CilCode.Pop){Pop();continue;}
            if(op==CilCode.Ret)return method.Signature!.ReturnsValue?Pop():null;
            if(op is CilCode.Box or CilCode.Unbox_Any or CilCode.Castclass)continue;
            if(op is CilCode.Call or CilCode.Callvirt && i.Operand is IMethodDescriptor called)
            {
                var signature=called.Signature!;var values=new object?[signature.ParameterTypes.Count+(signature.HasThis?1:0)];
                for(int n=values.Length-1;n>=0;n--)values[n]=Pop();
                var result=Call(called,values,module,depth+1,ref budget);if(signature.ReturnsValue)stack.Add(result);continue;
            }
            if(op is CilCode.Br or CilCode.Br_S){pc=Target(i.Operand)-1;continue;}
            if(op is CilCode.Brtrue or CilCode.Brtrue_S or CilCode.Brfalse or CilCode.Brfalse_S)
            {bool truth=Truth(Pop());if(truth==(op is CilCode.Brtrue or CilCode.Brtrue_S))pc=Target(i.Operand)-1;continue;}
            if(op==CilCode.Switch)
            {int index=Convert.ToInt32(Pop());var table=(IList<ICilLabel>)i.Operand!;if(unchecked((uint)index)<(uint)table.Count)pc=Target(table[index])-1;continue;}
            if(i.OpCode.FlowControl==CilFlowControl.ConditionalBranch)
            {var right=Pop();var left=Pop();if(Branch(op,left,right))pc=Target(i.Operand)-1;continue;}
            if(IsUnary(op)){stack.Add(Unary(op,Pop()));continue;}
            if(IsBinary(op)){var right=Pop();stack.Add(Binary(op,Pop(),right));continue;}
            throw new NotSupportedException("Key helper is not pure/supported: "+i.OpCode);
        }
        throw new NotSupportedException("Key helper did not return.");
    }

    private static bool Truth(object? value)=>value!=null&&(value is not int integer||integer!=0)&&(value is not long wide||wide!=0);
    private static bool IsUnary(CilCode op)=>op is CilCode.Conv_I1 or CilCode.Conv_U1 or CilCode.Conv_I2 or CilCode.Conv_U2 or CilCode.Conv_I4 or CilCode.Conv_U4 or CilCode.Conv_I8 or CilCode.Conv_U8 or CilCode.Neg or CilCode.Not;
    private static bool IsBinary(CilCode op)=>op is CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.Div or CilCode.Div_Un or CilCode.Rem or CilCode.Rem_Un or CilCode.Xor or CilCode.And or CilCode.Or or CilCode.Shl or CilCode.Shr or CilCode.Shr_Un or CilCode.Ceq or CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un;
    private static object Unary(CilCode op,object? value)
    {
        if(value is not int and not long)throw new NotSupportedException("Non-integer key arithmetic.");
        long n=value is long wide?wide:Convert.ToInt32(value,CultureInfo.InvariantCulture);
        return op switch {CilCode.Conv_I1=>(int)unchecked((sbyte)n),CilCode.Conv_U1=>(int)unchecked((byte)n),CilCode.Conv_I2=>(int)unchecked((short)n),CilCode.Conv_U2=>(int)unchecked((ushort)n),
            CilCode.Conv_I4 or CilCode.Conv_U4=>unchecked((int)n),CilCode.Conv_I8=>n,CilCode.Conv_U8=>value is int?unchecked((long)(uint)(int)value):n,
            CilCode.Neg=>value is long?(object)unchecked(-n):unchecked(-(int)n),CilCode.Not=>value is long?(object)~n:~(int)n,_=>throw new NotSupportedException()};
    }
    private static object Binary(CilCode op,object? left,object? right)
    {
        if(op==CilCode.Ceq)return Equals(left,right)?1:0;
        if(left is not int and not long || right is not int and not long)throw new NotSupportedException("Non-integer key arithmetic.");
        bool wide=left is long;long a=wide?(long)left!:Convert.ToInt32(left),b=right is long w?w:Convert.ToInt32(right);
        ulong ua=wide?unchecked((ulong)a):unchecked((uint)(int)a),ub=wide?unchecked((ulong)b):unchecked((uint)(int)b);
        if(op is CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un)return op switch {CilCode.Cgt=>a>b?1:0,CilCode.Clt=>a<b?1:0,CilCode.Cgt_Un=>ua>ub?1:0,_=>ua<ub?1:0};
        long result=op switch {CilCode.Add=>unchecked(a+b),CilCode.Sub=>unchecked(a-b),CilCode.Mul=>unchecked(a*b),CilCode.Div=>a/b,CilCode.Rem=>a%b,CilCode.Div_Un=>unchecked((long)(ua/ub)),CilCode.Rem_Un=>unchecked((long)(ua%ub)),
            CilCode.Xor=>a^b,CilCode.And=>a&b,CilCode.Or=>a|b,CilCode.Shl=>a<<((int)b&(wide?63:31)),CilCode.Shr=>a>>((int)b&(wide?63:31)),CilCode.Shr_Un=>unchecked((long)(ua>>((int)b&(wide?63:31)))),_=>throw new NotSupportedException()};
        return wide?(object)result:unchecked((int)result);
    }
    private static bool Branch(CilCode op,object? left,object? right)
    {
        bool equal=Equals(left,right);return op switch {
            CilCode.Beq or CilCode.Beq_S=>equal,CilCode.Bne_Un or CilCode.Bne_Un_S=>!equal,
            CilCode.Bgt or CilCode.Bgt_S=>(int)Binary(CilCode.Cgt,left,right)!=0,CilCode.Bgt_Un or CilCode.Bgt_Un_S=>(int)Binary(CilCode.Cgt_Un,left,right)!=0,
            CilCode.Blt or CilCode.Blt_S=>(int)Binary(CilCode.Clt,left,right)!=0,CilCode.Blt_Un or CilCode.Blt_Un_S=>(int)Binary(CilCode.Clt_Un,left,right)!=0,
            CilCode.Bge or CilCode.Bge_S=>(int)Binary(CilCode.Clt,left,right)==0,CilCode.Bge_Un or CilCode.Bge_Un_S=>(int)Binary(CilCode.Clt_Un,left,right)==0,
            CilCode.Ble or CilCode.Ble_S=>(int)Binary(CilCode.Cgt,left,right)==0,CilCode.Ble_Un or CilCode.Ble_Un_S=>(int)Binary(CilCode.Cgt_Un,left,right)==0,_=>throw new NotSupportedException()};
    }
}
