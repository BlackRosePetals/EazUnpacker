using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Core.IO;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace EazyDevirt;

/// <summary>Executable regression fixtures for shared indirect-store recovery.</summary>
public static class VmRecoveryTests
{
    public static void Run()
    {
        var module = new ModuleDefinition("StoreFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("StoreFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Stores", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var cases = new (string Name, Type Type, object Value, CilCode Code)[] {
            ("Byte", typeof(byte), (byte)231, CilCode.Stind_I1),
            ("Int32", typeof(int), -123456789, CilCode.Stind_I4),
            ("Single", typeof(float), 3.25f, CilCode.Stind_R4),
            ("String", typeof(string), "reference write", CilCode.Stind_Ref),
            ("DateTime", typeof(DateTime), new DateTime(2026, 10, 5), CilCode.Stobj)
        };
        foreach (var test in cases)
        {
            var element = module.DefaultImporter.ImportType(test.Type).ToTypeSignature();
            var method = new MethodDefinition(test.Name, MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, new ByReferenceTypeSignature(element), element));
            type.Methods.Add(method);
            var vm = Build(method, new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldarg_1),
                new CilInstruction(CilOpCodes.Stind_I), new CilInstruction(CilOpCodes.Ret));
            IndirectStoreResolver.Rewrite(vm);
            if (vm.Instructions[2].OpCode.Code != test.Code) throw new Exception("Wrong indirect store: " + test.Name);
            foreach (var instruction in vm.Instructions) method.CilMethodBody!.Instructions.Add(instruction);
        }
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = Assembly.Load(bytes.ToArray()).GetType("Fixture.Stores")!;
        foreach (var test in cases)
        {
            object[] arguments = { test.Type.IsValueType ? Activator.CreateInstance(test.Type)! : null!, test.Value };
            loaded.GetMethod(test.Name)!.Invoke(null, arguments);
            if (!Equals(arguments[0], test.Value)) throw new Exception("Indirect store execution failed: " + test.Name);
        }
        var ambiguous = Build(type.Methods[0], new CilInstruction(CilOpCodes.Ldarg_0),
            new CilInstruction(CilOpCodes.Br, (uint)2), new CilInstruction(CilOpCodes.Ldarg_1),
            new CilInstruction(CilOpCodes.Stind_I), new CilInstruction(CilOpCodes.Ret));
        try { IndirectStoreResolver.Rewrite(ambiguous); throw new Exception("Accepted an unresolved store join."); }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS: byte/int/float/reference/struct indirect stores execute correctly; unresolved joins rejected.");
    }

    private static VMMethod Build(MethodDefinition method, params CilInstruction[] instructions)
    {
        method.CilMethodBody = new CilMethodBody();
        return new VMMethod(method, "", 0) { Instructions = instructions.ToList(),
            VirtualOffsets = Enumerable.Range(0, instructions.Length).Select(i => (uint)i).ToList() };
    }
}
