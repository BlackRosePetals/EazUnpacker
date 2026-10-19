using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using EazFixer.Processors;

namespace EazFixer
{
    internal class EazContext : IEnumerable<ProcessorBase>
    {
        public ModuleDef Module;
        public Assembly Assembly;
        public ProcessorBase[] Processors;
        public string RuntimePath;
        public bool RuntimeInitializerSuppressed;

        public EazContext(string file, ProcessorBase[] procs)
        {
            if (!File.Exists(file)) throw new Exception($"Failed (File: {file} does not exist)");

            Module = ModuleDefMD.Load(file);
            PreserveSerializedMaxStack(Module);
            // Decoder invocation must not run the application's unrelated startup.
            // Work on a second module: the output model above keeps its original
            // <Module> initializer and every business initialization call intact.
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(Flags.OutFile ?? file));
            var runtimeDirectory = Path.Combine(outputDirectory, "decoder-runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtimeDirectory);
            RuntimePath = Path.Combine(runtimeDirectory, Path.GetFileName(file));
            using (var runtimeModule = ModuleDefMD.Load(file))
            {
                PreserveSerializedMaxStack(runtimeModule);
                RuntimeInitializerSuppressed = SuppressRuntimeInitializer(runtimeModule);
                runtimeModule.Write(RuntimePath, new ModuleWriterOptions(runtimeModule) { MetadataOptions = { Flags = MetadataFlags.PreserveAll } });
            }
            File.WriteAllText(Path.Combine(runtimeDirectory, "decoder-runtime-info.txt"),
                "Source: " + Path.GetFullPath(file) + Environment.NewLine + "Decoder runtime: " + RuntimePath + Environment.NewLine +
                "Module initializer suppressed in decoder runtime only: " + RuntimeInitializerSuppressed + Environment.NewLine +
                "The output model retains the original module initializer. Metadata tokens, MVID, resource data and assembly identity are preserved." + Environment.NewLine);
            Assembly = Assembly.LoadFile(RuntimePath);
            Processors = procs;
        }

        internal static bool SuppressRuntimeInitializer(ModuleDef runtimeModule)
        {
            var initializer = runtimeModule.GlobalType?.FindStaticConstructor();
            if (initializer == null || !initializer.HasBody) return false;
            var serializedMaxStack = initializer.Body.MaxStack;
            initializer.Body = new CilBody { MaxStack = serializedMaxStack, KeepOldMaxStack = serializedMaxStack > 0 };
            initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return true;
        }

        internal static void PreserveSerializedMaxStack(ModuleDef module)
        {
            // dnlib's simple stack calculator rejects some CLR-valid obfuscated
            // joins. String replacement does not increase the evaluation-stack
            // peak, so retain each known serialized upper bound, not a guessed one.
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                    if (method.HasBody && method.Body.MaxStack > 0) method.Body.KeepOldMaxStack = true;
        }

        internal EazContext(ModuleDef module, Assembly assembly, ProcessorBase[] procs)
        {
            Module = module;
            Assembly = assembly;
            Processors = procs;
        }

        //allow enumerating Processors
        public IEnumerator<ProcessorBase> GetEnumerator() => Processors.AsEnumerable().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        //allow easily getting other processors by type
        public T Get<T>() where T : ProcessorBase => (T)this[typeof(T)];
        public ProcessorBase this[Type index] => Processors.Single(a => a.GetType() == index);
    }
}
