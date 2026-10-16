using System.Reflection;

namespace Unpacker;

// Scoped worker resolution of exact managed identities from declared probing paths.
internal sealed class DependencyLoadScope : IDisposable
{
    private readonly string[] directories;
    private readonly HashSet<string> resolving=new(StringComparer.OrdinalIgnoreCase);
    internal DependencyLoadScope(string originalInput,string? assembly=null)
    {
        directories=SourceCompiler.DependencyDirectories(assembly??originalInput,originalInput);
        AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
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
                foreach(string directory in directories)
                foreach(string extension in new[]{".dll",".exe"})
                {
                    string candidate=Path.Combine(directory,requested.Name+extension);
                    if(!File.Exists(candidate))continue;
                    AssemblyName actual;
                    try {actual=AssemblyName.GetAssemblyName(candidate);}catch(BadImageFormatException){continue;}
                    if(!string.Equals(requested.Name,actual.Name,StringComparison.OrdinalIgnoreCase)
                        || requested.Version!=null && requested.Version!=actual.Version
                        || !string.Equals(requested.CultureName??"",actual.CultureName??"",StringComparison.OrdinalIgnoreCase)
                        || !Enumerable.SequenceEqual(requested.GetPublicKeyToken()??Array.Empty<byte>(),actual.GetPublicKeyToken()??Array.Empty<byte>()))continue;
                    try {return Assembly.LoadFrom(candidate);}
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
