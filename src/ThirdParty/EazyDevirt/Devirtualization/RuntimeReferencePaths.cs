using AsmResolver.DotNet;

namespace EazyDevirt.Devirtualization;

/// <summary>Finds installed modern runtime reference targets for static metadata resolution.</summary>
internal static class RuntimeReferencePaths
{
    internal static IEnumerable<string> Discover(ModuleDefinition module)
    {
        // A Framework worker can still inspect modern IL. Its resolver must see
        // System.Runtime/System.IO forwarding assemblies and their CoreLib,
        // rather than trying to satisfy these references from Framework's GAC.
        int major = module.AssemblyReferences
            .Where(reference => reference.Name == "System.Runtime" || reference.Name == "System.Private.CoreLib")
            .Select(reference => reference.Version?.Major ?? 0).DefaultIfEmpty().Max();
        if (major < 5) yield break;
        var roots = new[] {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet")
        };
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared)) continue;
            var versions = Directory.GetDirectories(shared)
                .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
                .Where(item => item.Version?.Major == major)
                .OrderByDescending(item => item.Version);
            foreach (var item in versions)
                if (emitted.Add(item.Path)) yield return item.Path;
        }
    }

    private static Version? ParseVersion(string value) => Version.TryParse(value.Split('-')[0], out var version) ? version : null;
}
