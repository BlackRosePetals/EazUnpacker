using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace EazyDevirt.Core.IO;

/// <summary>Preserves reflection VM access without widening the original private API.</summary>
internal static class VmAccessBridge
{
    internal static IReadOnlyList<MethodDefinition> Rewrite(MethodDefinition caller, TypeDefinition? owner = null)
    {
        owner ??= caller.DeclaringType;
        var bridges = new List<MethodDefinition>();
        foreach (var instruction in caller.CilMethodBody!.Instructions)
        {
            if (instruction.OpCode.Code != CilCode.Call || instruction.Operand is not IMethodDescriptor descriptor)
                continue;
            var target = descriptor.Resolve();
            if (target == null || !target.IsPrivate || !target.IsStatic || target.IsConstructor ||
                target.DeclaringType == owner || target.DeclaringModule != owner?.DeclaringModule ||
                target.GenericParameters.Count != 0 || target.DeclaringType!.GenericParameters.Count != 0)
                continue;
            string name = "__VmAccess_" + target.MetadataToken.ToUInt32().ToString("X8");
            var bridge = target.DeclaringType.Methods.FirstOrDefault(m => m.Name == name && m.IsAssembly && m.IsStatic &&
                m.CilMethodBody?.Instructions.Any(i => i.OpCode.Code == CilCode.Call && ReferenceEquals(i.Operand, target)) == true);
            if (bridge == null)
            {
                while (target.DeclaringType.Methods.Any(m => m.Name == name)) name += "_";
                bridge = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                    MethodSignature.CreateStatic(target.Signature!.ReturnType, target.Signature.ParameterTypes.ToArray()));
                target.DeclaringType.Methods.Add(bridge);
                bridge.CilMethodBody = new CilMethodBody();
                foreach (var parameter in bridge.Parameters)
                    bridge.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldarg, parameter));
                bridge.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Call, target));
                bridge.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
                bridge.CilMethodBody.MaxStack = bridge.CilMethodBody.ComputeMaxStack();
            }
            instruction.Operand = bridge;
            if (!bridges.Contains(bridge)) bridges.Add(bridge);
        }
        return bridges;
    }
}
