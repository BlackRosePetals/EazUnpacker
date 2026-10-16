using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Architecture.InlineOperands;

namespace EazyDevirt.Core.Architecture;

/// <summary>Derives wire formats from the sample's reader switch tables instead of version-specific enums.</summary>
internal static class VMFormatDiscovery
{
    internal static (int Static, int Generic) MethodFlags(TypeDefinition runtime)
    {
        var record=runtime.DeclaringModule!.GetAllTypes().FirstOrDefault(t=>t.Fields.Count==6 &&
            t.Fields.Count(f=>f.Signature?.FieldType.FullName=="System.Byte")==1 &&
            t.Fields.Count(f=>f.Signature?.FieldType.FullName=="System.String")==1 &&
            !t.Fields.Any(f=>f.Signature?.FieldType.FullName=="System.Int32"));
        if(record==null)throw new NotSupportedException("Cannot discover VM method operand flags.");
        var masks=new Dictionary<MethodDefinition,int>();
        foreach(var getter in record.Methods.Where(m=>m.Signature?.ReturnType.FullName=="System.Boolean" && m.CilMethodBody!=null)) {
            var ins=getter.CilMethodBody!.Instructions;
            for(int i=1;i<ins.Count;i++)if(ins[i].OpCode.Code==CilCode.And && ins[i-1].IsLdcI4())masks[getter]=ins[i-1].GetLdcI4Constant();
        }
        foreach(var method in runtime.Methods.Where(m=>m.CilMethodBody!=null)) {
            var ins=method.CilMethodBody!.Instructions;
            for(int i=0;i<ins.Count-1;i++) {
                if(ins[i].Operand is not MethodDefinition getter || !masks.TryGetValue(getter,out int mask))continue;
                if(ins[i+1].Operand is IMethodDescriptor helper && helper.Signature?.ReturnType.FullName=="System.Reflection.BindingFlags") {
                    var generic=masks.Where(p=>p.Key!=getter).Select(p=>p.Value).Distinct().ToArray();
                    if(generic.Length!=1)throw new NotSupportedException("Ambiguous VM generic flag.");
                    Console.WriteLine($"VM method flags: static={mask}, generic={generic[0]}");return(mask,generic[0]);
                }
            }
        }
        throw new NotSupportedException("Cannot discover VM static flag usage.");
    }
    internal static byte TokenTag(TypeDefinition runtime)
    {
        foreach(var parser in runtime.Methods.Where(m=>m.Parameters.Count==1 && m.Parameters[0].ParameterType.FullName=="System.Int32" && m.CilMethodBody!=null))
        {
            var record=parser.Signature?.ReturnType.ToTypeDefOrRef()?.Resolve();
            if(record?.Fields.Count!=3 || !record.Fields.Any(f=>f.Signature?.FieldType.FullName=="System.Byte") ||
                !record.Fields.Any(f=>f.Signature?.FieldType.FullName=="System.Int32"))continue;
            var ins=parser.CilMethodBody!.Instructions;
            for(int i=0;i<ins.Count-1;i++) {
                if(ins[i].Operand is not MethodDefinition getter || getter.DeclaringType!=record || getter.Signature?.ReturnType.FullName!="System.Byte" || getter.Parameters.Count!=0)continue;
                int at=i+1;byte tag=0;
                if(ins[at].IsLdcI4()) {tag=checked((byte)ins[at].GetLdcI4Constant());at++;}
                if(ins[at].Operand is not ICilLabel target)continue;
                int fall=at+1,jump=ins.GetIndexByOffset(target.Offset);
                bool readsToken(int start) {
                    var seen=new HashSet<int>();
                    while(start>=0 && start<ins.Count && seen.Add(start)) {
                        var item=ins[start];
                        if(item.Operand is MethodDefinition setter && setter.DeclaringType==record && setter.Parameters.Count==1 && setter.Parameters[0].ParameterType.FullName=="System.Int32")return true;
                        if(item.OpCode.Code is CilCode.Br or CilCode.Br_S && item.Operand is ICilLabel label)start=ins.GetIndexByOffset(label.Offset);
                        else if(item.OpCode.FlowControl is CilFlowControl.ConditionalBranch or CilFlowControl.Return)break;
                        else start++;
                    }
                    return false;
                }
                byte? found=ins[at].OpCode.Code switch {
                    CilCode.Bne_Un or CilCode.Bne_Un_S when readsToken(fall) => tag,
                    CilCode.Beq or CilCode.Beq_S when readsToken(jump) => tag,
                    CilCode.Brtrue or CilCode.Brtrue_S when readsToken(fall) => (byte)0,
                    CilCode.Brfalse or CilCode.Brfalse_S when readsToken(jump) => (byte)0,
                    CilCode.Brtrue or CilCode.Brtrue_S when readsToken(jump) => (byte)1,
                    CilCode.Brfalse or CilCode.Brfalse_S when readsToken(fall) => (byte)1,
                    _ => null
                };
                if(found.HasValue) {Console.WriteLine("VM token tag: "+found.Value);return found.Value;}
            }
        }
        throw new NotSupportedException("Cannot derive VM metadata token discriminator.");
    }
    internal static Dictionary<int, CilOperandType> OperandTypes(TypeDefinition runtime)
    {
        var parser = runtime.Methods.FirstOrDefault(m => m.Parameters.Count == 2 &&
            m.Parameters[1].ParameterType.FullName == "System.Byte" && m.CilMethodBody?.Instructions.Any(i =>
                i.OpCode.Code == CilCode.Switch && i.Operand is IList<ICilLabel> labels && labels.Count == 13) == true)
            ?? throw new NotSupportedException("Cannot discover VM operand reader.");
        var instructions = parser.CilMethodBody!.Instructions;
        var targets = (IList<ICilLabel>)instructions.First(i => i.OpCode.Code == CilCode.Switch).Operand!;
        string readerType = parser.Parameters[0].ParameterType.FullName;
        var result = new Dictionary<int, CilOperandType>();
        for (int index = 0; index < targets.Count; index++)
        {
            var block = UntilReturn(instructions, targets[index]);
            string? primitive = block.Select(i => i.Operand).OfType<IMethodDescriptor>().FirstOrDefault(m =>
                m.DeclaringType?.FullName == readerType && m.Signature?.ReturnsValue == true)?.Signature!.ReturnType.FullName;
            result[index] = primitive switch {
                "System.Int32" => block.Any(i => i.OpCode.Code == CilCode.Newarr) ? CilOperandType.InlineSwitch : CilOperandType.InlineI,
                "System.UInt32" => CilOperandType.InlineBrTarget,
                "System.UInt16" => CilOperandType.InlineVar,
                "System.Byte" => CilOperandType.ShortInlineVar,
                "System.SByte" => CilOperandType.ShortInlineI,
                "System.Int64" => CilOperandType.InlineI8,
                "System.Single" => CilOperandType.ShortInlineR,
                "System.Double" => CilOperandType.InlineR,
                null when block.Any(i => i.OpCode.Code == CilCode.Ldnull) => CilOperandType.InlineNone,
                _ => throw new NotSupportedException("Unsupported VM wire primitive: " + primitive)
            };
        }
        Console.WriteLine("VM operand layout: " + string.Join(", ", result.Select(p => p.Key + "=" + p.Value)));
        return result;
    }

    internal static Dictionary<byte, VMInlineOperandType> InlineTypes(TypeDefinition runtime)
    {
        foreach (var parser in runtime.Methods.Where(m => m.Parameters.Count == 1 && m.CilMethodBody != null))
        {
            var instructions = parser.CilMethodBody!.Instructions;
            var dispatch = instructions.FirstOrDefault(i => i.OpCode.Code == CilCode.Switch &&
                i.Operand is IList<ICilLabel> labels && labels.Count == 5);
            if (dispatch == null) continue;
            var result = new Dictionary<byte, VMInlineOperandType>();
            var targets = (IList<ICilLabel>)dispatch.Operand!;
            for (byte index = 0; index < targets.Count; index++)
            {
                var type = UntilReturn(instructions, targets[index]).Where(i => i.OpCode.Code == CilCode.Newobj)
                    .Select(i => i.Operand).OfType<IMethodDescriptor>().FirstOrDefault()?.DeclaringType?.Resolve();
                if (type == null) break;
                var fields = type.Fields.Where(f => !f.IsStatic).Select(f => f.Signature!.FieldType.FullName).ToArray();
                VMInlineOperandType? kind = fields.Length == 2 && fields.All(f => f == "System.Int32") ? VMInlineOperandType.EazCall
                    : fields.Length == 1 && fields[0] == "System.String" ? VMInlineOperandType.UserString
                    : fields.Contains("System.Byte") && fields.Contains("System.String") ? VMInlineOperandType.Method
                    : fields.Count(f => f == "System.Boolean") == 2 && fields.Contains("System.String") ? VMInlineOperandType.Type
                    : fields.Count(f => f == "System.Boolean") == 1 && fields.Contains("System.String") ? VMInlineOperandType.Field : null;
                if (!kind.HasValue) break;
                result[index] = kind.Value;
            }
            if (result.Count == 5 && result.Values.Distinct().Count() == 5)
            {
                Console.WriteLine("VM inline layout: " + string.Join(", ", result.Select(p => p.Key + "=" + p.Value)));
                return result;
            }
        }
        throw new NotSupportedException("Cannot derive VM inline operand tags.");
    }

    private static List<CilInstruction> UntilReturn(CilInstructionCollection instructions, ICilLabel label)
    {
        var result = new List<CilInstruction>();
        int start = instructions.GetIndexByOffset(label.Offset);
        if (start < 0) throw new InvalidDataException("Invalid VM reader branch.");
        for (int i = start; i < instructions.Count; i++) {
            result.Add(instructions[i]);
            if (instructions[i].OpCode.Code == CilCode.Ret) break;
        }
        return result;
    }
}
