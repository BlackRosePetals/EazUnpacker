using System.IO.Compression;
using System.Security.Cryptography;
using dnlib.DotNet;
using Newtonsoft.Json;

namespace Unpacker;

/// <summary>Materializes managed Costura resources for static resolution and isolated validation.</summary>
internal static class CosturaDependencies
{
    internal static int Extract(string input, string directory)
    {
        using var module = ModuleDefMD.Load(input);
        var evidence = new List<object>();
        long total = 0;
        foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r =>
            r.Name.String.StartsWith("costura.", StringComparison.OrdinalIgnoreCase) &&
            (r.Name.String.EndsWith(".dll.compressed", StringComparison.OrdinalIgnoreCase) ||
             r.Name.String.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))
        {
            if (evidence.Count >= 512) throw new InvalidDataException("Too many embedded Costura dependencies.");
            byte[] payload = resource.CreateReader().ToArray();
            if (resource.Name.String.EndsWith(".compressed", StringComparison.OrdinalIgnoreCase))
            {
                using var source = new MemoryStream(payload);
                using var inflater = new DeflateStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                byte[] buffer = new byte[65536]; int count;
                while ((count = inflater.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > 64 * 1024 * 1024) throw new InvalidDataException("Embedded dependency exceeds size limit.");
                    output.Write(buffer, 0, count);
                }
                payload = output.ToArray();
            }
            total += payload.Length;
            if (total > 256 * 1024 * 1024) throw new InvalidDataException("Embedded dependencies exceed total size limit.");
            using var dependency = ModuleDefMD.Load(payload);
            string name = dependency.Assembly?.Name.String ?? throw new InvalidDataException("Embedded Costura DLL has no assembly identity.");
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name))
                throw new InvalidDataException("Unsafe embedded dependency name.");
            string outputPath = Path.Combine(directory, name + ".dll");
            if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Embedded dependency would overwrite its input.");
            if (File.Exists(outputPath) && !Enumerable.SequenceEqual(File.ReadAllBytes(outputPath), payload))
                throw new InvalidDataException("Conflicting embedded dependency: " + name);
            File.WriteAllBytes(outputPath, payload);
            using var hash = SHA256.Create();
            evidence.Add(new { Resource = resource.Name.String, Assembly = dependency.Assembly!.FullName,
                Output = outputPath, Sha256 = BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "").ToLowerInvariant() });
        }
        if (evidence.Count != 0) File.WriteAllText(Path.Combine(directory, "costura-dependencies.json"), JsonConvert.SerializeObject(evidence, Formatting.Indented));
        return evidence.Count;
    }
}
