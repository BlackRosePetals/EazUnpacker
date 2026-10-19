using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;

namespace Unpacker;

internal sealed class CliProgress : IDisposable
{
    private static readonly string[] Names={"","输入检查","字符串/资源","VM 还原","命名/兼容","CLR/恢复输出","反编译","编译/验证","结果汇总"};
    private readonly object gate=new();
    private readonly Stopwatch elapsed=Stopwatch.StartNew();
    private readonly TextWriter console;
    private readonly StreamWriter human,events;
    private readonly Timer timer;
    private int stage;
    private long stageStarted,lastWritten;
    private string current="准备工作后端";
    private bool disposed;
    internal CliProgress(string directory,TextWriter? output=null,int heartbeatMilliseconds=10000)
    {
        console=output??Console.Out;
        human=new StreamWriter(Path.Combine(directory,"progress.log"),false,new System.Text.UTF8Encoding(false)){AutoFlush=true};
        events=new StreamWriter(Path.Combine(directory,"progress.jsonl"),false,new System.Text.UTF8Encoding(false)){AutoFlush=true};
        int poll=Math.Min(1000,heartbeatMilliseconds);
        timer=new Timer(_=>Heartbeat(heartbeatMilliseconds),null,poll,poll);
    }
    internal void Report(int stage,string state,string message,int? current=null,int? total=null)=>Report(new ProgressEvent{Stage=stage,State=state,Message=message,Current=current,Total=total});
    internal void Failure(string message){lock(gate){Report(stage==0?1:stage,"error",message);}}
    internal void Warning(string message){lock(gate){Report(stage==0?1:stage,"warning",message);}}
    internal void Report(ProgressEvent item)
    {
        lock(gate){
            if(disposed)return;
            if(item.Stage!=stage){stage=item.Stage;stageStarted=elapsed.ElapsedMilliseconds;}
            current=item.Message;
            events.WriteLine(JsonConvert.SerializeObject(new{Utc=DateTime.UtcNow,ElapsedSeconds=elapsed.Elapsed.TotalSeconds,StageElapsedSeconds=(elapsed.ElapsedMilliseconds-stageStarted)/1000.0,Event=item}));
            string state=item.State switch {"start"=>"开始","success"=>"完成","warning"=>"提示","error"=>"失败","progress"=>"进度",_=>"信息"};
            string count=item.Current.HasValue && item.Total>0
                ?" "+item.Current+"/"+item.Total+" ("+(100.0*item.Current.Value/item.Total.Value).ToString("F1",CultureInfo.InvariantCulture)+"%，本步骤)":"";
            Write(state,item.Message+count);
        }
    }
    private void Heartbeat(int interval)
    {
        lock(gate){
            if(disposed || elapsed.ElapsedMilliseconds-lastWritten<interval)return;
            try {Write("等待",current+"；仍在等待后端，此阶段已用 "+((elapsed.ElapsedMilliseconds-stageStarted)/1000.0).ToString("F1",CultureInfo.InvariantCulture)+"s");}
            catch(IOException){} // A disconnected console must not crash a worker timer.
        }
    }
    private void Write(string state,string message)
    {
        string safe=message.Replace('\r',' ').Replace('\n',' ').Replace('\u001b',' ');
        string timing=state is "完成" or "失败" or "等待"?"  | 总耗时 "+elapsed.Elapsed.TotalSeconds.ToString("F1",CultureInfo.InvariantCulture)+"s":"";
        string line="["+DateTime.Now.ToString("HH:mm:ss")+"] ["+stage+"/8 "+Names[stage]+"] ["+state+"] "+safe+timing;
        human.WriteLine(line);console.WriteLine(line);console.Flush();lastWritten=elapsed.ElapsedMilliseconds;
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;timer.Dispose();human.Dispose();events.Dispose();}}
}
