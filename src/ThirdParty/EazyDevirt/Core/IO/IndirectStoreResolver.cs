using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.Core.IO;

internal static class IndirectStoreResolver
{
    // Eazfuscator shares one handler for all indirect stores. Infer its
    // destination from the address producer, preserving reference write barriers.
    internal static void Rewrite(VMMethod method)
    {
        var boundaries = new HashSet<uint>();
        foreach (var instruction in method.Instructions)
        {
            if (instruction.OpCode.OperandType is CilOperandType.InlineBrTarget or CilOperandType.ShortInlineBrTarget)
                boundaries.Add((uint)instruction.Operand!);
            if (instruction.Operand is int[] targets)
                foreach (int target in targets) boundaries.Add(unchecked((uint)target));
        }
        foreach (var handler in method.VMExceptionHandlers)
        {
            boundaries.Add(handler.TryStart);
            boundaries.Add(handler.HandlerStart);
            if (handler.HandlerType == CilExceptionHandlerType.Filter) boundaries.Add(handler.FilterStart);
        }
        for (int i = 0; i < method.Instructions.Count; i++)
        {
            var store = method.Instructions[i];
            if (store.OpCode.Code != CilCode.Stind_I) continue;
            var producer = FindAddressProducer(method, i, boundaries);
            TypeSignature? address = AddressType(method, producer);
            var element = address switch
            {
                ByReferenceTypeSignature byRef => byRef.BaseType,
                PointerTypeSignature pointer => pointer.BaseType,
                _ => null
            };
            if (element == null)
                throw new NotSupportedException("Cannot prove indirect store destination type.");
            store.OpCode = element.FullName switch
            {
                "System.Boolean" or "System.Byte" or "System.SByte" => CilOpCodes.Stind_I1,
                "System.Char" or "System.Int16" or "System.UInt16" => CilOpCodes.Stind_I2,
                "System.Int32" or "System.UInt32" => CilOpCodes.Stind_I4,
                "System.Int64" or "System.UInt64" => CilOpCodes.Stind_I8,
                "System.Single" => CilOpCodes.Stind_R4,
                "System.Double" => CilOpCodes.Stind_R8,
                "System.IntPtr" or "System.UIntPtr" => CilOpCodes.Stind_I,
                _ when element is PointerTypeSignature => CilOpCodes.Stind_I,
                _ when element is GenericParameterSignature || element.IsValueType => CilOpCodes.Stobj,
                _ => CilOpCodes.Stind_Ref
            };
            store.Operand = store.OpCode.Code == CilCode.Stobj ? element.ToTypeDefOrRef() : null;
        }
    }

    private static CilInstruction FindAddressProducer(VMMethod method, int storeIndex, HashSet<uint> boundaries)
    {
        int depth = 1; // The address is underneath the value being stored.
        for (int i = storeIndex - 1; i >= 0; i--)
        {
            var instruction = method.Instructions[i];
            if (instruction.OpCode.FlowControl is CilFlowControl.Branch or CilFlowControl.ConditionalBranch
                or CilFlowControl.Return or CilFlowControl.Throw)
                break;
            if (instruction.OpCode.Code == CilCode.Dup)
                depth = depth < 2 ? 0 : depth - 1;
            else
            {
                int pushes = instruction.GetStackPushCount();
                if (depth < pushes) return instruction;
                depth = depth - pushes + instruction.GetStackPopCount(method.Parent.CilMethodBody!);
            }
            if (boundaries.Contains(method.VirtualOffsets[i])) break;
        }
        throw new NotSupportedException("Indirect store address crosses an unresolved control-flow join.");
    }

    private static TypeSignature? AddressType(VMMethod method, CilInstruction instruction) => instruction.OpCode.Code switch
    {
        CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1 or CilCode.Ldarg_2 or CilCode.Ldarg_3
            => instruction.GetParameter(method.Parent.Parameters).ParameterType,
        CilCode.Ldarga or CilCode.Ldarga_S
            => new ByReferenceTypeSignature(instruction.GetParameter(method.Parent.Parameters).ParameterType),
        CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3
            => instruction.GetLocalVariable(method.Locals).VariableType,
        CilCode.Ldloca or CilCode.Ldloca_S
            => new ByReferenceTypeSignature(instruction.GetLocalVariable(method.Locals).VariableType),
        CilCode.Ldflda or CilCode.Ldsflda when instruction.Operand is IFieldDescriptor field
            => new ByReferenceTypeSignature(field.Signature!.FieldType),
        CilCode.Ldelema when instruction.Operand is ITypeDefOrRef type
            => new ByReferenceTypeSignature(type.ToTypeSignature()),
        CilCode.Call or CilCode.Callvirt when instruction.Operand is IMethodDescriptor called
            => called.Signature!.ReturnType.InstantiateGenericTypes(GenericContext.FromMethod(called)),
        _ => null
    };
}
