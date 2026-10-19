using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

internal static class AssemblyInspectorTests
{
    internal static void Run(string root)
    {
        string path=Path.Combine(root,"vm-detection.dll");
        using(var module=new ModuleDefUser("VmDetection.dll"))
        {
            var assembly=new AssemblyDefUser("VmDetection",new Version(1,0));assembly.Modules.Add(module);
            var type=new TypeDefUser("Fixture","VM",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
            var stream=module.Import(typeof(Stream)).ToTypeSig();
            var signature=MethodSig.CreateStatic(module.CorLibTypes.Object,stream,module.CorLibTypes.String,new SZArraySig(module.CorLibTypes.Object));
            MethodDef Add(string name,MethodSig sig,params Instruction[] body)
            {
                var method=new MethodDefUser(name,sig,MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};
                type.Methods.Add(method);foreach(var i in body)method.Body.Instructions.Add(i);return method;
            }
            var executor=Add("Execute",signature,OpCodes.Ldnull.ToInstruction(),OpCodes.Ret.ToInstruction());
            Add("ExecutePosition",MethodSig.CreateStatic(module.CorLibTypes.Void,stream,module.CorLibTypes.Int64,
                module.CorLibTypes.String),OpCodes.Ret.ToInstruction());
            Add("Wrapper",signature,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_1.ToInstruction(),OpCodes.Ldarg_2.ToInstruction(),
                OpCodes.Call.ToInstruction(executor),OpCodes.Ret.ToInstruction());
            var businessType=new TypeDefUser("Fixture","Business",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(businessType);
            foreach(bool streamParameter in new[]{false,true})
            {
                var method=Add(streamParameter?"ProtectedStreamMethod":"ProtectedMethod",
                    streamParameter?MethodSig.CreateStatic(module.CorLibTypes.Object,stream):MethodSig.CreateStatic(module.CorLibTypes.Object),
                    OpCodes.Ldnull.ToInstruction(),OpCodes.Ldstr.ToInstruction("encoded-position"),OpCodes.Ldnull.ToInstruction(),
                    OpCodes.Call.ToInstruction(executor),OpCodes.Ret.ToInstruction());
                type.Methods.Remove(method);businessType.Methods.Add(method);
            }
            var external=new TypeRefUser(module,"Other","API",new AssemblyRefUser("ExternalVMFixture",new Version(1,0)));
            var externalCall=new MemberRefUser(module,"Execute",signature,external);
            Add("NotOurVM",MethodSig.CreateStatic(module.CorLibTypes.Object),
                OpCodes.Ldnull.ToInstruction(),OpCodes.Ldstr.ToInstruction("not-a-vm-key"),OpCodes.Ldnull.ToInstruction(),
                OpCodes.Call.ToInstruction(externalCall),OpCodes.Ret.ToInstruction());
            // Aspose.Cells contains a TIFF text writer with these parameter
            // types. Unlike the real runtime it has no decoded-position entry;
            // it is business code, not eight extra virtualized methods.
            var formatType=new TypeDefUser("Fixture","Formatter",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(formatType);
            var formatter=Add("WriteFormatted",MethodSig.CreateStatic(module.CorLibTypes.Void,stream,module.CorLibTypes.String,
                new SZArraySig(module.CorLibTypes.Object)),OpCodes.Ret.ToInstruction());
            type.Methods.Remove(formatter);formatType.Methods.Add(formatter);
            var sameSignature=Add("ProtectedExecutorShapedSignature",signature,OpCodes.Ldarg_0.ToInstruction(),
                OpCodes.Ldstr.ToInstruction("encoded-position"),OpCodes.Ldarg_2.ToInstruction(),
                OpCodes.Call.ToInstruction(executor),OpCodes.Ret.ToInstruction());
            type.Methods.Remove(sameSignature);formatType.Methods.Add(sameSignature);
            Add("FormatCaller",MethodSig.CreateStatic(module.CorLibTypes.Void),
                OpCodes.Ldnull.ToInstruction(),OpCodes.Ldstr.ToInstruction("format {0}"),OpCodes.Ldnull.ToInstruction(),
                OpCodes.Call.ToInstruction(formatter),OpCodes.Ret.ToInstruction());
            module.Write(path);
        }
        var result=AssemblyInspector.Inspect(path);
        if(result.VirtualizedMethods.Count!=3)throw new Exception("VM detection lost protected Stream/executor-shaped parameters or counted executor wrappers/external APIs.");
        Console.WriteLine("PASS: VM detection includes protected Stream-parameter methods and excludes executor wrappers/external signature lookalikes.");
    }
}
