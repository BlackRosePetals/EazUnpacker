using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

internal static class RecoveryStatistics
{
    internal static IReadOnlyList<string> Save(SampleResult result,string directory)
    {
        JObject? Read(string name)
        {
            try {string path=Path.Combine(directory,name);return File.Exists(path)?JObject.Parse(File.ReadAllText(path)):null;}
            catch(Exception ex) when(ex is IOException or JsonException){return null;}
        }
        var vm=Read("vm-summary.json");var flow=Read("control-flow-cleanup.json");var clr=Read("clr-validation.json");
        var stats=new {
            Status=result.Status,AssemblyMethods=result.Before?.Methods,
            DetectedVmMethods=(int?)vm?["DetectedMethods"],RestoredVmMethods=(int?)vm?["RestoredMethods"],
            PartiallyRestoredVmMethods=(int?)vm?["PartiallyRestoredMethods"],RestoredVmCallSites=(int?)vm?["RestoredVmCallSites"],RemainingVmCallSites=(int?)vm?["RemainingVmCallSites"],
            DecryptedRegions=(int?)vm?["DecryptedRegions"],VerifiedKeys=(int?)vm?["VerifiedKeys"],
            RenamedIdentifiers=result.Stages.FirstOrDefault(s=>s.Name=="Names")?.Changes,
            InlinedExpressions=result.Stages.FirstOrDefault(s=>s.Name=="InlineExpressions")?.Changes,
            FoldedBranches=(int?)flow?["FoldedBranches"],RemovedInstructions=(int?)flow?["RemovedInstructions"],
            StringLiteralsBefore=result.Before?.StringLiterals,StringLiteralsAfter=result.After?.StringLiterals,
            ClrValidationRequested=result.ClrValidationRequested,ClrExpected=(int?)clr?["Expected"],ClrPrepared=(int?)clr?["Prepared"],ClrFailed=(int?)clr?["Failed"],ClrReportComplete=(bool?)clr?["Complete"]
        };
        File.WriteAllText(Path.Combine(directory,"recovery-statistics.json"),JsonConvert.SerializeObject(stats,Formatting.Indented));
        var lines=new List<string>();
        if(vm!=null)lines.Add($"恢复汇总：VM 方法 {stats.RestoredVmMethods}/{stats.DetectedVmMethods}，部分恢复 {stats.PartiallyRestoredVmMethods}；已恢复调用点 {stats.RestoredVmCallSites}，剩余 {stats.RemainingVmCallSites}；解密区域 {stats.DecryptedRegions}，密钥 {stats.VerifiedKeys}");
        else lines.Add(result.Stages.Any(s=>s.Name=="VM")?"恢复汇总：VM 总量统计不可用，请查看 VM 阶段报告。":"恢复汇总：未执行 VM 还原；不将普通方法计为已恢复 VM 方法。");
        if(stats.RenamedIdentifiers.HasValue||flow!=null)lines.Add($"可读性汇总：重命名 {stats.RenamedIdentifiers?.ToString()??"未统计"} 项；折叠分支 {stats.FoldedBranches?.ToString()??"未统计"}，删除 IL 指令 {stats.RemovedInstructions?.ToString()??"未统计"}");
        if(result.Before!=null&&result.After!=null)lines.Add($"字符串字面量：{stats.StringLiteralsBefore} → {stats.StringLiteralsAfter}");
        if(stats.InlinedExpressions.HasValue)lines.Add("表达式内联："+stats.InlinedExpressions+" 处");
        if(clr!=null)lines.Add($"CLR 检查：通过 {stats.ClrPrepared}/{stats.ClrExpected}，失败 {stats.ClrFailed}"+(stats.ClrReportComplete==true?"":"；检查尚未完成"));
        else lines.Add(result.ClrValidationRequested?"CLR 检查：已请求，尚无检查结果。":"CLR 检查：未请求（默认关闭）。");
        return lines;
    }
}
