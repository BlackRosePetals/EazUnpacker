using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions.Interfaces;

namespace EazyDevirt.PatternMatching.Patterns;

internal record PushStackPattern : IPattern
{
    public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Callvirt };
    public override bool MatchEntireBody => false;
    public override bool Verify(CilInstructionCollection instructions, int index = 0) =>
        instructions[index].Operand is IMethodDescriptor method && StackAccess.IsPush(method) &&
        method.Signature?.ParameterTypes.Count == 1 && method.Signature.ReturnType.FullName == "System.Void";
}
