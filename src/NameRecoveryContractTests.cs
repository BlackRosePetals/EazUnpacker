using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MethodAttributes=dnlib.DotNet.MethodAttributes;
using TypeAttributes=dnlib.DotNet.TypeAttributes;

namespace Unpacker;

internal static class NameRecoveryContractTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        using var module=new ModuleDefUser("PropertyContracts.dll");
        new AssemblyDefUser("PropertyContracts"+Guid.NewGuid().ToString("N"),new Version(1,0)).Modules.Add(module);
        var contract=new TypeDefUser("Fixture","SetterContract",null)
            {Attributes=TypeAttributes.Public|TypeAttributes.Interface|TypeAttributes.Abstract};
        module.Types.Add(contract);
        var setContract=new MethodDefUser("#=setter",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.Int32),
            MethodAttributes.Public|MethodAttributes.Abstract|MethodAttributes.Virtual|MethodAttributes.NewSlot);
        contract.Methods.Add(setContract);
        contract.Properties.Add(new PropertyDefUser("#=property",PropertySig.CreateInstance(module.CorLibTypes.Int32)){SetMethod=setContract});
        var baseType=new TypeDefUser("Fixture","PropertyBase",module.CorLibTypes.Object.TypeDefOrRef)
            {Attributes=TypeAttributes.Public|TypeAttributes.Abstract};
        module.Types.Add(baseType);
        baseType.Interfaces.Add(new InterfaceImplUser(contract));
        var getter=new MethodDefUser("#=getter",MethodSig.CreateInstance(module.CorLibTypes.Int32),
            MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot){Body=new CilBody()};
        getter.Body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(37));getter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
        var setter=new MethodDefUser("#=setter",setContract.MethodSig.Clone(),
            MethodAttributes.Public|MethodAttributes.Abstract|MethodAttributes.Virtual|MethodAttributes.NewSlot);
        baseType.Methods.Add(getter);baseType.Methods.Add(setter);
        baseType.Properties.Add(new PropertyDefUser("#=property",PropertySig.CreateInstance(module.CorLibTypes.Int32)){GetMethod=getter,SetMethod=setter});
        var explicitContract=new TypeDefUser("Fixture","ExplicitGetter",null)
            {Attributes=TypeAttributes.Public|TypeAttributes.Interface|TypeAttributes.Abstract};
        module.Types.Add(explicitContract);baseType.Interfaces.Add(new InterfaceImplUser(explicitContract));
        var declaration=new MethodDefUser("#=getter",getter.MethodSig.Clone(),
            MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot|MethodAttributes.Abstract);
        explicitContract.Methods.Add(declaration);
        explicitContract.Properties.Add(new PropertyDefUser("#=explicitProperty",PropertySig.CreateInstance(module.CorLibTypes.Int32)){GetMethod=declaration});
        var explicitGetter=new MethodDefUser("#=explicitGetter",getter.MethodSig.Clone(),
            MethodAttributes.Private|MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot){Body=new CilBody()};
        explicitGetter.Body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());explicitGetter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
        baseType.Methods.Add(explicitGetter);explicitGetter.Overrides.Add(new MethodOverride(explicitGetter,declaration));
        var derived=new TypeDefUser("Fixture","PropertyDerived",baseType){Attributes=TypeAttributes.Public};
        module.Types.Add(derived);
        var overrideSetter=new MethodDefUser("#=setter",setContract.MethodSig.Clone(),MethodAttributes.Public|MethodAttributes.Virtual)
            {Body=new CilBody()};
        overrideSetter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());derived.Methods.Add(overrideSetter);
        var parentContract=new TypeDefUser("Fixture","ParentGetter",null)
            {Attributes=TypeAttributes.Public|TypeAttributes.Interface|TypeAttributes.Abstract};
        module.Types.Add(parentContract);
        var inheritedGetter=new MethodDefUser("#=getter",getter.MethodSig.Clone(),
            MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot|MethodAttributes.Abstract);
        parentContract.Methods.Add(inheritedGetter);
        parentContract.Properties.Add(new PropertyDefUser("#=parentRow",PropertySig.CreateInstance(module.CorLibTypes.Int32)){GetMethod=inheritedGetter});
        var childContract=new TypeDefUser("Fixture","ChildGetter",null)
            {Attributes=TypeAttributes.Public|TypeAttributes.Interface|TypeAttributes.Abstract};
        module.Types.Add(childContract);childContract.Interfaces.Add(new InterfaceImplUser(parentContract));
        var inheritor=new TypeDefUser("Fixture","InheritedImplementation",derived){Attributes=TypeAttributes.Public};
        module.Types.Add(inheritor);inheritor.Interfaces.Add(new InterfaceImplUser(childContract));
        // Deliberately omit the derived property row. The accessor restoration
        // pass must create it without splitting the setter's dispatch contract.
        string input=Path.Combine(root,"property-contract-input.dll"),output=Path.Combine(root,"property-contract-output.dll");
        module.Write(input);
        using(var before=ModuleDefMD.Load(input))
        {
            uint contractToken=before.Types.Single(t=>t.Name=="SetterContract").MDToken.Raw;
            uint derivedToken=before.Types.Single(t=>t.Name=="PropertyDerived").MDToken.Raw;
            uint inheritedToken=before.Types.Single(t=>t.Name=="InheritedImplementation").MDToken.Raw;
            uint parentToken=before.Types.Single(t=>t.Name=="ParentGetter").MDToken.Raw;
            void CheckInherited(Assembly assembly)
            {
                var inheritedType=assembly.ManifestModule.ResolveType((int)inheritedToken);
                var parent=assembly.ManifestModule.ResolveType((int)parentToken);
                var map=inheritedType.GetInterfaceMap(parent);
                if(map.TargetMethods.Length!=1 || map.TargetMethods[0].DeclaringType?.Name!="PropertyBase")
                    throw new Exception("Inherited method no longer implements its parent-interface contract.");
            }
            AssertDispatch(Assembly.Load(File.ReadAllBytes(input)),contractToken,derivedToken);
            CheckInherited(Assembly.Load(File.ReadAllBytes(input)));
            NameRecoveryPass.Process(input,output,Path.Combine(root,"property-contract-names.json"));
            AssertDispatch(Assembly.Load(File.ReadAllBytes(output)),contractToken,derivedToken);
            CheckInherited(Assembly.Load(File.ReadAllBytes(output)));
        }
        using var recovered=ModuleDefMD.Load(output);
        var names=recovered.GetTypes().SelectMany(t=>t.Properties).Select(p=>p.Name.String).Distinct().ToArray();
        if(names.Length!=2 || recovered.Types.Single(t=>t.Name=="PropertyDerived").Properties.Count!=1)
            throw new Exception("Partial property accessors lost their shared naming contract.");
    }

    private static void AssertDispatch(Assembly assembly,uint interfaceToken,uint typeToken)
    {
        var contract=assembly.ManifestModule.ResolveType((int)interfaceToken);
        var derived=assembly.ManifestModule.ResolveType((int)typeToken);
        var map=derived.GetInterfaceMap(contract);
        if(map.TargetMethods.Length!=1 || map.TargetMethods[0].DeclaringType!=derived || map.TargetMethods[0].IsAbstract)
            throw new Exception("Recovered setter no longer implements its implicit interface/base contract.");
    }
}
