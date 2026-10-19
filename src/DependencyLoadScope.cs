using System.Reflection;
using System.Xml.Linq;

namespace Unpacker;

// Scoped worker resolution from declared probing paths. CoreCLR permits a
// higher loaded version; Framework keeps its exact-version binding policy.
internal sealed class DependencyLoadScope : IDisposable
{
    private readonly string[] directories;
    private readonly HashSet<string> resolving=new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<object> Bindings=new();
#if NETCOREAPP
    private const bool AllowHigherVersions=true;
#else
    private const bool AllowHigherVersions=false;
#endif
    internal DependencyLoadScope(string originalInput,string? assembly=null)
    {
        directories=DependencyDirectories(assembly??originalInput,originalInput);
        AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
    }

    internal static bool MatchesIdentity(AssemblyName requested,AssemblyName actual,bool allowHigherVersions)=>
        string.Equals(requested.Name,actual.Name,StringComparison.OrdinalIgnoreCase)
        && (requested.Version==null || requested.Version==actual.Version
            || allowHigherVersions && actual.Version!=null && actual.Version>requested.Version)
        && string.Equals(requested.CultureName??"",actual.CultureName??"",StringComparison.OrdinalIgnoreCase)
        && Enumerable.SequenceEqual(requested.GetPublicKeyToken()??Array.Empty<byte>(),actual.GetPublicKeyToken()??Array.Empty<byte>());

    internal static string[] DependencyDirectories(string assembly,string originalInput)
    {
        string originalDirectory=Path.GetDirectoryName(Path.GetFullPath(originalInput))!;
        var paths=new List<string>{Path.GetDirectoryName(Path.GetFullPath(assembly))!};
        foreach(string path in (Environment.GetEnvironmentVariable("UNPACKER_DEPENDENCY_PATHS")??"").Split(Path.PathSeparator))
            if(!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path))paths.Add(Path.GetFullPath(path));
        paths.Add(originalDirectory);
        string config=originalInput+".config";
        if(File.Exists(config))
        {
            // Read data only; do not activate config extensions or AppDomain managers.
            var settings=new System.Xml.XmlReaderSettings {DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null};
            using var reader=System.Xml.XmlReader.Create(config,settings);
            var xml=XDocument.Load(reader);
            XNamespace ns="urn:schemas-microsoft-com:asm.v1";
            foreach(string path in xml.Descendants(ns+"probing").SelectMany(p=>((string?)p.Attribute("privatePath")??"").Split(';')))
            {
                if(string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))continue;
                string directory=Path.GetFullPath(Path.Combine(originalDirectory,path.Trim()));
                if(directory.StartsWith(originalDirectory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(directory))paths.Add(directory);
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private Assembly? Resolve(object? sender,ResolveEventArgs args)
    {
        lock(resolving)
        {
            if(!resolving.Add(args.Name))return null;
            try
            {
                var requested=new AssemblyName(args.Name);
                if(string.IsNullOrEmpty(requested.Name) || Path.GetFileName(requested.Name)!=requested.Name)return null;
                // Eaz runtimes sometimes request their own identity from a LoadFile
                // context. Reuse that instance instead of loading a second copy.
                if(args.RequestingAssembly?.FullName==requested.FullName)return args.RequestingAssembly;
                var loaded=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.FullName==requested.FullName);
                if(loaded!=null)return loaded;
                // Prefer an exact on-disk identity over a roll-forward candidate
                // in an earlier probing directory, then apply CoreCLR's policy.
                for(int pass=0;pass<(AllowHigherVersions?2:1);pass++)
                foreach(string directory in directories)
                foreach(string extension in new[]{".dll",".exe"})
                {
                    string candidate=Path.Combine(directory,requested.Name+extension);
                    if(!File.Exists(candidate))continue;
                    AssemblyName actual;
                    try {actual=AssemblyName.GetAssemblyName(candidate);}catch(BadImageFormatException){continue;}
                    if(!MatchesIdentity(requested,actual,pass==1))continue;
                    try
                    {
                        var dependency=Assembly.LoadFrom(candidate);
                        Bindings.Add(new {Requested=args.Name,Loaded=dependency.FullName,Path=candidate,
                            Basis=pass==0?"Exact managed identity in a declared probing directory":"CoreCLR equal-or-higher version binding; name/culture/public key token preserved"});
                        return dependency;
                    }
                    catch(BadImageFormatException) { }
                    catch(FileLoadException) { }
                }
                return null;
            }
            finally{resolving.Remove(args.Name);}
        }
    }
    public void Dispose()=>AppDomain.CurrentDomain.AssemblyResolve-=Resolve;
}
