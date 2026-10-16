using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class ControlFlowCleanupTests
{
    internal static void Run(string directory)
    {
        using var fixtureModule=BuildFixture();
        using var beforeBytes=new MemoryStream();
        fixtureModule.Write(beforeBytes);
        // Mirror the pipeline: the cleanup pass always runs on an assembly that
        // was loaded from disk, so method max-stack metadata is present.
        using var module=ModuleDefMD.Load(beforeBytes.ToArray());
        var stats=ControlFlowCleanupPass.Clean(module);
        module.Assembly.Name="FlowCleanupAfter";
        using var afterBytes=new MemoryStream();
        module.Write(afterBytes);
        File.WriteAllBytes(Path.Combine(directory,"cleanup-before.dll"),beforeBytes.ToArray());
        File.WriteAllBytes(Path.Combine(directory,"cleanup-after.dll"),afterBytes.ToArray());

        var before=Assembly.Load(beforeBytes.ToArray());
        var after=Assembly.Load(afterBytes.ToArray());
        object Invoke(Assembly assembly,string name,params object[] arguments)=>
            assembly.GetType("FlowCleanup.FlowCleanupFixture")!.GetMethod(name)!.Invoke(null,arguments)!;

        // A literal-true branch folds to its taken path; the dead fallback is gone.
        foreach(int value in new[]{int.MinValue,-1,0,1,int.MaxValue})
        {
            if((int)Invoke(before,"Evaluate",value)!=42 || (int)Invoke(after,"Evaluate",value)!=42)
                throw new Exception("Literal-true branch folding changed Evaluate behavior.");
        }
        var fixture=module.GetTypes().First(t=>t.Name=="FlowCleanupFixture");
        var evaluate=fixture.FindMethod("Evaluate")!;
        if(evaluate.Body.Instructions.Count>=8
            || evaluate.Body.Instructions.Any(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S))
            throw new Exception("Literal branch and its unreachable fall-through were not removed.");

        // A non-literal predicate must be left untouched.
        var branch=fixture.FindMethod("BranchOnArgument")!;
        if(branch.Body.Instructions.Count(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S)!=1)
            throw new Exception("Non-literal predicate was rewritten.");
        if((int)Invoke(before,"BranchOnArgument",0)!=7 || (int)Invoke(after,"BranchOnArgument",0)!=7
            || (int)Invoke(before,"BranchOnArgument",5)!=9 || (int)Invoke(after,"BranchOnArgument",5)!=9)
            throw new Exception("Non-literal predicate behavior changed.");

        // Constant branches inside a protected region fold without breaking the finally.
        var protectedMethod=fixture.FindMethod("TryEvaluate")!;
        if(protectedMethod.Body.ExceptionHandlers.Count!=1 || protectedMethod.Body.ExceptionHandlers[0].HandlerType!=ExceptionHandlerType.Finally
            || protectedMethod.Body.ExceptionHandlers[0].TryStart==null || protectedMethod.Body.Instructions.IndexOf(protectedMethod.Body.ExceptionHandlers[0].TryStart)<0
            || protectedMethod.Body.Instructions.Any(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S))
            throw new Exception("Exception region was lost or altered, or a protected-region literal branch survived.");
        if((int)Invoke(before,"TryEvaluate",1)!=12 || (int)Invoke(after,"TryEvaluate",1)!=12
            || (int)Invoke(before,"TryEvaluate",-31)!=-20 || (int)Invoke(after,"TryEvaluate",-31)!=-20)
            throw new Exception("Protected-region folding changed behavior.");
        int afterCounter=(int)after.GetType("FlowCleanup.FlowCleanupFixture")!.GetField("Counter")!.GetValue(null)!;
        int beforeCounter=(int)before.GetType("FlowCleanup.FlowCleanupFixture")!.GetField("Counter")!.GetValue(null)!;
        if(afterCounter!=2||beforeCounter!=2)
            throw new Exception("Finally side effect count changed: "+beforeCounter+"/"+afterCounter+".");

        // A literal that other branches target must fold too: those edges are
        // retargeted to the folded branch and widened when needed.
        var retarget=fixture.FindMethod("RetargetEvaluate")!;
        if(retarget.Body.Instructions.Any(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S))
            throw new Exception("A literal branch targeted by another branch survived.");
        if((int)Invoke(before,"RetargetEvaluate",4)!=5 || (int)Invoke(after,"RetargetEvaluate",4)!=5)
            throw new Exception("Retargeting a folded branch changed behavior.");

        if(stats.FoldedBranches<4 || stats.RemovedInstructions<4)
            throw new Exception("Control-flow cleanup did not report the expected folds: "+stats.FoldedBranches+"/"+stats.RemovedInstructions+".");
        Console.WriteLine("PASS: literal conditional branches folded and unreachable instructions removed across "+stats.FoldedBranches+" branches; non-literal predicates, exception regions and finally side effects preserved.");
    }

    private static ModuleDefUser BuildFixture()
    {
        var module=new ModuleDefUser("FlowCleanup.dll");
        var assembly=new AssemblyDefUser("FlowCleanupBefore",new Version(1,0));assembly.Modules.Add(module);
        var type=new TypeDefUser("FlowCleanup","FlowCleanupFixture",module.CorLibTypes.Object.TypeDefOrRef)
            {Attributes=TypeAttributes.Public|TypeAttributes.Abstract|TypeAttributes.Sealed|TypeAttributes.BeforeFieldInit};
        module.Types.Add(type);
        type.Fields.Add(new FieldDefUser("Counter",new FieldSig(module.CorLibTypes.Int32),FieldAttributes.Public|FieldAttributes.Static));

        var evaluate=Method(type,module,"Evaluate",true);
        var lAfter=Nop();
        var lTaken=Nop();
        Add(evaluate,
            OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(lAfter),lAfter,
            OpCodes.Ldc_I4_5.ToInstruction(),OpCodes.Brtrue.ToInstruction(lTaken),
            OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction(),
            lTaken,OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());

        var branch=Method(type,module,"BranchOnArgument",true);
        var lBranchTrue=Nop();
        Add(branch,
            OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(lBranchTrue),
            OpCodes.Ldc_I4_S.ToInstruction((sbyte)7),OpCodes.Ret.ToInstruction(),
            lBranchTrue,OpCodes.Ldc_I4_S.ToInstruction((sbyte)9),OpCodes.Ret.ToInstruction());

        var protectedMethod=Method(type,module,"TryEvaluate",true);
        protectedMethod.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
        var lInjected=Nop();
        var afterHandler=OpCodes.Ldloc_0.ToInstruction();
        var leave=OpCodes.Leave.ToInstruction(afterHandler);
        var tryStart=OpCodes.Ldc_I4_5.ToInstruction();
        var handlerStart=OpCodes.Ldsfld.ToInstruction(type.Fields[0]);
        Add(protectedMethod,
            OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            tryStart,OpCodes.Brtrue.ToInstruction(lInjected),
            OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            lInjected,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            OpCodes.Ldloc_0.ToInstruction(),OpCodes.Ldc_I4_S.ToInstruction((sbyte)10),OpCodes.Add.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            leave,
            handlerStart,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Stsfld.ToInstruction(type.Fields[0]),OpCodes.Endfinally.ToInstruction(),
            afterHandler,OpCodes.Ret.ToInstruction());
        protectedMethod.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
            {TryStart=tryStart,TryEnd=handlerStart,HandlerStart=handlerStart,HandlerEnd=afterHandler});

        var retarget=Method(type,module,"RetargetEvaluate",true);
        retarget.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
        var lCondition=OpCodes.Ldc_I4_5.ToInstruction();
        var lRetargetTrue=Nop();
        Add(retarget,
            OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            OpCodes.Br.ToInstruction(lCondition),
            lCondition,OpCodes.Brtrue.ToInstruction(lRetargetTrue),
            OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            lRetargetTrue,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),
            OpCodes.Ldloc_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        return module;
    }

    private static void Add(MethodDef method,params Instruction[] instructions)
    {
        foreach(var instruction in instructions)method.Body.Instructions.Add(instruction);
    }

    private static MethodDef Method(TypeDef type,ModuleDefUser module,string name,bool withArgument)
    {
        var sig=withArgument?MethodSig.CreateStatic(module.CorLibTypes.Int32,module.CorLibTypes.Int32)
            :MethodSig.CreateStatic(module.CorLibTypes.Int32);
        var method=new MethodDefUser(name,sig,MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};
        type.Methods.Add(method);
        return method;
    }

    private static Instruction Nop()=>OpCodes.Nop.ToInstruction();
}
