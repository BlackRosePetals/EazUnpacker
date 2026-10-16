using System.Reflection.Metadata;
using ICSharpCode.Decompiler.DebugInfo;
using SequencePoint=ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace Unpacker;

internal sealed class RecoveredDebugInfo : IDebugInfoProvider
{
    private readonly Dictionary<MethodDefinitionHandle,List<Variable>> variables=new();
    public string Description=>"Recovered typed local names";
    public string SourceFileName { get; }

    internal RecoveredDebugInfo(string pdb)
    {
        SourceFileName=pdb;
        using var stream=File.OpenRead(pdb);
        using var provider=MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader=provider.GetMetadataReader();
        foreach(var scopeHandle in reader.LocalScopes)
        {
            var scope=reader.GetLocalScope(scopeHandle);
            if(!variables.TryGetValue(scope.Method,out var locals))variables.Add(scope.Method,locals=new());
            foreach(var localHandle in scope.GetLocalVariables())
            {
                var local=reader.GetLocalVariable(localHandle);
                if(locals.All(v=>v.Index!=local.Index))locals.Add(new Variable(local.Index,reader.GetString(local.Name)));
            }
        }
    }
    public IList<SequencePoint> GetSequencePoints(MethodDefinitionHandle method)=>Array.Empty<SequencePoint>();
    public IList<Variable> GetVariables(MethodDefinitionHandle method)=>variables.TryGetValue(method,out var locals)?locals:Array.Empty<Variable>();
    public bool TryGetName(MethodDefinitionHandle method,int index,out string name)
    {
        name=variables.TryGetValue(method,out var locals)?locals.FirstOrDefault(v=>v.Index==index).Name??"":"";
        return name.Length>0;
    }
    public bool TryGetExtraTypeInfo(MethodDefinitionHandle method,int index,out PdbExtraTypeInfo extraTypeInfo)
    {extraTypeInfo=default;return false;}
}
