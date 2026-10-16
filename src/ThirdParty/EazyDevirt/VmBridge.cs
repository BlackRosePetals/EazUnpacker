using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Code.Cil;
using EazyDevirt.Devirtualization;
using EazyDevirt.Devirtualization.Options;

namespace EazyDevirt;

/// <summary>Runs the devirtualizer in a per-input worker, preserving unrecovered methods.</summary>
public static class VmBridge
{
    public static int Process(string input, string output,Action<string,int,int>? progress=null)
    {
        var options = new DevirtualizationOptions {
            Assembly = new FileInfo(input), OutputPath = new DirectoryInfo(Path.GetDirectoryName(output)!),
            Verbosity = 2, PreserveAll = true, KeepTypes = true, NoVerify = false,
            SaveAnyway = true, OnlySaveDevirted = true, RequireDepsForGenericMethods = true
        };
        var ctx = new DevirtualizationContext(options);
        ctx.Progress=progress;
        DevirtualizationContext.Instance = ctx;
        bool completed = new Devirtualizer(ctx).Run();
        int restored = ctx.VMMethods?.Count(m=>m.SuccessfullyDevirtualized) ?? 0;
        Console.WriteLine($"VM restored: {restored}/{ctx.VMMethods?.Count ?? 0}; pipeline complete: {completed}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "vm-methods.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(ctx.VMMethods?.Select(m => new {
                Token = "0x" + m.Parent.MetadataToken.ToUInt32().ToString("X8"), Method = m.Parent.FullName,
                Restored = m.SuccessfullyDevirtualized, m.EncryptedRegions, m.Failure,
                Instructions = m.Instructions.Count, ExceptionHandlers = m.ExceptionHandlers.Count
            }), Newtonsoft.Json.Formatting.Indented));
        if (restored == 0) return 0;
        ctx.Module.Write(output, new ManagedPEImageBuilder(new DotNetDirectoryFactory(MetadataBuilderFlags.PreserveAll) {
            MethodBodySerializer = new CilMethodBodySerializer { ComputeMaxStackOnBuildOverride = false }
        }));
        return restored;
    }
}
