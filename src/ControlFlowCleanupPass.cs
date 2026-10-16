using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using Newtonsoft.Json;

namespace Unpacker;

internal sealed class ControlFlowCleanupStats
{
    internal int FoldedBranches,RemovedInstructions;
    internal long CodeBytesBefore,CodeBytesAfter;
    internal readonly List<object> Methods=new();
}

// Removes literal conditional branches and the instructions that only the
// untaken side of those branches can reach. Only a branch whose condition is a
// compile-time literal (or a comparison of two literals) is folded, and only a
// statically unreachable instruction is dropped. Method/type definitions,
// metadata tokens, exception-region boundaries, reflection-visible members and
// every still-reachable side effect are retained.
internal static class ControlFlowCleanupPass
{
    internal static int Process(string input,string output,string report)
    {
        using var module=ModuleDefMD.Load(input);
        var stats=Clean(module);
        File.WriteAllText(report,JsonConvert.SerializeObject(new{
            FoldedBranches=stats.FoldedBranches,RemovedInstructions=stats.RemovedInstructions,
            Methods=stats.Methods,
            Preserved="Method/type definitions, metadata tokens, exception-region boundaries, reflection-visible members and every reachable side effect."
        },Formatting.Indented));
        if(stats.FoldedBranches+stats.RemovedInstructions==0)return 0;
        // This stage is the last writer of the recovered assembly, so it reuses
        // the same compacted metadata flags the naming stage selected and keeps
        // the recovered portable PDB (method scopes) beside the output.
        module.Write(output,new ModuleWriterOptions(module){MetadataOptions={Flags=MetadataFlags.PreserveAll
            & ~MetadataFlags.PreservePropertyRids & ~MetadataFlags.PreserveEventRids & ~MetadataFlags.PreserveParamRids}});
        string pdb=Path.ChangeExtension(input,".pdb");
        if(File.Exists(pdb))File.Copy(pdb,Path.ChangeExtension(output,".pdb"),true);
        return stats.FoldedBranches+stats.RemovedInstructions;
    }

    internal static ControlFlowCleanupStats Clean(ModuleDef module)
    {
        var stats=new ControlFlowCleanupStats();
        foreach(var method in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray())
        {
            try
            {
                var (folded,removed,beforeBytes,afterBytes)=CleanMethod(method);
                if(folded==0&&removed==0)continue;
                stats.FoldedBranches+=folded;stats.RemovedInstructions+=removed;
                stats.CodeBytesBefore+=beforeBytes;stats.CodeBytesAfter+=afterBytes;
                stats.Methods.Add(new{Method="0x"+method.MDToken.Raw.ToString("X8"),Name=method.FullName,
                    FoldedBranches=folded,RemovedInstructions=removed,CodeBytesBefore=beforeBytes,CodeBytesAfter=afterBytes,
                    ExceptionHandlers=method.Body.ExceptionHandlers.Count});
            }
            catch(Exception ex)
            {
                // Fail closed: a method we cannot reason about keeps its original body.
                stats.Methods.Add(new{Method="0x"+method.MDToken.Raw.ToString("X8"),Name=method.FullName,Status="skipped",Detail=ex.Message});
            }
        }
        return stats;
    }

    private static (int Folded,int Removed,long Before,long After) CleanMethod(MethodDef method)
    {
        var body=method.Body;
        var ins=body.Instructions;
        if(ins.Count<2)return (0,0,0,0);
        long beforeBytes=ins.Sum(i=>i.GetSize());
        var index=new Dictionary<Instruction,int>(ins.Count);
        for(int i=0;i<ins.Count;i++)index[ins[i]]=i;

        // Exception-region boundaries are reachability roots and can never be
        // removed, so protected regions keep a valid start and end instruction.
        var boundaries=new HashSet<Instruction>();
        foreach(var eh in body.ExceptionHandlers)
            foreach(var boundary in new[]{eh.TryStart,eh.TryEnd,eh.HandlerStart,eh.HandlerEnd,eh.FilterStart})
                if(boundary!=null)boundaries.Add(boundary);
        var targets=new HashSet<Instruction>();

        var removed=new HashSet<Instruction>();
        var unconditional=new Dictionary<Instruction,bool>();
        int folded=0;
        for(int i=1;i<ins.Count;i++)
        {
            var branch=ins[i];
            bool isTrue=branch.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
            bool isFalse=branch.OpCode.Code is Code.Brfalse or Code.Brfalse_S;
            if(!isTrue&&!isFalse)continue;
            if(!TryLiteralCondition(ins,i,out int start,out bool truth))continue;
            bool take=isTrue==truth;
            if(take&&branch.Operand is not Instruction)continue;
            bool guarded=false;
            int last=take?i-1:i;
            for(int k=start;k<=last;k++)if(removed.Contains(ins[k])){guarded=true;break;}
            if(guarded)continue;
            for(int k=start;k<i;k++)removed.Add(ins[k]);
            if(take)unconditional[branch]=branch.OpCode.Code is Code.Brtrue_S or Code.Brfalse_S;else removed.Add(branch);
            folded++;
        }

        Instruction? NextLive(Instruction from)
        {
            for(int k=index[from]+1;k<ins.Count;k++)if(!removed.Contains(ins[k]))return ins[k];
            return null;
        }

        // Region nesting of an instruction, used to prove that a retargeted edge
        // does not enter or leave a protected region.
        string RegionsOf(Instruction instruction)
        {
            var regions=new List<int>();int at=index[instruction];
            for(int r=0;r<body.ExceptionHandlers.Count;r++)
            {
                var handler=body.ExceptionHandlers[r];
                int tryStart=handler.TryStart==null?-1:index[handler.TryStart];
                int tryEnd=handler.TryEnd==null?ins.Count:index[handler.TryEnd];
                int handlerStart=handler.HandlerStart==null?-1:index[handler.HandlerStart];
                int handlerEnd=handler.HandlerEnd==null?ins.Count:index[handler.HandlerEnd];
                int filterStart=handler.FilterStart==null?-1:index[handler.FilterStart];
                if((tryStart>=0&&at>=tryStart&&at<tryEnd)||(filterStart>=0&&at>=filterStart&&at<handlerStart)
                    ||(handlerStart>=0&&at>=handlerStart&&at<handlerEnd))regions.Add(r);
            }
            return string.Join(",",regions);
        }

        // Every reference that pointed at a folded-away instruction follows it to
        // the next survivor. A folded literal and its branch are stack-neutral, so
        // retargeted edges keep the same stack shape. Fail closed if an edge would
        // cross a protected-region boundary, which only leave/endfinally may do.
        var retargets=new List<(Instruction Instruction, Instruction Replacement)>();
        var tables=new List<(IList<Instruction> Table, int Index, Instruction Replacement)>();
        foreach(var instruction in ins)
        {
            if(instruction.Operand is Instruction target&&removed.Contains(target))
            {
                var replacement=NextLive(target);
                // The edge already existed as instruction->target; it stays legal
                // while the replacement sits in the same regions as the target.
                if(replacement==null||RegionsOf(target)!=RegionsOf(replacement))return (0,0,0,0);
                retargets.Add((instruction,replacement));
            }
            else if(instruction.Operand is IList<Instruction> table)
                for(int k=0;k<table.Count;k++)if(removed.Contains(table[k]))
                {
                    var replacement=NextLive(table[k]);
                    if(replacement==null||RegionsOf(table[k])!=RegionsOf(replacement))return (0,0,0,0);
                    tables.Add((table,k,replacement));
                }
        }
        // Keep protected regions valid when a folded-away instruction was one of
        // their boundaries: the region simply starts or ends at the next survivor.
        var handlerPlan=new List<(ExceptionHandler Handler, Instruction? TryStart, Instruction? TryEnd, Instruction? HandlerStart, Instruction? HandlerEnd, Instruction? FilterStart)>();
        foreach(var handler in body.ExceptionHandlers)
        {
            var tryStart=handler.TryStart!=null&&removed.Contains(handler.TryStart)?NextLive(handler.TryStart):handler.TryStart;
            var tryEnd=handler.TryEnd!=null&&removed.Contains(handler.TryEnd)?NextLive(handler.TryEnd):handler.TryEnd;
            var handlerStart=handler.HandlerStart!=null&&removed.Contains(handler.HandlerStart)?NextLive(handler.HandlerStart):handler.HandlerStart;
            var handlerEnd=handler.HandlerEnd!=null&&removed.Contains(handler.HandlerEnd)?NextLive(handler.HandlerEnd):handler.HandlerEnd;
            var filterStart=handler.FilterStart!=null&&removed.Contains(handler.FilterStart)?NextLive(handler.FilterStart):handler.FilterStart;
            if(handler.TryStart!=null&&tryStart==null)return (0,0,0,0);
            if(handler.HandlerStart!=null&&handlerStart==null)return (0,0,0,0);
            if(handler.FilterStart!=null&&filterStart==null)return (0,0,0,0);
            if(tryStart!=null&&tryEnd!=null&&ReferenceEquals(tryStart,tryEnd))return (0,0,0,0);
            if(handlerStart!=null&&handlerEnd!=null&&ReferenceEquals(handlerStart,handlerEnd))return (0,0,0,0);
            handlerPlan.Add((handler,tryStart,tryEnd,handlerStart,handlerEnd,filterStart));
        }

        foreach(var item in retargets){item.Instruction.Operand=item.Replacement;Widen(item.Instruction);}
        foreach(var item in tables)item.Table[item.Index]=item.Replacement;
        foreach(var item in handlerPlan)
        {
            item.Handler.TryStart=item.TryStart;item.Handler.TryEnd=item.TryEnd;
            item.Handler.HandlerStart=item.HandlerStart;item.Handler.HandlerEnd=item.HandlerEnd;
            item.Handler.FilterStart=item.FilterStart;
        }
        boundaries.Clear();
        foreach(var handler in body.ExceptionHandlers)
            foreach(var boundary in new[]{handler.TryStart,handler.TryEnd,handler.HandlerStart,handler.HandlerEnd,handler.FilterStart})
                if(boundary!=null)boundaries.Add(boundary);
        targets.Clear();
        targets.UnionWith(boundaries);
        foreach(var instruction in ins)
        {
            if(instruction.Operand is Instruction target)targets.Add(target);
            else if(instruction.Operand is IList<Instruction> table)foreach(var item in table)targets.Add(item);
        }

        var reachable=new HashSet<Instruction>();
        var pending=new Stack<Instruction>();
        // The method entry is the first instruction that survives folding; a
        // literal at offset zero is stack-neutral and may be folded away.
        var entry=ins.FirstOrDefault(i=>!removed.Contains(i));
        if(entry!=null)pending.Push(entry);
        foreach(var boundary in boundaries)if(!removed.Contains(boundary))pending.Push(boundary);
        while(pending.Count>0)
        {
            var current=pending.Pop();
            if(current==null||removed.Contains(current)||!reachable.Add(current))continue;
            var next=NextLive(current);
            if(unconditional.ContainsKey(current))
            {
                if(current.Operand is Instruction jump)pending.Push(jump);
                continue;
            }
            switch(current.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                    if(current.Operand is Instruction branchTarget)pending.Push(branchTarget);
                    break;
                case FlowControl.Cond_Branch:
                    if(current.Operand is Instruction conditional)pending.Push(conditional);
                    else if(current.Operand is IList<Instruction> table)foreach(var item in table)pending.Push(item);
                    if(next!=null)pending.Push(next);
                    break;
                case FlowControl.Return:
                case FlowControl.Throw:
                    break;
                default:
                    if(next!=null)pending.Push(next);
                    break;
            }
        }

        int dead=0;
        foreach(var instruction in ins.ToArray())
        {
            if(reachable.Contains(instruction)||removed.Contains(instruction)||ReferenceEquals(instruction,entry))continue;
            if(boundaries.Contains(instruction)||targets.Contains(instruction))continue;
            removed.Add(instruction);dead++;
        }
        if(folded==0&&dead==0)return (0,0,0,0);

        // Preserve the original branch encoding: removal only shortens the
        // distance to the target, so a short branch stays in range. dnlib's
        // SimplifyBranches would rewrite these as long branches and grow the body.
        foreach(var pair in unconditional)pair.Key.OpCode=pair.Value?OpCodes.Br_S:OpCodes.Br;
        foreach(var instruction in removed)ins.Remove(instruction);
        if(ins.Count==0)ins.Add(OpCodes.Ret.ToInstruction());
        body.UpdateInstructionOffsets();
        // Removing instructions and fall-through edges can only lower the real
        // stack depth, so the original (already validated) maximum stays a valid
        // upper bound. dnlib's linear recomputation cannot follow the obfuscated
        // shape. A body created in memory without a written maximum uses the
        // instruction count as a safe bound instead.
        if(body.MaxStack==0)body.MaxStack=(ushort)Math.Min(ushort.MaxValue,Math.Max(1,ins.Count));
        body.KeepOldMaxStack=true;
        return (folded,dead,beforeBytes,ins.Sum(i=>i.GetSize()));
    }

    private static void Widen(Instruction instruction)
    {
        switch(instruction.OpCode.Code)
        {
            case Code.Br_S:instruction.OpCode=OpCodes.Br;break;
            case Code.Brtrue_S:instruction.OpCode=OpCodes.Brtrue;break;
            case Code.Brfalse_S:instruction.OpCode=OpCodes.Brfalse;break;
            case Code.Beq_S:instruction.OpCode=OpCodes.Beq;break;
            case Code.Bge_S:instruction.OpCode=OpCodes.Bge;break;
            case Code.Bge_Un_S:instruction.OpCode=OpCodes.Bge_Un;break;
            case Code.Bgt_S:instruction.OpCode=OpCodes.Bgt;break;
            case Code.Bgt_Un_S:instruction.OpCode=OpCodes.Bgt_Un;break;
            case Code.Ble_S:instruction.OpCode=OpCodes.Ble;break;
            case Code.Ble_Un_S:instruction.OpCode=OpCodes.Ble_Un;break;
            case Code.Blt_S:instruction.OpCode=OpCodes.Blt;break;
            case Code.Blt_Un_S:instruction.OpCode=OpCodes.Blt_Un;break;
            case Code.Bne_Un_S:instruction.OpCode=OpCodes.Bne_Un;break;
            case Code.Leave_S:instruction.OpCode=OpCodes.Leave;break;
        }
    }

    private static bool TryLiteralCondition(IList<Instruction> ins,int branchIndex,out int start,out bool truth)
    {
        start=0;truth=false;
        int cursor=branchIndex-1;
        while(cursor>=0&&IsConversion(ins[cursor].OpCode.Code))cursor--;
        if(cursor<0)return false;
        if(TryConstant(ins[cursor],out long single))
        {
            start=cursor;truth=single!=0;return true;
        }
        if(cursor<2||!IsComparison(ins[cursor].OpCode.Code))return false;
        // Walk back over each operand's conversions to its literal.
        int right=cursor-1;
        while(right>=0&&IsConversion(ins[right].OpCode.Code))right--;
        if(right<0||!TryConstant(ins[right],out long second))return false;
        int left=right-1;
        while(left>=0&&IsConversion(ins[left].OpCode.Code))left--;
        if(left<0||!TryConstant(ins[left],out long first))return false;
        bool wide=ins[left].OpCode.Code==Code.Ldc_I8||ins[right].OpCode.Code==Code.Ldc_I8;
        switch(ins[cursor].OpCode.Code)
        {
            case Code.Ceq:truth=first==second;break;
            case Code.Cgt:truth=wide?first>second:(int)first>(int)second;break;
            case Code.Cgt_Un:truth=wide?(ulong)first>(ulong)second:(uint)(int)first>(uint)(int)second;break;
            case Code.Clt:truth=wide?first<second:(int)first<(int)second;break;
            case Code.Clt_Un:truth=wide?(ulong)first<(ulong)second:(uint)(int)first<(uint)(int)second;break;
            default:return false;
        }
        start=left;
        return true;
    }

    private static bool IsConversion(Code code)=>code is Code.Conv_I or Code.Conv_I1 or Code.Conv_I2 or Code.Conv_I4 or Code.Conv_I8
        or Code.Conv_U or Code.Conv_U1 or Code.Conv_U2 or Code.Conv_U4 or Code.Conv_U8 or Code.Conv_Ovf_I or Code.Conv_Ovf_I1
        or Code.Conv_Ovf_I2 or Code.Conv_Ovf_I4 or Code.Conv_Ovf_I8 or Code.Conv_Ovf_U or Code.Conv_Ovf_U1 or Code.Conv_Ovf_U2
        or Code.Conv_Ovf_U4 or Code.Conv_Ovf_U8;

    private static bool IsComparison(Code code)=>code is Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un;

    private static bool TryConstant(Instruction instruction,out long value)
    {
        value=0;
        if(instruction.IsLdcI4()){value=instruction.GetLdcI4Value();return true;}
        switch(instruction.OpCode.Code)
        {
            case Code.Ldc_I8:value=(long)instruction.Operand;return true;
            case Code.Ldnull:value=0;return true;
            default:return false;
        }
    }
}
