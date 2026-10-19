using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using MethodImplAttributes=dnlib.DotNet.MethodImplAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class ControlFlowGarbageTests
{
    internal static void Run(string directory)
    {
        using var fixture=BuildFixture();using var beforeBytes=new MemoryStream();fixture.Write(beforeBytes);
        using var module=ModuleDefMD.Load(beforeBytes.ToArray());
        var type=module.GetTypes().Single(t=>t.Name=="GarbageFixture");
        var protectedBody=type.FindMethod("Protected")!.Body;
        var anchors=protectedBody.ExceptionHandlers.SelectMany(h=>new[]{h.TryStart,h.TryEnd,h.HandlerStart,h.HandlerEnd}).ToArray();
        var stats=ControlFlowCleanupPass.Clean(module);
        if(stats.FoldedBranches<3||stats.RemovedInstructions<12)throw new Exception("Redundant branches/pure garbage were not removed.");
        if(type.FindMethod("PureGarbage")!.Body.Instructions.Any(i=>i.OpCode.Code is Code.Pop or Code.Nop))
            throw new Exception("Discarded pure expressions or dup/pop remain.");
        foreach(string name in new[]{"ConditionalFallThrough","SwitchFallThrough","RelationalFallThrough"})
        {
            var ins=type.FindMethod(name)!.Body.Instructions;
            if(ins.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch)||!ins.Any(i=>i.OpCode.Code==Code.Call)
                ||!ins.Any(i=>i.OpCode.Code==Code.Pop))throw new Exception("A redundant branch lost its side-effecting predicate in "+name+".");
        }
        if(!type.FindMethod("StringGarbage")!.Body.Instructions.Any(i=>i.OpCode.Code==Code.Ldstr))
            throw new Exception("A potentially throwing string resolution was removed.");
        if(!type.FindMethod("AlternatePopEntry")!.Body.Instructions.Any(i=>i.OpCode.Code==Code.Pop))
            throw new Exception("A directly targeted pop was removed with only one of its producers.");
        foreach(string name in new[]{"UnknownRelationalFallThrough","AlternateRelationEntry"})
            if(!type.FindMethod(name)!.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch))
                throw new Exception("An unknown/alternate-entry comparison lost one of its two stack consumes in "+name+".");
        if(!anchors.SequenceEqual(protectedBody.ExceptionHandlers.SelectMany(h=>new[]{h.TryStart,h.TryEnd,h.HandlerStart,h.HandlerEnd})))
            throw new Exception("Padding cleanup moved an exception-region anchor.");
        module.Assembly.Name="GarbageAfter";using var afterBytes=new MemoryStream();module.Write(afterBytes);
        File.WriteAllBytes(Path.Combine(directory,"garbage-before.dll"),beforeBytes.ToArray());
        File.WriteAllBytes(Path.Combine(directory,"garbage-after.dll"),afterBytes.ToArray());
        var before=Assembly.Load(beforeBytes.ToArray());var after=Assembly.Load(afterBytes.ToArray());
        object Invoke(Assembly assembly,string name,params object[] args)=>assembly.GetType("FlowCleanup.GarbageFixture")!.GetMethod(name)!.Invoke(null,args)!;
        foreach(var assembly in new[]{before,after})
        {
            foreach(int value in new[]{int.MinValue,-1,0,1,int.MaxValue})
            {
                if((int)Invoke(assembly,"PureGarbage",value)!=value||(int)Invoke(assembly,"Padding",value)!=42
                    ||(int)Invoke(assembly,"AlternatePopEntry",value)!=42||(int)Invoke(assembly,"UnknownRelationalFallThrough",value)!=42
                    ||(int)Invoke(assembly,"AlternateRelationEntry",value)!=42)throw new Exception("Garbage cleanup changed stack/alternate-entry behavior.");
            }
            foreach(string name in new[]{"ConditionalFallThrough","SwitchFallThrough","RelationalFallThrough","StringGarbage","Protected"})
                if((int)Invoke(assembly,name)!=42)throw new Exception("Garbage cleanup changed "+name+".");
            int count=(int)assembly.GetType("FlowCleanup.GarbageFixture")!.GetField("Counter")!.GetValue(null)!;
            if(count!=4)throw new Exception("Predicate/finally side effects changed: "+count+".");
            foreach(string name in new[]{"ThrowingGarbage","CheckedGarbage"})
            {
                try{Invoke(assembly,name);throw new Exception("Throwing garbage stopped throwing.");}
                catch(TargetInvocationException ex) when(ex.InnerException is DivideByZeroException or OverflowException){}
            }
        }
        var second=ControlFlowCleanupPass.Clean(module);
        if(second.FoldedBranches!=0||second.RemovedInstructions!=0)throw new Exception("Garbage cleanup did not reach a fixed point.");
        Console.WriteLine("PASS: nop/br-next, redundant boolean/switch successors and nonthrowing integer/null/dup-pop garbage removed; stack consumption, calls, strings, alternate entries, throwing arithmetic and exact EH anchors preserved.");
    }

    private static ModuleDefUser BuildFixture()
    {
        var module=new ModuleDefUser("Garbage.dll");new AssemblyDefUser("GarbageBefore",new Version(1,0)).Modules.Add(module);
        var type=new TypeDefUser("FlowCleanup","GarbageFixture",module.CorLibTypes.Object.TypeDefOrRef)
            {Attributes=TypeAttributes.Public|TypeAttributes.Abstract|TypeAttributes.Sealed};module.Types.Add(type);
        var counter=new FieldDefUser("Counter",new FieldSig(module.CorLibTypes.Int32),dnlib.DotNet.FieldAttributes.Public|dnlib.DotNet.FieldAttributes.Static);type.Fields.Add(counter);
        var touch=Method("Touch");Add(touch,OpCodes.Ldsfld.ToInstruction(counter),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),
            OpCodes.Stsfld.ToInstruction(counter),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Ret.ToInstruction());
        var pure=Method("PureGarbage",true);Add(pure,OpCodes.Ldc_I4_S.ToInstruction((sbyte)17),OpCodes.Ldc_I4_5.ToInstruction(),OpCodes.Xor.ToInstruction(),
            OpCodes.Pop.ToInstruction(),OpCodes.Ldnull.ToInstruction(),OpCodes.Pop.ToInstruction(),OpCodes.Ldc_I8.ToInstruction(123L),OpCodes.Pop.ToInstruction(),
            OpCodes.Ldc_I4_S.ToInstruction((sbyte)12),OpCodes.Ldc_I4_3.ToInstruction(),OpCodes.Div.ToInstruction(),OpCodes.Pop.ToInstruction(),
            OpCodes.Ldarg_0.ToInstruction(),OpCodes.Dup.ToInstruction(),OpCodes.Pop.ToInstruction(),OpCodes.Ret.ToInstruction());
        var padding=Method("Padding",true);var pad=OpCodes.Nop.ToInstruction();var next=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(padding,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(pad),OpCodes.Nop.ToInstruction(),pad,OpCodes.Br.ToInstruction(next),next,OpCodes.Ret.ToInstruction());
        foreach(bool isSwitch in new[]{false,true})
        {
            var method=Method(isSwitch?"SwitchFallThrough":"ConditionalFallThrough");var result=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
            Add(method,OpCodes.Call.ToInstruction(touch),isSwitch?OpCodes.Switch.ToInstruction(new[]{result,result}):OpCodes.Brtrue.ToInstruction(result),result,OpCodes.Ret.ToInstruction());
        }
        var relation=Method("RelationalFallThrough");var relationResult=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(relation,OpCodes.Call.ToInstruction(touch),OpCodes.Ldc_I4_2.ToInstruction(),OpCodes.Beq.ToInstruction(relationResult),relationResult,OpCodes.Ret.ToInstruction());
        var unknownRelation=Method("UnknownRelationalFallThrough",true);var unknownResult=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        Add(unknownRelation,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Beq.ToInstruction(unknownResult),unknownResult,OpCodes.Ret.ToInstruction());
        var alternateRelation=Method("AlternateRelationEntry",true);var alternateResult=OpCodes.Ldc_I4_S.ToInstruction((sbyte)42);
        var alternateCompare=OpCodes.Beq.ToInstruction(alternateResult);var alternateLeft=OpCodes.Ldc_I4.ToInstruction(200);
        Add(alternateRelation,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(alternateLeft),OpCodes.Ldc_I4.ToInstruction(100),OpCodes.Ldc_I4_0.ToInstruction(),
            OpCodes.Br.ToInstruction(alternateCompare),alternateLeft,OpCodes.Ldc_I4_1.ToInstruction(),alternateCompare,alternateResult,OpCodes.Ret.ToInstruction());
        var text=Method("StringGarbage");Add(text,OpCodes.Ldstr.ToInstruction("retained resolution"),OpCodes.Pop.ToInstruction(),OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());
        var alternate=Method("AlternatePopEntry",true);var alt=OpCodes.Ldc_I4.ToInstruction(456);var pop=OpCodes.Pop.ToInstruction();
        Add(alternate,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Brtrue.ToInstruction(alt),OpCodes.Ldc_I4.ToInstruction(123),OpCodes.Br.ToInstruction(pop),alt,pop,
            OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());
        var throwing=Method("ThrowingGarbage");Add(throwing,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Ldc_I4_0.ToInstruction(),OpCodes.Div.ToInstruction(),
            OpCodes.Pop.ToInstruction(),OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());
        var checkedMethod=Method("CheckedGarbage");Add(checkedMethod,OpCodes.Ldc_I4.ToInstruction(300),OpCodes.Conv_Ovf_U1.ToInstruction(),
            OpCodes.Pop.ToInstruction(),OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Ret.ToInstruction());
        var protectedMethod=Method("Protected");protectedMethod.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
        var start=OpCodes.Nop.ToInstruction();var interior=OpCodes.Nop.ToInstruction();var handler=OpCodes.Call.ToInstruction(touch);var exit=OpCodes.Ldloc_0.ToInstruction();
        Add(protectedMethod,start,OpCodes.Nop.ToInstruction(),OpCodes.Br.ToInstruction(interior),interior,
            OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Pop.ToInstruction(),OpCodes.Ldc_I4_S.ToInstruction((sbyte)42),OpCodes.Stloc_0.ToInstruction(),OpCodes.Leave.ToInstruction(exit),
            handler,OpCodes.Pop.ToInstruction(),OpCodes.Endfinally.ToInstruction(),exit,OpCodes.Ret.ToInstruction());
        protectedMethod.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally){TryStart=start,TryEnd=handler,HandlerStart=handler,HandlerEnd=exit});
        return module;

        MethodDef Method(string name,bool argument=false)
        {
            var signature=argument?MethodSig.CreateStatic(module.CorLibTypes.Int32,module.CorLibTypes.Int32):MethodSig.CreateStatic(module.CorLibTypes.Int32);
            var method=new MethodDefUser(name,signature,MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};type.Methods.Add(method);return method;
        }
    }
    private static void Add(MethodDef method,params Instruction[] instructions)
    {foreach(var instruction in instructions)method.Body.Instructions.Add(instruction);}
}
