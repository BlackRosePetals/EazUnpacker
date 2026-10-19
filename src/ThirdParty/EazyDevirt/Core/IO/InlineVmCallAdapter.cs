using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.Core.IO;

/// <summary>Replaces an inlined executor call without replacing its containing business method.</summary>
internal static class InlineVmCallAdapter
{
    internal static MethodDefinition Create(VMMethod method, TypeDefinition declaringType, TypeSignature[] arguments)
    {
        if (declaringType.GenericParameters.Count != 0 || arguments.Any(type => type is PointerTypeSignature || HasOpenParameter(type) ||
            type is ByReferenceTypeSignature reference && reference.BaseType is ByReferenceTypeSignature or PointerTypeSignature))
            throw new NotSupportedException("Inlined VM adapter requires closed, managed argument signatures.");
        if (method.ExecutorInstruction?.Operand is not IMethodDescriptor executor || executor.Signature == null)
            throw new InvalidDataException("Inlined VM call has no executor instruction.");
        if (executor.Signature.ParameterTypes.Count != 3 ||
            executor.Signature.ReturnType.FullName is not ("System.Object" or "System.Void"))
            throw new NotSupportedException("Inlined VM executor has an unsupported calling convention.");
        var parameters = executor.Signature.ParameterTypes.ToList();
        if (executor.Signature.HasThis) parameters.Insert(0, executor.DeclaringType!.ToTypeSignature());
        if (parameters.LastOrDefault()?.FullName != "System.Object[]")
            throw new NotSupportedException("Inlined VM executor does not end with its argument array.");
        method.ArgumentTypes = arguments;
        method.AdapterDeclaringType = declaringType;
        return new MethodDefinition("__RecoveredVM_" + method.MethodKey.ToString("X") + "_" + method.Parent.MetadataToken.Rid + "_" + method.Parent.CilMethodBody!.Instructions.IndexOf(method.ExecutorInstruction),
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(executor.Signature.ReturnType, parameters.ToArray()));
    }

    internal static void RewriteBody(VMMethod method, CilMethodBody body, TypeSignature returnType)
    {
        if (returnType is ByReferenceTypeSignature or PointerTypeSignature || HasOpenParameter(returnType))
            throw new NotSupportedException("Inlined VM adapter requires a closed, non-byref return signature.");
        var adapter = method.RecoveredAdapter!;
        var arguments = method.ArgumentTypes!;
        if(body.Instructions.Count==0)throw new InvalidDataException("Inlined VM body is empty.");
        var original=body.Instructions.ToArray();
        var offsets=original.ToDictionary(i=>i.Offset);
        ICilLabel? Label(ICilLabel? label,bool end=false)
        {
            if(label==null)return null;
            if(offsets.TryGetValue(label.Offset,out var target))return target.CreateLabel();
            if(end&&label.Offset==body.Instructions.Size)return null;
            throw new InvalidDataException("Inlined VM label does not identify an instruction boundary.");
        }
        // Convert offset labels to instruction identities before inserting the
        // unpacking prologue or changing argument instruction sizes.
        foreach (var instruction in body.Instructions)
        {
            if (instruction.Operand is ICilLabel label)
                instruction.Operand = Label(label)!;
            else if (instruction.Operand is IList<ICilLabel> table)
                instruction.Operand = table.Select(item => Label(item)!).ToArray();
        }
        foreach(var handler in body.ExceptionHandlers)
        {
            handler.TryStart=Label(handler.TryStart);handler.TryEnd=Label(handler.TryEnd,true);
            handler.HandlerStart=Label(handler.HandlerStart);handler.HandlerEnd=Label(handler.HandlerEnd,true);
            handler.FilterStart=Label(handler.FilterStart);
        }
        var elements = arguments.Select(type => type is ByReferenceTypeSignature reference ? reference.BaseType : type).ToArray();
        var locals = elements.Select(type => new CilLocalVariable(type)).ToArray();
        foreach (var local in locals) body.LocalVariables.Add(local);
        var rewritten=new List<CilInstruction>(original.Length+16);
        foreach (var instruction in original)
        {
            rewritten.Add(instruction);
            switch (instruction.OpCode.Code)
            {
                case CilCode.Ldarg: case CilCode.Ldarg_S: case CilCode.Ldarg_0: case CilCode.Ldarg_1: case CilCode.Ldarg_2: case CilCode.Ldarg_3:
                    int index = ArgumentIndex(instruction);
                    instruction.Operand = locals[index];
                    instruction.OpCode = arguments[index] is ByReferenceTypeSignature ? CilOpCodes.Ldloca : CilOpCodes.Ldloc; break;
                case CilCode.Starg: case CilCode.Starg_S:
                    if (arguments[ArgumentIndex(instruction)] is ByReferenceTypeSignature)
                        throw new NotSupportedException("Rebinding an inlined byref argument requires address alias analysis.");
                    instruction.Operand = locals[ArgumentIndex(instruction)]; instruction.OpCode = CilOpCodes.Stloc; break;
                case CilCode.Ldarga: case CilCode.Ldarga_S:
                    if (arguments[ArgumentIndex(instruction)] is ByReferenceTypeSignature)
                        throw new NotSupportedException("Taking an inlined byref argument's address requires address alias analysis.");
                    instruction.Operand = locals[ArgumentIndex(instruction)]; instruction.OpCode = CilOpCodes.Ldloca; break;
                case CilCode.Ret:
                    CilOpCode? conversion = adapter.Signature!.ReturnsValue
                        ? returnType.FullName == "System.Void" ? CilOpCodes.Ldnull : returnType.IsValueType ? CilOpCodes.Box : null
                        : returnType.FullName == "System.Void" ? null : CilOpCodes.Pop;
                    if (conversion.HasValue)
                    {
                        instruction.OpCode = conversion.Value;
                        instruction.Operand = conversion.Value.Code == CilCode.Box ? returnType.ToTypeDefOrRef() : null;
                        rewritten.Add(new CilInstruction(CilOpCodes.Ret));
                    }
                    break;
            }
        }
        body.Instructions.Clear();foreach(var instruction in rewritten)body.Instructions.Add(instruction);
        var prologue = new List<CilInstruction>();
        int arrayIndex = adapter.Signature!.ParameterTypes.Count - 1;
        for (int i = 0; i < arguments.Length; i++)
        {
            prologue.Add(new CilInstruction(CilOpCodes.Ldarg, adapter.Parameters[arrayIndex]));
            prologue.Add(new CilInstruction(CilOpCodes.Ldc_I4, i));
            prologue.Add(new CilInstruction(CilOpCodes.Ldelem_Ref));
            prologue.Add(new CilInstruction(elements[i].IsValueType ? CilOpCodes.Unbox_Any : CilOpCodes.Castclass, elements[i].ToTypeDefOrRef()));
            prologue.Add(new CilInstruction(CilOpCodes.Stloc, locals[i]));
        }
        if (arguments.Any(type => type is ByReferenceTypeSignature))
        {
            var start = body.Instructions[0];
            var result = adapter.Signature.ReturnsValue ? new CilLocalVariable(adapter.Signature.ReturnType) : null;
            if (result != null) body.LocalVariables.Add(result);
            var epilogue = result != null ? new CilInstruction(CilOpCodes.Ldloc, result) : new CilInstruction(CilOpCodes.Ret);
            rewritten.Clear();
            foreach (var ret in body.Instructions)
            {
                rewritten.Add(ret);
                if(ret.OpCode.Code!=CilCode.Ret)continue;
                if (result != null)
                {
                    ret.OpCode = CilOpCodes.Stloc; ret.Operand = result;
                    rewritten.Add(new CilInstruction(CilOpCodes.Leave, epilogue.CreateLabel()));
                }
                else { ret.OpCode = CilOpCodes.Leave; ret.Operand = epilogue.CreateLabel(); }
            }
            body.Instructions.Clear();foreach(var instruction in rewritten)body.Instructions.Add(instruction);
            var finallyStart = new CilInstruction(CilOpCodes.Nop);
            // An original null end label denoted the old method end, not the new finally.
            foreach (var handler in body.ExceptionHandlers)
            {
                handler.TryEnd ??= finallyStart.CreateLabel();
                handler.HandlerEnd ??= finallyStart.CreateLabel();
            }
            body.Instructions.Add(finallyStart);
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] is not ByReferenceTypeSignature) continue;
                body.Instructions.Add(new CilInstruction(CilOpCodes.Ldarg, adapter.Parameters[arrayIndex]));
                body.Instructions.Add(new CilInstruction(CilOpCodes.Ldc_I4, i));
                body.Instructions.Add(new CilInstruction(CilOpCodes.Ldloc, locals[i]));
                if (elements[i].IsValueType) body.Instructions.Add(new CilInstruction(CilOpCodes.Box, elements[i].ToTypeDefOrRef()));
                body.Instructions.Add(new CilInstruction(CilOpCodes.Stelem_Ref));
            }
            body.Instructions.Add(new CilInstruction(CilOpCodes.Endfinally));
            body.Instructions.Add(epilogue);
            if (result != null) body.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
            body.ExceptionHandlers.Add(new CilExceptionHandler {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start.CreateLabel(), TryEnd = finallyStart.CreateLabel(),
                HandlerStart = finallyStart.CreateLabel(), HandlerEnd = epilogue.CreateLabel()
            });
        }
        rewritten.Clear();rewritten.AddRange(prologue);rewritten.AddRange(body.Instructions);
        body.Instructions.Clear();foreach(var instruction in rewritten)body.Instructions.Add(instruction);
        body.Instructions.CalculateOffsets();
    }

    internal static int ArgumentIndex(CilInstruction instruction) => instruction.OpCode.Code switch
    {
        CilCode.Ldarg_0 => 0, CilCode.Ldarg_1 => 1, CilCode.Ldarg_2 => 2, CilCode.Ldarg_3 => 3,
        _ when instruction.Operand is Parameter parameter => parameter.MethodSignatureIndex,
        _ => Convert.ToInt32(instruction.Operand)
    };

    private static bool HasOpenParameter(TypeSignature type) => type switch {
        GenericParameterSignature => true,
        TypeSpecificationSignature specification => HasOpenParameter(specification.BaseType),
        GenericInstanceTypeSignature generic => generic.TypeArguments.Any(HasOpenParameter),
        _ => false
    };
}
