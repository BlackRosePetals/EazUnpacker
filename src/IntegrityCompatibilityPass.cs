using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using Newtonsoft.Json;

namespace Unpacker;

internal static class IntegrityCompatibilityPass
{
    // Rewriting invalidates the original strong-name signature. Adapt only the
    // identified self-integrity guard, not native verification APIs in general.
    internal static int Process(string input,string output,string report)
    {
        using var module=ModuleDefMD.Load(input);
        var imports=module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasImplMap
            && m.ImplMap.Name=="StrongNameSignatureVerificationEx" && m.IsStatic
            && m.ReturnType.ElementType==ElementType.Boolean && m.MethodSig.Params.Count==3
            && m.MethodSig.Params[0].ElementType==ElementType.String && m.MethodSig.Params[1].ElementType==ElementType.Boolean
            && m.MethodSig.Params[2] is ByRefSig {Next.ElementType:ElementType.Boolean}).ToDictionary(m=>m.FullName);
        var changes=new List<object>();
        MethodDef? compatibility=null;
        foreach(var type in module.GetTypes().ToArray())
        {
            foreach(var caller in type.Methods.Where(m=>m.HasBody).ToArray())
            {
                var il=caller.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
                var calls=il.Select(i=>i.Operand).OfType<IMethod>().ToArray();
                bool guard=il.Any(i=>i.OpCode.Code==Code.Ldstr && i.Operand is string text
                        && text.IndexOf("Debugger detected",StringComparison.OrdinalIgnoreCase)>=0)
                    && calls.Any(m=>m.DeclaringType.FullName=="System.Environment" && m.Name=="Exit")
                    && calls.Any(m=>m.DeclaringType.FullName=="System.AppDomain" && m.Name=="get_BaseDirectory")
                    && calls.Any(m=>m.DeclaringType.FullName=="System.AppDomain" && m.Name=="get_FriendlyName");
                if(!guard)continue;
                foreach(var instruction in il.Where(i=>i.OpCode.Code==Code.Call).ToArray())
                {
                    if(instruction.Operand is not IMethod called || !imports.TryGetValue(called.FullName,out var native))continue;
                    int index=Array.IndexOf(il,instruction);
                    bool CallsAt(int offset,string owner,string name)=>index+offset>=0 && il[index+offset].Operand is IMethod m && m.DeclaringType.FullName==owner && m.Name==name;
                    if(index<8 || !CallsAt(-8,"System.AppDomain","get_CurrentDomain") || !CallsAt(-7,"System.AppDomain","get_BaseDirectory")
                        || il[index-6].OpCode.Code!=Code.Ldstr || il[index-6].Operand is not string separator || separator!="\\"
                        || !CallsAt(-5,"System.AppDomain","get_CurrentDomain") || !CallsAt(-4,"System.AppDomain","get_FriendlyName")
                        || !CallsAt(-3,"System.String","Concat") || !il[index-2].IsLdcI4() || il[index-2].GetLdcI4Value()!=0
                        || il[index-1].OpCode.Code is not (Code.Ldloca or Code.Ldloca_S))continue;
                    if(compatibility==null)
                    {
                        string typeName="UnpackIntegrityCompatibility";
                        while(module.Types.Any(t=>t.Name==typeName))typeName+="_";
                        var helperType=new TypeDefUser("",typeName,module.CorLibTypes.Object.TypeDefOrRef)
                            {Attributes=TypeAttributes.NotPublic|TypeAttributes.Abstract|TypeAttributes.Sealed|TypeAttributes.BeforeFieldInit};
                        module.Types.Add(helperType);
                        string name="AcceptRewrittenAssemblyIntegrity";
                        compatibility=new MethodDefUser(name,native.MethodSig.Clone(),MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static)
                            {Body=new CilBody()};
                        compatibility.Body.Instructions.Add(OpCodes.Ldarg_2.ToInstruction());
                        compatibility.Body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction());
                        compatibility.Body.Instructions.Add(OpCodes.Stind_I1.ToInstruction());
                        compatibility.Body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction());
                        compatibility.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
                        helperType.Methods.Add(compatibility);
                    }
                    changes.Add(new {Caller="0x"+caller.MDToken.Raw.ToString("X8"),Offset=instruction.Offset,Native=native.FullName,
                        Replacement=compatibility.FullName,Evidence="Self-path strong-name verification + debugger diagnostic + Environment.Exit; original signature invalidated by recovery.",
                        Note="Generated self-integrity compatibility, not re-signing. Other debugger checks and business branches retained."});
                    instruction.Operand=compatibility;
                }
            }
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(changes,Formatting.Indented));
        if(changes.Count>0)module.Write(output,new ModuleWriterOptions(module){MetadataOptions={Flags=MetadataFlags.PreserveAll}});
        return changes.Count;
    }
}
