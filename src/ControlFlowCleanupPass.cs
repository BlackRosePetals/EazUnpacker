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

// Conservative integer expression folding, block-local constant propagation,
// switch-dispatch edge specialization, jump-chain threading and stack-neutral
// garbage removal. No target code is executed. Unknown/throwing/architecture-
// dependent expressions and all reachable side effects stay intact.
internal static class ControlFlowCleanupPass
{
    internal static int Process(string input,string output,string report)
    {
        using var module=ModuleDefMD.Load(input);
        var stats=Clean(module);
        File.WriteAllText(report,JsonConvert.SerializeObject(new{
            FoldedBranches=stats.FoldedBranches,RemovedInstructions=stats.RemovedInstructions,
            Methods=stats.Methods,
            Preserved="Method/type definitions, metadata tokens, exception-region semantics, reflection-visible members and every reachable side effect.",
            Limits="Only provable integer constants, side-effect-free dispatchers, redundant same-region jumps/nops and pure discarded integer/null expressions are simplified; arbitrary predicates, address-exposed locals, floating point, checked/native conversions and VM recovery are not inferred."
        },Formatting.Indented));
        if(stats.FoldedBranches+stats.RemovedInstructions==0)return 0;
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
            // All edits are transactional per method, including switch arrays
            // and exception boundaries. Unexpected metadata must never leave a
            // partially rewritten body in the output.
            var body=method.Body;
            var original=body.Instructions.ToArray();
            var opcodes=original.Select(i=>i.OpCode).ToArray();
            var operands=original.Select(i=>i.Operand is IList<Instruction> table?(object)table.ToArray():i.Operand).ToArray();
            var handlers=body.ExceptionHandlers.Select(h=>(Handler:h,h.TryStart,h.TryEnd,h.HandlerStart,h.HandlerEnd,h.FilterStart)).ToArray();
            ushort maxStack=body.MaxStack;bool keepMaxStack=body.KeepOldMaxStack;
            try
            {
                long before=original.Sum(i=>i.GetSize());int folded=0;
                for(int round=0;round<16;round++)
                {
                    int count=body.Instructions.Count;int changes=SimplifyRound(method);folded+=changes;
                    if(changes==0&&count==body.Instructions.Count)break;
                }
                int removed=original.Length-body.Instructions.Count;
                // Retargeting can grow one edge while another still uses the
                // short encoding. Other pipeline stages can also have grown an
                // otherwise unchanged method, so normalize every body's widths.
                body.SimplifyBranches();body.OptimizeBranches();body.UpdateInstructionOffsets();
                // A loaded body's stored maximum is an authoritative upper
                // bound even when this pass makes no change. The second cleanup
                // after save/reload must not send valid obfuscated IL back to
                // dnlib's less capable stack-flow calculator.
                if(maxStack>0)body.KeepOldMaxStack=true;
                if(folded==0&&removed==0)continue;
                if(body.MaxStack==0)body.MaxStack=(ushort)Math.Min(ushort.MaxValue,Math.Max(1,body.Instructions.Count));
                body.KeepOldMaxStack=true;
                long after=body.Instructions.Sum(i=>i.GetSize());
                stats.FoldedBranches+=folded;stats.RemovedInstructions+=removed;
                stats.CodeBytesBefore+=before;stats.CodeBytesAfter+=after;
                stats.Methods.Add(new{Method="0x"+method.MDToken.Raw.ToString("X8"),Name=method.FullName,
                    FoldedBranches=folded,RemovedInstructions=removed,CodeBytesBefore=before,CodeBytesAfter=after,
                    ExceptionHandlers=body.ExceptionHandlers.Count});
            }
            catch(Exception ex)
            {
                body.Instructions.Clear();
                for(int i=0;i<original.Length;i++)
                {original[i].OpCode=opcodes[i];original[i].Operand=operands[i];body.Instructions.Add(original[i]);}
                foreach(var h in handlers)
                {
                    h.Handler.TryStart=h.TryStart;h.Handler.TryEnd=h.TryEnd;
                    h.Handler.HandlerStart=h.HandlerStart;h.Handler.HandlerEnd=h.HandlerEnd;h.Handler.FilterStart=h.FilterStart;
                }
                body.MaxStack=maxStack;body.KeepOldMaxStack=keepMaxStack||maxStack>0;body.UpdateInstructionOffsets();
                stats.Methods.Add(new{Method="0x"+method.MDToken.Raw.ToString("X8"),Name=method.FullName,Status="skipped",Detail=ex.Message});
            }
        }
        return stats;
    }

    private static int SimplifyRound(MethodDef method)
    {
        var body=method.Body;var ins=body.Instructions;if(ins.Count<2)return 0;
        int trivial=RemoveUnusedLocalStores(body)+RemoveTrivialInstructions(body);
        var context=new FlowContext(body);
        var removed=new HashSet<Instruction>();var branches=new Dictionary<Instruction,Instruction>();int folded=trivial;
        for(int i=1;i<ins.Count;i++)
        {
            if(!TryDecision(context,i,0,null,out int start,out var target))continue;
            // Entries into an expression's middle (including its consumer) can
            // carry different values. Only its first instruction may be targeted.
            bool guarded=false;
            for(int k=start;k<=i;k++)
                if(removed.Contains(ins[k])||(k>start&&context.Entries.Contains(ins[k])))
                {guarded=true;break;}
            if(guarded)continue;
            for(int k=start;k<i;k++)removed.Add(ins[k]);
            if(target==null)removed.Add(ins[i]);else branches[ins[i]]=target;
            folded++;
        }
        // Most methods have no foldable predicate. Reuse their graph instead
        // of allocating three identical graphs (including EH-region maps).
        if(removed.Count==0)
        {
            int threaded=ThreadEdges(body,context);RemoveUnreachable(body,context);return folded+threaded;
        }
        Instruction? NextLive(Instruction from)
        {
            for(int k=context.Index[from]+1;k<ins.Count;k++)if(!removed.Contains(ins[k]))return ins[k];
            return null;
        }
        Instruction? Map(Instruction? target)
        {
            if(target==null||!removed.Contains(target))return target;
            var mapped=NextLive(target);
            if(mapped==null||context.Region(target)!=context.Region(mapped))
                throw new InvalidOperationException("A fold would cross an exception-region boundary.");
            return mapped;
        }
        // Validate the complete edit before touching the instruction list. Each
        // protected component has a distinct identity: try != handler != filter.
        var operands=new Dictionary<Instruction,object>();
        foreach(var instruction in ins)
        {
            if(removed.Contains(instruction))continue;
            if(branches.TryGetValue(instruction,out var chosen))operands[instruction]=Map(chosen)!;
            else if(instruction.Operand is Instruction target)operands[instruction]=Map(target)!;
            else if(instruction.Operand is IList<Instruction> table)operands[instruction]=table.Select(t=>Map(t)!).ToArray();
        }
        var handlerPlan=body.ExceptionHandlers.Select(h=>(Handler:h,TryStart:Map(h.TryStart),TryEnd:Map(h.TryEnd),
            HandlerStart:Map(h.HandlerStart),HandlerEnd:Map(h.HandlerEnd),FilterStart:Map(h.FilterStart))).ToArray();
        foreach(var h in handlerPlan)
            if(h.TryStart==null||h.HandlerStart==null
                ||(h.TryEnd!=null&&context.Index[h.TryStart]>=context.Index[h.TryEnd])
                ||(h.HandlerEnd!=null&&context.Index[h.HandlerStart]>=context.Index[h.HandlerEnd])
                ||(h.Handler.FilterStart!=null&&(h.FilterStart==null||context.Index[h.FilterStart]>=context.Index[h.HandlerStart])))return 0;
        foreach(var item in operands)
        {
            item.Key.Operand=item.Value;
            if(branches.ContainsKey(item.Key))item.Key.OpCode=OpCodes.Br;
            else if(item.Value is Instruction target&&!ReferenceEquals(target,context.OriginalTarget(item.Key)))Widen(item.Key);
        }
        foreach(var h in handlerPlan)
        {
            h.Handler.TryStart=h.TryStart;h.Handler.TryEnd=h.TryEnd;
            h.Handler.HandlerStart=h.HandlerStart;h.Handler.HandlerEnd=h.HandlerEnd;h.Handler.FilterStart=h.FilterStart;
        }
        foreach(var instruction in removed)ins.Remove(instruction);
        var rewritten=new FlowContext(body);
        folded+=ThreadEdges(body,rewritten);RemoveUnreachable(body,rewritten);return folded;
    }

    private static int RemoveUnusedLocalStores(CilBody body)
    {
        var ins=body.Instructions;var context=new FlowContext(body);int changes=0;
        for(int i=0;i+1<ins.Count;i++)
        {
            int local=LoadLocal(ins[i]);
            if(local<0||StoreLocal(ins[i+1])!=local||context.Entries.Contains(ins[i])||context.Entries.Contains(ins[i+1])||
                context.Region(ins[i])!=context.Region(ins[i+1]))continue;
            ins[i].OpCode=OpCodes.Nop;ins[i].Operand=null;ins[i+1].OpCode=OpCodes.Nop;ins[i+1].Operand=null;changes++;
        }
        var read=new HashSet<int>();
        foreach(var instruction in ins)
        {
            int local=LoadLocal(instruction);if(local>=0)read.Add(local);
            if(instruction.OpCode.Code is Code.Ldloca or Code.Ldloca_S && instruction.Operand is Local address)read.Add(address.Index);
        }
        foreach(var instruction in ins)
        {
            int local=StoreLocal(instruction);if(local<0||read.Contains(local))continue;
            // Preserve the producer, including any call, field access, string
            // resolution or exception. Only an unobservable local write is lost.
            instruction.OpCode=OpCodes.Pop;instruction.Operand=null;changes++;
        }
        return changes;
    }

    private static int RemoveTrivialInstructions(CilBody body)
    {
        var ins=body.Instructions;var context=new FlowContext(body);
        var removed=new HashSet<Instruction>();int folded=0;
        for(int i=0;i+1<ins.Count;i++)
        {
            var instruction=ins[i];var next=ins[i+1];
            if(context.Region(instruction)!=context.Region(next))continue;
            // Retain EH anchors themselves: changing which instruction is the
            // protected component's legal entry is more than deleting padding.
            if(!context.Boundaries.Contains(instruction)
                &&(instruction.OpCode.Code==Code.Nop
                    ||(instruction.OpCode.Code is Code.Br or Code.Br_S&&ReferenceEquals(instruction.Operand,next))))
            {
                removed.Add(instruction);
                if(instruction.OpCode.Code!=Code.Nop)folded++;
                continue;
            }
            // When every successor is the fall-through, only consuming the
            // predicate matters. Keep its producer (including calls, field
            // loads and potential exceptions) and preserve the exact stack pop.
            bool boolean=instruction.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S;
            bool redundant=boolean&&ReferenceEquals(instruction.Operand,next);
            if(instruction.OpCode.Code==Code.Switch&&instruction.Operand is IList<Instruction> table)
                redundant=table.All(t=>ReferenceEquals(t,next));
            if(redundant){instruction.OpCode=OpCodes.Pop;instruction.Operand=null;folded++;}
            // A redundant relational branch consumes two operands. If its top
            // operand is a closed nonthrowing expression, discard that producer
            // and replace the comparison by exactly one pop for the other
            // operand. Do not turn two consumes into one for an unknown pair.
            if(IsRelationalBranch(instruction.OpCode.Code)&&ReferenceEquals(instruction.Operand,next))
            {
                int cursor=i-1,budget=256;
                if(!TryValue(context,ref cursor,0,null,0,ref budget,out _))continue;
                int rightStart=cursor+1;bool relationSafe=true;
                for(int k=rightStart;k<=i;k++)
                    if(context.Boundaries.Contains(ins[k])||(k>rightStart&&context.Entries.Contains(ins[k]))
                        ||context.Region(ins[k])!=context.Region(next)){relationSafe=false;break;}
                if(!relationSafe)continue;
                for(int k=rightStart;k<i;k++)removed.Add(ins[k]);
                instruction.OpCode=OpCodes.Pop;instruction.Operand=null;folded++;
                // The preceding producer still appears in this round's graph;
                // reconsider this pop only after its removal has been applied.
                continue;
            }
            if(instruction.OpCode.Code!=Code.Pop)continue;
            int start=i-1;
            if(start<0)continue;
            if(ins[start].OpCode.Code!=Code.Dup)
            {
                int cursor=start,budget=256;
                if(!TryValue(context,ref cursor,0,null,0,ref budget,out _))continue;
                start=cursor+1;
            }
            bool safe=true;
            for(int k=start;k<=i;k++)
                if(context.Boundaries.Contains(ins[k])||(k>start&&context.Entries.Contains(ins[k]))
                    ||context.Region(ins[k])!=context.Region(next)){safe=false;break;}
            if(!safe)continue;
            // dup/pop is an identity for any existing stack value. All other
            // candidates were evaluated by the same nonthrowing integer/null
            // interpreter as branch folding. In particular ldstr, ldtoken,
            // fields, calls, allocations and checked conversions are excluded.
            for(int k=start;k<=i;k++)removed.Add(ins[k]);
        }
        if(removed.Count==0)return folded;
        Instruction Map(Instruction target)
        {
            if(!removed.Contains(target))return target;
            int index=context.Index[target]+1;
            while(index<ins.Count&&removed.Contains(ins[index]))index++;
            if(index>=ins.Count||context.Region(target)!=context.Region(ins[index]))
                throw new InvalidOperationException("A padding removal would cross an exception-region boundary.");
            return ins[index];
        }
        foreach(var instruction in ins)
        {
            if(removed.Contains(instruction))continue;
            if(instruction.Operand is Instruction target)
            {
                var mapped=Map(target);
                if(!ReferenceEquals(target,mapped)){instruction.Operand=mapped;Widen(instruction);}
            }
            else if(instruction.Operand is IList<Instruction> table)
                for(int k=0;k<table.Count;k++)table[k]=Map(table[k]);
        }
        for(int i=ins.Count-1;i>=0;i--)if(removed.Contains(ins[i]))ins.RemoveAt(i);
        return folded;
    }

    private static int ThreadEdges(CilBody body,FlowContext context)
    {
        var ins=body.Instructions;int changes=0;
        Instruction Resolve(Instruction original)
        {
            // A branch from outside a try may legally enter its first
            // instruction, but not an equivalent instruction farther inside.
            // Region equality between two targets alone does not prove that
            // bypassing the protected component's entry is a valid CLR edge.
            if(context.Boundaries.Contains(original))return original;
            var current=original;var seen=new HashSet<Instruction>();
            while(seen.Add(current))
            {
                Instruction? next=null;
                if(current.OpCode.Code==Code.Nop&&context.Index[current]+1<ins.Count)next=ins[context.Index[current]+1];
                else if(current.OpCode.Code is Code.Br or Code.Br_S)next=current.Operand as Instruction;
                if(next==null||context.Region(original)!=context.Region(next))return current;
                current=next;
            }
            return original; // Preserve the entry of a pure infinite jump cycle.
        }
        foreach(var source in ins)
        {
            if(source.OpCode.Code is Code.Leave or Code.Leave_S)continue;
            if(source.Operand is Instruction target)
            {
                var replacement=Resolve(target);
                if(!ReferenceEquals(target,replacement)){source.Operand=replacement;Widen(source);changes++;}
            }
            else if(source.Operand is IList<Instruction> table)
                for(int k=0;k<table.Count;k++)
                {
                    var replacement=Resolve(table[k]);
                    if(!ReferenceEquals(table[k],replacement)){table[k]=replacement;changes++;}
                }
        }
        // Specialize each predecessor independently. Shared dispatchers can see
        // different states on different edges; their bodies must not inherit one
        // predecessor's constant. Only closed, pure stack expressions are skipped.
        foreach(var source in ins)
        {
            if(source.OpCode.Code is not (Code.Br or Code.Br_S)||source.Operand is not Instruction entry)continue;
            if(context.Region(source)!=context.Region(entry))continue;
            int entryIndex=context.Index[entry],end=entryIndex;
            while(end<ins.Count&&end-entryIndex<128&&ins[end].OpCode.FlowControl is not
                (FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw))end++;
            if(end>=ins.Count||end-entryIndex>=128)continue;
            if(!TryDecision(context,end,entryIndex,context.Index[source],out int start,out var chosen))continue;
            if(chosen==null){if(end+1>=ins.Count)continue;chosen=ins[end+1];}
            bool purePrefix=true;
            for(int k=entryIndex;k<start;k++)if(ins[k].OpCode.Code!=Code.Nop){purePrefix=false;break;}
            if(!purePrefix||ReferenceEquals(chosen,entry)||context.Region(entry)!=context.Region(chosen))continue;
            bool sameRegion=true;
            for(int k=entryIndex;k<=end;k++)if(context.Region(ins[k])!=context.Region(entry)){sameRegion=false;break;}
            if(!sameRegion)continue;
            source.Operand=chosen;Widen(source);changes++;
        }
        return changes;
    }

    private static void RemoveUnreachable(CilBody body,FlowContext context)
    {
        var ins=body.Instructions;if(ins.Count==0)return;
        var reachable=new HashSet<Instruction>();var pending=new Stack<Instruction>();
        pending.Push(ins[0]);
        // All EH endpoints are conservative roots, retaining valid boundaries
        // even for regions without an ordinary incoming control-flow edge.
        foreach(var boundary in context.Boundaries)pending.Push(boundary);
        while(pending.Count>0)
        {
            var current=pending.Pop();if(!reachable.Add(current))continue;
            int index=context.Index[current];
            if(current.Operand is Instruction target)pending.Push(target);
            else if(current.Operand is IList<Instruction> table)foreach(var item in table)pending.Push(item);
            if(current.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw)continue;
            if(index+1<ins.Count)pending.Push(ins[index+1]);
        }
        // Every target of a reachable instruction is marked. Targets referenced
        // only by other unreachable blocks can therefore be removed as well.
        for(int i=ins.Count-1;i>=0;i--)if(!reachable.Contains(ins[i]))ins.RemoveAt(i);
    }

    private sealed class FlowContext
    {
        internal readonly CilBody Body;
        internal readonly Dictionary<Instruction,int> Index=new();
        internal readonly HashSet<Instruction> Entries=new(),Boundaries=new();
        private readonly int[] blockStarts;
        private readonly HashSet<int> addressTaken=new();
        private readonly string[] regions;
        private readonly Dictionary<Instruction,Instruction> originalTargets=new();
        private readonly Dictionary<int,HashSet<int>> predecessors=new();
        internal FlowContext(CilBody body)
        {
            Body=body;var ins=body.Instructions;
            for(int i=0;i<ins.Count;i++)Index.Add(ins[i],i);
            foreach(var h in body.ExceptionHandlers)
                foreach(var boundary in new[]{h.TryStart,h.TryEnd,h.HandlerStart,h.HandlerEnd,h.FilterStart})
                    if(boundary!=null)Boundaries.Add(boundary);
            Entries.UnionWith(Boundaries);
            foreach(var instruction in ins)
            {
                if(instruction.Operand is Instruction target){Entries.Add(target);originalTargets[instruction]=target;}
                else if(instruction.Operand is IList<Instruction> table)foreach(var item in table)Entries.Add(item);
                if((instruction.OpCode.Code is Code.Ldloca or Code.Ldloca_S)&&instruction.Operand is Local local)addressTaken.Add(local.Index);
            }
            blockStarts=new int[ins.Count];int block=0;
            for(int i=0;i<ins.Count;i++)
            {
                if(Entries.Contains(ins[i])||(i>0&&ins[i-1].OpCode.FlowControl is
                    FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw))block=i;
                blockStarts[i]=block;
            }
            regions=body.ExceptionHandlers.Count==0?Array.Empty<string>():new string[ins.Count];
            for(int i=0;i<regions.Length;i++)
            {
                var parts=new List<string>();
                for(int r=0;r<body.ExceptionHandlers.Count;r++)
                {
                    var h=body.ExceptionHandlers[r];
                    if(Contains(h.TryStart,h.TryEnd,i))parts.Add(r+"T");
                    if(Contains(h.HandlerStart,h.HandlerEnd,i))parts.Add(r+"H");
                    if(h.FilterStart!=null&&Contains(h.FilterStart,h.HandlerStart,i))parts.Add(r+"F");
                }
                regions[i]=string.Join(",",parts);
            }
            void Edge(int from,int to)
            {
                int blockIndex=blockStarts[to];
                if(!predecessors.TryGetValue(blockIndex,out var sources))predecessors.Add(blockIndex,sources=new HashSet<int>());
                sources.Add(from);
            }
            for(int i=0;i<ins.Count;i++)
            {
                var instruction=ins[i];
                if(instruction.Operand is Instruction target)Edge(i,Index[target]);
                else if(instruction.Operand is IList<Instruction> table)foreach(var destination in table)Edge(i,Index[destination]);
                if(i+1<ins.Count&&blockStarts[i+1]!=blockStarts[i]&&instruction.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw))Edge(i,i+1);
            }
        }
        private bool Contains(Instruction? start,Instruction? end,int at)=>start!=null&&at>=Index[start]&&(end==null||at<Index[end]);
        internal string Region(Instruction instruction)=>regions.Length==0?string.Empty:regions[Index[instruction]];
        internal Instruction? OriginalTarget(Instruction instruction)=>originalTargets.TryGetValue(instruction,out var target)?target:null;
        internal bool LocalValue(int localIndex,int useIndex,int depth,out Integer value)
        {
            value=default;
            if(depth>32||localIndex<0||localIndex>=Body.Variables.Count||addressTaken.Contains(localIndex))return false;
            var kind=Body.Variables[localIndex].Type.ElementType;
            if(kind==ElementType.ValueType && Body.Variables[localIndex].Type.ToTypeDefOrRef()?.ResolveTypeDef() is {IsEnum:true} enumeration)
                kind=enumeration.Fields.Single(f=>!f.IsStatic&&f.Name=="value__").FieldType.ElementType;
            if(kind is not (ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8))return false;
            int block=blockStarts[useIndex],exclusive=useIndex;
            var visited=new HashSet<int>();int remaining=4096;
            while(visited.Add(block)&&visited.Count<=32)
            {
                for(int i=exclusive-1;i>=block;i--)
                {
                    if(--remaining<0)return false;
                    if(StoreLocal(Body.Instructions[i])!=localIndex)continue;
                    int cursor=i-1,budget=256;
                    if(!TryValue(this,ref cursor,blockStarts[i],null,depth+1,ref budget,out value))return false;
                    return kind is ElementType.I4 or ElementType.U4?value.Kind==IntegerKind.I4:value.Kind==IntegerKind.I8;
                }
                // Only one ordinary predecessor, same protected component, and
                // no EH entry: do not assume a value at joins or loop headers.
                var entry=Body.Instructions[block];
                if(block==0||Boundaries.Contains(entry)||!predecessors.TryGetValue(block,out var incoming)||incoming.Count!=1)return false;
                int source=incoming.Single();
                if(Region(entry)!=Region(Body.Instructions[source]))return false;
                exclusive=source+1;block=blockStarts[source];
            }
            return false;
        }
    }

    private enum IntegerKind { I4,I8,Null }
    private readonly struct Integer
    {
        internal readonly long Value;internal readonly IntegerKind Kind;
        internal Integer(long value,IntegerKind kind){Value=kind==IntegerKind.I4?unchecked((int)value):value;Kind=kind;}
        internal static Integer I4(int value)=>new(value,IntegerKind.I4);
    }

    private static bool TryDecision(FlowContext context,int branchIndex,int minimum,int? localUse,
        out int start,out Instruction? target)
    {
        start=0;target=null;var ins=context.Body.Instructions;var branch=ins[branchIndex];Code code=branch.OpCode.Code;
        bool boolean=code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S;
        bool relation=IsRelationalBranch(code);
        if(!boolean&&!relation&&code!=Code.Switch)return false;
        int cursor=branchIndex-1,budget=256;
        if(!TryValue(context,ref cursor,minimum,localUse,0,ref budget,out var right))return false;
        bool take;
        if(code==Code.Switch)
        {
            if(right.Kind!=IntegerKind.I4||branch.Operand is not IList<Instruction> table)return false;
            if(unchecked((uint)(int)right.Value)<(uint)table.Count)target=table[(int)right.Value];
            start=cursor+1;return true;
        }
        if(boolean)take=(right.Value!=0)==(code is Code.Brtrue or Code.Brtrue_S);
        else
        {
            if(!TryValue(context,ref cursor,minimum,localUse,0,ref budget,out var left)||!Compare(code,left,right,out take))return false;
        }
        if(branch.Operand is not Instruction destination)return false;
        if(take)target=destination;start=cursor+1;return true;
    }

    private static bool TryValue(FlowContext context,ref int cursor,int minimum,int? localUse,int depth,ref int budget,out Integer value)
    {
        value=default;if(depth>32||--budget<0)return false;var ins=context.Body.Instructions;
        while(cursor>=minimum&&ins[cursor].OpCode.Code==Code.Nop)cursor--;
        if(cursor<minimum)return false;
        var instruction=ins[cursor];int useIndex=cursor;cursor--;
        if(instruction.IsLdcI4()){value=Integer.I4(instruction.GetLdcI4Value());return true;}
        if(instruction.OpCode.Code==Code.Ldc_I8){value=new Integer((long)instruction.Operand,IntegerKind.I8);return true;}
        if(instruction.OpCode.Code==Code.Ldnull){value=new Integer(0,IntegerKind.Null);return true;}
        int local=LoadLocal(instruction);if(local>=0)return context.LocalValue(local,localUse??useIndex,depth+1,out value);
        Code code=instruction.OpCode.Code;
        bool unary=code is Code.Conv_I1 or Code.Conv_U1 or Code.Conv_I2 or Code.Conv_U2 or Code.Conv_I4 or Code.Conv_U4
            or Code.Conv_I8 or Code.Neg or Code.Not;
        bool binary=code is Code.Add or Code.Sub or Code.Mul or Code.And or Code.Or or Code.Xor or Code.Shl or Code.Shr or Code.Shr_Un
            or Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un or Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un;
        if(!unary&&!binary)return false;
        if(!TryValue(context,ref cursor,minimum,localUse,depth+1,ref budget,out var right))return false;
        if(unary)return Unary(code,right,out value);
        if(!TryValue(context,ref cursor,minimum,localUse,depth+1,ref budget,out var left))return false;
        return Binary(code,left,right,out value);
    }

    private static bool Unary(Code code,Integer input,out Integer value)
    {
        value=default;if(input.Kind==IntegerKind.Null)return false;long x=input.Value;
        switch(code)
        {
            case Code.Conv_I1:value=Integer.I4(unchecked((sbyte)x));return true;
            case Code.Conv_U1:value=Integer.I4(unchecked((byte)x));return true;
            case Code.Conv_I2:value=Integer.I4(unchecked((short)x));return true;
            case Code.Conv_U2:value=Integer.I4(unchecked((ushort)x));return true;
            case Code.Conv_I4:case Code.Conv_U4:value=Integer.I4(unchecked((int)x));return true;
            case Code.Conv_I8:value=new Integer(x,IntegerKind.I8);return true;
            case Code.Neg:value=new Integer(unchecked(-x),input.Kind);return true;
            case Code.Not:value=new Integer(~x,input.Kind);return true;
            default:return false;
        }
    }

    private static bool Binary(Code code,Integer left,Integer right,out Integer value)
    {
        value=default;
        if(code is Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un)
        {if(!Compare(code,left,right,out bool result))return false;value=Integer.I4(result?1:0);return true;}
        if(left.Kind==IntegerKind.Null||right.Kind==IntegerKind.Null)return false;
        bool shift=code is Code.Shl or Code.Shr or Code.Shr_Un;
        if(shift?right.Kind!=IntegerKind.I4:left.Kind!=right.Kind)return false;
        long x=left.Value,y=right.Value,resultValue;bool wide=left.Kind==IntegerKind.I8;
        switch(code)
        {
            case Code.Add:resultValue=unchecked(x+y);break;
            case Code.Sub:resultValue=unchecked(x-y);break;
            case Code.Mul:resultValue=unchecked(x*y);break;
            case Code.And:resultValue=x&y;break;
            case Code.Or:resultValue=x|y;break;
            case Code.Xor:resultValue=x^y;break;
            case Code.Shl:resultValue=wide?x<<((int)y&63):(int)x<<((int)y&31);break;
            case Code.Shr:resultValue=wide?x>>((int)y&63):(int)x>>((int)y&31);break;
            case Code.Shr_Un:resultValue=wide?unchecked((long)((ulong)x>>((int)y&63))):(uint)(int)x>>((int)y&31);break;
            case Code.Div:case Code.Rem:
                if(y==0||(y==-1&&x==(wide?long.MinValue:int.MinValue)))return false;
                resultValue=code==Code.Div?x/y:x%y;break;
            case Code.Div_Un:case Code.Rem_Un:
                if(y==0)return false;
                resultValue=wide?unchecked((long)(code==Code.Div_Un?(ulong)x/(ulong)y:(ulong)x%(ulong)y))
                    :code==Code.Div_Un?(uint)(int)x/(uint)(int)y:(uint)(int)x%(uint)(int)y;break;
            default:return false;
        }
        value=new Integer(resultValue,left.Kind);return true;
    }

    private static bool Compare(Code code,Integer left,Integer right,out bool result)
    {
        result=false;if(left.Kind!=right.Kind)return false;long x=left.Value,y=right.Value;
        bool equal=code is Code.Ceq or Code.Beq or Code.Beq_S or Code.Bne_Un or Code.Bne_Un_S;
        if(left.Kind==IntegerKind.Null&&!equal)return false;
        bool unsigned=code is Code.Cgt_Un or Code.Clt_Un or Code.Bge_Un or Code.Bge_Un_S or Code.Bgt_Un or Code.Bgt_Un_S
            or Code.Ble_Un or Code.Ble_Un_S or Code.Blt_Un or Code.Blt_Un_S;
        int order=unsigned?(left.Kind==IntegerKind.I8?((ulong)x).CompareTo((ulong)y):((uint)(int)x).CompareTo((uint)(int)y)):x.CompareTo(y);
        switch(code)
        {
            case Code.Ceq:case Code.Beq:case Code.Beq_S:result=x==y;break;
            case Code.Bne_Un:case Code.Bne_Un_S:result=x!=y;break;
            case Code.Cgt:case Code.Cgt_Un:case Code.Bgt:case Code.Bgt_S:case Code.Bgt_Un:case Code.Bgt_Un_S:result=order>0;break;
            case Code.Clt:case Code.Clt_Un:case Code.Blt:case Code.Blt_S:case Code.Blt_Un:case Code.Blt_Un_S:result=order<0;break;
            case Code.Bge:case Code.Bge_S:case Code.Bge_Un:case Code.Bge_Un_S:result=order>=0;break;
            case Code.Ble:case Code.Ble_S:case Code.Ble_Un:case Code.Ble_Un_S:result=order<=0;break;
            default:return false;
        }
        return true;
    }

    private static int LoadLocal(Instruction i)=>i.OpCode.Code switch
    {Code.Ldloc_0=>0,Code.Ldloc_1=>1,Code.Ldloc_2=>2,Code.Ldloc_3=>3,Code.Ldloc or Code.Ldloc_S=>i.Operand is Local l?l.Index:-1,_=>-1};
    private static int StoreLocal(Instruction i)=>i.OpCode.Code switch
    {Code.Stloc_0=>0,Code.Stloc_1=>1,Code.Stloc_2=>2,Code.Stloc_3=>3,Code.Stloc or Code.Stloc_S=>i.Operand is Local l?l.Index:-1,_=>-1};

    private static bool IsRelationalBranch(Code code)=>code is Code.Beq or Code.Beq_S or Code.Bne_Un or Code.Bne_Un_S or Code.Bge or Code.Bge_S
        or Code.Bge_Un or Code.Bge_Un_S or Code.Bgt or Code.Bgt_S or Code.Bgt_Un or Code.Bgt_Un_S
        or Code.Ble or Code.Ble_S or Code.Ble_Un or Code.Ble_Un_S or Code.Blt or Code.Blt_S or Code.Blt_Un or Code.Blt_Un_S;

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
}
