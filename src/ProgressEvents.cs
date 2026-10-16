using System.Diagnostics;
using Newtonsoft.Json;

namespace Unpacker;

internal sealed class ProgressEvent
{
    public int Stage {get;set;}
    public string State {get;set;}="info";
    public string Message {get;set;}="";
    public int? Current {get;set;}
    public int? Total {get;set;}
}

// Workers keep verbose backend output in their log files; only framed progress
// is relayed through nested stdout pipes. analyzer stdout remains pure JSON.
internal static class ProgressEvents
{
    internal const string EnvironmentKey="UNPACKER_PROGRESS";
    internal const string Prefix="@@UNPACKER_PROGRESS@@";
    private static readonly object Gate=new();
    internal static int CurrentStage {get;private set;}=1;
    internal static bool Enabled=>Environment.GetEnvironmentVariable(EnvironmentKey)=="1";
    internal static void Send(int stage,string state,string message,int? current=null,int? total=null)
    {
        if(!Enabled)return;
        lock(Gate){CurrentStage=stage;Console.Out.WriteLine(Prefix+JsonConvert.SerializeObject(new ProgressEvent{Stage=stage,State=state,Message=message,Current=current,Total=total}));Console.Out.Flush();}
    }
    internal static bool TryRead(string line,out ProgressEvent? value)
    {
        value=null;
        if(!line.StartsWith(Prefix,StringComparison.Ordinal) || line.Length>65536)return false;
        try {
            value=JsonConvert.DeserializeObject<ProgressEvent>(line.Substring(Prefix.Length));
            return value!=null && value.Stage is >=1 and <=8 && value.Message!=null && value.Message.Length<=4096
                && new[]{"start","info","progress","success","warning","error"}.Contains(value.State)
                && ((!value.Current.HasValue && !value.Total.HasValue) || (value.Current>=0 && value.Total>0 && value.Current<=value.Total));
        }
        catch(JsonException){return false;}
    }
    internal static void Backend(int stage,string name,string state,int count,string prefix="")
    {
        bool initialization=name.EndsWith(" 初始化",StringComparison.Ordinal);
        if(initialization)name=name.Substring(0,name.Length-4);
        name=name switch {"StringFixer"=>"字符串恢复","ResourceResolver"=>"资源恢复","AssemblyResolver"=>"依赖程序集恢复","ControlFlow"=>"控制流整理",_=>name};
        string description=state switch {"start"=>initialization?"初始化 "+name:"开始 "+name,"not-detected"=>name+"：未检测到对应保护，跳过",
            "success"=>name+"：完成，变化 "+count+" 项","failed"=>name+"：失败，详见后端日志",_=>name+"："+state};
        Send(stage,state=="failed"?"warning":"info",prefix+description);
    }
    internal sealed class Counter
    {
        private readonly int stage;
        private readonly object gate=new();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private long last=-1000;
        private string lastMessage="";
        private int maximum=-1,lastTotal=-1;
        internal Counter(int stage)=>this.stage=stage;
        internal void Report(string message,int current,int total)
        {
            lock(gate){
                if(total<=0){if(message!=lastMessage){Send(stage,"info",message);lastMessage=message;}return;}
                if(total!=lastTotal){maximum=-1;lastTotal=total;}
                if(current<maximum)return; // Parallel decompiler reports can arrive out of order.
                maximum=current;
                if(current<0 || current>total)return;
                if(clock.ElapsedMilliseconds-last<750 && current!=total)return;
                last=clock.ElapsedMilliseconds;lastMessage=message;
                Send(stage,"progress",message,current,total);
            }
        }
    }
}
