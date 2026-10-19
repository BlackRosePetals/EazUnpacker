using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class ValidationReportTests
{
    internal static void Run(string root)
    {
        var constrained=typeof(ValidationReportTests).GetMethod(nameof(ConstrainedMethod),BindingFlags.Static|BindingFlags.NonPublic)!;
        var closed=(MethodInfo)ClrValidator.CloseMethod(constrained,typeof(ValidationReportTests).Assembly);
        if(closed.ContainsGenericParameters || closed.GetGenericArguments().Single()!=typeof(LateCandidate))
            throw new Exception("Constraint-valid generic types late in metadata were excluded by the candidate cap.");
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(closed.MethodHandle);
        var identity=new AssemblyName("VersionPolicyFixture, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var higher=new AssemblyName("VersionPolicyFixture, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null");
        if(DependencyLoadScope.MatchesIdentity(identity,higher,false) || !DependencyLoadScope.MatchesIdentity(identity,higher,true)
            || DependencyLoadScope.MatchesIdentity(higher,identity,true)
            || DependencyLoadScope.MatchesIdentity(identity,new AssemblyName("OtherFixture, Version=2.0.0.0"),true))
            throw new Exception("Runtime dependency policy accepted a downgrade/different identity or changed Framework exact-version behavior.");
        string directory=Path.Combine(root,"validation-reports");Directory.CreateDirectory(directory);
        string report=Path.Combine(directory,"validation.json");
        if(ClrValidator.HasCompleteReport(report,1))throw new Exception("Missing CLR report was accepted.");
        File.WriteAllText(report,"not json");
        if(ClrValidator.HasCompleteReport(report,1))throw new Exception("Malformed CLR report was accepted.");
        var valid=new JObject { ["Prepared"]=1,["Failed"]=0,["Skipped"]=0,
            ["Methods"]=new JArray(new JObject {["Token"]="0x06000001",["Status"]="success"}) };
        void Save()=>File.WriteAllText(report,valid.ToString());
        Save();
        if(!ClrValidator.HasCompleteReport(report,1) || ClrValidator.HasCompleteReport(report,0)
            || ClrValidator.HasCompleteReport(report,2))throw new Exception("CLR report coverage count was not checked.");
        valid["Complete"]=false;Save();
        if(ClrValidator.HasCompleteReport(report,1))throw new Exception("An unfinished CLR checkpoint was accepted.");
        valid.Remove("Complete");Save();
        valid["Prepared"]=2;((JArray)valid["Methods"]!).Add(valid["Methods"]![0]!.DeepClone());Save();
        if(ClrValidator.HasCompleteReport(report,2))throw new Exception("Duplicate CLR method tokens were accepted.");
        valid["Prepared"]=1;((JArray)valid["Methods"]!).RemoveAt(1);valid["Methods"]![0]!["Status"]="failed";Save();
        if(ClrValidator.HasCompleteReport(report,1))throw new Exception("Failed CLR method was accepted.");
        valid["Prepared"]=new JObject();Save();
        if(ClrValidator.HasCompleteReport(report,1))throw new Exception("Invalid CLR counter shape was accepted.");

        string assemblyPath=Path.Combine(directory,"ValidationFixture.dll");
        using(var module=new ModuleDefUser("ValidationFixture.dll"))
        {
            var assembly=new AssemblyDefUser("ValidationFixture",new Version(1,0));assembly.Modules.Add(module);
            var type=new TypeDefUser("Validation","Fixture",module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=dnlib.DotNet.TypeAttributes.Public|dnlib.DotNet.TypeAttributes.Abstract|dnlib.DotNet.TypeAttributes.Sealed};
            module.Types.Add(type);
            foreach(bool missing in new[]{false,true})
            {
                var method=new MethodDefUser(missing?"NeedsDependency":"Constant",MethodSig.CreateStatic(module.CorLibTypes.Int32),
                    dnlib.DotNet.MethodImplAttributes.IL,dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static);
                method.Body=new CilBody();type.Methods.Add(method);
                if(missing)
                {
                    var reference=new AssemblyRefUser("UnpackerFixtureMissingDependency",new Version(1,0,0,0));
                    var externalType=new TypeRefUser(module,"Missing","External",reference);
                    var call=new MemberRefUser(module,"Compute",MethodSig.CreateStatic(module.CorLibTypes.Int32),externalType);
                    method.Body.Instructions.Add(OpCodes.Call.ToInstruction(call));
                }
                else method.Body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction());
                method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            }
            module.Write(assemblyPath);
        }
        string vmReport=Path.Combine(directory,"vm-methods.json");
        File.WriteAllText(vmReport,"[{\"Token\":\"0x06000001\",\"Restored\":true,\"Inlined\":true}]");
        try {ClrValidator.ReadTargets(vmReport);throw new Exception("An unvalidated inline adapter was accepted.");}
        catch(InvalidDataException) { }
        File.WriteAllText(vmReport,"[{\"Token\":\"0x06000001\",\"Restored\":true,\"Inlined\":true,\"RecoveredAdapterToken\":\"0x06000002\"}]");
        if(ClrValidator.ReadTargets(vmReport).Count!=2)throw new Exception("Inline adapter was not included in CLR coverage.");
        File.WriteAllText(vmReport,"[{\"Token\":\"0x06000001\",\"Restored\":true,\"AccessBridgeTokens\":[\"0x06000002\",\"0x06000002\"]}]");
        var bridgeTargets=ClrValidator.ReadTargets(vmReport);
        if(bridgeTargets.Count!=2 || bridgeTargets[1].Role!="recovered-access-bridge")
            throw new Exception("Private-access bridges were missing or duplicated in CLR coverage.");
        File.WriteAllText(vmReport,"[{\"Token\":\"0x06000001\",\"Restored\":true,\"Inlined\":true,\"RecoveredAdapterToken\":\"0x06000002\"}]");
        int code=ProcessRunner.Run(Assembly.GetExecutingAssembly().Location,
            new[]{"--validate-worker",assemblyPath,vmReport,report,"5",assemblyPath},Path.Combine(directory,"worker.log"),20);
        var output=JObject.Parse(File.ReadAllText(report));
        if(code!=1 || (int?)output["Prepared"]!=1 || (int?)output["Failed"]!=1
            || (output["MissingDependencies"] as JArray)?.Any(n=>n.ToString().Contains("UnpackerFixtureMissingDependency"))!=true
            || (string?)output["Methods"]?[1]?["FailureKind"]!="missing-dependency"
            || (string?)output["Methods"]?[1]?["Role"]!="recovered-inline-adapter"
            || ClrValidator.HasCompleteReport(report,2))
            throw new Exception("Missing dependency was not reported separately from invalid recovered IL.");
        Console.WriteLine("PASS: CLR report coverage rejects missing/malformed/duplicate/failed entries; isolated JIT classifies missing dependencies without accepting them as successful validation.");
    }

    private abstract class LateConstraint { }
    private sealed class LateCandidate : LateConstraint {public LateCandidate() { }}
    private static int ConstrainedMethod<T>() where T:LateConstraint,new()=>1;
}
