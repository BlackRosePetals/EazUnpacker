using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Parsing;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Core.Architecture.InlineOperands;
using EazyDevirt.Devirtualization;

namespace EazyDevirt.Core.IO;

internal class Resolver
{
    public Resolver(DevirtualizationContext ctx)
    {
        Ctx = ctx;
        VMStreamReader = new VMBinaryReader(new CryptoStreamV3(Ctx.VMResolverStream, Ctx.MethodCryptoKey, true));
        Cache = new Dictionary<int, object?>();
    }
    
    private DevirtualizationContext Ctx { get; }
    
    private VMBinaryReader VMStreamReader { get; }
    
    private Dictionary<int, object?> Cache { get; }

    public ITypeDefOrRef? ResolveType(VMInlineOperand inlineOperand)
    {
        if (inlineOperand.IsToken)
            return Ctx.Module.LookupMember<ITypeDefOrRef>(inlineOperand.Token);
        return ResolveType(inlineOperand.Position);
    }
    
    public ITypeDefOrRef? ResolveType(int position)
    {
        if (Cache.TryGetValue(position, out var result))
            return (ITypeDefOrRef?)result;
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);

        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var lookup = Ctx.Module.LookupMember<ITypeDefOrRef>(inlineOperand.Token);
            Cache.Add(position, lookup);
            return lookup;
        }

        if (!inlineOperand.HasData || inlineOperand.Data is not VMTypeData data)
            throw new Exception("VM inline operand expected to have type data!");

        if (data.IsGenericParameterIndex)
        {
            Ctx.Console.Info($"VM generic parameter: name={data.Name}; method={data.GenericMethodParameterIndex}; type={data.GenericTypeParameterIndex}");
            if (data.GenericMethodParameterIndex >= 0)
            {
                var genericTypeDefOrRef = ApplyParameterShape(new GenericParameterSignature(Ctx.Module, GenericParameterType.Method, data.GenericMethodParameterIndex), data.Name).ToTypeDefOrRef();
                Cache.Add(position, genericTypeDefOrRef);
                return genericTypeDefOrRef;
            }
            
            if (data.GenericTypeParameterIndex >= 0)
            {
                var genericTypeDefOrRef = ApplyParameterShape(new GenericParameterSignature(Ctx.Module, GenericParameterType.Type, data.GenericTypeParameterIndex), data.Name).ToTypeDefOrRef();
                Cache.Add(position, genericTypeDefOrRef);
                return genericTypeDefOrRef;
            }
        }

        var typeSig = TypeNameParser.Parse(Ctx.Module, data.Name);
        if (typeSig is null)
            throw new Exception($"Failed to parse vm type {data.Name}");
        typeSig=RepairTypeScope(typeSig);

        if (data.HasGenericTypeParameters)
            typeSig = InstantiateTypeShape(typeSig, data.GenericParameters.Select(x => ResolveType(x)!.ToTypeSignature()).ToArray());

        var typeDefOrRef = typeSig.ToTypeDefOrRef();
        if (SignatureComparer.Default.Equals(typeDefOrRef.Scope?.GetAssembly(), Ctx.Module.Assembly))
        {
            var resolvedType = typeDefOrRef.Resolve();
            if (resolvedType is not null && typeDefOrRef is TypeReference)
                typeDefOrRef = resolvedType;
            else if (resolvedType is null)
                Ctx.Console.Warning($"Failed to resolve same assembly vm type {data.Name}, using its reference instead.");
        }
        else
            typeDefOrRef = Ctx.Importer.ImportType(typeDefOrRef);
        
        Cache.Add(position, typeDefOrRef);
        return typeDefOrRef;
    }

    private static TypeSignature ApplyParameterShape(TypeSignature parameter, string name)
    {
        // Reflection serializes the parameter index separately from its array/
        // reference suffix. Preserve that suffix around the generic parameter.
        string shape = name.Split(',')[0];
        if (shape.EndsWith("&", StringComparison.Ordinal))
            return new ByReferenceTypeSignature(ApplyParameterShape(parameter, shape.Substring(0, shape.Length - 1)));
        if (shape.EndsWith("[]", StringComparison.Ordinal))
            return new SzArrayTypeSignature(ApplyParameterShape(parameter, shape.Substring(0, shape.Length - 2)));
        if (shape.EndsWith("*", StringComparison.Ordinal))
            return new PointerTypeSignature(ApplyParameterShape(parameter, shape.Substring(0, shape.Length - 1)));
        return parameter;
    }

    private static TypeSignature InstantiateTypeShape(TypeSignature signature, TypeSignature[] arguments) => signature switch
    {
        ByReferenceTypeSignature reference => new ByReferenceTypeSignature(InstantiateTypeShape(reference.BaseType, arguments)),
        SzArrayTypeSignature array => new SzArrayTypeSignature(InstantiateTypeShape(array.BaseType, arguments)),
        PointerTypeSignature pointer => new PointerTypeSignature(InstantiateTypeShape(pointer.BaseType, arguments)),
        _ => signature.MakeGenericInstanceType(arguments)
    };

    private TypeSignature RepairTypeScope(TypeSignature signature)
    {
        if(signature is TypeDefOrRefSignature named)
        {
            var type=named.Type.Resolve();
            if(type==null && named.Type is TypeReference reference && SignatureComparer.Default.Equals(reference.Scope?.GetAssembly(),Ctx.Module.Assembly))
            {
                type=Ctx.Module.GetAllTypes().FirstOrDefault(t=>t.FullName==reference.FullName);
                var candidates=Ctx.Module.AssemblyReferences.Select(a=>new TypeReference(Ctx.Module,a,reference.Namespace,reference.Name))
                    .Select(r=>r.Resolve()).Where(t=>t!=null).Distinct().ToArray();
                if(type==null && candidates.Length==1)type=candidates[0];
            }
            if(type!=null)return type.DeclaringModule==Ctx.Module ? type.ToTypeSignature() : Ctx.Importer.ImportTypeSignature(type.ToTypeSignature());
        }
        else if(signature is SzArrayTypeSignature array)return new SzArrayTypeSignature(RepairTypeScope(array.BaseType));
        else if(signature is ByReferenceTypeSignature reference)return new ByReferenceTypeSignature(RepairTypeScope(reference.BaseType));
        else if(signature is GenericInstanceTypeSignature generic)
        {
            var definition = RepairTypeScope(new TypeDefOrRefSignature(generic.GenericType, generic.IsValueType));
            return new GenericInstanceTypeSignature(definition.ToTypeDefOrRef(), definition.IsValueType, generic.TypeArguments.Select(RepairTypeScope).ToArray());
        }
        return signature;
    }
    
    public IFieldDescriptor? ResolveField(int position)
    {
        if (Cache.TryGetValue(position, out var result))
            return (IFieldDescriptor?)result;

        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);

        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var lookup = Ctx.Module.LookupMember<IFieldDescriptor>(inlineOperand.Token);
            Cache.Add(position, lookup);
            return lookup;
        }

        if (!inlineOperand.HasData || inlineOperand.Data is not VMFieldData data)
            throw new Exception("VM inline operand expected to have field data!");

        var declaringType = ResolveType(data.DeclaringType.Position);
        if (declaringType is null)
        {
            Ctx.Console.Error($"Unable to resolve vm field {data.Name} declaring type!");
            Cache.Add(position, null);
            return null;
        }

        if (declaringType.Resolve() is { } declaringTypeDef)
        {
            var fieldDef = declaringTypeDef.Fields.FirstOrDefault(f => f.Name == data.Name && f.IsStatic == data.IsStatic);
            var fieldReference = fieldDef == null ? null : declaringType.CreateMemberReference(fieldDef.Name,
                new FieldSignature(Ctx.Importer.ImportTypeSignature(fieldDef.Signature!.FieldType)));
            Cache.Add(position, fieldReference);
            return fieldReference;
        }

        // we can't create our own reference since we don't know the field's type.
        // maybe it could be inferred from where it's being used, but that would require a lot of rework.
        Ctx.Console.Error($"Unable to resolve vm field {declaringType.Name}.{data.Name}");
        return null;
    }

    private IMethodDescriptor? ResolveMethod(string name, ITypeDefOrRef declaringTypeDefOrRef, MethodSignature vmMethodSig, TypeSignature[] vmParameters, GenericContext vmGenericContext)
    {
        // Methods utilizing generics need to be resolved to get 100% accuracy.
        // This is because Eazfuscator uses the actual generic type instead of the generic parameter type.
        // Example:
        // The signature we need is:
        // !0 class TestClass`1<int32>::TestMethod<string, int32>(!0, string, !!1, !!0)
        // However, the signature Eazfuscator gives us is:
        // int32 class TestClass`1<int32>::TestMethod<string, int32>(int32, string, int32, string)
        // We can't convert them into their generic parameter type because we don't know the position of the generic type in the arguments, so we could inadvertently replace the wrong argument:
        // !0 class TestClass`1<int32>::TestMethod<string, int32>(!0, !!0, !!1, string)
        // In the future, we could probably implement something with Echo to analyze devirtualized code to find the correct type positions, but for now this is the easiest way.
        // EazCall also stores by-ref element types for non-generic helpers.
        // Resolve against metadata whenever available to preserve their real signature.
        var resolvedDeclaringType = declaringTypeDefOrRef.Resolve();
        if (!vmGenericContext.IsEmpty || resolvedDeclaringType is not null)
        {
            var declaringTypeDef = resolvedDeclaringType;
            if (declaringTypeDef is null)
            {
                if (Ctx.Options.RequireDepsForGenericMethods)
                {
                    Ctx.Console.Error($"Unable to resolve generic method {name} declaring type (Assembly: {declaringTypeDefOrRef.Scope?.GetAssembly()?.FullName})!");
                    return null;
                }
                
                Ctx.Console.Warning($"Unable to resolve generic method {name} declaring type (Assembly: {declaringTypeDefOrRef.Scope?.GetAssembly()?.FullName})! Method reference may be broken.");
                
                // This is fragile and will probably break your method signatures.
            
                var methodRef = declaringTypeDefOrRef.CreateMemberReference(name, vmMethodSig);
                if (vmGenericContext.Method?.TypeArguments.Count > 0)
                    return methodRef.MakeGenericInstanceMethod(vmGenericContext.Method.TypeArguments.ToArray());
                return methodRef;
            }

            // Look for matching methods
            var matches = new List<MethodDefinition>();
            var exactMatches = new List<MethodDefinition>();
            foreach (var method in declaringTypeDef.Methods)
            {
                if (method.IsStatic != !vmMethodSig.HasThis
                    || method.Name != name
                    || method.GenericParameters.Count != (vmGenericContext.Method?.TypeArguments.Count ?? 0)) 
                    continue;
                
                if (method.Signature!.ParameterTypes.Count != vmMethodSig.ParameterTypes.Count)
                    continue;
                    
                // Found a potential match, now build a signature from the real method and compare it with the signature built from our vm data to ensure we have the correct method
    
                // Instantiate generic types for real method from vm method data
                // Instantiate the definition, including generics nested in by-ref,
                // array and generic-instance signatures. Parameters also exposes
                // the implicit receiver and is not the metadata signature list.
                var realMethodSignature = method.Signature.InstantiateGenericTypes(vmGenericContext);
                if (SignatureComparer.Default.Equals(realMethodSignature, vmMethodSig))
                    exactMatches.Add(method);
    
                // Compare instantiated real method signature with our signature built from vm method data
                // VM reflection stores a by-ref parameter's element type. Keep
                // the definition's actual by-ref signature in the emitted call.
                var comparisonSignature = realMethodSignature.HasThis
                    ? MethodSignature.CreateInstance(realMethodSignature.ReturnType, realMethodSignature.GenericParameterCount, realMethodSignature.ParameterTypes)
                    : MethodSignature.CreateStatic(realMethodSignature.ReturnType, realMethodSignature.GenericParameterCount, realMethodSignature.ParameterTypes);
                for (int i = 0; i < comparisonSignature.ParameterTypes.Count; i++)
                    if (comparisonSignature.ParameterTypes[i] is ByReferenceTypeSignature byRef &&
                        vmMethodSig.ParameterTypes[i] is not ByReferenceTypeSignature)
                        comparisonSignature.ParameterTypes[i] = byRef.BaseType;
                if (!SignatureComparer.Default.Equals(comparisonSignature, vmMethodSig))
                    continue;
                matches.Add(method);
            }
            if (exactMatches.Count != 0) matches = exactMatches;
            if (matches.Count == 1)
            {
                var reference = declaringTypeDefOrRef.CreateMemberReference(matches[0].Name, matches[0].Signature);
                return vmGenericContext.Method?.TypeArguments.Count > 0
                    ? reference.MakeGenericInstanceMethod(vmGenericContext.Method.TypeArguments.ToArray()) : reference;
            }
            
            Ctx.Console.Error($"Failed to resolve method {declaringTypeDefOrRef.FullName}::{name}: {vmMethodSig}");
            foreach (var candidate in declaringTypeDef.Methods.Where(m => m.Name == name))
                Ctx.Console.Warning($"Candidate {candidate.Signature} => {candidate.Signature!.InstantiateGenericTypes(vmGenericContext)}");
            return null;
        }
        
        return declaringTypeDefOrRef.CreateMemberReference(name, vmMethodSig);
    }
    
    public IMethodDescriptor? ResolveMethod(int position)
    {
        if (Cache.TryGetValue(position, out var result))
            return (IMethodDescriptor?)result;
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);

        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var lookup = Ctx.Module.LookupMember<IMethodDescriptor>(inlineOperand.Token);
            Cache.Add(position, lookup);
            return lookup;
        }
        
        if (!inlineOperand.HasData)
            throw new Exception("VM inline operand expected to have data!");

        if (inlineOperand.Data is not VMMethodData data)
            throw new Exception("VM inline operand expected to have method data!");

        var declaringTypeDefOrRef = ResolveType(data.DeclaringType.Position);
        if (declaringTypeDefOrRef == null)
        {
            Ctx.Console.Error($"Failed to resolve declaring type on vm method {data.Name}!");
            return null;
        }

        var returnType = ResolveType(data.ReturnType.Position);
        if (returnType == null)
        {
            Ctx.Console.Error($"Failed to resolve return type on vm method {data.Name}!");
            return null;
        }
        
        var vmGenericInstanceMethodSig = data.HasGenericParameters ? new GenericInstanceMethodSignature(data.GenericParameters.Select(x => ResolveType(x)?.ToTypeSignature()!)) : null;
        var vmGenericContext = GenericContext.FromType(declaringTypeDefOrRef).WithMethod(vmGenericInstanceMethodSig!);
        var vmParameters = data.Parameters.Select(x => ResolveType(x)?.ToTypeSignature()!).ToArray();

        var vmMethodSig = (data.IsStatic
                ? MethodSignature.CreateStatic(returnType.ToTypeSignature(),
                    vmGenericContext.Method?.TypeArguments.Count ?? 0, vmParameters)
                : MethodSignature.CreateInstance(returnType.ToTypeSignature(),
                    vmGenericContext.Method?.TypeArguments.Count ?? 0, vmParameters))
            .InstantiateGenericTypes(vmGenericContext);
        
        var method = ResolveMethod(data.Name, declaringTypeDefOrRef, vmMethodSig, vmParameters, vmGenericContext)?.ImportWith(Ctx.Importer);
        if (method is null) 
            return null;
        
        Cache.Add(position, method);
        return (IMethodDescriptor)method;
    }

    public IMemberDescriptor? ResolveToken(int position)
    {
        if (Cache.TryGetValue(position, out var result))
            return (IMemberDescriptor?)result;
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);
        
        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var member = Ctx.Module.LookupMember(inlineOperand.Token);

            Cache.Add(position, member);
            return member switch
            {
                ITypeDescriptor typeDesc => typeDesc.ToTypeSignature(),
                IFieldDescriptor fieldDesc => fieldDesc,
                IMethodDescriptor methodDesc => methodDesc,
                _ => throw new ArgumentOutOfRangeException(nameof(member), member.GetType().FullName, "Member not a type, field, or method definition.")
            };
        }

        if (!inlineOperand.HasData)
            throw new Exception("VM inline operand expected to have data!");

        return inlineOperand.Data.Type switch
        {
            VMInlineOperandType.Type => ResolveType(position),
            VMInlineOperandType.Field => ResolveField(position),
            VMInlineOperandType.Method => ResolveMethod(position),
            VMInlineOperandType.EazCall => ResolveEazCall(position),
            // VMInlineOperandType.UserString => ResolveString(position),
            _ => throw new ArgumentOutOfRangeException(nameof(inlineOperand.Data.Type), inlineOperand.Data.Type, "VM inline operand data is neither Type, Field, Method, nor EazCall!")
        };
    }

    public IMethodDescriptor? ResolveEazCall(int value)
    {
        var noGenericArgs = (value & 0x80000000) != 0; 
        var useConstrainedType  = (value & 0x40000000) != 0;
        var position = value & 0x3FFFFFFF;
        if (Cache.TryGetValue(position, out var result))
            return (IMethodDescriptor?)result;

        if (noGenericArgs)
            return ResolveEazCall(position, new GenericContext(), useConstrainedType);
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);
        
        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var lookup = Ctx.Module.LookupMember<IMethodDescriptor>(inlineOperand.Token);
            Cache.Add(position, lookup);
            return lookup;
        }
        
        if (!inlineOperand.HasData)
            throw new Exception("VM inline operand expected to have data!");

        if (inlineOperand.Data is not VMEazCallData data)
            throw new Exception("VM inline operand expected to have eazcall data!");

        return ResolveEazCall(data);
    }

    private IMethodDescriptor? ResolveEazCall(VMEazCallData eazCallData)
    {
        var useConstrainedType  = (eazCallData.EazCallValue & 0x40000000) != 0;
        var position = eazCallData.EazCallValue & -0x40000001;

        var method = ResolveMethod(eazCallData.VMMethodPosition);
        var genericContext = GenericContext.FromMethod(method!);
        
        return ResolveEazCall(position, genericContext, useConstrainedType);
    }

    private IMethodDescriptor? ResolveEazCall(int position, GenericContext vmGenericContext, bool useConstrainedType)
    {
        if(!Ctx.HeaderLayout.TypeSlotsCalibrated || !Ctx.HeaderLayout.StaticMaskCalibrated)
            throw new NotSupportedException("EazCall method header could not be calibrated.");
        if (Cache.TryGetValue(position, out var result))
            return (IMethodDescriptor?)result;
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);

        var methodInfo = new VMMethodInfo(VMStreamReader,Ctx.HeaderLayout);
        
        // i don't think anything special needs to be done for constrained
        // eaz just includes the types needed in the data anyway
        // but log it just in case
        if (useConstrainedType)
            Ctx.Console.Warning("Constrained type EazCall detected");

        var declaringTypeDefOrRef = ResolveType(methodInfo.VMDeclaringType);
        if (declaringTypeDefOrRef is null)
            throw new Exception("Failed to resolve eaz call declaring type!");
        
        var returnType = ResolveType(methodInfo.VMReturnType);
        if (returnType == null)
        {
            Ctx.Console.Error($"Failed to resolve eaz call {methodInfo.Name} return type!");
            return null;
        }
        
        var vmParameters = methodInfo.VMParameters.Select(x => ResolveType(x.VMType)?.ToTypeSignature()!).ToArray();

        var vmMethodSig = (methodInfo.IsStatic
                ? MethodSignature.CreateStatic(returnType.ToTypeSignature(),
                    vmGenericContext.Method?.TypeArguments.Count ?? 0, vmParameters)
                : MethodSignature.CreateInstance(returnType.ToTypeSignature(),
                    vmGenericContext.Method?.TypeArguments.Count ?? 0, vmParameters.Skip(1))) // instance eaz calls will contain declaring type as first parameter
            .InstantiateGenericTypes(vmGenericContext);
        
        var method = ResolveMethod(methodInfo.Name, declaringTypeDefOrRef, vmMethodSig, vmParameters, vmGenericContext);
        if (method is null)
        {        
            Ctx.Console.Error($"Failed to resolve eaz call {methodInfo.Name}!");
            return null;
        }

        Cache.Add(position, method);
        return method;
    }

    public string ResolveString(int position)
    {
        if (Cache.TryGetValue(position, out var result))
            return (string)result!;
        
        Ctx.VMResolverStream.Seek(position, SeekOrigin.Begin);
        
        var inlineOperand = new VMInlineOperand(VMStreamReader);
        if (inlineOperand.IsToken)
        {
            var lookup = Ctx.Module.LookupString(inlineOperand.Token);
            Cache.Add(position, lookup);
            return lookup;
        }

        if (!inlineOperand.HasData || inlineOperand.Data is not VMUserStringData data) 
            throw new Exception("VM inline operand expected to have string data!");
        
        Cache.Add(position, data.Value);
        return data.Value;
    }
}
