using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions;
using EazyDevirt.Core.IO;
using Org.BouncyCastle.Math;

namespace EazyDevirt.Devirtualization.Pipeline;

internal sealed class ResourceParsing : StageBase
{
    private ManifestResource? resource;
    private byte[] key = Array.Empty<byte>();
    private string modulus = "";

    public ResourceParsing(DevirtualizationContext ctx) : base(ctx) { }

    private protected override bool Init()
    {
        foreach (var method in Ctx.Module.GetAllTypes().SelectMany(t=>t.Methods))
        {
            if(!method.IsStatic || method.Signature?.ReturnType.FullName!="System.IO.Stream" || method.Parameters.Count!=0 || method.CilMethodBody==null)continue;
            var ins=method.CilMethodBody.Instructions;
            if(!ins.Any(i=>i.Operand is IMethodDescriptor m && m.Name=="GetManifestResourceStream"))continue;
            string? name=ins.Where(i=>i.OpCode.Code==CilCode.Ldstr).Select(i=>i.Operand as string)
                .FirstOrDefault(s=>Ctx.Module.Resources.Any(r=>r.Name==s));
            var field=ins.Where(i=>i.OpCode.Code==CilCode.Ldtoken).Select(i=>i.Operand).OfType<FieldDefinition>().FirstOrDefault(f=>f.FieldRva is DataSegment);
            var modulusMethod=ins.Select(i=>i.Operand).OfType<MethodDefinition>().FirstOrDefault(m=>m.IsStatic && m.Parameters.Count==0 && m.Signature?.ReturnType.FullName=="System.String");
            string? value=modulusMethod?.CilMethodBody?.Instructions.FirstOrDefault(i=>i.OpCode.Code==CilCode.Ldstr)?.Operand as string;
            if(name==null || field==null || value==null)continue;
            var vmGetter=method.DeclaringType!.Methods.FirstOrDefault(m=>m.IsStatic && m.Parameters.Count==0 && m.Signature?.ReturnsValue==true &&
                m.Signature.ReturnType.ToTypeDefOrRef()?.Resolve() is { } t &&
                t.Methods.Any(x=>x.Signature?.ReturnType.FullName=="System.Void" && x.Parameters.Count==3 &&
                    x.Parameters[0].ParameterType.FullName=="System.IO.Stream" && x.Parameters[1].ParameterType.FullName=="System.Int64"));
            if(vmGetter==null)continue;
            resource=Ctx.Module.Resources.First(r=>r.Name==name);
            key=((DataSegment)field.FieldRva!).Data;modulus=value;
            Ctx.VMDeclaringType=vmGetter.Signature!.ReturnType.ToTypeDefOrRef()!.Resolve()!;
            Ctx.VMResourceGetterMdToken=method.MetadataToken;
            Ctx.Console.Info($"VM getter {method.MetadataToken}; runtime {Ctx.VMDeclaringType.MetadataToken}; resource {name}");
            return true;
        }
        Ctx.Console.Error("No supported VM resource getter found by signature and resource references.");return false;
    }

    public override bool Run()
    {
        if(!Init())return false;
        var tail=Convert.FromBase64String(modulus);
        var bytes=new byte[key.Length+tail.Length];Buffer.BlockCopy(key,0,bytes,0,key.Length);Buffer.BlockCopy(tail,0,bytes,key.Length,tail.Length);
        var mod=new BigInteger(1,bytes);var exp=BigInteger.ValueOf(65537);
        var data=resource!.GetData()!;
        Ctx.VMStream=new VMCipherStream(data,mod,exp);Ctx.VMResolverStream=new VMCipherStream(data,mod,exp);
        return true;
    }
}
