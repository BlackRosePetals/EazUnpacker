using System.Reflection;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using ICSharpCode.Decompiler.CSharp.Syntax;

namespace Unpacker;

internal static class AsyncSemanticFixture
{
    public static int FinallyCount;
    public static async Task<string> Evaluate(int mode,Task gate)
    {
        string trace="start";
        try {
            await gate.ConfigureAwait(false);
            trace+=";resumed";
            if(mode==1)throw new InvalidOperationException("fixture-fault");
            if(mode==2)throw new OperationCanceledException();
            return trace+=";ok";
        }
        catch(OperationCanceledException){return trace+";cancel";}
        finally {System.Threading.Interlocked.Increment(ref FinallyCount);}
    }
}

internal static class AsyncRecoveryTests
{
    internal static void Run(string directory)
    {
        CheckAst();
        using var module=ModuleDefMD.Load(Assembly.GetExecutingAssembly().Location);
        var owner=module.GetTypes().Single(t=>t.Name==nameof(AsyncSemanticFixture));
        var kickoff=owner.Methods.Single(m=>m.Name=="Evaluate");
        var state=owner.NestedTypes.Single();
        var builder=state.Fields.Single(f=>f.FieldType.FullName.Contains("AsyncTaskMethodBuilder"));
        int wrapped=0;
        foreach(var method in new[]{kickoff,state.Methods.Single(m=>m.Name=="MoveNext")})
        {
            var il=method.Body.Instructions;
            for(int i=0;i<il.Count;i++)
            {
                if(il[i].Operand is not MethodSpec call || !call.DeclaringType.FullName.Contains("AsyncTaskMethodBuilder"))continue;
                if(call.Name=="Start")
                {
                    var stub=Stub(owner,"StartForwarder",module.CorLibTypes.Void,new ByRefSig(state.ToTypeSig()));
                    Add(stub,OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldflda.ToInstruction(builder),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(call),OpCodes.Ret.ToInstruction());
                    il[i].Operand=stub;il.Insert(i+1,OpCodes.Pop.ToInstruction());wrapped++;i++;
                }
                else if(call.Name=="AwaitUnsafeOnCompleted")
                {
                    if(i<4 || il[i-3].OpCode.Code!=Code.Ldflda)throw new Exception("Unexpected fixture await shape.");
                    var stub=Stub(state,"AwaitForwarder",module.CorLibTypes.Void,new ByRefSig(call.GenericInstMethodSig.GenericArguments[0]),new ByRefSig(state.ToTypeSig()));
                    Add(stub,OpCodes.Ldarg_1.ToInstruction(),OpCodes.Ldflda.ToInstruction(builder),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldarg_1.ToInstruction(),OpCodes.Call.ToInstruction(call),OpCodes.Ret.ToInstruction());
                    il[i-4].OpCode=OpCodes.Nop;il[i-4].Operand=null;
                    il[i-3].OpCode=OpCodes.Nop;il[i-3].Operand=null;
                    il[i].Operand=stub;wrapped++;
                }
            }
        }
        if(wrapped!=2)throw new Exception("Missing builder fixture call sites.");
        Assembly Snapshot(string suffix){module.Assembly.Name="UnpackAsyncFixture"+suffix;using var bytes=new MemoryStream();module.Write(bytes);File.WriteAllBytes(Path.Combine(directory,"async-"+suffix+".dll"),bytes.ToArray());return Assembly.Load(bytes.ToArray());}
        var before=Snapshot("Before");
        if(AsyncRecoveryPass.Process(module,Path.Combine(directory,"async-fixture.json"))!=2)throw new Exception("Async proxy recovery count mismatch.");
        state.Name="RegeneratedStateMachine";
        var attribute=kickoff.CustomAttributes.Single(a=>a.TypeFullName=="System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        int attributeIndex=kickoff.CustomAttributes.IndexOf(attribute);
        kickoff.CustomAttributes[attributeIndex]=new CustomAttribute(attribute.Constructor,new[]{new CAArgument(attribute.Constructor.MethodSig.Params[0],state.ToTypeSig())});
        var after=Snapshot("After");
        string vm=Path.Combine(directory,"async-vm.json"),mapping=Path.Combine(directory,"async-mapped.json");
        using(var saved=ModuleDefMD.Load(Path.Combine(directory,"async-Before.dll")))
        {
            var move=saved.GetTypes().Single(t=>t.DeclaringType?.Name==nameof(AsyncSemanticFixture)).Methods.Single(m=>m.Name=="MoveNext");
            File.WriteAllText(vm,"[{\"Token\":\"0x"+move.MDToken.Raw.ToString("X8")+"\",\"Restored\":true}]");
        }
        RebuiltValidation.MapMethods(Path.Combine(directory,"async-Before.dll"),Path.Combine(directory,"async-After.dll"),vm,mapping);
        var mapped=Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(mapping)).Single();
        if((string?)mapped["MappingKind"]!="async-state-machine-by-owner" || !((string?)mapped["RebuiltSignature"]??"").Contains("RegeneratedStateMachine"))
            throw new Exception("Async rebuilt mapping did not follow the owner attribute.");
        kickoff.CustomAttributes.RemoveAt(attributeIndex);Snapshot("MissingAttribute");
        try {RebuiltValidation.MapMethods(Path.Combine(directory,"async-Before.dll"),Path.Combine(directory,"async-MissingAttribute.dll"),vm,mapping);throw new Exception("Accepted unproven regenerated async mapping.");}
        catch(InvalidDataException){}
        Console.WriteLine("PASS: regenerated async mapping follows unique owner metadata and rejects missing owner attributes.");
        int checks=0;
        foreach(bool suspend in new[]{false,true})
        foreach(int mode in new[]{0,1,2})
        {
            string Evaluate(Assembly assembly)
            {
                var type=assembly.GetType(typeof(AsyncSemanticFixture).FullName!)!;
                var gate=new TaskCompletionSource<bool>();
                if(!suspend)gate.SetResult(true);
                var task=(Task<string>)type.GetMethod("Evaluate")!.Invoke(null,new object[]{mode,gate.Task})!;
                if(suspend && task.IsCompleted)throw new Exception("Fixture did not really suspend.");
                if(suspend)gate.SetResult(true);
                try{return task.GetAwaiter().GetResult();}
                catch(InvalidOperationException ex){return "fault:"+ex.Message;}
            }
            string expected=mode==1?"fault:fixture-fault":mode==2?"start;resumed;cancel":"start;resumed;ok";
            if(Evaluate(before)!=expected || Evaluate(after)!=expected)throw new Exception("Async behavioral comparison failed.");
            checks++;
        }
        foreach(var assembly in new[]{before,after})
            if((int)assembly.GetType(typeof(AsyncSemanticFixture).FullName!)!.GetField("FinallyCount")!.GetValue(null)!=checks)
                throw new Exception("Async finally side-effect count changed.");
        Console.WriteLine("PASS: async forwarding normalization preserves completed/suspended success, fault, cancellation and finally in 6 paired cases.");
    }
    private static MethodDef Stub(TypeDef owner,string name,TypeSig result,params TypeSig[] arguments)
    {
        var method=new MethodDefUser(name,MethodSig.CreateStatic(result,arguments),MethodImplAttributes.IL,MethodAttributes.Private|MethodAttributes.Static)
            {Body=new CilBody()};owner.Methods.Add(method);return method;
    }
    private static void Add(MethodDef method,params Instruction[] instructions){foreach(var instruction in instructions)method.Body.Instructions.Add(instruction);}
    private static void CheckAst()
    {
        var body=new BlockStatement();
        body.Statements.Add(new IfElseStatement(new BinaryOperatorExpression(new PrimitiveExpression(3u),BinaryOperatorType.InEquality,new PrimitiveExpression(0)),
            new BlockStatement(new ReturnStatement(new PrimitiveExpression(7)))));
        body.Statements.Add(new IfElseStatement(new InvocationExpression(new IdentifierExpression("HasSideEffects")),new BlockStatement()));
        body.Statements.Add(new TryCatchStatement{TryBlock=new BlockStatement(new ExpressionStatement(new InvocationExpression(new IdentifierExpression("Work")))),FinallyBlock=new BlockStatement()});
        var stats=new ReadabilityStats();body.AcceptVisitor(new ReadableControlFlow(stats));
        if(stats.ConstantConditions!=1 || stats.EmptyFinally!=1 || body.Descendants.OfType<IfElseStatement>().Count()!=1
            || !body.ToString().Contains("HasSideEffects"))throw new Exception("Literal-only control-flow cleanup changed nonliteral code.");
        Console.WriteLine("PASS: source simplification removes only literal predicates and empty finally; side-effect predicates retained.");
    }
}
