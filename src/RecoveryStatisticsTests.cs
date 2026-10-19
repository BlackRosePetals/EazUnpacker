using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class RecoveryStatisticsTests
{
    internal static void Run(string root)
    {
        string dir=Path.Combine(root,"statistics");Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"vm-summary.json"),"{\"DetectedMethods\":2,\"RestoredMethods\":1,\"PartiallyRestoredMethods\":1,\"RestoredVmCallSites\":3,\"RemainingVmCallSites\":1,\"DecryptedRegions\":2,\"VerifiedKeys\":2}");
        File.WriteAllText(Path.Combine(dir,"clr-validation.json"),"{\"Expected\":4,\"Prepared\":3,\"Failed\":1,\"Complete\":false}");
        var result=new SampleResult{Status="partial",Before=new AssemblyInfo{Methods=100,StringLiterals=2},After=new AssemblyInfo{StringLiterals=7}};
        string lines=string.Join("\n",RecoveryStatistics.Save(result,dir));
        var json=JObject.Parse(File.ReadAllText(Path.Combine(dir,"recovery-statistics.json")));
        if(!lines.Contains("VM 方法 1/2")||!lines.Contains("调用点 3")||!lines.Contains("检查尚未完成")||(string?)json["Status"]!="partial"||json["RemovedInstructions"]?.Type!=JTokenType.Null)
            throw new Exception("Recovery summary confused hosts/call sites or fabricated missing statistics.");
        File.Delete(Path.Combine(dir,"clr-validation.json"));
        lines=string.Join("\n",RecoveryStatistics.Save(result,dir));
        json=JObject.Parse(File.ReadAllText(Path.Combine(dir,"recovery-statistics.json")));
        if(!lines.Contains("未请求")||(bool?)json["ClrValidationRequested"]!=false||json["ClrPrepared"]?.Type!=JTokenType.Null)throw new Exception("Disabled CLR check was reported as passed.");
        Console.WriteLine("PASS: final recovery statistics distinguish VM hosts/call sites, preserve failures and leave missing counts unknown.");
    }
}
