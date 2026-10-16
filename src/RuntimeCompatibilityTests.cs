using System.Security.Cryptography;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Unpacker;

internal static class RuntimeCompatibilityTests
{
    internal static void Run(string root)
    {
        string input=Path.Combine(root,"signed-original.exe"),output=Path.Combine(root,"signed-recovered.exe");
        using(var rsa=new RSACryptoServiceProvider(1024,new CspParameters {KeyNumber=(int)KeyNumber.Signature}) {PersistKeyInCsp=false})
        using(var module=new ModuleDefUser("SignatureFixture.exe") {Kind=ModuleKind.Console})
        {
            new AssemblyDefUser("SignatureFixture",new Version(1,0)).Modules.Add(module);
            var type=new TypeDefUser("Fixture","#=Entry",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
            var entry=new MethodDefUser("#=Entry",MethodSig.CreateStatic(module.CorLibTypes.Int32),MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static)
                {Body=new CilBody()};
            entry.Body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(37));entry.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            type.Methods.Add(entry);module.EntryPoint=entry;
            var options=new ModuleWriterOptions(module);options.InitializeStrongNameSigning(module,new StrongNameKey(rsa.ExportCspBlob(true)));
            module.Write(input,options);
        }
        string source="<configuration><runtime><bypassTrustedAppStrongNames enabled='false'/><assemblyBinding xmlns='urn:schemas-microsoft-com:asm.v1'><probing privatePath='Components;Components\\Vendor'/></assemblyBinding></runtime><appSettings><add key='Preserve' value='yes'/></appSettings></configuration>";
        File.WriteAllText(input+".config",source);
        if(ProcessRunner.Run(input,Array.Empty<string>(),Path.Combine(root,"signed-input.log"),10)!=37)
            throw new Exception("Valid signed fixture did not start with strict verification.");
        NameRecoveryPass.Process(input,output,Path.Combine(root,"signature-names.json"));
        using(var before=ModuleDefMD.Load(input))
        using(var after=ModuleDefMD.Load(output))
        {
            if(!after.Assembly.HasPublicKey || !Enumerable.SequenceEqual(before.Assembly.PublicKey.Data,after.Assembly.PublicKey.Data))
                throw new Exception("Runtime compatibility changed public key identity.");
        }
        RuntimeCompatibility.WriteConfiguration(input+".config",output+".config",output+".config");
        var xml=XDocument.Load(output+".config");
        if((string?)xml.Root?.Element("appSettings")?.Element("add")?.Attribute("value")!="yes"
            || !File.ReadAllText(output+".config").Contains("Components;Components\\Vendor") || File.ReadAllText(input+".config")!=source)
            throw new Exception("Runtime configuration lost settings or changed input.");
        if(ProcessRunner.Run(output,Array.Empty<string>(),Path.Combine(root,"signed-output.log"),10)!=37)
            throw new Exception("Rewritten signed fixture did not start with application compatibility policy.");
        RuntimeCompatibility.WriteConfiguration("",output+".config",output+".config");
        if(XDocument.Load(output+".config").Descendants("bypassTrustedAppStrongNames").Count()!=1)
            throw new Exception("Repeated runtime config generation duplicated policy.");
        Console.WriteLine("PASS: signed fixture starts before/after rewriting; original identity, probing/config settings and input preserved; per-app signature policy is idempotent.");
    }
}
