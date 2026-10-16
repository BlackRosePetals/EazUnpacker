using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json.Linq;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class SafeReadabilityTests
{
    internal static void Run(string root)
    {
        using var module=new ModuleDefUser("ForwardingFixture.dll");
        new AssemblyDefUser("ForwardingFixture"+Guid.NewGuid().ToString("N"),new Version(1,0)).Modules.Add(module);
        var type=new TypeDefUser("Fixture","Forwarders",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};module.Types.Add(type);
        MethodDef Method(TypeDef owner,string name,bool publicMethod,params TypeSig[] parameters)
        {
            var method=new MethodDefUser(name,MethodSig.CreateStatic(module.CorLibTypes.Int32,parameters),dnlib.DotNet.MethodImplAttributes.IL,
                MethodAttributes.Static|(publicMethod?MethodAttributes.Public:MethodAttributes.Private)){Body=new CilBody()};owner.Methods.Add(method);return method;
        }
        void Set(MethodDef method,params Instruction[] instructions){foreach(var instruction in instructions)method.Body.Instructions.Add(instruction);}
        var leaf=Method(type,"Leaf",true,module.CorLibTypes.Int32);Set(leaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldc_I4_1.ToInstruction(),OpCodes.Add.ToInstruction(),OpCodes.Ret.ToInstruction());
        var proxy=Method(type,"Forward",true,module.CorLibTypes.Int32);Set(proxy,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(leaf),OpCodes.Ret.ToInstruction());
        var use=Method(type,"Use",true,module.CorLibTypes.Int32);Set(use,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(proxy),OpCodes.Ret.ToInstruction());
        var pair=Method(type,"First",true,module.CorLibTypes.Int32,module.CorLibTypes.Int32);Set(pair,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var reordered=Method(type,"Reordered",true,module.CorLibTypes.Int32,module.CorLibTypes.Int32);Set(reordered,OpCodes.Ldarg_1.ToInstruction(),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(pair),OpCodes.Ret.ToInstruction());
        var useReordered=Method(type,"UseReordered",true,module.CorLibTypes.Int32,module.CorLibTypes.Int32);Set(useReordered,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_1.ToInstruction(),OpCodes.Call.ToInstruction(reordered),OpCodes.Ret.ToInstruction());
        var guardedType=new TypeDefUser("Fixture","Initialized",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};module.Types.Add(guardedType);
        var cctor=new MethodDefUser(".cctor",MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodImplAttributes.IL,
            MethodAttributes.Static|MethodAttributes.Private|MethodAttributes.SpecialName|MethodAttributes.RTSpecialName){Body=new CilBody()};cctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());guardedType.Methods.Add(cctor);
        var guarded=Method(guardedType,"ForwardWithInitialization",true,module.CorLibTypes.Int32);Set(guarded,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(leaf),OpCodes.Ret.ToInstruction());
        var useGuarded=Method(type,"UseGuarded",true,module.CorLibTypes.Int32);Set(useGuarded,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(guarded),OpCodes.Ret.ToInstruction());
        var privateLeaf=Method(type,"PrivateLeaf",false,module.CorLibTypes.Int32);Set(privateLeaf,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ret.ToInstruction());
        var privateProxy=Method(type,"PublicForwardToPrivate",true,module.CorLibTypes.Int32);Set(privateProxy,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(privateLeaf),OpCodes.Ret.ToInstruction());
        var outsider=new TypeDefUser("Fixture","Outsider",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};module.Types.Add(outsider);
        var usePrivate=Method(outsider,"UsePrivate",true,module.CorLibTypes.Int32);Set(usePrivate,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(privateProxy),OpCodes.Ret.ToInstruction());
        Assembly Snapshot(){using var bytes=new MemoryStream();module.Write(bytes);return Assembly.Load(bytes.ToArray());}
        var before=Snapshot();
        string report=Path.Combine(root,"proxy-proof.json");
        if(SafeProxyPass.Process(module,report)!=1 || use.Body.Instructions[1].Operand!=leaf
            || useReordered.Body.Instructions[2].Operand!=reordered || useGuarded.Body.Instructions[1].Operand!=guarded
            || usePrivate.Body.Instructions[1].Operand!=privateProxy || !type.Methods.Contains(proxy))throw new Exception("Unsafe proxy transformation or lost definitions.");
        var after=Snapshot();
        foreach(int value in new[]{int.MinValue,-1,0,1,57,int.MaxValue})
        {
            object Eval(Assembly assembly,string name,params object[] values)=>assembly.GetType("Fixture.Forwarders")!.GetMethod(name)!.Invoke(null,values)!;
            if(!Equals(Eval(before,"Use",value),Eval(after,"Use",value)) || !Equals(Eval(before,"UseReordered",value,17),Eval(after,"UseReordered",value,17)))
                throw new Exception("Proxy simplification changed results.");
        }
        string file=Path.Combine(root,"string-index-fixture.dll");
        using(var data=new MemoryStream())
        {
            using(var writer=new System.Resources.ResourceWriter(data)){writer.AddResource("NeedKey","Need a license key? Get it now...");writer.Generate();module.Resources.Add(new EmbeddedResource("Fixture.resources",data.ToArray(),dnlib.DotNet.ManifestResourceAttributes.Private));}
        }
        module.Write(file);
        string index=Path.Combine(root,"search-index");
        if(StringSearchIndex.Run(file,file,index)!=0 || !File.ReadAllText(Path.Combine(index,"strings.txt")).Contains("Need a license key? Get it now..."))
            throw new Exception("Safe resource string index lost searchable text.");
        if(JObject.Parse(File.ReadAllText(Path.Combine(index,"index.json")))["Modules"]!.Any(m=>(bool?)m["InputUnchanged"]!=true))
            throw new Exception("Search index modified input.");
        using(var data=new MemoryStream())
        {
            using(var writer=new System.Resources.ResourceWriter(data))
            {
                writer.AddResource("literal","extended-reader string");
                writer.AddResourceData("opaque","Never.Load.This.Type, Missing.Assembly",new byte[]{1,2,3});writer.Generate();
                var bytes=data.ToArray();
                using var headerReader=new BinaryReader(new MemoryStream(bytes));headerReader.ReadInt32();headerReader.ReadInt32();int size=headerReader.ReadInt32();headerReader.ReadString();string set=headerReader.ReadString();
                using var header=new MemoryStream();using(var binary=new BinaryWriter(header,System.Text.Encoding.UTF8,true))
                {binary.Write("System.Resources.Extensions.DeserializingResourceReader");binary.Write(set);}
                if(header.Length>size)throw new Exception("Invalid extended resource fixture header.");
                Array.Clear(bytes,12,size);Array.Copy(header.ToArray(),0,bytes,12,header.Length);
                using var safe=SafeResourceStrings.Open(bytes);safe.GetResourceData("literal",out string kind,out byte[] raw);
                using var literal=new BinaryReader(new MemoryStream(raw));
                if(kind!="ResourceTypeCode.String" || literal.ReadString()!="extended-reader string")throw new Exception("Extended resource string header normalization failed.");
                safe.GetResourceData("opaque",out string opaque,out _);
                if(!opaque.StartsWith("Never.Load.This.Type"))throw new Exception("Opaque resource data was unexpectedly interpreted.");
            }
        }
        Console.WriteLine("PASS: proxy results preserved; reordered args, type initialization and private access retained; string resources indexed without object deserialization/input edits.");
    }
}
