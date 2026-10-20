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
                    EazyDevirt.Logging.ConsoleLogger.Detail($"VM method flags: static={mask}, generic={generic[0]}");return(mask,generic[0]);
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
                if(found.HasValue) {EazyDevirt.Logging.ConsoleLogger.Detail("VM token tag: "+found.Value);return found.Value;}
            }
        }
        throw new NotSupportedException("Cannot derive VM metadata token discriminator.");
    }
    internal static Dictionary<int, CilOperandType> OperandTypes(TypeDefinition runtime)
    {
        // The discriminator can be widened to Int32 without changing the wire
        // format. Match the reader's behavior, not just its parameter width.
        var candidates = runtime.Methods.Where(m => m.Parameters.Count == 2 &&
            m.Parameters[1].ParameterType.FullName is "System.Byte" or "System.Int32" &&
            m.Signature?.ReturnsValue == true && m.CilMethodBody?.Instructions.Any(i =>
                i.OpCode.Code == CilCode.Switch && i.Operand is IList<ICilLabel> labels && labels.Count == 13) == true);
        var layouts = new List<Dictionary<int, CilOperandType>>();
        foreach (var candidate in candidates)
        {
            var layout = ReadOperandTypes(candidate);
            if (layout != null) layouts.Add(layout);
        }
        if (layouts.Count != 1)
            throw new NotSupportedException(layouts.Count == 0
                ? "Cannot discover VM operand reader."
                : "Ambiguous VM operand reader.");
        var result = layouts[0];
        EazyDevirt.Logging.ConsoleLogger.Detail("VM operand layout: " + string.Join(", ", result.Select(p => p.Key + "=" + p.Value)));
        return result;
    }

    private static Dictionary<int, CilOperandType>? ReadOperandTypes(MethodDefinition parser)
    {
        var instructions = parser.CilMethodBody!.Instructions;
        var targets = (IList<ICilLabel>)instructions.First(i => i.OpCode.Code == CilCode.Switch &&
            i.Operand is IList<ICilLabel> labels && labels.Count == 13).Operand!;
        string readerType = parser.Parameters[0].ParameterType.FullName;
        var result = new Dictionary<int, CilOperandType>();
        for (int index = 0; index < targets.Count; index++)
        {
            var block = ReachableInstructions(instructions, targets[index]);
            var primitives = block.Select(i => i.Operand).OfType<IMethodDescriptor>().Where(m =>
                m.DeclaringType?.FullName == readerType && m.Signature?.ReturnsValue == true)
                .Select(m => m.Signature!.ReturnType.FullName).Distinct().ToArray();
            if (primitives.Length > 1) return null;
            string? primitive = primitives.SingleOrDefault();
            CilOperandType? kind = primitive switch {
                "System.Int32" => block.Any(i => i.OpCode.Code == CilCode.Newarr) ? CilOperandType.InlineSwitch : CilOperandType.InlineI,
                "System.UInt32" => CilOperandType.InlineBrTarget,
                "System.UInt16" => CilOperandType.InlineVar,
                "System.Byte" => CilOperandType.ShortInlineVar,
                "System.SByte" => CilOperandType.ShortInlineI,
                "System.Int64" => CilOperandType.InlineI8,
                "System.Single" => CilOperandType.ShortInlineR,
                "System.Double" => CilOperandType.InlineR,
                null when block.Any(i => i.OpCode.Code == CilCode.Ldnull) => CilOperandType.InlineNone,
                _ => null
            };
            if (!kind.HasValue) return null;
            result[index] = kind.Value;
        }
        // Reject unrelated switch helpers rather than guessing a partial layout.
        return result.Values.Distinct().Count() == 10 ? result : null;
    }

    private static List<CilInstruction> ReachableInstructions(CilInstructionCollection instructions, ICilLabel start)
    {
        var result = new List<CilInstruction>();
        var seen = new HashSet<int>();
        var pending = new Queue<int>();
        void Enqueue(ICilLabel label)
        {
            int index = instructions.GetIndexByOffset(label.Offset);
            if (index < 0) throw new InvalidDataException("Invalid VM reader branch.");
            pending.Enqueue(index);
        }
        Enqueue(start);
        while (pending.Count != 0)
        {
            int index = pending.Dequeue();
            if (index >= instructions.Count || !seen.Add(index)) continue;
            var instruction = instructions[index];
            result.Add(instruction);
            if (instruction.Operand is ICilLabel branch) Enqueue(branch);
            else if (instruction.Operand is IList<ICilLabel> labels)
                foreach (var label in labels) Enqueue(label);
            if (instruction.OpCode.FlowControl is not (CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw))
                pending.Enqueue(index + 1);
        }
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
            VMTypeWireLayout? typeLayout = null;
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
                if (kind == VMInlineOperandType.Type)
                    typeLayout = VMTypeWireLayout.Discover(runtime, type, UntilReturn(instructions, targets[index]));
            }
            if (result.Count == 5 && result.Values.Distinct().Count() == 5)
            {
                EazyDevirt.Devirtualization.DevirtualizationContext.Instance.TypeOperandLayout = typeLayout!;
                EazyDevirt.Logging.ConsoleLogger.Detail("VM inline layout: " + string.Join(", ", result.Select(p => p.Key + "=" + p.Value)));
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
