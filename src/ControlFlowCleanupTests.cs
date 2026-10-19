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
        foreach(string name in new[]{"TryEntryJump","TryEntryDispatcher"})
        {
            if((int)Invoke(before,name)!=42||(int)Invoke(after,name)!=42)
                throw new Exception("A branch into a protected component's first instruction was threaded into its interior: "+name);
            var protectedBody=fixture.FindMethod(name)!.Body;
            var handler=protectedBody.ExceptionHandlers.Single();
            int first=protectedBody.Instructions.IndexOf(handler.TryStart),end=protectedBody.Instructions.IndexOf(handler.TryEnd);
            for(int index=0;index<first;index++)
                if(protectedBody.Instructions[index].Operand is Instruction destination &&
                    protectedBody.Instructions.IndexOf(destination)>first && protectedBody.Instructions.IndexOf(destination)<end)
                    throw new Exception("An outside edge bypasses TryStart in "+name);
        }

        // A literal that other branches target must fold too: those edges are
        // retargeted to the folded branch and widened when needed.
        var retarget=fixture.FindMethod("RetargetEvaluate")!;
        if(retarget.Body.Instructions.Any(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S))
            throw new Exception("A literal branch targeted by another branch survived.");
        if((int)Invoke(before,"RetargetEvaluate",4)!=5 || (int)Invoke(after,"RetargetEvaluate",4)!=5)
            throw new Exception("Retargeting a folded branch changed behavior.");

        foreach(string name in new[]{"TruncatingConversion","SignedConversion","UnsignedComparison","ArithmeticPredicate",
            "OverflowWrapping","WideComparison","LiteralSwitch","DefaultSwitch","LocalSwitch","NullPredicate"})
        {
            if((int)Invoke(before,name)!=42||(int)Invoke(after,name)!=42)
                throw new Exception("Integer expression/switch folding changed "+name+" behavior.");
            if(fixture.FindMethod(name)!.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch))
                throw new Exception("A provable integer predicate survived in "+name+".");
        }
        foreach(int value in new[]{int.MinValue,-1,0,1,int.MaxValue})
        {
            int expected=unchecked(value+40);
            if((int)Invoke(before,"StateDispatcher",value)!=expected||(int)Invoke(after,"StateDispatcher",value)!=expected)
                throw new Exception("Predecessor-specific switch dispatch changed behavior.");
            int chosen=value==0?42:7;
            if((int)Invoke(before,"InteriorExpressionEntry",value)!=chosen||(int)Invoke(after,"InteriorExpressionEntry",value)!=chosen)
                throw new Exception("A branch into the middle of an expression was folded unsafely.");
            if((int)Invoke(before,"ConsumerEntry",value)!=chosen||(int)Invoke(after,"ConsumerEntry",value)!=chosen)
                throw new Exception("A directly targeted conditional consumer was folded unsafely.");
            int chainResult=value==0?7:42;
            if((int)Invoke(before,"JumpChain",value)!=chainResult||(int)Invoke(after,"JumpChain",value)!=chainResult)
                throw new Exception("Jump-chain threading changed behavior.");
            if((int)Invoke(before,"MultiPredecessorDispatcher",value)!=chosen||(int)Invoke(after,"MultiPredecessorDispatcher",value)!=chosen)
                throw new Exception("Different dispatcher predecessor constants were conflated.");
        }
        if(fixture.FindMethod("StateDispatcher")!.Body.Instructions.Any(i=>i.OpCode.Code==Code.Switch)
            ||fixture.FindMethod("MultiPredecessorDispatcher")!.Body.Instructions.Any(i=>i.OpCode.Code==Code.Switch))
            throw new Exception("Provable per-edge state dispatchers were not eliminated.");
        if(fixture.FindMethod("JumpChain")!.Body.Instructions.Count(i=>i.OpCode.Code is Code.Br or Code.Br_S)>0)
            throw new Exception("Redundant basic-block jump chains survived.");
        if((int)Invoke(before,"AddressExposedLocal")!=42||(int)Invoke(after,"AddressExposedLocal")!=42
            ||!fixture.FindMethod("AddressExposedLocal")!.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch))
            throw new Exception("Address-exposed local was propagated across a by-reference call.");
        foreach(string name in new[]{"ThrowingConversion","ThrowingArithmetic","ThrowingDivision"})
        {
            foreach(var assembly in new[]{before,after})
            {
                try{Invoke(assembly,name);throw new Exception("Throwing integer expression stopped throwing in "+name+".");}
                catch(TargetInvocationException ex) when(ex.InnerException is OverflowException or DivideByZeroException){}
            }
            if(!fixture.FindMethod(name)!.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch))
                throw new Exception("Potentially throwing expression was removed in "+name+".");
        }

        // The output must be a fixed point, not just behaviorally correct once.
        var second=ControlFlowCleanupPass.Clean(module);
        if(second.FoldedBranches!=0||second.RemovedInstructions!=0)
            throw new Exception("Control-flow cleanup did not reach a stable fixed point.");

        // Model pre-VM save -> reload -> naming cleanup -> final save. dnlib
        // resets KeepOldMaxStack on reload; unchanged bodies must retain their
        // serialized upper bound instead of entering its stack-flow calculator.
        using(var reloaded=ModuleDefMD.Load(afterBytes.ToArray()))
        {
            var reloadedFixture=reloaded.GetTypes().First(t=>t.Name=="FlowCleanupFixture");
            var authoritative=reloadedFixture.FindMethod("AuthoritativeMaxStack")!;
            if(authoritative.Body.MaxStack!=123)throw new Exception("The first cleanup lost an unchanged method's stored max-stack.");
            var oldMaxima=reloaded.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody)
                .ToDictionary(m=>m,m=>m.Body.MaxStack);
            foreach(var method in oldMaxima.Keys)method.Body.KeepOldMaxStack=false;
            // A separate stage can grow a previously short edge without changing
            // its semantics. Even a no-fold cleanup must repair its encoding.
            var grown=reloadedFixture.FindMethod("BranchOnArgument")!;
            var grownBranch=grown.Body.Instructions.First(i=>i.OpCode.Code is Code.Brtrue or Code.Brtrue_S);
            grownBranch.OpCode=OpCodes.Brtrue_S;
            int insertion=grown.Body.Instructions.IndexOf(grownBranch)+1;
            // Padding is intentionally not a removable nop/integer expression:
            // retain ldstr because resolving/allocating it may throw.
            for(int i=0;i<75;i++)
            {
                grown.Body.Instructions.Insert(insertion,OpCodes.Pop.ToInstruction());
                grown.Body.Instructions.Insert(insertion,OpCodes.Ldstr.ToInstruction("branch padding"));
            }
            var noChange=ControlFlowCleanupPass.Clean(reloaded);
            if(noChange.FoldedBranches!=0||noChange.RemovedInstructions!=0)
                throw new Exception("Save/reload cleanup unexpectedly changed the recovered control-flow graph.");
            foreach(var method in oldMaxima.Keys)
                if(!method.Body.KeepOldMaxStack||method.Body.MaxStack!=oldMaxima[method])
                    throw new Exception("No-change cleanup failed to preserve stored max-stack in "+method.FullName+".");
            if(grownBranch.OpCode.Code!=Code.Brtrue)throw new Exception("An unchanged method retained an out-of-range short branch.");
            reloaded.Assembly.Name="FlowCleanupReloaded";
            using var replayBytes=new MemoryStream();reloaded.Write(replayBytes);
            File.WriteAllBytes(Path.Combine(directory,"cleanup-reloaded.dll"),replayBytes.ToArray());
            using var replayModule=ModuleDefMD.Load(replayBytes.ToArray());
            if(replayModule.GetTypes().First(t=>t.Name=="FlowCleanupFixture").FindMethod("AuthoritativeMaxStack")!.Body.MaxStack!=123)
                throw new Exception("A second save recalculated an unchanged method's authoritative max-stack.");
            var replay=Assembly.Load(replayBytes.ToArray());
            if((int)Invoke(replay,"AuthoritativeMaxStack")!=42||(int)Invoke(replay,"BranchOnArgument",0)!=7
                ||(int)Invoke(replay,"BranchOnArgument",1)!=9||(int)Invoke(replay,"StateDispatcher",2)!=42)
                throw new Exception("Save/reload cleanup changed execution behavior.");
        }

        if(stats.FoldedBranches<4 || stats.RemovedInstructions<4)
            throw new Exception("Control-flow cleanup did not report the expected folds: "+stats.FoldedBranches+"/"+stats.RemovedInstructions+".");
        Console.WriteLine("PASS: integer expressions, relational branches, literal/local switches, per-predecessor state dispatch and jump chains simplified across "+stats.FoldedBranches+" edges; alternate entries, by-reference mutation, throwing expressions, exception regions and finally effects preserved.");
        Console.WriteLine("PASS: save/reload/no-change cleanup preserves stored max-stack and repairs grown short branches before a second successful CLR load/execution.");
        Console.WriteLine("PASS: external edges retain exact try-region entry after jump threading and predecessor-specific switch specialization.");
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

        Predicate(type,module,"TruncatingConversion",false,OpCodes.Brfalse,
            OpCodes.Ldc_I4.ToInstruction(256),OpCodes.Conv_U1.ToInstruction());
        Predicate(type,module,"SignedConversion",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4.ToInstruction(255),OpCodes.Conv_I1.ToInstruction(),OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Ceq.ToInstruction());
        Predicate(type,module,"UnsignedComparison",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Cgt_Un.ToInstruction());
        Predicate(type,module,"ArithmeticPredicate",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4.ToInstruction(13),OpCodes.Ldc_I4.ToInstruction(9),OpCodes.Xor.ToInstruction(),
            OpCodes.Ldc_I4_4.ToInstruction(),OpCodes.Ceq.ToInstruction());
        Predicate(type,module,"OverflowWrapping",false,OpCodes.Beq,
            OpCodes.Ldc_I4.ToInstruction(int.MaxValue),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),
            OpCodes.Ldc_I4.ToInstruction(int.MinValue));
        Predicate(type,module,"WideComparison",false,OpCodes.Blt,
            OpCodes.Ldc_I8.ToInstruction(long.MinValue),OpCodes.Ldc_I8.ToInstruction(0L));
        Predicate(type,module,"NullPredicate",false,OpCodes.Brfalse,OpCodes.Ldnull.ToInstruction());
        Predicate(type,module,"ThrowingConversion",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4.ToInstruction(int.MaxValue),OpCodes.Conv_Ovf_U1.ToInstruction());
        Predicate(type,module,"ThrowingArithmetic",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4.ToInstruction(int.MaxValue),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add_Ovf.ToInstruction());
        Predicate(type,module,"ThrowingDivision",false,OpCodes.Brtrue,
            OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Div.ToInstruction());

        foreach(string name in new[]{"LiteralSwitch","DefaultSwitch","LocalSwitch"})
        {
            var switchMethod=Method(type,module,name,false);
            var zero=OpCodes.Ldc_I4_7.ToInstruction();var one=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
            if(name=="LocalSwitch")
            {
                switchMethod.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
                Add(switchMethod,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloc_0.ToInstruction());
            }
            else Add(switchMethod,OpCodes.Ldc_I4.ToInstruction(name=="DefaultSwitch"?-1:5),
                OpCodes.Ldc_I4.ToInstruction(name=="DefaultSwitch"?0:4),OpCodes.Sub.ToInstruction());
            Add(switchMethod,OpCodes.Switch.ToInstruction(new[]{zero,one}),
                OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction(),
                zero,OpCodes.Ret.ToInstruction(),one,OpCodes.Ret.ToInstruction());
        }

        var state=Method(type,module,"StateDispatcher",true);
        state.Body.Variables.Add(new Local(module.CorLibTypes.Int32));state.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
        var dispatch=OpCodes.Ldloc_0.ToInstruction();var caseZero=OpCodes.Ldloc_1.ToInstruction();var caseOne=OpCodes.Ldloc_1.ToInstruction();
        var defaultState=OpCodes.Ldc_I4.ToInstruction(-999);
        Add(state,OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Stloc_1.ToInstruction(),OpCodes.Br.ToInstruction(dispatch),
            caseZero,OpCodes.Ldc_I4.ToInstruction(40),OpCodes.Add.ToInstruction(),OpCodes.Stloc_1.ToInstruction(),
            OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(dispatch),
            caseOne,OpCodes.Ret.ToInstruction(),defaultState,OpCodes.Ret.ToInstruction(),
            dispatch,OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Xor.ToInstruction(),
            OpCodes.Switch.ToInstruction(new[]{caseZero,caseOne}),OpCodes.Br.ToInstruction(defaultState));

        var multi=Method(type,module,"MultiPredecessorDispatcher",true);
        multi.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
        var setOne=OpCodes.Ldc_I4_1.ToInstruction();var multiDispatch=OpCodes.Ldloc_0.ToInstruction();
        var multiZero=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);var multiOne=OpCodes.Ldc_I4_7.ToInstruction();
        Add(multi,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(setOne),
            OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(multiDispatch),
            setOne,OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(multiDispatch),
            multiZero,OpCodes.Ret.ToInstruction(),multiOne,OpCodes.Ret.ToInstruction(),
            multiDispatch,OpCodes.Switch.ToInstruction(new[]{multiZero,multiOne}),
            OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Ret.ToInstruction());

        var interior=Method(type,module,"InteriorExpressionEntry",true);
        var negative=OpCodes.Ldc_I4_M1.ToInstruction();var middle=OpCodes.Ldc_I4_1.ToInstruction();var interiorTaken=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(interior,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(negative),
            OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Br.ToInstruction(middle),
            negative,middle,OpCodes.Add.ToInstruction(),OpCodes.Brtrue.ToInstruction(interiorTaken),
            OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Ret.ToInstruction(),interiorTaken,OpCodes.Ret.ToInstruction());

        var consumer=Method(type,module,"ConsumerEntry",true);
        var alternate=OpCodes.Ldc_I4_0.ToInstruction();var consumerTaken=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        var consumerBranch=OpCodes.Brtrue.ToInstruction(consumerTaken);
        Add(consumer,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(alternate),
            OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Br.ToInstruction(consumerBranch),
            alternate,consumerBranch,OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Ret.ToInstruction(),consumerTaken,OpCodes.Ret.ToInstruction());

        var chain=Method(type,module,"JumpChain",true);
        var hop=Nop();var hopTwo=Nop();var chainTaken=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(chain,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(hop),OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Ret.ToInstruction(),
            hop,OpCodes.Br.ToInstruction(hopTwo),hopTwo,OpCodes.Br.ToInstruction(chainTaken),chainTaken,OpCodes.Ret.ToInstruction());

        var setState=new MethodDefUser("SetState",MethodSig.CreateStatic(module.CorLibTypes.Void,new ByRefSig(module.CorLibTypes.Int32)),
            MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};type.Methods.Add(setState);
        Add(setState,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stind_I4.ToInstruction(),OpCodes.Ret.ToInstruction());
        var exposed=Method(type,module,"AddressExposedLocal",false);
        exposed.Body.Variables.Add(new Local(module.CorLibTypes.Int32));var exposedTaken=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(exposed,OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloca.ToInstruction(exposed.Body.Variables[0]),
            OpCodes.Call.ToInstruction(setState),OpCodes.Ldloc_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(exposedTaken),
            OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Ret.ToInstruction(),exposedTaken,OpCodes.Ret.ToInstruction());
        var maxStack=Method(type,module,"AuthoritativeMaxStack",false);
        // A deliberately generous, valid serialized bound detects silent
        // recalculation even on runtimes where dnlib happens to handle this IL.
        maxStack.Body.MaxStack=123;maxStack.Body.KeepOldMaxStack=true;
        for(int i=0;i<70;i++)Add(maxStack,OpCodes.Nop.ToInstruction());
        Add(maxStack,OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());
        foreach(bool dispatcher in new[]{false,true})
        {
            var entry=Method(type,module,dispatcher?"TryEntryDispatcher":"TryEntryJump",false);
            entry.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
            entry.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
            var first=dispatcher?OpCodes.Ldloc_0.ToInstruction():OpCodes.Ldc_I4_1.ToInstruction();
            var chosen=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
            var handler=OpCodes.Ldsfld.ToInstruction(type.Fields.Single(f=>f.Name=="Counter"));
            var exit=OpCodes.Ldloc_1.ToInstruction();
            Add(entry,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Br.ToInstruction(first),first);
            if(dispatcher)Add(entry,OpCodes.Switch.ToInstruction(new[]{chosen,chosen}));
            else Add(entry,OpCodes.Brtrue.ToInstruction(chosen));
            Add(entry,OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Stloc_1.ToInstruction(),OpCodes.Leave.ToInstruction(exit),
                chosen,OpCodes.Stloc_1.ToInstruction(),OpCodes.Leave.ToInstruction(exit),
                handler,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),
                OpCodes.Stsfld.ToInstruction(type.Fields.Single(f=>f.Name=="Counter")),OpCodes.Endfinally.ToInstruction(),exit,OpCodes.Ret.ToInstruction());
            entry.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {TryStart=first,TryEnd=handler,HandlerStart=handler,HandlerEnd=exit});
        }
        return module;
    }

    private static void Predicate(TypeDef type,ModuleDefUser module,string name,bool withArgument,OpCode branch,params Instruction[] expression)
    {
        var method=Method(type,module,name,withArgument);var taken=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(method,expression);Add(method,branch.ToInstruction(taken),OpCodes.Ldc_I4_7.ToInstruction(),OpCodes.Ret.ToInstruction(),taken,OpCodes.Ret.ToInstruction());
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
