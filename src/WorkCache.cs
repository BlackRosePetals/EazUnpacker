using System.Security.Cryptography;
using System.Text;

namespace Unpacker;

internal static class WorkCache
{
    internal static string ForOutput(string output)
    {
        using var sha = SHA256.Create();
        string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(output).ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unpacker", "work", key);
    }
}
