using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace EazyDevirt.PatternMatching;

/// <summary>
/// Provides temporary, semantics-preserving instruction traces for VM pattern
/// analysis. Original bodies are restored before any recovered module is saved.
/// </summary>
internal sealed class RuntimePatternNormalization : IDisposable
{
    private readonly Dictionary<MethodDefinition, CilMethodBody> _originals = new();

    internal RuntimePatternNormalization(TypeDefinition runtime)
    {
        var types = new HashSet<TypeDefinition> { runtime };
        foreach (var method in runtime.Methods.Where(m => m.CilMethodBody != null))
            foreach (var descriptor in method.CilMethodBody!.Instructions.Select(i => i.Operand).OfType<IMemberDescriptor>())
                if (descriptor.DeclaringType?.Resolve() is { } type && type.DeclaringModule == runtime.DeclaringModule)
                    types.Add(type);
        try
        {
            foreach (var method in types.SelectMany(t => t.Methods))
            {
                var body = method.CilMethodBody;
                if (body == null || body.ExceptionHandlers.Count != 0 || body.Instructions.Count == 0)
                    continue; // Moving blocks across an exception boundary is not safe.
                var normalized = Normalize(body);
                _originals.Add(method, body);
                method.CilMethodBody = normalized;
            }
        }
        catch { Dispose(); throw; }
        EazyDevirt.Logging.ConsoleLogger.Detail($"VM temporary pattern normalization: {_originals.Count} exception-free helper bodies.");
    }

    internal static CilMethodBody Normalize(CilMethodBody body)
    {
        var original = body.Instructions.ToArray();
        var positions = original.Select((instruction, index) => (instruction, index))
            .ToDictionary(pair => pair.instruction, pair => pair.index);
        int Index(ICilLabel label) => positions.TryGetValue(body.Instructions.GetByOffset(label.Offset)!, out int value)
            ? value : throw new InvalidDataException("VM helper contains a non-instruction branch label.");
        var aliases = new Dictionary<CilInstruction, CilInstruction>();
        var clones = new Dictionary<CilInstruction, CilInstruction>();
        var output = new List<CilInstruction>();
        var branchTargets = new HashSet<int>(original.SelectMany(instruction => instruction.Operand switch
        {
            ICilLabel label => new[] { label.Offset },
            IList<ICilLabel> labels => labels.Select(label => label.Offset),
            _ => Enumerable.Empty<int>()
        }));
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.Count != 0)
        {
            int at = pending.Dequeue();
            var visitedBranches = new HashSet<int>();
            bool traceStart = true;
            while (at >= 0 && at < original.Length)
            {
                var instruction = original[at];
                if (instruction.IsLdcI4() && at + 2 < original.Length &&
                    !branchTargets.Contains(original[at + 1].Offset) &&
                    original[at + 1].OpCode.Code is CilCode.Brtrue or CilCode.Brtrue_S or CilCode.Brfalse or CilCode.Brfalse_S &&
                    original[at + 1].Operand is ICilLabel constantTarget)
                {
                    bool take = (instruction.GetLdcI4Constant() != 0) ==
                        (original[at + 1].OpCode.Code is CilCode.Brtrue or CilCode.Brtrue_S);
                    int target = take ? Index(constantTarget) : at + 2;
                    if (!visitedBranches.Add(at)) throw new InvalidDataException("Cyclic VM constant branch trace.");
                    aliases[instruction] = original[target];
                    aliases[original[at + 1]] = original[target];
                    at = target;
                    continue;
                }
                if (instruction.OpCode.Code is CilCode.Br or CilCode.Br_S && instruction.Operand is ICilLabel jump)
                {
                    int target = Index(jump);
                    if (!visitedBranches.Add(at))
                    {
                        // An unconditional branch cycle is observable nontermination.
                        var selfLoop = new CilInstruction(CilOpCodes.Br, instruction.CreateLabel());
                        clones[instruction] = selfLoop; output.Add(selfLoop); break;
                    }
                    aliases[instruction] = original[target];
                    at = target;
                    continue;
                }
                if (clones.ContainsKey(instruction))
                {
                    if (!traceStart) output.Add(new CilInstruction(CilOpCodes.Br, instruction.CreateLabel()));
                    break;
                }
                var clone = new CilInstruction(instruction.OpCode, instruction.Operand);
                clones.Add(instruction, clone); output.Add(clone);
                traceStart = false;
                if (instruction.Operand is ICilLabel branch) pending.Enqueue(Index(branch));
                else if (instruction.Operand is IList<ICilLabel> table)
                    foreach (var label in table) pending.Enqueue(Index(label));
                if (instruction.OpCode.FlowControl is CilFlowControl.Return or CilFlowControl.Throw)
                    break;
                at++;
            }
        }
        // Only delete a pure push/pop when no independent edge enters the pop.
        var targets = new HashSet<CilInstruction>();
        CilInstruction TargetSource(CilInstruction source)
        {
            var seen = new HashSet<CilInstruction>();
            while (!clones.ContainsKey(source) && aliases.TryGetValue(source, out var next) && seen.Add(source)) source = next;
            return source;
        }
        foreach (var instruction in output)
        {
            if (instruction.Operand is ICilLabel label) targets.Add(TargetSource(original[Index(label)]));
            else if (instruction.Operand is IList<ICilLabel> table)
                foreach (var item in table) targets.Add(TargetSource(original[Index(item)]));
        }
        for (int i = output.Count - 2; i >= 0; i--)
        {
            var push = output[i]; var pop = output[i + 1];
            if (!(push.IsLdcI4() || push.OpCode.Code is CilCode.Ldc_I8 or CilCode.Ldc_R4 or CilCode.Ldc_R8 or CilCode.Ldnull) ||
                pop.OpCode.Code != CilCode.Pop || i + 2 >= output.Count)
                continue;
            var pushSource = clones.FirstOrDefault(pair => pair.Value == push).Key;
            var popSource = clones.FirstOrDefault(pair => pair.Value == pop).Key;
            if (pushSource == null || popSource == null || targets.Contains(popSource)) continue;
            var successor = output[i + 2];
            var source = clones.FirstOrDefault(pair => pair.Value == successor).Key;
            if (source != null)
            {
                aliases[pushSource] = source; aliases[popSource] = source;
                clones.Remove(pushSource); clones.Remove(popSource);
                output.RemoveRange(i, 2);
            }
        }
        CilInstruction Resolve(CilInstruction source)
        {
            var visited = new HashSet<CilInstruction>();
            while (!clones.ContainsKey(source) && aliases.TryGetValue(source, out var next) && visited.Add(source)) source = next;
            return clones.TryGetValue(source, out var clone) ? clone
                : throw new InvalidDataException("VM helper normalization lost a branch target.");
        }
        var result = new CilMethodBody { InitializeLocals = body.InitializeLocals, MaxStack = body.MaxStack };
        foreach (var local in body.LocalVariables) result.LocalVariables.Add(new CilLocalVariable(local.VariableType));
        foreach (var instruction in output)
        {
            if (instruction.Operand is ICilLabel label) instruction.Operand = Resolve(original[Index(label)]).CreateLabel();
            else if (instruction.Operand is IList<ICilLabel> table)
                instruction.Operand = table.Select(label => Resolve(original[Index(label)]).CreateLabel()).ToArray();
            else if (instruction.Operand is CilLocalVariable local)
                instruction.Operand = result.LocalVariables[body.LocalVariables.IndexOf(local)];
            result.Instructions.Add(instruction);
        }
        result.Instructions.CalculateOffsets();
        return result;
    }

    public void Dispose()
    {
        foreach (var pair in _originals) pair.Key.CilMethodBody = pair.Value;
    }
}
