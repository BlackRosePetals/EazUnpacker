using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;

namespace Unpacker;

internal static class ProgressTests
{
    internal static void Run(string root)
    {
        if(ProgressEvents.TryRead("backend noise",out _) || ProgressEvents.TryRead(ProgressEvents.Prefix+"{malformed}",out _)
            || ProgressEvents.TryRead(ProgressEvents.Prefix+JsonConvert.SerializeObject(new ProgressEvent{Stage=9,Message="bad"}),out _)
            || ProgressEvents.TryRead(ProgressEvents.Prefix+JsonConvert.SerializeObject(new ProgressEvent{Stage=3,Message="bad",Current=5,Total=2}),out _))
            throw new Exception("Accepted invalid progress framing/counts.");
        var clock=Stopwatch.StartNew();var received=new List<(ProgressEvent Value,long Milliseconds)>();
        string nested=Path.Combine(root,"progress-nested.log"),outer=Path.Combine(root,"progress-outer.log");
        int code=ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--test-progress-relay",nested},outer,10,
            e=>received.Add((e,clock.ElapsedMilliseconds)));
        if(code!=0 || received.Count!=2 || received[0].Value.Current!=0 || received[1].Value.Current!=2
            || received[1].Milliseconds-received[0].Milliseconds<1000 || !received[0].Value.Message.Contains("实时"))
            throw new Exception("Nested UTF-8 progress was lost, buffered until exit, or contaminated by raw backend output.");
        string raw=File.ReadAllText(nested);
        if(!raw.Contains("raw fixture stdout") || !raw.Contains("raw fixture stderr") || !raw.Contains("{malformed}")
            || File.ReadAllText(outer).Contains("raw fixture stdout"))throw new Exception("Raw backend log/relay policy changed.");
        Console.WriteLine("PASS: nested UTF-8 progress arrives before child exit; stdout/stderr retained on disk without raw console flooding.");
        var failure=new List<ProgressEvent>();
        code=ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"source",Path.Combine(root,"missing.dll"),Path.Combine(root,"missing-source"),Path.Combine(root,"missing-report.json"),Path.Combine(root,"missing.dll")},
            Path.Combine(root,"progress-failure.log"),10,failure.Add);
        if(code==0 || failure.Count==0 || failure.Any(e=>e.Stage!=6) || !failure.Any(e=>e.State=="error"))throw new Exception("Failure exit code or decompiler-stage attribution was lost.");
        Console.WriteLine("PASS: early backend failure retains nonzero exit and reports the correct active stage.");
        string directory=Path.Combine(root,"progress-render");Directory.CreateDirectory(directory);
        var output=new StringWriter();
        using(var renderer=new CliProgress(directory,output,80))
        {
            renderer.Report(3,"start","长操作\r\n测试");
            // Wait on the actual heartbeat output, not a single timing-sensitive sleep.
            var deadline=Stopwatch.StartNew();
            string ReadLive(){using var file=new FileStream(Path.Combine(directory,"progress.log"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd();}
            while(!ReadLive().Contains("[等待]") && deadline.ElapsedMilliseconds<2000)Thread.Sleep(20);
            renderer.Report(8,"success","完成");
        }
        string text=output.ToString();Thread.Sleep(150);
        if(text!=output.ToString() || !text.Contains("[等待]") || !text.Contains("[8/8 结果汇总]") || text.Contains("\u001b")
            || File.ReadAllLines(Path.Combine(directory,"progress.jsonl")).Length!=2)throw new Exception("Heartbeat/render/disposal output is invalid.");
        Console.WriteLine("PASS: redirected output has readable stages, bounded heartbeat, JSONL audit and no writes after disposal.");
    }
}
