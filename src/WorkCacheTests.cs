namespace Unpacker;

internal static class WorkCacheTests
{
    internal static void Run()
    {
        string expected=Path.Combine(WorkCache.ProgramDirectory,"temp"),one=WorkCache.ForOutput("fixture.exe");
        if(!one.StartsWith(expected+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||one!=WorkCache.ForOutput(Path.GetFullPath("fixture.exe"))||one==WorkCache.ForOutput("other.exe"))throw new Exception("Program-local cache identity is unstable.");
        string temporary=WorkCache.CreateTemporary("test-path");
        try{if(!temporary.StartsWith(expected+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(temporary))throw new Exception("Temporary path escaped the program directory.");}
        finally{Directory.Delete(temporary);}
        foreach(string invalid in new[]{"../escape","C:\\elsewhere","","with/slash"})
        {try{WorkCache.CreateTemporary(invalid);throw new Exception("Unsafe temporary category accepted.");}catch(ArgumentException){}}
        if(Environment.GetEnvironmentVariable("TEMP")!=expected||Environment.GetEnvironmentVariable("TMP")!=expected)throw new Exception("Child temporary environment not portable.");
        Console.WriteLine("PASS: cache, analysis and child temporary roots remain under the executable directory with stable output identities.");
    }
}
