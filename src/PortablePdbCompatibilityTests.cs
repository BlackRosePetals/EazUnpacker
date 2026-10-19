using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Unpacker;

internal static class PortablePdbCompatibilityTests
{
    internal static void Run(string root)
    {
        string directory=Path.Combine(root,"portable-symbols");Directory.CreateDirectory(directory);
        string original=Path.Combine(directory,"original.dll"),pointed=Path.Combine(directory,"pointed.dll"),output=Path.Combine(directory,"fixture-recovered.dll");
        using(var module=new ModuleDefUser("Symbols.dll"))
        {
            var assembly=new AssemblyDefUser("Symbols",new Version(1,0));assembly.Modules.Add(module);
            foreach(string name in new[]{"First","Last"})
            {
                var type=new TypeDefUser("Fixture",name,module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
                var method=new MethodDefUser("Original",MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static);
                method.Body=new CilBody();method.Body.Variables.Add(new Local(module.CorLibTypes.Int32));method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());type.Methods.Add(method);
            }
            module.Write(original);
        }
        using(var module=ModuleDefMD.Load(original))
        {
            var type=module.GetTypes().Single(t=>t.Name=="First");
            var late=new MethodDefUser("LateAdapter",MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static);
            late.Body=new CilBody();late.Body.Instructions.Add(OpCodes.Ret.ToInstruction());type.Methods.Add(late);
            module.Write(pointed,new ModuleWriterOptions(module){MetadataOptions={Flags=MetadataFlags.PreserveAll}});
        }
        NameRecoveryPass.Process(pointed,output,Path.Combine(directory,"map.json"));
        string pdb=Path.ChangeExtension(output,".pdb");
        using(var symbols=MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb)))
            if(symbols.GetMetadataReader().LocalVariables.Count!=2)throw new Exception("Portable symbols lost generated local names.");
        if(PortablePdbCompatibility.Normalize(output,pdb)!=0)throw new Exception("Portable symbol normalization is not idempotent.");
        string renamedDirectory=Path.Combine(directory,"published");Directory.CreateDirectory(renamedDirectory);
        string renamed=Path.Combine(renamedDirectory,"arbitrary-output-name.dll"),renamedPdb=Path.ChangeExtension(renamed,".pdb");
        File.Copy(output,renamed);File.Copy(pdb,renamedPdb);PortablePdbCompatibility.PublishSidecar(renamed,renamedPdb);
        if(Directory.GetFiles(renamedDirectory,"*.pdb").Length!=1)throw new Exception("Publication created a recovered-name PDB alias.");
        using var pe=new PEReader(File.OpenRead(renamed));
        if(!pe.TryOpenAssociatedPortablePdb(renamed,path=>File.Exists(path)?File.OpenRead(path):null,out var provider,out _))
            throw new Exception("Renamed output lost its CodeView-associated portable symbols.");
        provider!.Dispose();
        pe.Dispose();
        string unicode=Path.Combine(renamedDirectory,new string('長',40)+"_unpacked.dll"),unicodePdb=Path.ChangeExtension(unicode,".pdb");
        File.Copy(output,unicode);File.Copy(pdb,unicodePdb);File.Copy(pdb,Path.Combine(renamedDirectory,Path.GetFileName(pdb)));
        byte[] metadata;using(var source=new PEReader(File.OpenRead(output)))metadata=source.GetMetadata().GetContent().ToArray();
        PortablePdbCompatibility.PublishSidecar(unicode,unicodePdb);byte[] published=File.ReadAllBytes(unicode);
        PortablePdbCompatibility.PublishSidecar(unicode,unicodePdb);
        if(!Enumerable.SequenceEqual(published,File.ReadAllBytes(unicode))||File.Exists(Path.Combine(renamedDirectory,Path.GetFileName(pdb))))throw new Exception("Sidecar binding is not idempotent or retained its obsolete recovered alias.");
        using(var publishedPe=new PEReader(File.OpenRead(unicode)))
        {
            if(!metadata.SequenceEqual(publishedPe.GetMetadata().GetContent()))throw new Exception("PDB filename rebinding altered assembly metadata.");
            if(!publishedPe.TryOpenAssociatedPortablePdb(unicode,path=>File.Exists(path)?File.OpenRead(path):null,out var unicodeProvider,out string? associated)||associated!=unicodePdb)throw new Exception("Long Unicode output lost its single named PDB.");
            unicodeProvider!.Dispose();
        }
        Console.WriteLine("PASS: MethodPtr symbols remain valid; arbitrary/long Unicode output names bind one PDB without recovered aliases, metadata edits or repeated growth.");
    }
}
