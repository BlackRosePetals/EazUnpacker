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
            TypeSignature? address;
            try { address = AddressType(method, FindAddressProducer(method, i, boundaries)); }
            catch (NotSupportedException) { address = FindAddressAcrossEdges(method, i); }
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

    private static TypeSignature FindAddressAcrossEdges(VMMethod method, int storeIndex)
    {
        var indices = method.VirtualOffsets.Select((offset, index) => (offset, index))
            .GroupBy(pair => pair.offset).ToDictionary(group => group.Key, group => group.First().index);
        var predecessors = Enumerable.Range(0, method.Instructions.Count).Select(_ => new List<int>()).ToArray();
        void Edge(uint offset, int source)
        {
            if (!indices.TryGetValue(offset, out int target))
                throw new NotSupportedException("Indirect store predecessor has no VM instruction target.");
            predecessors[target].Add(source);
        }
        for (int i = 0; i < method.Instructions.Count; i++)
        {
            var instruction = method.Instructions[i];
            if (instruction.OpCode.OperandType is CilOperandType.InlineBrTarget or CilOperandType.ShortInlineBrTarget)
                Edge((uint)instruction.Operand!, i);
            else if (instruction.Operand is int[] targets)
                foreach (int target in targets) Edge(unchecked((uint)target), i);
            if (i + 1 < method.Instructions.Count && instruction.OpCode.FlowControl is not
                (CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw))
                predecessors[i + 1].Add(i);
        }
        var pending = new Stack<(int Index, int Depth)>();
        foreach (int predecessor in predecessors[storeIndex]) pending.Push((predecessor, 1));
        var visited = new HashSet<(int, int)>();
        TypeSignature? result = null;
        while (pending.Count != 0)
        {
            var state = pending.Pop();
            if (!visited.Add(state)) continue;
            if (visited.Count > 20000 || state.Depth < 0 || state.Depth > 4096)
                throw new NotSupportedException("Indirect store address proof exceeded its bounded CFG search.");
            var instruction = method.Instructions[state.Index];
            if (instruction.OpCode.FlowControl is CilFlowControl.Return or CilFlowControl.Throw ||
                instruction.OpCode.Code is CilCode.Leave or CilCode.Leave_S or CilCode.Endfinally or CilCode.Endfilter)
                throw new NotSupportedException("Indirect store address crosses a stack-reset boundary.");
            int depth;
            if (instruction.OpCode.Code == CilCode.Dup)
                depth = state.Depth < 2 ? 0 : state.Depth - 1;
            else
            {
                int pushes = instruction.GetStackPushCount();
                if (state.Depth < pushes)
                {
                    var address = AddressType(method, instruction);
                    if (address is not (ByReferenceTypeSignature or PointerTypeSignature))
                        throw new NotSupportedException("Indirect store predecessor does not prove an address type.");
                    if (result != null && !SignatureComparer.Default.Equals(result, address))
                        throw new NotSupportedException("Indirect store incoming address types disagree.");
                    result = address;
                    continue;
                }
                depth = checked(state.Depth - pushes + instruction.GetStackPopCount(method.Parent.CilMethodBody!));
            }
            if (predecessors[state.Index].Count == 0)
                throw new NotSupportedException("Indirect store address has no proven incoming producer.");
            foreach (int predecessor in predecessors[state.Index]) pending.Push((predecessor, depth));
        }
        return result ?? throw new NotSupportedException("Indirect store address CFG has no typed producer.");
    }

    private static TypeSignature? AddressType(VMMethod method, CilInstruction instruction) => instruction.OpCode.Code switch
    {
        CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1 or CilCode.Ldarg_2 or CilCode.Ldarg_3
            => ArgumentType(method, instruction),
        CilCode.Ldarga or CilCode.Ldarga_S
            => new ByReferenceTypeSignature(ArgumentType(method, instruction)),
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

    private static TypeSignature ArgumentType(VMMethod method, CilInstruction instruction) => method.ArgumentTypes is { } arguments
        ? arguments[InlineVmCallAdapter.ArgumentIndex(instruction)]
        : instruction.GetParameter(method.Parent.Parameters).ParameterType;
}
