using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions.Interfaces;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.PatternMatching.Patterns.OpCodes;

internal record CeqBooleanResult : IOpCodePattern
{
    public override IList<CilOpCode> Pattern => new[] {
        CilOpCodes.Call, CilOpCodes.Ldc_I4_0, CilOpCodes.Cgt_Un
    };
    public override CilOpCode? CilOpCode => CilOpCodes.Ceq;
    public override bool MatchEntireBody => false;
    public override bool Verify(VMOpCode opcode, int index) =>
        PatternMatcher.MatchesPattern(new CeqInnerPattern(),
            opcode.SerializedDelegateMethod.CilMethodBody!.Instructions[index].Operand as SerializedMethodDefinition);
}

internal record BleSharedResult : IOpCodePattern
{
    public override IList<CilOpCode> Pattern => new[] {
        CilOpCodes.Bne_Un_S, CilOpCodes.Ldloc_1, CilOpCodes.Ldloc_0,
        CilOpCodes.Call, CilOpCodes.Ldc_I4_0, CilOpCodes.Ceq, CilOpCodes.Stloc_2,
        CilOpCodes.Ldloc_2, CilOpCodes.Brfalse_S
    };
    public override CilOpCode? CilOpCode => CilOpCodes.Ble;
    public override bool MatchEntireBody => false;
    public override bool InterchangeLdlocOpCodes => true;
    public override bool InterchangeStlocOpCodes => true;
    public override bool Verify(VMOpCode opcode, int index)
    {
        var instructions = opcode.SerializedDelegateMethod.CilMethodBody!.Instructions;
        if (instructions[index].Operand is not ICilLabel target) return false;
        int alternative = instructions.GetIndexByOffset(target.Offset);
        return alternative >= 0 && alternative + 5 < instructions.Count &&
            PatternMatcher.MatchesPattern(new Cgt_UnInnerPattern(), instructions[index + 3].Operand as SerializedMethodDefinition) &&
            PatternMatcher.MatchesPattern(new CgtInnerPattern(), instructions[alternative + 2].Operand as SerializedMethodDefinition) &&
            instructions[alternative + 3].IsLdcI4() && instructions[alternative + 3].GetLdcI4Constant() == 0 &&
            instructions[alternative + 4].OpCode.Code == CilCode.Ceq;
    }
}

// The unordered/unsigned form uses ordered Cgt for floating operands (so
// negating it accepts NaN), and Cgt_Un for integer operands. The ordinary Ble
// form above deliberately uses the opposite pair; do not merge their signs.
internal record BleUnsignedSharedResult : BleSharedResult
{
    public override CilOpCode? CilOpCode => CilOpCodes.Ble_Un;
    public override bool Verify(VMOpCode opcode, int index)
    {
        var instructions = opcode.SerializedDelegateMethod.CilMethodBody!.Instructions;
        if (instructions[index].Operand is not ICilLabel target) return false;
        int alternative = instructions.GetIndexByOffset(target.Offset);
        return alternative >= 0 && alternative + 5 < instructions.Count &&
            PatternMatcher.MatchesPattern(new CgtInnerPattern(), instructions[index + 3].Operand as SerializedMethodDefinition) &&
            PatternMatcher.MatchesPattern(new Cgt_UnInnerPattern(), instructions[alternative + 2].Operand as SerializedMethodDefinition) &&
            instructions[alternative + 3].IsLdcI4() && instructions[alternative + 3].GetLdcI4Constant() == 0 &&
            instructions[alternative + 4].OpCode.Code == CilCode.Ceq;
    }
}
