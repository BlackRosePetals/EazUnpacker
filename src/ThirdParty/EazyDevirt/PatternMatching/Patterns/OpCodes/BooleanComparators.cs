using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions.Interfaces;
using EazyDevirt.Core.Architecture;

namespace EazyDevirt.PatternMatching.Patterns.OpCodes;

internal abstract record BooleanComparator : IOpCodePattern
{
    protected abstract IPattern Inner { get; }
    public override IList<CilOpCode> Pattern=>new List<CilOpCode>{CilOpCodes.Call,CilOpCodes.Ldc_I4_0,CilOpCodes.Cgt_Un,CilOpCodes.Newobj,CilOpCodes.Callvirt,CilOpCodes.Ret};
    public override bool MatchEntireBody=>false;
    public override bool Verify(VMOpCode opcode,int index=0)=>opcode.SerializedDelegateMethod.CilMethodBody!.Instructions[index].Operand is MethodDefinition helper
        && helper.IsStatic && helper.Signature?.ReturnType.FullName=="System.Boolean" && helper.Signature.ParameterTypes.Count==2
        && PatternMatcher.MatchesPattern(Inner,helper);
}
internal record BooleanCgt: BooleanComparator {protected override IPattern Inner=>new CgtInnerPattern(); public override CilOpCode? CilOpCode=>CilOpCodes.Cgt;}
internal record BooleanCgtUnsigned: BooleanComparator {protected override IPattern Inner=>new Cgt_UnInnerPattern(); public override CilOpCode? CilOpCode=>CilOpCodes.Cgt_Un;}
internal record BooleanClt: BooleanComparator {protected override IPattern Inner=>new CltInnerPattern(); public override CilOpCode? CilOpCode=>CilOpCodes.Clt;}
internal record BooleanCltUnsigned: BooleanComparator {protected override IPattern Inner=>new Clt_UnInnerPattern(); public override CilOpCode? CilOpCode=>CilOpCodes.Clt_Un;}
internal record BooleanEqual: BooleanComparator {protected override IPattern Inner=>new CeqInnerPattern(); public override CilOpCode? CilOpCode=>CilOpCodes.Ceq;}
