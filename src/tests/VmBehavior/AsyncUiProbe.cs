using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

internal static class AsyncUiProbe
{
    // Exercises only directory discovery (which creates the probe's own Backup
    // folder), never the patching action. WorkerDesktop isolates all target UI.
    internal static int Run(string path,string report)
    {
        var failures=new List<string>();
        ThreadExceptionEventHandler onError=(_,e)=>failures.Add(e.Exception.ToString());
        Application.ThreadException+=onError;
        try
        {
            Control.CheckForIllegalCrossThreadCalls=true;
            var assembly=Assembly.LoadFile(Path.GetFullPath(path));
            var type=assembly.GetType("formMain",true)!;
            using var form=(Form)Activator.CreateInstance(type)!;
            object Field(string name)=>type.GetField(name,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance)!.GetValue(form)!;
            form.Show();Application.DoEvents();
            // A bounded DoEvents harness is not Application.Run; explicitly
            // install the UI context that the real message loop supplies.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var group=(Control)Field("groupBox1");
            type.GetMethod("btnAutoFill_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,new[]{Field("btnAutoFill"),EventArgs.Empty});
            bool disabled=!group.Enabled;
            var clock=Stopwatch.StartNew();
            while(!group.Enabled && failures.Count==0 && clock.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(5);}
            Application.DoEvents();
            var directories=new Dictionary<string,string>();
            foreach(string name in new[]{"txtNugetPackages","txtVSExt","txtCliTool","txtBackup"})directories[name]=((TextBox)Field(name)).Text;
            bool passed=disabled && group.Enabled && failures.Count==0;
            File.WriteAllText(report,JsonConvert.SerializeObject(new{Status=passed?"passed":"failed",Desktop=Unpacker.WorkerDesktop.CurrentName,
                Target=Path.GetFullPath(path),InitiallyDisabled=disabled,Reenabled=group.Enabled,ElapsedMilliseconds=clock.ElapsedMilliseconds,Directories=directories,Errors=failures},Formatting.Indented));
            form.Close();return passed?0:1;
        }
        finally {Application.ThreadException-=onError;}
    }
}
