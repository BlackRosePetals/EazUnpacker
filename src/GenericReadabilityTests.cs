using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class GenericReadabilityTests
{
    internal static void Run(string root)
    {
        string directory=Path.Combine(root,"generic-readability");Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"input.exe"),output=Path.Combine(directory,"output.exe");
        using(var module=new ModuleDefUser("Readability.exe")){new AssemblyDefUser("Readability"+Guid.NewGuid().ToString("N"),new Version(1,0)).Modules.Add(module);
            module.Kind=ModuleKind.Console;
            foreach(var pair in new[]{("One","Class841"),("One","Class842"),("Two","Class843")})
                module.Types.Add(new TypeDefUser(pair.Item1,pair.Item2,module.CorLibTypes.GetTypeRef("System","Exception")){Attributes=TypeAttributes.Public});
            var app=new TypeDefUser("Fixture","Launch",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};module.Types.Add(app);
            var main=Method(app,"Main",module.CorLibTypes.Void,new SZArraySig(module.CorLibTypes.String));main.Parameters[0].CreateParamDef();main.Parameters[0].Name="stringArrayArgument0";
            main.Body.Instructions.Add(OpCodes.Ret.ToInstruction());module.EntryPoint=main;
            var parameters=Method(app,"Parameters",module.CorLibTypes.Void,module.CorLibTypes.Int32,module.CorLibTypes.Int32,module.CorLibTypes.Int32,module.CorLibTypes.Boolean);
            for(int i=0;i<parameters.Parameters.Count;i++){parameters.Parameters[i].CreateParamDef();parameters.Parameters[i].Name=i==2?"value":i==3?"boolArgument3":"intArgument"+i;}
            parameters.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            var getter=new MethodDefUser("get_Item",MethodSig.CreateInstance(module.CorLibTypes.Int32,module.CorLibTypes.Int32),MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.HideBySig){Body=new CilBody()};app.Methods.Add(getter);
            getter.Parameters[1].CreateParamDef();getter.Parameters[1].Name="value";getter.Body.Instructions.Add(OpCodes.Ldarg_1.ToInstruction());getter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            var setter=new MethodDefUser("set_Item",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.Int32,module.CorLibTypes.Int32),MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.HideBySig){Body=new CilBody()};app.Methods.Add(setter);
            setter.Parameters[1].CreateParamDef();setter.Parameters[1].Name="value";setter.Parameters[2].CreateParamDef();setter.Parameters[2].Name="value2";setter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            app.Properties.Add(new PropertyDefUser("Item",PropertySig.CreateInstance(module.CorLibTypes.Int32,module.CorLibTypes.Int32)){GetMethod=getter,SetMethod=setter});
            var enumeration=new TypeDefUser("Fixture","Level",module.CorLibTypes.GetTypeRef("System","Enum")){Attributes=TypeAttributes.Public|TypeAttributes.Sealed};module.Types.Add(enumeration);
            enumeration.Fields.Add(new FieldDefUser("value__",new FieldSig(module.CorLibTypes.Int32),dnlib.DotNet.FieldAttributes.Public|dnlib.DotNet.FieldAttributes.SpecialName|dnlib.DotNet.FieldAttributes.RTSpecialName));
            var constant=Method(app,"ConstantEnum",module.CorLibTypes.Int32,module.CorLibTypes.Int32);
            constant.Body.Variables.Add(new Local(enumeration.ToTypeSig()));constant.Body.Variables.Add(new Local(module.CorLibTypes.String));
            var success=OpCodes.Ldstr.ToInstruction("unused-role-text");
            foreach(var i in new[]{Instruction.CreateLdcI4(7),OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloc_0.ToInstruction(),Instruction.CreateLdcI4(3),OpCodes.Beq.ToInstruction(success),
                OpCodes.Ldloc_0.ToInstruction(),Instruction.CreateLdcI4(7),OpCodes.Beq.ToInstruction(success),OpCodes.Newobj.ToInstruction(module.Import(typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!)),OpCodes.Throw.ToInstruction(),
                success,OpCodes.Stloc_1.ToInstruction(),OpCodes.Ldloc_1.ToInstruction(),OpCodes.Stloc_1.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction()})constant.Body.Instructions.Add(i);
            var dynamic=Method(app,"DynamicEnum",module.CorLibTypes.Boolean,module.CorLibTypes.Int32);dynamic.Body.Variables.Add(new Local(enumeration.ToTypeSig()));
            foreach(var i in new[]{OpCodes.Ldarg_0.ToInstruction(),OpCodes.Stloc_0.ToInstruction(),OpCodes.Ldloc_0.ToInstruction(),Instruction.CreateLdcI4(7),OpCodes.Ceq.ToInstruction(),OpCodes.Ret.ToInstruction()})dynamic.Body.Instructions.Add(i);
            module.Write(input);
        }
        NameRecoveryPass.Process(input,output,Path.Combine(directory,"names.json"));
        using(var module=ModuleDefMD.Load(output))
        {
            if(module.EntryPoint!.DeclaringType.Name!="Program"||module.EntryPoint.Parameters[0].Name!="args")throw new Exception("CLI entry-point role naming failed.");
            if(module.Types.Count(t=>t.Namespace=="One"&&t.Name=="Exception")!=1||module.Types.Count(t=>t.Namespace=="One"&&t.Name=="Exception2")!=1||module.Types.Count(t=>t.Namespace=="Two"&&t.Name=="Exception")!=1)
                throw new Exception("Base-type naming did not respect namespace/collision scopes.");
            var parameters=module.EntryPoint.DeclaringType.FindMethod("Parameters")!.Parameters;
            if(!parameters.Select(p=>p.Name).SequenceEqual(new[]{"value2","value3","value","flag"}))throw new Exception("Concise parameters collided with preserved semantic names.");
            var indexer=module.EntryPoint.DeclaringType.Properties.Single(p=>p.Name=="Item");
            if(indexer.GetMethod.Parameters[1].Name!="index"||indexer.SetMethod.Parameters[1].Name!="index"||indexer.SetMethod.Parameters[2].Name!="value")throw new Exception("Indexer parameter collides with its implicit setter value.");
            var method=module.EntryPoint.DeclaringType.FindMethod("ConstantEnum")!;
            if(method.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch||i.OpCode.Code is Code.Throw or Code.Newobj||i.IsStloc()))
                throw new Exception("Enum-constant dead branches or unused/self local stores remain.");
        }
        var before=Assembly.Load(File.ReadAllBytes(input));var after=Assembly.Load(File.ReadAllBytes(output));
        foreach(int value in new[]{int.MinValue,-1,0,3,7,8,int.MaxValue})
        {
            object Invoke(Assembly a,string owner,string method)=>a.GetType(owner)!.GetMethod(method)!.Invoke(null,new object[]{value})!;
            if(!Equals(Invoke(before,"Fixture.Launch","ConstantEnum"),Invoke(after,"Fixture.Program","ConstantEnum"))||
                !Equals(Invoke(before,"Fixture.Launch","DynamicEnum"),Invoke(after,"Fixture.Program","DynamicEnum")))throw new Exception("Generic enum simplification changed dynamic behavior.");
        }
        Console.WriteLine("PASS: namespace-aware inherited names, CLI Program/args, concise collision-safe parameters, enum constants and dead local stores preserve behavior.");
    }
    private static MethodDef Method(TypeDef owner,string name,TypeSig returns,params TypeSig[] parameters)
    {
        var method=new MethodDefUser(name,MethodSig.CreateStatic(returns,parameters),MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};owner.Methods.Add(method);return method;
    }
}
