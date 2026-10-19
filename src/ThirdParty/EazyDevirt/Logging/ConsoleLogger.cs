using EazyDevirt.Core.Abstractions;
using EazyDevirt.Core.Abstractions.Interfaces;

namespace EazyDevirt.Logging;

internal class ConsoleLogger : ILogger
{
    private static readonly System.Threading.AsyncLocal<ConsoleLogger?> Active = new();
    private readonly object gate = new();
    private StreamWriter? details;
    private int verbosity, warnings;

    internal IDisposable Capture(string path, int level)
    {
        var previous=Active.Value;verbosity=level;warnings=0;
        details=new StreamWriter(path,false,new System.Text.UTF8Encoding(false)){AutoFlush=true};Active.Value=this;
        return new CaptureScope(()=>{lock(gate){details?.Dispose();details=null;Active.Value=previous;}});
    }
    private sealed class CaptureScope(Action close) : IDisposable { public void Dispose()=>close(); }
    internal static void Detail(object message)
    {
        if(Active.Value is { } logger)logger.Info(message);
        else if(Environment.GetEnvironmentVariable("UNPACKER_VM_DEBUG")=="1")Console.WriteLine(message);
    }
    internal void Summary(object message)
    {
        lock(gate){Record("summary",message);Console.WriteLine(message);}
    }
    private void Record(string level,object message)=>details?.WriteLine("["+DateTime.Now.ToString("HH:mm:ss")+"] ["+level+"] "+message);
    public void Success(object message) {lock(gate){Record("success",message);if(verbosity>0)WriteLine(message, ConsoleColor.Cyan, '+');}}

    public void Warning(object message)
    {
        lock(gate){Record("warning",message);warnings++;
            if(verbosity>0||warnings<=10)WriteLine(message,ConsoleColor.Yellow,'-');
            else if(warnings==11)Console.WriteLine("[VM] 其余警告已收录到 vm-detail.log。");}
    }

    public void Error(object message) {lock(gate){Record("error",message);WriteLine(message, ConsoleColor.Red, '!');}}

    public void Info(object message) {lock(gate){Record("detail",message);if(verbosity>0)WriteLine(message, ConsoleColor.Gray, '*');}}

    public void InfoStr(object message, object message2) {lock(gate){Record("detail",message2+": "+message);if(verbosity>0)WriteLineInfo(message, ConsoleColor.Red, message2);}}
    
    public void ShowInfo(Version version, Version eazVersion)
    {
        Console.WriteLine();
        Console.WriteLine();
        WriteLineMiddle(@"                ▄███▄   ██   ▄▄▄▄▄▄ ▀▄    ▄                              ", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                █▀   ▀  █ █ ▀   ▄▄▀   █  █                               ", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                ██▄▄    █▄▄█ ▄▀▀   ▄▀  ▀█        _            _      _   ", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                █▄   ▄▀ █  █ ▀▀▀▀▀▀    █      __| | _____   _(_)_ __| |_ ", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                ▀███▀      █         ▄▀      / _` |/ _ \ \ / / | '__| __|", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                          █                 | (_| |  __/\ V /| | |  | |_ ", ConsoleColor.DarkMagenta);
        WriteLineMiddle(@"                         ▀                   \__,_|\___| \_/ |_|_|   \__|", ConsoleColor.DarkMagenta);
        Console.WriteLine();
        
        WriteMiddle(@"Version - ", ConsoleColor.DarkMagenta);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(version);
        Console.ResetColor();
        
        WriteMiddle(@"Eazfuscator.NET Version - ", ConsoleColor.DarkMagenta);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(eazVersion);
        Console.ResetColor();
        
        WriteMiddle(@"Developers - ", ConsoleColor.DarkMagenta);
        
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("puff");
        Console.ResetColor();

        WriteMiddle(@"Github Repo - ", ConsoleColor.DarkMagenta);
        
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("https://github.com/puff/EazyDevirt");
        Console.ResetColor();
        
        WriteMiddle(@"Thanks to - ", ConsoleColor.DarkMagenta);
        
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("void-stack for the many contributions."); 
        
        // 39 = ("saneki for the eazdevirt project.".length + "Thanks to - ".length) - ("Thanks to - ".length / 2)
        Console.WriteLine(string.Format("{0," + (Console.WindowWidth / 2
                                                 + 39 + "}"), "saneki for the eazdevirt project."));
        
        // 47 = ("TobitoFatitoRE for the HexDevirt project.".length + "Thanks to - ".length) - ("Thanks to - ".length / 2)
        Console.WriteLine(string.Format("{0," + (Console.WindowWidth / 2 
                                                 + 47 + "}"), "TobitoFatitoRE for the HexDevirt project."));
        
        // 65 = ("Washi1337 for the wonderful AsmResolver and Echo libraries.".length + "Thanks to - ".length) - ("Thanks to - ".length / 2)
        Console.WriteLine(string.Format("{0," + (Console.WindowWidth / 2
                                                 + 65 + "}"), "Washi1337 for the wonderful AsmResolver and Echo libraries."));
        Console.ResetColor();
    }

    private void WriteMiddle(object message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(string.Format("{0," + (Console.WindowWidth / 2 + message.ToString()?.Length / 2) + "}",
            message));
        Console.ResetColor();
    }

    private void WriteLineMiddle(object message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(string.Format("{0," + (Console.WindowWidth / 2 + message.ToString()?.Length / 2) + "}",
            message));
        Console.ResetColor();
    }
    
    private void WriteLine(object message, ConsoleColor color, char character)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("[");
        Console.ForegroundColor = color;
        Console.Write(character);
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("] ");
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private void WriteLineInfo(object message, ConsoleColor color, object msg2)
    {
        Console.ForegroundColor = color;
        Console.Write("[");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(msg2);
        Console.ForegroundColor = color;
        Console.Write("] ");
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
