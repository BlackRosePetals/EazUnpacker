using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace EazFixer
{
    /// <summary>Folds constant branches and lays out reachable traces, keeping exception regions intact.</summary>
    public static class ControlFlowPass
    {
        public static int Process(ModuleDef module)
        {
            int changed=0;
            foreach(var method in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody))
            {
                var body=method.Body;
                body.SimplifyBranches();
                if(body.ExceptionHandlers.Count>0) {body.OptimizeBranches();continue;}
                var ins=body.Instructions.ToArray();
                if(ins.Length==0)continue;
                var targets=new HashSet<Instruction>(ins.SelectMany(i=>i.Operand is Instruction t?new[]{t}:i.Operand as IList<Instruction> ?? Array.Empty<Instruction>()));
                foreach(var eh in body.ExceptionHandlers) {
                    if(eh.TryStart!=null)targets.Add(eh.TryStart);if(eh.TryEnd!=null)targets.Add(eh.TryEnd);
                    if(eh.HandlerStart!=null)targets.Add(eh.HandlerStart);if(eh.HandlerEnd!=null)targets.Add(eh.HandlerEnd);
                    if(eh.FilterStart!=null)targets.Add(eh.FilterStart);
                }
                for(int i=1;i<ins.Length;i++) {
                    var prev=ins[i-1];var curr=ins[i];
                    if(!prev.IsLdcI4() || targets.Contains(curr))continue;
                    if(curr.OpCode.Code==Code.Brtrue || curr.OpCode.Code==Code.Brtrue_S || curr.OpCode.Code==Code.Brfalse || curr.OpCode.Code==Code.Brfalse_S) {
                        bool take=(prev.GetLdcI4Value()!=0)==(curr.OpCode.Code==Code.Brtrue || curr.OpCode.Code==Code.Brtrue_S);
                        prev.OpCode=OpCodes.Nop;prev.Operand=null;
                        curr.OpCode=take?OpCodes.Br:OpCodes.Nop;if(!take)curr.Operand=null;changed++;
                    }
                }
                // Moving instructions across protected regions would change exception semantics.
                if(body.ExceptionHandlers.Count>0){body.OptimizeBranches();continue;}
                var next=new Dictionary<Instruction,Instruction>();
                for(int i=0;i<ins.Length-1;i++)next[ins[i]]=ins[i+1];
                var order=new List<Instruction>();var visited=new HashSet<Instruction>();var queue=new Queue<Instruction>();queue.Enqueue(ins[0]);
                while(queue.Count>0) {
                    var current=queue.Dequeue();
                    while(current!=null && visited.Add(current)) {
                        order.Add(current);
                        var flow=current.OpCode.FlowControl;
                        if(flow==FlowControl.Branch && current.Operand is Instruction jump) {current=jump;continue;}
                        if(flow==FlowControl.Return || flow==FlowControl.Throw)break;
                        if(flow==FlowControl.Cond_Branch) {
                            if(current.Operand is Instruction branch)queue.Enqueue(branch);
                            else if(current.Operand is IList<Instruction> branches)foreach(var target in branches)queue.Enqueue(target);
                        }
                        if(!next.TryGetValue(current,out var fall))break;
                        if(visited.Contains(fall)) {order.Add(Instruction.Create(OpCodes.Br,fall));break;}
                        current=fall;
                    }
                }
                for(int i=0;i<order.Count-1;i++) {
                    if(order[i].OpCode.FlowControl==FlowControl.Branch && order[i].Operand==order[i+1]) {
                        order[i].OpCode=OpCodes.Nop;order[i].Operand=null;
                    }
                }
                // Branch labels may point at nops. Retarget before removing them.
                var redirect=new Dictionary<Instruction,Instruction>();
                Instruction survivor=null;
                for(int i=order.Count-1;i>=0;i--) {
                    if(order[i].OpCode.Code==Code.Nop && survivor!=null)redirect[order[i]]=survivor;
                    else survivor=order[i];
                }
                foreach(var instruction in order) {
                    if(instruction.Operand is Instruction t && redirect.TryGetValue(t,out var replacement))instruction.Operand=replacement;
                    else if(instruction.Operand is IList<Instruction> array)for(int i=0;i<array.Count;i++)if(redirect.TryGetValue(array[i],out replacement))array[i]=replacement;
                }
                order.RemoveAll(i=>redirect.ContainsKey(i));
                var liveTargets=new HashSet<Instruction>(order.SelectMany(i=>i.Operand is Instruction t?new[]{t}:i.Operand as IList<Instruction> ?? Array.Empty<Instruction>()));
                // Harmless dispatcher sentinel values are discarded after their branch trampoline.
                for(int i=order.Count-1;i>0;i--) {
                    if(order[i].OpCode.Code==Code.Pop && order[i-1].IsLdcI4() && !liveTargets.Contains(order[i]) && !liveTargets.Contains(order[i-1])) {
                        order.RemoveAt(i);order.RemoveAt(i-1);i--;changed++;
                    }
                }
                changed+=Math.Max(0,ins.Length-order.Count);
                body.Instructions.Clear();foreach(var instruction in order)body.Instructions.Add(instruction);
                body.OptimizeBranches();
            }
            return changed;
        }
    }
}
