using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class InlineControlFlowTests
{
    internal static void Run(string root)
    {
        using var module=new ModuleDefUser("InlineFixture.dll");new AssemblyDefUser("InlineFixture"+Guid.NewGuid().ToString("N"),new Version(1,0)).Modules.Add(module);
        var owner=new TypeDefUser("Fixture","T",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};module.Types.Add(owner);
        MethodDef M(string name,int args=0)
        {
            var method=new MethodDefUser(name,MethodSig.CreateStatic(module.CorLibTypes.Int32,Enumerable.Repeat<TypeSig>(module.CorLibTypes.Int32,args).ToArray()),MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody{InitLocals=true,MaxStack=8}};
            owner.Methods.Add(method);return method;
        }
        void Set(MethodDef m,params Instruction[] code){foreach(var i in code)m.Body.Instructions.Add(i);}
        var pure=M("Pure",2);Set(pure,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Xor.ToInstruction(),Instruction.CreateLdcI4(31),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
        var counter=new FieldDefUser("Counter",new FieldSig(module.CorLibTypes.Int32),dnlib.DotNet.FieldAttributes.Static|dnlib.DotNet.FieldAttributes.Public);owner.Fields.Add(counter);
        var effect=M("Effect",1);Set(effect,OpCodes.Ldsfld.ToInstruction(counter),Instruction.CreateLdcI4(10),OpCodes.Mul.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Stsfld.ToInstruction(counter),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var use=M("Use",1);Set(use,OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stsfld.ToInstruction(counter),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(effect),OpCodes.Ldc_I4_2.ToInstruction(),OpCodes.Call.ToInstruction(effect),OpCodes.Call.ToInstruction(pure),OpCodes.Ldsfld.ToInstruction(counter),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
        var division=M("Division",1);Set(division,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Div.ToInstruction(),OpCodes.Ret.ToInstruction());
        var divisionUse=M("DivisionUse",1);Set(divisionUse,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(division),OpCodes.Ret.ToInstruction());
        var prefixed=M("Tail",2);Set(prefixed,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_1.ToInstruction(),OpCodes.Tailcall.ToInstruction(),OpCodes.Call.ToInstruction(pure),OpCodes.Ret.ToInstruction());
        var noinline=M("NoInline",1);noinline.ImplAttributes|=dnlib.DotNet.MethodImplAttributes.NoInlining;Set(noinline,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var noinlineUse=M("NoInlineUse",1);Set(noinlineUse,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(noinline),OpCodes.Ret.ToInstruction());
        var unique=M("Unique",1);unique.Body.Variables.Add(new Local(module.CorLibTypes.Int32));var entry=OpCodes.Ldloc_0.ToInstruction();var bad=Instruction.CreateLdcI4(-99);
        Set(unique,Instruction.CreateLdcI4(7),OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(entry),entry,Instruction.CreateLdcI4(7),OpCodes.Bne_Un.ToInstruction(bad),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction(),bad,OpCodes.Ret.ToInstruction());
        var joined=M("Joined",1);joined.Body.Variables.Add(new Local(module.CorLibTypes.Int32));var other=Instruction.CreateLdcI4(3);var join=OpCodes.Ldloc_0.ToInstruction();
        Set(joined,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brfalse.ToInstruction(other),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(join),other,OpCodes.Stloc_0.ToInstruction(),join,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Ceq.ToInstruction(),OpCodes.Ret.ToInstruction());
        string before=Path.Combine(root,"inline-before.dll"),after=Path.Combine(root,"inline-after.dll");module.Write(before);
        int inlined=InlineExpressionPass.Process(module,Path.Combine(root,"inline-expressions.json"));
        if(inlined<1||use.Body.Instructions.Any(i=>i.Operand==pure)||!divisionUse.Body.Instructions.Any(i=>i.Operand==division)||!prefixed.Body.Instructions.Any(i=>i.Operand==pure)||!noinlineUse.Body.Instructions.Any(i=>i.Operand==noinline))throw new Exception("Integer inlining either missed a leaf or crossed an excluded boundary.");
        ControlFlowCleanupPass.Clean(module);
        if(unique.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch))throw new Exception("Unique-predecessor constant did not fold.");
        module.Write(after);
        var original=Assembly.Load(File.ReadAllBytes(before));var rewritten=Assembly.Load(File.ReadAllBytes(after));
        foreach(string method in new[]{"Use","Unique","Joined","DivisionUse","NoInlineUse"})foreach(int n in new[]{int.MinValue,-2,-1,0,1,2,7,19,int.MaxValue})
        {
            object? Invoke(Assembly assembly){try{return assembly.GetType("Fixture.T")!.GetMethod(method)!.Invoke(null,new object[]{n});}catch(TargetInvocationException e){return e.InnerException!.GetType().FullName;}}
            if(!Equals(Invoke(original),Invoke(rewritten)))throw new Exception("Inlining/flow changed execution: "+method+"("+n+")");
        }
        Console.WriteLine("PASS: bounded integer-leaf inlining preserves exact-once side effects/unused arguments; division, prefixes and NoInlining retained; unique predecessors fold and unknown joins preserve behavior.");
    }
}
