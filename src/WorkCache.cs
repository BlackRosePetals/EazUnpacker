using System.Security.Cryptography;
using System.Text;

namespace Unpacker;

internal static class WorkCache
{
    internal static string ProgramDirectory
    {
        get
        {
            var directory=new DirectoryInfo(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
            if(directory.Name.Equals("modern",StringComparison.OrdinalIgnoreCase)&&directory.Parent?.Name.Equals("worker",StringComparison.OrdinalIgnoreCase)==true&&File.Exists(Path.Combine(directory.Parent.Parent!.FullName,"Unpacker.exe")))return directory.Parent.Parent.FullName;
            if(directory.Name.Equals("worker",StringComparison.OrdinalIgnoreCase)&&directory.Parent!=null&&File.Exists(Path.Combine(directory.Parent.FullName,"Unpacker.exe")))return directory.Parent.FullName;
            return directory.FullName;
        }
    }
    internal static string TempRoot=>Path.Combine(ProgramDirectory,"temp");
    internal static void Initialize()
    {
        Directory.CreateDirectory(TempRoot);
        Environment.SetEnvironmentVariable("TEMP",TempRoot);Environment.SetEnvironmentVariable("TMP",TempRoot);
    }
    internal static string CreateTemporary(string category)
    {
        if(string.IsNullOrWhiteSpace(category)||category.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'))throw new ArgumentException("Invalid temporary category.",nameof(category));
        string path=Path.Combine(TempRoot,category+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;
    }
    internal static string ForOutput(string output)
    {
        using var sha = SHA256.Create();
        string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(output).ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
        return Path.Combine(TempRoot,"work",key);
    }
}
