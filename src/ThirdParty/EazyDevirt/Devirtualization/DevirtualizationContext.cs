using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Core.IO;
using EazyDevirt.Devirtualization.Options;
using EazyDevirt.Logging;
using EazyDevirt.PatternMatching;

namespace EazyDevirt.Devirtualization;

internal record DevirtualizationContext
{
    // got lazy
    public static DevirtualizationContext Instance { get; set; } = null!;
    
    public DevirtualizationOptions Options { get; }
    public ModuleDefinition Module { get; }
    public PatternMatcher PatternMatcher { get; }
    public ConsoleLogger Console { get; }
    public Action<string,int,int>? Progress {get;set;}
    public ReferenceImporter Importer { get; }
    
    public MetadataToken VMResourceGetterMdToken { get; set; }
    
    /// <summary>
    /// VM Type fields set in the vm type's constructor.
    /// Used in pattern matching opcodes.
    /// </summary>
    public Dictionary<FieldDefinition, ITypeDefOrRef> VMTypeFields { get; set; } = null!;

    /// <summary>
    /// VM method locals field.
    /// Used in pattern matching ldloc and stloc opcodes.
    /// </summary>
    public FieldDefinition VMLocalsField { get; set; } = null!;

    /// <summary>
    /// VM method arguments field.
    /// Used in pattern matching ldarg and starg opcodes.
    /// </summary>
    public FieldDefinition VMArgumentsField { get; set; } = null!;
    public MethodDefinition VMExecuteVMMethod { get; set; } = null!;
    public TypeDefinition VMDeclaringType { get; set; } = null!;
    public VMCipherStream VMStream { get; set; } = null!;
    public VMCipherStream VMResolverStream { get; set; } = null!;
    public int PositionCryptoKey { get; set; }
    public int MethodCryptoKey { get; set; }

    public List<VMMethod> VMMethods { get; set; } = null!;
    public VMHeaderLayout HeaderLayout { get; set; } = null!;
    public Dictionary<int, AsmResolver.PE.DotNet.Cil.CilOperandType> OperandLayout { get; set; } = new();
    public Dictionary<byte, EazyDevirt.Core.Architecture.InlineOperands.VMInlineOperandType> InlineLayout { get; set; } = new();
    public byte TokenTag { get; set; }
    public int MethodStaticMask { get; set; }
    public int MethodGenericMask { get; set; }

    public DevirtualizationContext(DevirtualizationOptions opts)
    {
        Options = opts;
        Module = ModuleDefinition.FromFile(Options.Assembly.FullName);
        if(Module.MetadataResolver is DefaultMetadataResolver metadata && metadata.AssemblyResolver is AssemblyResolverBase resolver)
        {
            resolver.SearchDirectories.Add(Path.GetDirectoryName(Options.Assembly.FullName)!);
            resolver.SearchDirectories.Add(AppDomain.CurrentDomain.BaseDirectory);
            resolver.SearchDirectories.Add(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        }
        PatternMatcher = new PatternMatcher();
        Console = new ConsoleLogger();
        Importer = Module.DefaultImporter;
        // hacky fix for resolved types within current assembly / module
        Importer.ImportScope(new AssemblyReference(Module.Assembly!));
    }
}

