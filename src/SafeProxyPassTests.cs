using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json.Linq;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class SafeProxyPassTests
{
    internal static void Run(string root)
    {
        using var module=new ModuleDefUser("ProxyCoverage.dll");
        var assembly=new AssemblyDefUser("ProxyCoverage"+Guid.NewGuid().ToString("N"),new Version(1,0));assembly.Modules.Add(module);
        TypeDef Type(string name,bool beforeFieldInit=false)
        {
            var type=new TypeDefUser("Fixture",name,module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=TypeAttributes.Public|(beforeFieldInit?TypeAttributes.BeforeFieldInit:0)};
            module.Types.Add(type);return type;
        }
        MethodDef Method(TypeDef owner,string name,TypeSig result,params TypeSig[] args)
        {
            var method=new MethodDefUser(name,MethodSig.CreateStatic(result,args),dnlib.DotNet.MethodImplAttributes.IL,
                MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};owner.Methods.Add(method);return method;
        }
        void Set(MethodDef method,params Instruction[] il){foreach(var instruction in il)method.Body.Instructions.Add(instruction);}
        MethodDef Forward(TypeDef type,string name,MethodDef target,OpCode? code=null)
        {
            var method=Method(type,name,target.ReturnType,target.MethodSig.Params.ToArray());
            for(int i=0;i<method.Parameters.Count;i++)method.Body.Instructions.Add(OpCodes.Ldarg.ToInstruction(method.Parameters[i]));
            Set(method,(code??OpCodes.Call).ToInstruction(target),OpCodes.Ret.ToInstruction());return method;
        }
        var type=Type("Chains");
        var leaf=Method(type,"Leaf",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        Set(leaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
        var inner=Forward(type,"Inner",leaf);var outer=Forward(type,"Outer",inner);var chainUse=Forward(type,"Use",outer);
        var spilled=Forward(type,"Spilled",leaf);var local=new Local(module.CorLibTypes.Int32);spilled.Body.Variables.Add(local);
        spilled.Body.Instructions.RemoveAt(spilled.Body.Instructions.Count-1);
        Set(spilled,OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloc_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var spillUse=Forward(type,"UseSpilled",spilled);
        var memberUse=Forward(type,"UseMemberRef",leaf);
        memberUse.Body.Instructions[1].Operand=new MemberRefUser(module,outer.Name,outer.MethodSig,new TypeRefUser(module,type.Namespace,type.Name,module));
        var prefixed=Forward(type,"UseTail",inner);prefixed.Body.Instructions.Insert(1,OpCodes.Tailcall.ToInstruction());

        var u4=Method(type,"Unsigned",module.CorLibTypes.UInt32,module.CorLibTypes.UInt32);
        Set(u4,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_M1.ToInstruction(),OpCodes.Xor.ToInstruction(),OpCodes.Ret.ToInstruction());
        var signed=Method(type,"SignedForward",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        Set(signed,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(u4),OpCodes.Ret.ToInstruction());
        var signedUse=Forward(type,"UseSigned",signed);
        var u8=Method(type,"Unsigned64",module.CorLibTypes.UInt64,module.CorLibTypes.UInt64);
        Set(u8,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Not.ToInstruction(),OpCodes.Ret.ToInstruction());
        var signed64=Method(type,"SignedForward64",module.CorLibTypes.Int64,module.CorLibTypes.Int64);
        Set(signed64,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(u8),OpCodes.Ret.ToInstruction());
        var signed64Use=Forward(type,"UseSigned64",signed64);
        var small=Method(type,"Unsigned8",module.CorLibTypes.Byte,module.CorLibTypes.Byte);Set(small,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var smallProxy=Method(type,"Signed8",module.CorLibTypes.SByte,module.CorLibTypes.SByte);
        Set(smallProxy,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(small),OpCodes.Ret.ToInstruction());
        var smallUse=Forward(type,"UseSigned8",smallProxy);
        var floatLeaf=Method(type,"FloatLeaf",module.CorLibTypes.Single,module.CorLibTypes.Single);
        Set(floatLeaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var floatProxy=Forward(type,"FloatSpill",floatLeaf);floatProxy.Body.Variables.Add(new Local(module.CorLibTypes.Single));
        floatProxy.Body.Instructions.RemoveAt(floatProxy.Body.Instructions.Count-1);
        Set(floatProxy,OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloc_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var floatUse=Forward(type,"UseFloatSpill",floatProxy);

        var cycleA=Method(type,"CycleA",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        var cycleB=Method(type,"CycleB",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        Set(cycleA,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(cycleB),OpCodes.Ret.ToInstruction());
        Set(cycleB,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(cycleA),OpCodes.Ret.ToInstruction());
        var cycleUse=Forward(type,"UseCycle",cycleA);
        var nonLeaf=Method(type,"NonLeaf",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        Set(nonLeaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(leaf),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
        var nonLeafProxy=Forward(type,"NonLeafProxy",nonLeaf);var nonLeafUse=Forward(type,"UseNonLeaf",nonLeafProxy);

        (MethodDef Proxy,MethodDef Use,MethodDef Leaf) Initialized(string name,bool beforeFieldInit)
        {
            var owner=Type(name,beforeFieldInit);var counter=new FieldDefUser("Counter",new FieldSig(module.CorLibTypes.Int32),dnlib.DotNet.FieldAttributes.Public|dnlib.DotNet.FieldAttributes.Static);owner.Fields.Add(counter);
            var cctor=Method(owner,".cctor",module.CorLibTypes.Void);cctor.Attributes=MethodAttributes.Private|MethodAttributes.Static|MethodAttributes.SpecialName|MethodAttributes.RTSpecialName;
            Set(cctor,OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Stsfld.ToInstruction(counter),OpCodes.Ret.ToInstruction());
            var target=Method(owner,"Leaf",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
            Set(target,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldsfld.ToInstruction(counter),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
            var proxy=Forward(owner,"Proxy",target);return(proxy,Forward(owner,"Use",proxy),target);
        }
        var precise=Initialized("Precise",false);var relaxed=Initialized("Relaxed",true);
        var outsiderUse=Forward(type,"UseOtherInitializer",precise.Proxy);
        var instanceUse=new MethodDefUser("InstanceUse",MethodSig.CreateInstance(module.CorLibTypes.Int32,module.CorLibTypes.Int32),
            dnlib.DotNet.MethodImplAttributes.IL,MethodAttributes.Public){Body=new CilBody()};precise.Proxy.DeclaringType.Methods.Add(instanceUse);
        Set(instanceUse,OpCodes.Ldarg_1.ToInstruction(),OpCodes.Call.ToInstruction(precise.Proxy),OpCodes.Ret.ToInstruction());

        var receiver=Type("Receiver");
        var instance=new MethodDefUser("Constant",MethodSig.CreateInstance(module.CorLibTypes.Int32),dnlib.DotNet.MethodImplAttributes.IL,MethodAttributes.Public){Body=new CilBody()};receiver.Methods.Add(instance);
        Set(instance,OpCodes.Ldc_I4.ToInstruction(17),OpCodes.Ret.ToInstruction());
        MethodDef ReceiverProxy(string name,OpCode code)
        {
            var proxy=Method(type,name,module.CorLibTypes.Int32,receiver.ToTypeSig());
            Set(proxy,OpCodes.Ldarg_0.ToInstruction(),code.ToInstruction(instance),OpCodes.Ret.ToInstruction());return proxy;
        }
        var virtualProxy=ReceiverProxy("VirtualProxy",OpCodes.Callvirt);var directProxy=ReceiverProxy("DirectProxy",OpCodes.Call);
        var virtualUse=Forward(type,"UseVirtual",virtualProxy);var directUse=Forward(type,"UseDirect",directProxy);

        var valueType=new TypeDefUser("Fixture","Number",module.CorLibTypes.GetTypeRef("System","ValueType"))
            {Attributes=TypeAttributes.Public|TypeAttributes.Sealed|TypeAttributes.SequentialLayout};module.Types.Add(valueType);
        var valueField=new FieldDefUser("Value",new FieldSig(module.CorLibTypes.Int32),dnlib.DotNet.FieldAttributes.Public);valueType.Fields.Add(valueField);
        var valueLeaf=new MethodDefUser("Read",MethodSig.CreateInstance(module.CorLibTypes.Int32),dnlib.DotNet.MethodImplAttributes.IL,MethodAttributes.Public){Body=new CilBody()};valueType.Methods.Add(valueLeaf);
        Set(valueLeaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldfld.ToInstruction(valueField),OpCodes.Ret.ToInstruction());
        var valueProxy=Method(type,"ValueProxy",module.CorLibTypes.Int32,new ByRefSig(valueType.ToTypeSig()));
        Set(valueProxy,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(valueLeaf),OpCodes.Ret.ToInstruction());
        var valueUse=Forward(type,"UseValue",valueProxy);

        var varArg=Method(type,"VarArg",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
        varArg.MethodSig.CallingConvention=CallingConvention.VarArg;
        Set(varArg,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var varArgProxy=Forward(type,"VarArgProxy",varArg);var varArgUse=Forward(type,"UseVarArg",varArgProxy);

        Assembly Snapshot()
        {
            assembly.Name="ProxyCoverage"+Guid.NewGuid().ToString("N");using var bytes=new MemoryStream();module.Write(bytes);return Assembly.Load(bytes.ToArray());
        }
        var before=Snapshot();string report=Path.Combine(root,"proxy-coverage.json");
        if(SafeProxyPass.Process(module,report)==0)throw new Exception("Proxy coverage fixture was not transformed.");
        void Target(MethodDef caller,MethodDef expected)
        {
            var instruction=caller.Body.Instructions.Single(i=>i.OpCode.Code is Code.Call or Code.Callvirt);
            if(instruction.Operand!=expected)throw new Exception("Wrong proxy target: "+caller.FullName);
        }
        Target(chainUse,leaf);Target(spillUse,leaf);Target(memberUse,leaf);Target(prefixed,inner);
        Target(signedUse,u4);Target(signed64Use,u8);Target(smallUse,smallProxy);Target(floatUse,floatProxy);
        Target(cycleUse,cycleA);Target(cycleA,cycleB);Target(cycleB,cycleA);Target(nonLeafUse,nonLeafProxy);
        Target(precise.Use,precise.Leaf);Target(relaxed.Use,relaxed.Proxy);Target(outsiderUse,precise.Proxy);Target(instanceUse,precise.Proxy);
        Target(virtualUse,instance);Target(directUse,instance);
        Target(valueUse,valueLeaf);Target(varArgUse,varArgProxy);
        if(virtualUse.Body.Instructions[1].OpCode!=OpCodes.Callvirt || directUse.Body.Instructions[1].OpCode!=OpCodes.Call)
            throw new Exception("Instance forwarding lost null-check semantics.");
        if(!type.Methods.Contains(outer) || !type.Methods.Contains(spilled) || !JArray.Parse(File.ReadAllText(report)).Any(c=>(int?)c["ChainDepth"]>=2))
            throw new Exception("Proxy chains lost definitions or evidence.");
        var after=Snapshot();
        object? Eval(Assembly a,string owner,string method,params object?[] values)=>a.GetType("Fixture."+owner)!.GetMethod(method)!.Invoke(null,values);
        foreach(int input in new[]{int.MinValue,-1,0,1,57,int.MaxValue})
        foreach(string name in new[]{"Use","UseSpilled","UseMemberRef","UseSigned","UseOtherInitializer","UseNonLeaf"})
            if(!Equals(Eval(before,"Chains",name,input),Eval(after,"Chains",name,input)))throw new Exception("Proxy changed results: "+name);
        foreach(long input in new[]{long.MinValue,-1L,0L,1L,long.MaxValue})
            if(!Equals(Eval(before,"Chains","UseSigned64",input),Eval(after,"Chains","UseSigned64",input)))throw new Exception("64-bit forwarding changed results.");
        foreach(string owner in new[]{"Precise","Relaxed"})
            if(!Equals(Eval(before,owner,"Use",42),Eval(after,owner,"Use",42)))throw new Exception("Initialization changed results.");
        foreach(var snapshot in new[]{before,after})
        {
            var number=Activator.CreateInstance(snapshot.GetType("Fixture.Number")!)!;number.GetType().GetField("Value")!.SetValue(number,73);
            if(!Equals(Eval(snapshot,"Chains","UseValue",number),73))throw new Exception("Value-type byref receiver changed results.");
            if(!Equals(Eval(snapshot,"Chains","UseDirect",new object?[]{null}),17))throw new Exception("Direct call acquired a null check.");
            try{Eval(snapshot,"Chains","UseVirtual",new object?[]{null});throw new Exception("Callvirt lost its null check.");}
            catch(TargetInvocationException ex)when(ex.InnerException is NullReferenceException){}
        }
        // Also exercise a real metadata MemberRef with module-scoped TypeRef, not
        // only the mutable ModuleDefUser representation used to build fixtures.
        memberUse.Body.Instructions[1].Operand=new MemberRefUser(module,outer.Name,outer.MethodSig,new TypeRefUser(module,type.Namespace,type.Name,module));
        using(var bytes=new MemoryStream())
        {
            module.Write(bytes);using var reloaded=ModuleDefMD.Load(bytes.ToArray());
            var owner=reloaded.Find("Fixture.Chains",false);
            if(SafeProxyPass.Process(reloaded,Path.Combine(root,"proxy-coverage-reloaded.json"))==0
                || owner.FindMethod("UseMemberRef").Body.Instructions[1].Operand!=owner.FindMethod("Leaf"))
                throw new Exception("Loaded module-scoped MemberRef was not restored.");
        }
        Console.WriteLine("PASS: proxy chains, return spills, local MemberRefs, byref receivers and I4/U4/I8/U8 preserve results; cycles, narrow integers, varargs, prefixes, unknown leaves and initialization triggers retained; callvirt null checks preserved.");
    }
}
