using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace EazyDevirt.Core.Architecture;

internal record VMMethod(MethodDefinition Parent, string EncodedMethodKey, long MethodKey)
{
    public MethodDefinition Parent { get; } = Parent;
    public string EncodedMethodKey { get; } = EncodedMethodKey;
    public long MethodKey { get; } = MethodKey;
    public VMMethodInfo MethodInfo { get; set; } = null!;
    public List<VMExceptionHandler> VMExceptionHandlers { get; set; } = new();
    
    public List<CilExceptionHandler> ExceptionHandlers { get; set; } = new();
    // public List<Parameter> Parameters { get; set; } = new();
    public List<CilLocalVariable> Locals { get; set; } = new();
    public List<CilInstruction> Instructions { get; set; } = new();
    public List<uint> VirtualOffsets { get; set; } = new();
    public int EncryptedRegions { get; set; }
    public string? Failure { get; set; }
    public CilInstruction? ExecutorInstruction { get; set; }
    public MethodDefinition? RecoveredAdapter { get; set; }
    public List<MethodDefinition> AccessBridges { get; } = new();
    public TypeDefinition? AdapterDeclaringType { get; set; }
    public TypeSignature[]? ArgumentTypes { get; set; }
    public bool Inlined => RecoveredAdapter != null;
    
    
    public bool SuccessfullyDevirtualized { get; set; }
    public bool HasHomomorphicEncryption { get; set; }
    public int CodeSize { get; set; }
    public long CodePosition { get; set; }
    public long InitialCodeStreamPosition { get; set; }
    
    public override string ToString() =>
        $"Parent: {Parent.MetadataToken} | EncodedMethodKey: {EncodedMethodKey} | MethodKey: 0x{MethodKey:X} | " +
        $"MethodInfo: [{MethodInfo}] | VMExceptionHandlers: [{string.Join(", ", VMExceptionHandlers)}] | " +
        $"ExceptionHandlers: {ExceptionHandlers?.Count} | Locals: {Locals?.Count} | " +
        $"Instructions: {Instructions?.Count}";
}

// TODO: The order of these fields are scrambled across samples. See issue #3
internal record VMMethodInfo
{
    private readonly VMHeaderLayout _layout;
    private readonly int _firstType;
    private readonly int _secondType;
    public int VMDeclaringType => _layout.FirstTypeIsReturn ? _secondType : _firstType;
    public string Name { get; }
    public byte BindingFlags { get; }
    public bool IsStatic => (BindingFlags & _layout.StaticMask) > 0;
    public bool IsSecurityCritical => (BindingFlags & 4) > 0;
    public bool IsUnknownFlag2 => (BindingFlags & 8) > 0;
    public int VMReturnType => _layout.FirstTypeIsReturn ? _firstType : _secondType;
    public List<VMLocal> VMLocals { get; }
    public List<VMParameter> VMParameters { get; }

    public ITypeDefOrRef DeclaringType { get; set; } = null!;
    public ITypeDefOrRef ReturnType { get; set; } = null!;
    
    public VMMethodInfo(BinaryReader reader, VMHeaderLayout layout)
    {
        _layout=layout;
        Name="";VMLocals=new();VMParameters=new();int types=0;
        foreach(var field in layout.Fields)
        {
            switch(field) {
                case VMHeaderField.Type:
                    if(types++==0)_firstType=reader.ReadInt32();else _secondType=reader.ReadInt32();break;
                case VMHeaderField.Name:Name=reader.ReadString();break;
                case VMHeaderField.Flags:BindingFlags=reader.ReadByte();break;
                case VMHeaderField.Locals:
                    int locals=reader.ReadInt16();if(locals<0)throw new InvalidDataException("Negative VM locals.");
                    for(int i=0;i<locals;i++)VMLocals.Add(new VMLocal(reader.ReadInt32()));break;
                case VMHeaderField.Parameters:
                    int parameters=reader.ReadInt16();if(parameters<0)throw new InvalidDataException("Negative VM parameters.");
                    for(int i=0;i<parameters;i++) {
                        bool flag=false;int type;
                        if(layout.ParameterBooleanFirst) {flag=reader.ReadBoolean();type=reader.ReadInt32();}
                        else {type=reader.ReadInt32();flag=reader.ReadBoolean();}
                        VMParameters.Add(new VMParameter(type,flag));
                    }
                    break;
            }
        }
    }

    public override string ToString() =>
        $"VMDeclaringType: 0x{VMDeclaringType:X} | Name: {Name} | BindingFlags: {BindingFlags} | " +
        $"IsStatic: {IsStatic} | IsSecurityCritical: {IsSecurityCritical} | IsUnknownFlag2: {IsUnknownFlag2} | " +
        $"VMReturnType: 0x{VMReturnType:X} | VMLocals: [{string.Join(", ", VMLocals)}] | VMParameters: [{string.Join(", ", VMParameters)}] | " +
        $"DeclaringType: {DeclaringType?.FullName} | ReturnType: {ReturnType?.FullName}";
}

internal record VMLocal(int VMType)
{
    public int VMType { get; } = VMType;
    
    public override string ToString() => $"VMType: 0x{VMType:X}";
}

internal record VMParameter(int VMType, bool In)
{
    public int VMType { get; } = VMType;
    public bool In { get; } = In;
    
    public override string ToString() => $"VMType: 0x{VMType:X} | In: {In}";
}

