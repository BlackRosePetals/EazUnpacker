using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

internal static class StartupProbe
{
    private sealed class WindowInfo
    {
        public string Title="", Class="", Text="";
    }

    internal static int Run(string target,string report,string[] arguments)
    {
        if(!Unpacker.WorkerDesktop.CurrentName.StartsWith("Unpacker-",StringComparison.Ordinal))
            throw new InvalidOperationException("Startup tests require a private desktop.");
        target=Path.GetFullPath(target);
        string status="probe-error",detail=""; int? exit=null; int pid=0;
        var windows=new List<WindowInfo>();
        string stdout="",stderr="";
        var watch=Stopwatch.StartNew();
        using(var job=new ProcessJob())
        using(var process=new Process {StartInfo=new ProcessStartInfo(target,string.Join(" ",arguments.Select(Quote))) {
            UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(target)!,
            RedirectStandardOutput=true,RedirectStandardError=true }})
        {
            try
            {
                process.Start();pid=process.Id;
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                job.Assign(process);
                while(watch.ElapsedMilliseconds<6500)
                {
                    if(process.HasExited) {exit=process.ExitCode;status="exited-before-ui";detail="Exited before stable UI was observed; exit 0 alone is not a pass.";break;}
                    var visible=Windows(pid);
                    foreach(var item in visible)
                        if(!windows.Any(w=>w.Title==item.Title && w.Class==item.Class && w.Text==item.Text))windows.Add(item);
                    var problem=visible.FirstOrDefault(w=>
                        System.Text.RegularExpressions.Regex.IsMatch(w.Title,@"debugger\s+(attached|detected)|unhandled exception",System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        || w.Class=="#32770" && System.Text.RegularExpressions.Regex.IsMatch(w.Title+" "+w.Text,@"debugger|exception|error|异常|错误|调试器",System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if(problem!=null) {status="runtime-error-dialog";detail=problem.Title+" "+problem.Text;break;}
                    if(watch.ElapsedMilliseconds>=1800 && visible.Any(w=>w.Class!="#32770" && !string.IsNullOrWhiteSpace(w.Title)))
                    {status="ui-started";detail="Visible non-dialog main window observed while process remained alive; no business actions invoked.";break;}
                    Thread.Sleep(100);
                }
                if(status=="probe-error") {status="no-ui-within-budget";detail="No ordinary main window observed within 6.5 seconds.";}
                job.Terminate();
                if(!process.WaitForExit(3000))process.Kill();
                stdout=output.Wait(2000)?output.Result:"<output pipe did not close>";
                stderr=error.Wait(2000)?error.Result:"<error pipe did not close>";
            }
            catch(Exception ex)
            {
                detail=ex.ToString();status="probe-error";
                try {job.Terminate();if(pid!=0 && !process.HasExited)process.Kill();}catch(InvalidOperationException) { }
            }
        }
        File.WriteAllText(report,JsonConvert.SerializeObject(new {Target=target,Status=status,Detail=detail,ExitCode=exit,
            TargetPid=pid,ElapsedMs=watch.ElapsedMilliseconds,Desktop=Unpacker.WorkerDesktop.CurrentName,Windows=windows,
            StandardOutput=stdout,StandardError=stderr,Scope="Startup/UI smoke only; not full behavior equivalence. Target process job terminated after observation."},Formatting.Indented));
        Console.WriteLine(status+": "+target);
        return status=="probe-error"?1:0;
    }

    private static List<WindowInfo> Windows(int pid)
    {
        var result=new List<WindowInfo>();
        EnumWindows((window,_)=> {
            GetWindowThreadProcessId(window,out uint owner);
            if(owner!=(uint)pid || !IsWindowVisible(window))return true;
            var title=new StringBuilder(2048);GetWindowText(window,title,title.Capacity);
            var kind=new StringBuilder(256);GetClassName(window,kind,kind.Capacity);
            var text=new List<string>();
            EnumChildWindows(window,(child,unused)=>{var value=new StringBuilder(2048);GetWindowText(child,value,value.Capacity);if(value.Length>0)text.Add(value.ToString());return true;},IntPtr.Zero);
            result.Add(new WindowInfo {Title=title.ToString(),Class=kind.ToString(),Text=string.Join(" | ",text)});return true;
        },IntPtr.Zero);
        return result;
    }
    private static string Quote(string value)=>"\""+System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(value,@"(\\*)""","$1$1\\\""),@"(\\+)$","$1$1")+"\"";
    private delegate bool WindowCallback(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent,WindowCallback callback,IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int length);

    private sealed class ProcessJob : IDisposable
    {
        private readonly IntPtr handle;
        internal ProcessJob()
        {
            handle=CreateJobObject(IntPtr.Zero,null);
            if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits=new ExtendedLimits {Basic=new BasicLimits {Flags=0x2000}};
            if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {int code=Marshal.GetLastWin32Error();CloseHandle(handle);throw new Win32Exception(code);}
        }
        internal void Assign(Process process)
        {if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());}
        internal void Terminate() {if(!TerminateJobObject(handle,0))throw new Win32Exception(Marshal.GetLastWin32Error());}
        public void Dispose()=>CloseHandle(handle);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateJobObject(IntPtr security,string? name);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits info,uint length);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateJobObject(IntPtr job,uint exitCode);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    }
}
