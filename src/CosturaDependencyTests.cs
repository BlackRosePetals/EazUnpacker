using System.IO.Compression;
using dnlib.DotNet;

namespace Unpacker;

internal static class CosturaDependencyTests
{
    internal static void Run(string root)
    {
        string directory = Path.Combine(root, "costura"); Directory.CreateDirectory(directory);
        byte[] Dependency(string name)
        {
            using var module = new ModuleDefUser(name + ".dll");
            var assembly = new AssemblyDefUser(name, new Version(1, 2, 3, 4)); assembly.Modules.Add(module);
            using var bytes = new MemoryStream(); module.Write(bytes); return bytes.ToArray();
        }
        string Input(string name, byte[] dependency)
        {
            using var module = new ModuleDefUser(name + ".dll");
            var assembly = new AssemblyDefUser(name, new Version(1, 0)); assembly.Modules.Add(module);
            using var bytes = new MemoryStream();
            using (var deflate = new DeflateStream(bytes, CompressionMode.Compress, true)) deflate.Write(dependency, 0, dependency.Length);
            module.Resources.Add(new EmbeddedResource("costura.alias.dll.compressed", bytes.ToArray(), ManifestResourceAttributes.Private));
            string path = Path.Combine(directory, name + ".dll"); module.Write(path); return path;
        }
        var payload = Dependency("ActualDependency"); var input = Input("Container", payload);
        if (CosturaDependencies.Extract(input, directory) != 1 || CosturaDependencies.Extract(input, directory) != 1 ||
            !Enumerable.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"ActualDependency.dll")), payload))
            throw new Exception("Costura resource was not extracted byte-for-byte under its managed identity.");
        File.WriteAllText(Path.Combine(directory,"ActualDependency.dll"), "conflict");
        try { CosturaDependencies.Extract(input, directory); throw new Exception("Conflicting dependency was overwritten."); }
        catch (InvalidDataException) { }
        string unsafeInput = Input("Unsafe", Dependency("..\\Escape"));
        try { CosturaDependencies.Extract(unsafeInput, directory); throw new Exception("Unsafe managed identity was accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: Costura extraction preserves exact bytes/identities, is repeatable and rejects conflicts/traversal.");
    }
}
