using System.Diagnostics;
using System.Text;

namespace Unpacker;

internal static class ProcessRunner
{
    internal static int Run(string exe, IEnumerable<string> args, string log, int timeoutSeconds, Action<ProgressEvent>? progress=null, Action<string>? output=null)
    {
        var guard = new object();
        using var writer = new StreamWriter(log, false, new UTF8Encoding(false)) { AutoFlush = true };
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!
            }
        };
        if(progress!=null || ProgressEvents.Enabled)process.StartInfo.EnvironmentVariables[ProgressEvents.EnvironmentKey]="1";
        void Receive(string? line)
        {
            if(line==null)return;
            lock(guard){
                writer.WriteLine(line);
                if(ProgressEvents.TryRead(line,out var item)){
                    if(progress!=null)progress(item!);
                    else ProgressEvents.Send(item!.Stage,item.State,item.Message,item.Current,item.Total);
                }
                else output?.Invoke(line);
            }
        }
        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        bool exited = process.WaitForExit(checked(timeoutSeconds * 1000));
        if (!exited && !process.HasExited)
        {
            // .NET Framework lacks Kill(entireProcessTree); use the Windows process-tree terminator.
            using var killer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (killer != null) { killer.BeginOutputReadLine(); killer.BeginErrorReadLine(); killer.WaitForExit(5000); }
            if (!process.WaitForExit(5000)) process.Kill();
        }
        process.WaitForExit();
        lock (guard) { if (!exited) writer.WriteLine("Worker timeout; process tree terminated."); writer.Flush(); }
        return exited ? process.ExitCode : 124;
    }

    // Windows CommandLineToArgvW quoting, including trailing backslashes and embedded quotes.
    private static string Quote(string arg)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '\"') { text.Append('\\', slashes * 2 + 1); text.Append(c); slashes = 0; continue; }
            text.Append('\\', slashes); slashes = 0; text.Append(c);
        }
        text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
    }
}
