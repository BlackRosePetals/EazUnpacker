using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions.Interfaces;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.PatternMatching.Patterns.OpCodes;

/// <summary>Recognizes the factored Boolean materialization used by older runtimes.</summary>
internal abstract record SharedBooleanComparator : IOpCodePattern
{
    protected abstract IPattern Inner { get; }
    public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Call, CilOpCodes.Brtrue_S, CilOpCodes.Ldc_I4_0 };
    public override bool MatchEntireBody => false;

    public override bool Verify(VMOpCode opcode, int index = 0) =>
        VerifyMethod(opcode.SerializedDelegateMethod, index);

    internal bool VerifyMethod(MethodDefinition method, int index)
    {
        var instructions = method.CilMethodBody!.Instructions;
        if (instructions[index].Operand is not MethodDefinition helper || !helper.IsStatic ||
            helper.Signature?.ReturnType.FullName != "System.Boolean" || helper.Parameters.Count != 2 ||
            !PatternMatcher.MatchesPattern(Inner, helper) ||
            instructions[index + 1].Operand is not ICilLabel trueLabel)
            return false;
        int trueIndex = instructions.GetIndexByOffset(trueLabel.Offset);
        int consumerIndex = index + 3;
        if (trueIndex < 0 || trueIndex + 1 >= instructions.Count || consumerIndex + 2 >= instructions.Count ||
            !instructions[trueIndex].IsLdcI4() || instructions[trueIndex].GetLdcI4Constant() != 1 ||
            instructions[trueIndex + 1].OpCode.Code is not (CilCode.Br or CilCode.Br_S) ||
            instructions[trueIndex + 1].Operand is not ICilLabel shared || shared.Offset != instructions[consumerIndex].Offset)
            return false;
        // Both arms must feed the same Int32 operand constructor/setter and VM push.
        var consumer = instructions[consumerIndex];
        if (consumer.OpCode.Code is not (CilCode.Newobj or CilCode.Callvirt or CilCode.Call) ||
            consumer.Operand is not IMethodDescriptor consume || consume.Signature?.ParameterTypes.Count != 1 ||
            consume.Signature.ParameterTypes[0].FullName != "System.Int32" ||
            instructions[consumerIndex + 1].Operand is not IMethodDescriptor push ||
            instructions[consumerIndex + 1].OpCode.Code is not (CilCode.Call or CilCode.Callvirt) ||
            push.Signature?.ReturnType.FullName != "System.Void" || push.Signature.ParameterTypes.Count != 1 ||
            push.DeclaringType != method.DeclaringType || instructions[consumerIndex + 2].OpCode.Code != CilCode.Ret)
            return false;
        return consume.DeclaringType?.Resolve()?.BaseType?.FullName == push.Signature.ParameterTypes[0].FullName;
    }
}

internal record SharedCgt : SharedBooleanComparator { protected override IPattern Inner => new CgtInnerPattern(); public override CilOpCode? CilOpCode => CilOpCodes.Cgt; }
internal record SharedCgtUnsigned : SharedBooleanComparator { protected override IPattern Inner => new Cgt_UnInnerPattern(); public override CilOpCode? CilOpCode => CilOpCodes.Cgt_Un; }
internal record SharedClt : SharedBooleanComparator { protected override IPattern Inner => new CltInnerPattern(); public override CilOpCode? CilOpCode => CilOpCodes.Clt; }
internal record SharedCltUnsigned : SharedBooleanComparator { protected override IPattern Inner => new Clt_UnInnerPattern(); public override CilOpCode? CilOpCode => CilOpCodes.Clt_Un; }
internal record SharedEqual : SharedBooleanComparator { protected override IPattern Inner => new CeqInnerPattern(); public override CilOpCode? CilOpCode => CilOpCodes.Ceq; }

/// <summary>Floating conversion whose enum arm falls through to the shared VM push.</summary>
internal abstract record SharedFloatingConversion : IOpCodePattern
{
    protected abstract CilOpCode Conversion { get; }
    public override CilOpCode? CilOpCode => Conversion;
    public override bool MatchEntireBody => false;
    public override bool InterchangeLdlocOpCodes => true;
    public override bool InterchangeStlocOpCodes => true;
    public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Ldloc_0, CilOpCodes.Castclass,
        CilOpCodes.Callvirt, CilOpCodes.Call, CilOpCodes.Conv_R_Un, Conversion, CilOpCodes.Stloc_2, CilOpCodes.Ldarg_0 };
    public override bool Verify(VMOpCode opcode, int index)
    {
        var instructions = opcode.SerializedDelegateMethod.CilMethodBody!.Instructions;
        return instructions[index + 3].Operand is IMethodDescriptor convert &&
            convert.FullName == "System.UInt64 System.Convert::ToUInt64(System.Object)" &&
            instructions.Count(i => i.OpCode.Code == CilCode.Conv_R_Un) == 1 &&
            instructions.Skip(index + 8).Any(i => i.Operand is IMethodDescriptor setter &&
                setter.Signature?.ReturnType.FullName == "System.Void" &&
                setter.Signature.ParameterTypes.Count == 1 && setter.Signature.ParameterTypes[0].FullName == "System.Double");
    }
}
internal record SharedConvR4 : SharedFloatingConversion { protected override CilOpCode Conversion => CilOpCodes.Conv_R4; }
internal record SharedConvR8 : SharedFloatingConversion { protected override CilOpCode Conversion => CilOpCodes.Conv_R8; }

/// <summary>Follows the switch bounds-check target instead of assuming adjacent basic blocks.</summary>
internal record SplitSwitch : IOpCodePattern
{
    public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Ldlen, CilOpCodes.Conv_I4, CilOpCodes.Conv_I8, CilOpCodes.Blt_S, CilOpCodes.Ret };
    public override CilOpCode? CilOpCode => CilOpCodes.Switch;
    public override bool MatchEntireBody => false;
    public override bool Verify(VMOpCode opcode, int index = 0) => VerifyMethod(opcode.SerializedDelegateMethod, index);

    internal bool VerifyMethod(MethodDefinition method, int index)
    {
        var instructions = method.CilMethodBody!.Instructions;
        if (index == 0 || !instructions[index - 1].IsLdloc() || instructions[index + 3].Operand is not ICilLabel target)
            return false;
        int at = instructions.GetIndexByOffset(target.Offset);
        if (at < 0 || at + 8 >= instructions.Count || !instructions[at].IsLdloc() || !instructions[at + 1].IsLdloc() ||
            instructions[index - 1].GetLocalVariable(method.CilMethodBody.LocalVariables) != instructions[at].GetLocalVariable(method.CilMethodBody.LocalVariables) ||
            instructions[at + 2].OpCode.Code != CilCode.Ldelem || instructions[at + 2].Operand is not ITypeDefOrRef element ||
            instructions[at + 3].Operand is not IMethodDescriptor getter || getter.DeclaringType?.FullName != element.FullName ||
            getter.Signature?.ReturnType.FullName != "System.Int32" || !instructions[at + 4].IsStloc() ||
            instructions[at + 5].OpCode.Code != CilCode.Ldarg_0 || !instructions[at + 6].IsLdloc() ||
            instructions[at + 4].GetLocalVariable(method.CilMethodBody.LocalVariables) != instructions[at + 6].GetLocalVariable(method.CilMethodBody.LocalVariables) ||
            instructions[at + 7].Operand is not MethodDefinition setter || setter.DeclaringType != method.DeclaringType ||
            setter.Parameters.Count != 1 || setter.Parameters[0].ParameterType.FullName != "System.UInt32" ||
            instructions[at + 8].OpCode.Code != CilCode.Ret)
            return false;
        return PatternMatcher.MatchesPattern(new SetBranchIndexPattern(), setter);
    }
}
