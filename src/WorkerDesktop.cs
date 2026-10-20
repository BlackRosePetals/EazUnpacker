using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Unpacker;

// UI isolation only, not a security sandbox. Do not switch the interactive desktop.
internal static class WorkerDesktop
{
    private const string Prefix = "Unpacker-";

    internal static string CurrentName => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

    internal static int Run(string[] args, Func<string[], int> action)
    {
        if (CurrentName.StartsWith(Prefix, StringComparison.Ordinal)) return action(args);
        string name = Prefix + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create isolated worker desktop; target will not run.");
        try
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf(typeof(StartupInfo)),
                Desktop = ObjectName(GetProcessWindowStation()) + "\\" + name,
                Flags = 0x100, // STARTF_USESTDHANDLES: preserve ProcessRunner's redirected pipes.
                Input = GetStdHandle(-10), Output = GetStdHandle(-11), Error = GetStdHandle(-12)
            };
            string exe = Assembly.GetEntryAssembly()!.Location;
            string[] launch=new[]{exe};
            if(exe.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
            {
                string host=Process.GetCurrentProcess().MainModule!.FileName;
                launch=System.IO.Path.GetFileNameWithoutExtension(host).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?new[]{host,exe}:new[]{host};
                exe=host;
            }
            var command = new StringBuilder(string.Join(" ", launch.Concat(args).Select(Quote)));
            if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                IntPtr.Zero, Environment.CurrentDirectory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start isolated worker; target will not run.");
            try
            {
                if(WaitForSingleObject(info.Process,0xFFFFFFFF)!=0 || !GetExitCodeProcess(info.Process,out uint code))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
            finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
        }
        finally { CloseDesktop(desktop); }
    }

    private static string ObjectName(IntPtr handle)
    {
        var value = new StringBuilder(512);
        if (!GetUserObjectInformation(handle, 2, value, value.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return value.ToString();
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { text.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
            text.Append('\\', slashes).Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int number);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string exe, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process,out uint code);
}
