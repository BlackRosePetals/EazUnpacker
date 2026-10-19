using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.Core.IO;

internal static class KeyExpressionEvaluatorTests
{
    internal static void Run()
    {
        // Random metadata names deliberately prevent sample-name based matching.
        var module = new ModuleDefinition("KeyFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var owner = new TypeDefinition("Fixture", "N"+Guid.NewGuid().ToString("N"), TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(owner);
        var host = new MethodDefinition("Host", MethodAttributes.Static | MethodAttributes.Public, MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        owner.Methods.Add(host);
        VMMethod Expression(params CilInstruction[] code) => new(host,"",0) { Instructions=code.ToList() };
        void Expect(VMMethod method, object expected)
        {
            var actual=KeyExpressionEvaluator.Candidates(method,method.Instructions.Count-1).ToArray();
            if(!actual.Contains(expected))throw new Exception("Generic key candidate missing: "+expected+"; got "+string.Join(",",actual));
        }
        void Reject(VMMethod method)
        {
            if(KeyExpressionEvaluator.Candidates(method,method.Instructions.Count-1).Any())throw new Exception("Unsupported key state was guessed.");
        }
        var local=new CilLocalVariable(module.CorLibTypeFactory.Int32);
        host.CilMethodBody=new CilMethodBody();host.CilMethodBody.LocalVariables.Add(local);
        var arithmetic=Expression(new CilInstruction(CilOpCodes.Ldc_I4,71),new(CilOpCodes.Ldc_I4,13),new(CilOpCodes.Stloc,local),new(CilOpCodes.Ldloc,local),new(CilOpCodes.Xor),new(CilOpCodes.Ldc_I4_3),new(CilOpCodes.Mul),new(CilOpCodes.Dup));
        arithmetic.Locals.Add(local); Expect(arithmetic,(71^13)*3);
        Expect(Expression(new CilInstruction(CilOpCodes.Ldc_I4_M1),new(CilOpCodes.Conv_U1)),255);
        Expect(Expression(new CilInstruction(CilOpCodes.Ldstr,"")),"");
        Reject(Expression(new CilInstruction(CilOpCodes.Ldarg_0)));
        var toString=module.DefaultImporter.ImportMethod(typeof(object).GetMethod("ToString")!);
        var objectLocal=new CilLocalVariable(module.CorLibTypeFactory.Object);
        host.CilMethodBody.LocalVariables.Add(objectLocal);
        var runtimeString=Expression(new CilInstruction(CilOpCodes.Ldarg_0),new(CilOpCodes.Callvirt,toString),new(CilOpCodes.Dup),new(CilOpCodes.Stloc,objectLocal),new(CilOpCodes.Ldloc,objectLocal));
        runtimeString.Locals.Add(local);runtimeString.Locals.Add(objectLocal);
        if(KeyExpressionEvaluator.InferKeyType(runtimeString,runtimeString.Instructions.Count-1)!="System.String")throw new Exception("Runtime string producer fell back to integer search.");
        Reject(runtimeString); // Knowing a type does not fabricate its value.
        if(KeyExpressionEvaluator.InferKeyType(Expression(new CilInstruction(CilOpCodes.Ldc_I4_1)),0)!="System.Int32")throw new Exception("Integer producer type lost.");
        if(KeyExpressionEvaluator.InferKeyType(Expression(new CilInstruction(CilOpCodes.Ldarg_0)),0)!=null)throw new Exception("Unknown key producer type guessed.");
        if(KeyExpressionEvaluator.InferKeyType(Expression(new CilInstruction(CilOpCodes.Nop)),99)!=null)throw new Exception("Invalid key index was accepted.");
        if(KeyExpressionEvaluator.IntrinsicCandidates(Expression(new CilInstruction(CilOpCodes.Ldc_I4_1))).Any())throw new Exception("COM candidates enabled without COM metadata.");
        var emptyField=module.DefaultImporter.ImportField(typeof(string).GetField("Empty")!);
        if(KeyExpressionEvaluator.IntrinsicCandidates(Expression(new CilInstruction(CilOpCodes.Ldsfld,emptyField))).Any())throw new Exception("Field member reference mistaken for a method.");
        var com=new TypeDefinition("Fixture","I"+Guid.NewGuid().ToString("N"),TypeAttributes.Interface|TypeAttributes.Import|TypeAttributes.Abstract,null);
        module.TopLevelTypes.Add(com);
        var show=new MethodDefinition("M"+Guid.NewGuid().ToString("N"),MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Abstract,MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
        show.PreserveSignature=true;com.Methods.Add(show);
        if(!KeyExpressionEvaluator.IntrinsicCandidates(Expression(new CilInstruction(CilOpCodes.Callvirt,show))).Contains(unchecked((int)0x800704c7)))throw new Exception("Generic COM cancellation outcome missing.");
        Reject(Expression(new CilInstruction(CilOpCodes.Ldnull),new(CilOpCodes.Ldc_I4_1),new(CilOpCodes.Add)));

        // Loop over a string in a local IL helper without invoking target code.
        var helper=new MethodDefinition("H"+Guid.NewGuid().ToString("N"),MethodAttributes.Static|MethodAttributes.Private,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,module.CorLibTypeFactory.String));
        owner.Methods.Add(helper); helper.CilMethodBody=new CilMethodBody();
        var sum=new CilLocalVariable(module.CorLibTypeFactory.Int32);var index=new CilLocalVariable(module.CorLibTypeFactory.Int32);
        helper.CilMethodBody.LocalVariables.Add(sum);helper.CilMethodBody.LocalVariables.Add(index);
        var getLength=module.DefaultImporter.ImportMethod(typeof(string).GetProperty("Length")!.GetMethod!);
        var getChar=module.DefaultImporter.ImportMethod(typeof(string).GetProperty("Chars")!.GetMethod!);
        var loop=new CilInstruction(CilOpCodes.Ldloc,sum);var check=new CilInstruction(CilOpCodes.Ldloc,index);
        foreach(var instruction in new CilInstruction[] {
            new(CilOpCodes.Ldc_I4_0),new(CilOpCodes.Stloc,sum),new(CilOpCodes.Ldc_I4_0),new(CilOpCodes.Stloc,index),new(CilOpCodes.Br,check.CreateLabel()),
            loop,new(CilOpCodes.Ldarg_0),new(CilOpCodes.Ldloc,index),new(CilOpCodes.Callvirt,getChar),new(CilOpCodes.Add),new(CilOpCodes.Stloc,sum),
            new(CilOpCodes.Ldloc,index),new(CilOpCodes.Ldc_I4_1),new(CilOpCodes.Add),new(CilOpCodes.Stloc,index),check,new(CilOpCodes.Ldarg_0),new(CilOpCodes.Callvirt,getLength),
            new(CilOpCodes.Blt,loop.CreateLabel()),new(CilOpCodes.Ldloc,sum),new(CilOpCodes.Ret) })helper.CilMethodBody.Instructions.Add(instruction);
        var getType=module.DefaultImporter.ImportMethod(typeof(Type).GetMethod("GetTypeFromHandle")!);
        var fullName=module.DefaultImporter.ImportMethod(typeof(Type).GetProperty("FullName")!.GetMethod!);
        Expect(Expression(new CilInstruction(CilOpCodes.Ldtoken,owner),new(CilOpCodes.Call,getType),new(CilOpCodes.Callvirt,fullName),new(CilOpCodes.Call,helper)),owner.FullName.Sum(c=>(int)c));
        var endless=new MethodDefinition("Endless",MethodAttributes.Static|MethodAttributes.Private,MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        owner.Methods.Add(endless);endless.CilMethodBody=new CilMethodBody();var back=new CilInstruction(CilOpCodes.Nop);
        endless.CilMethodBody.Instructions.Add(back);endless.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Br,back.CreateLabel()));
        Reject(Expression(new CilInstruction(CilOpCodes.Call,endless)));
        var field=new FieldDefinition("State",FieldAttributes.Static,module.CorLibTypeFactory.Int32);owner.Fields.Add(field);
        helper.CilMethodBody.Instructions.Clear();helper.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldc_I4_1));
        helper.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Stsfld,field));helper.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldc_I4_2));helper.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        Reject(Expression(new CilInstruction(CilOpCodes.Ldstr,"arbitrary"),new(CilOpCodes.Call,helper)));
        Console.WriteLine("PASS: generic key expressions, random metadata names and pure IL loops; unknown runtime state, side effects and endless loops rejected.");
    }
}
