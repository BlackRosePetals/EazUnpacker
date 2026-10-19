using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;

namespace Unpacker;

// Only compiler builder forwarding stubs are canonicalized. No user async body,
// scheduling policy, cancellation or exception handler is changed.
internal static class AsyncRecoveryPass
{
    internal static int Process(ModuleDef module,string report)
    {
        var changes=new List<object>();
        var all=module.GetTypes().SelectMany(t=>t.Methods).ToArray();
        var methods=all.GroupBy(m=>m.FullName).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single());
        var states=new HashSet<TypeDef>();
        foreach(var owner in all)
        foreach(var attribute in owner.CustomAttributes.Where(a=>a.TypeFullName=="System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
        {
            if(attribute.ConstructorArguments.Count!=1 || attribute.ConstructorArguments[0].Value is not TypeSig sig)continue;
            var state=module.GetTypes().SingleOrDefault(t=>t.FullName==sig.FullName);
            if(state!=null && state.Interfaces.Any(i=>i.Interface.FullName=="System.Runtime.CompilerServices.IAsyncStateMachine"))states.Add(state);
        }
        foreach(var caller in all.Where(m=>m.HasBody))
        {
            var il=caller.Body.Instructions;
            var boundaries=new HashSet<Instruction>(il.SelectMany(i=>i.Operand is Instruction target?new[]{target}:i.Operand as IList<Instruction>??Array.Empty<Instruction>()));
            foreach(var eh in caller.Body.ExceptionHandlers)
                foreach(var point in new[]{eh.TryStart,eh.TryEnd,eh.HandlerStart,eh.HandlerEnd,eh.FilterStart})if(point!=null)boundaries.Add(point);
            for(int i=0;i<il.Count;i++)
            {
                if(il[i].OpCode.Code!=Code.Call || il[i].Operand is not IMethod reference || !methods.TryGetValue(reference.FullName,out var stub) || !stub.IsPrivate || !stub.IsStatic
                    || stub.ReturnType.ElementType!=ElementType.Void || stub.HasGenericParameters
                    || !stub.HasBody || stub.Body.ExceptionHandlers.Count!=0 || stub.Body.Variables.Count!=0)continue;
                var body=stub.Body.Instructions.Where(x=>x.OpCode.Code!=Code.Nop).ToArray();
                if(stub.MethodSig.Params.Count==1 && stub.MethodSig.Params[0] is ByRefSig
                    && body.Length==5 && Arg(body[0],0) && body[1].OpCode.Code==Code.Ldflda && Arg(body[2],0)
                    && BuilderCall(body[3],"Start",states,out var start) && body[4].OpCode.Code==Code.Ret
                    && i>=3 && i+1<il.Count && il[i+1].OpCode.Code==Code.Pop
                    && il[i-2].OpCode.Code==Code.Ldflda && SameField(il[i-2].Operand,body[1].Operand)
                    && SameLoad(il[i-3],il[i-1]) && !new[]{il[i-2],il[i-1],il[i],il[i+1]}.Any(boundaries.Contains))
                {
                    il[i].Operand=start;il[i+1].OpCode=OpCodes.Nop;il[i+1].Operand=null;
                    changes.Add(new{Caller=caller.FullName,Stub=stub.FullName,Kind="builder.Start",Evidence="Exact self-builder ref forwarding; redundant stack pop removed"});
                }
                else if(stub.MethodSig.Params.Count==2 && stub.MethodSig.Params.All(p=>p is ByRefSig)
                    && caller.Body.MaxStack<ushort.MaxValue
                    && body.Length==6 && Arg(body[0],1) && body[1].OpCode.Code==Code.Ldflda && Arg(body[2],0) && Arg(body[3],1)
                    && BuilderCall(body[4],"AwaitUnsafeOnCompleted",states,out var awaitCall) && body[5].OpCode.Code==Code.Ret
                    && i>=2 && IsRefLoad(il[i-2]) && IsRefLoad(il[i-1]) && !boundaries.Contains(il[i-1]) && !boundaries.Contains(il[i]))
                {
                    var field=body[1].Operand;
                    var stateLoad=il[i-1];
                    // Insert before the first argument, preserving all existing labels.
                    var awaiterLoad=il[i-2].Clone();
                    il[i-2].OpCode=stateLoad.OpCode;il[i-2].Operand=stateLoad.Operand;
                    il.Insert(i-1,Instruction.Create(OpCodes.Ldflda,(IField)field));
                    il.Insert(i,awaiterLoad);i+=2;
                    il[i].Operand=awaitCall;
                    // Inlining this forwarding stub exposes the builder reference
                    // on the caller's evaluation stack: its peak can grow by one.
                    // Keep the serialized bound valid even when a preceding flow
                    // pass intentionally preserves max-stack for complex CFGs.
                    caller.Body.MaxStack++;
                    changes.Add(new{Caller=caller.FullName,Stub=stub.FullName,Kind="builder.AwaitUnsafeOnCompleted",Evidence="Exact ref-only builder forwarding; argument evaluation is side-effect free"});
                }
            }
        }
        foreach(var state in states)
        {
            if(!state.CustomAttributes.Any(a=>a.TypeFullName=="System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
            {
                var ctor=new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void),
                    new TypeRefUser(module,"System.Runtime.CompilerServices","CompilerGeneratedAttribute",module.CorLibTypes.AssemblyRef));
                state.CustomAttributes.Add(new CustomAttribute(ctor));
                changes.Add(new{Caller=state.FullName,Kind="compiler-generated metadata",Evidence="AsyncStateMachineAttribute + IAsyncStateMachine implementation"});
            }
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(changes,Formatting.Indented));
        return changes.Count;
    }
    private static bool BuilderCall(Instruction instruction,string name,HashSet<TypeDef> states,out IMethod? called)
    {
        called=instruction.Operand as IMethod;
        return instruction.OpCode.Code==Code.Call && called is MethodSpec spec && spec.Name==name
            && spec.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.Async",StringComparison.Ordinal)
            && spec.DeclaringType.FullName.Contains("MethodBuilder")
            && spec.GenericInstMethodSig.GenericArguments.Any(a=>states.Any(s=>s.FullName==a.FullName));
    }
    private static bool Arg(Instruction instruction,int index)=>instruction.OpCode.Code==(index==0?Code.Ldarg_0:Code.Ldarg_1);
    private static bool SameField(object a,object b)=>a is IField x && b is IField y && x.FullName==y.FullName;
    private static bool IsRefLoad(Instruction i)=>i.OpCode.Code is Code.Ldloca or Code.Ldloca_S or Code.Ldarg_0;
    private static bool SameLoad(Instruction a,Instruction b)=>IsRefLoad(a) && a.OpCode==b.OpCode && Equals(a.Operand,b.Operand);
}
