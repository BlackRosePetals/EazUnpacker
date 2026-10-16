using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using EazFixer.Processors;

namespace EazFixer
{
    /// <summary>A stage outcome with an explicit applicability/success distinction.</summary>
    public sealed class FixerStageResult
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public int Changes { get; set; }
    }

    /// <summary>Runs the retained reference processors without deleting VM dependencies.</summary>
    public static class FixerBridge
    {
        public static List<FixerStageResult> Process(string input, string output, bool stringsOnly = false, Action<string,string,int> progress = null, bool runControlFlow = true)
        {
            Flags.InFile = Path.GetFullPath(input);
            Flags.OutFile = Path.GetFullPath(output);
            Flags.KeepTypes = Flags.VirtFix = Flags.PreserveAll = true;
            var processors = stringsOnly ? new ProcessorBase[] {new StringFixer()}
                : new ProcessorBase[] {new StringFixer(),new ResourceResolver(),new Processors.AssemblyResolver()};
            var results = new List<FixerStageResult>();
            var ctx = new EazContext(Flags.InFile, processors);
            try
            {
                StacktracePatcher.Patch();
                foreach (var proc in processors){progress?.Invoke(proc.GetType().Name+" 初始化","start",0);proc.Initialize(ctx);}
                foreach (var proc in processors)
                {
                    int before = CountStrings(ctx.Module);
                    int resources = ctx.Module.Resources.Count;
                    progress?.Invoke(proc.GetType().Name,"start",0);
                    if (proc.Initialized) proc.Process();
                    if (proc.Processed) proc.Cleanup();
                    int changes = proc is StringFixer ? CountStrings(ctx.Module)-before : ctx.Module.Resources.Count-resources;
                    results.Add(new FixerStageResult {
                        Name = proc.GetType().Name,
                        Status = proc.Processed ? "success" : !proc.Initialized ? "not-detected" : "failed",
                        Detail = proc.ErrorMessage ?? "Processed; runtime types preserved.", Changes = changes });
                    progress?.Invoke(proc.GetType().Name,results[results.Count-1].Status,changes);
                }
                if(runControlFlow)
                {
                    progress?.Invoke("ControlFlow","start",0);
                    int flowChanges=ControlFlowPass.Process(ctx.Module);
                    progress?.Invoke("ControlFlow","success",flowChanges);
                    results.Add(new FixerStageResult{Name="ControlFlow",Status="success",Detail="Constant branches folded; protected regions preserved.",Changes=flowChanges});
                }
                progress?.Invoke("写入恢复程序集","start",0);
                ctx.Module.Write(Flags.OutFile, new ModuleWriterOptions(ctx.Module) { MetadataOptions = { Flags = MetadataFlags.PreserveAll } });
                return results;
            }
            finally
            {
                StacktracePatcher.UnPatch();
                ctx.Module.Dispose();
            }
        }

        private static int CountStrings(ModuleDef module) => module.GetTypes().SelectMany(t=>t.Methods)
            .Where(m=>m.HasBody).Sum(m=>m.Body.Instructions.Count(i=>i.OpCode.Code==Code.Ldstr));
    }
}
