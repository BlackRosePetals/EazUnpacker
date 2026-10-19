using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace EazyDevirt.Core.Architecture;

internal enum VMExceptionField { Kind, CatchType, HandlerStart, TryStart, TryLength, FilterStart }

internal sealed class VMExceptionLayout
{
    public List<VMExceptionField> Fields { get; } = new();
    public Dictionary<int, CilExceptionHandlerType> Kinds { get; } = new();

    public static VMExceptionLayout Discover(TypeDefinition runtime)
    {
        var parser = runtime.Methods.FirstOrDefault(m => m.Parameters.Count == 1 && m.CilMethodBody != null &&
            m.Signature?.ReturnType.ToTypeDefOrRef()?.Resolve() is { } type &&
            type.Fields.Count(f => f.Signature?.FieldType.FullName == "System.UInt32") == 4 &&
            type.Fields.Count(f => f.Signature?.FieldType.FullName == "System.Int32") == 2 &&
            m.CilMethodBody.Instructions.Select(i => i.Operand).OfType<MethodDefinition>().Count(c =>
                c.DeclaringType == type && c.Parameters.Count == 1 && c.Signature?.ReturnType.FullName == "System.Void") == 6)
            ?? throw new NotSupportedException("Cannot discover VM exception record parser.");
        var record = parser.Signature!.ReturnType.ToTypeDefOrRef()!.Resolve()!;
        var roles = new Dictionary<FieldDefinition, VMExceptionField>();
        FieldDefinition? GetterField(CilInstruction instruction) =>
            instruction.Operand is MethodDefinition getter && getter.DeclaringType == record
                ? getter.CilMethodBody?.Instructions.FirstOrDefault(i => i.OpCode.Code == CilCode.Ldfld)?.Operand as FieldDefinition
                : null;
        var layout = new VMExceptionLayout();
        foreach (var method in runtime.Methods.Where(m => m.CilMethodBody != null))
        {
            var instructions = method.CilMethodBody!.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (GetterField(instructions[i]) is not { } field) continue;
                if (i + 2 < instructions.Count && instructions[i + 1].IsLdcI4() &&
                    instructions[i + 2].Operand is IMethodDescriptor { Signature.ReturnType.FullName: "System.Type" })
                    roles[field] = VMExceptionField.CatchType;
                if (i + 2 < instructions.Count && instructions[i + 1].IsLdloc() &&
                    GetterField(instructions[i + 2]) is { } length && i + 3 < instructions.Count &&
                    instructions[i + 3].Operand is IMethodDescriptor { Signature.ReturnType.FullName: "System.Boolean", Signature.ParameterTypes.Count: 3 })
                {
                    roles[field] = VMExceptionField.TryStart;
                    roles[length] = VMExceptionField.TryLength;
                }
                if (field.Signature!.FieldType.FullName == "System.Int32" && i + 3 < instructions.Count &&
                    instructions[i + 1].IsStloc() && instructions[i + 2].IsLdloc() &&
                    instructions[i + 3].OpCode.Code == CilCode.Switch && instructions[i + 3].Operand is IList<ICilLabel> cases)
                {
                    roles[field] = VMExceptionField.Kind;
                    for (int kind = 0; kind < cases.Count; kind++)
                    {
                        int start = instructions.GetIndexByOffset(cases[kind].Offset);
                        var block = instructions.Skip(start).TakeWhile(x => x.OpCode.Code is not (CilCode.Br or CilCode.Br_S)).ToArray();
                        var constructor = block.FirstOrDefault(x => x.OpCode.Code == CilCode.Newobj)?.Operand as IMethodDescriptor;
                        if (constructor == null) continue;
                        var offsetGetter = block.FirstOrDefault(x => GetterField(x)?.Signature?.FieldType.FullName == "System.UInt32");
                        if (offsetGetter == null) continue;
                        bool hasExceptionObject = constructor.Signature!.ParameterTypes.Count == 2;
                        bool resolvesCatchType = block.Any(x => x.Operand is IMethodDescriptor { Signature.ReturnType.FullName: "System.Type" });
                        bool conditional = block.Length > 1 && block[1].OpCode.Code is CilCode.Brfalse or CilCode.Brfalse_S;
                        layout.Kinds[kind] = resolvesCatchType ? CilExceptionHandlerType.Exception
                            : hasExceptionObject ? CilExceptionHandlerType.Filter
                            : conditional ? CilExceptionHandlerType.Fault : CilExceptionHandlerType.Finally;
                        roles[GetterField(offsetGetter)!] = hasExceptionObject && !resolvesCatchType
                            ? VMExceptionField.FilterStart : VMExceptionField.HandlerStart;
                    }
                }
            }
        }
        foreach (var setter in parser.CilMethodBody!.Instructions.Select(i => i.Operand).OfType<MethodDefinition>())
        {
            if (setter.DeclaringType != record || setter.Parameters.Count != 1) continue;
            var field = setter.CilMethodBody?.Instructions.FirstOrDefault(i => i.OpCode.Code == CilCode.Stfld)?.Operand as FieldDefinition;
            if (field == null || !roles.TryGetValue(field, out var role))
                throw new NotSupportedException("Unknown VM exception field role.");
            layout.Fields.Add(role);
        }
        if (layout.Fields.Count != 6 || layout.Fields.Distinct().Count() != 6 || layout.Kinds.Count != 4)
            throw new NotSupportedException("Incomplete VM exception layout: " + string.Join(",", layout.Fields) + "; " + string.Join(",", layout.Kinds.Select(p => p.Key + "=" + p.Value)));
        EazyDevirt.Logging.ConsoleLogger.Detail("VM exception layout: " + string.Join(", ", layout.Fields) + "; kinds: " +
            string.Join(", ", layout.Kinds.Select(p => p.Key + "=" + p.Value)));
        return layout;
    }
}
