using System.IO.Compression;

namespace Unpacker;

internal static class SelfTests
{
    internal static int Run()
    {
        string root = WorkCache.CreateTemporary("tests");
        Directory.CreateDirectory(root);
        try
        {
            EazyDevirt.CipherStreamTests.Run();
            EazyDevirt.VmRecoveryTests.Run();
            EazFixer.FixerCompatibilityTests.Run();
            CheckRecoveryCheckpoint(root);
            ValidationReportTests.Run(root);
            RecoveryStatisticsTests.Run(root);
            WorkCacheTests.Run();
            InlineControlFlowTests.Run(root);
            CosturaDependencyTests.Run(root);
            PortablePdbCompatibilityTests.Run(root);
            AssemblyInspectorTests.Run(root);
            CheckControlFlow();
            ControlFlowCleanupTests.Run(root);
            ControlFlowGarbageTests.Run(root);
            SourceCompatibilityTests.Run(root);
            SafeReadabilityTests.Run(root);
            SafeProxyPassTests.Run(root);
            RuntimeCompatibilityTests.Run(root);
            CheckRebuiltMapping(root);
            NamingTests.Run(root);
            GenericReadabilityTests.Run(root);
            NameRecoveryContractTests.Run(root);
            AsyncRecoveryTests.Run(root);
            ProgressTests.Run(root);
            if(!WorkerDesktop.CurrentName.StartsWith("Unpacker-",StringComparison.Ordinal))throw new Exception("Worker is on the interactive desktop.");
            Console.WriteLine("PASS: worker is on a private non-interactive desktop: "+WorkerDesktop.CurrentName);
            WorkerDesktop.CheckDialogIsolation();
            string desktopLog=Path.Combine(root,"desktop-child.log");
            if(ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--desktop-probe"},desktopLog,10)!=0
                || !File.ReadAllText(desktopLog).Contains("Desktop: "+WorkerDesktop.CurrentName))throw new Exception("Child process did not inherit private desktop.");
            Console.WriteLine("PASS: bounded private dialog returned off the input desktop; threads and child processes inherit isolation.");
            CheckTimeout(root);
            CheckBundle(root, false); CheckBundle(root, true);
            ExpectInvalid(BuildBundle("../escape.dll", false), Path.Combine(root, "traversal"));
            ExpectInvalid(BuildBundle("C:/escape.dll", false), Path.Combine(root, "absolute"));
            var truncated = BuildBundle("ok.dll", false); Array.Resize(ref truncated, truncated.Length - 1);
            try { BundleExtractor.Extract(truncated, 128, Path.Combine(root, "truncated")); throw new Exception("Accepted truncated manifest"); }
            catch (EndOfStreamException) { }
            var bad = BuildBundle("ok.dll", false); Array.Copy(BitConverter.GetBytes(long.MaxValue), 0, bad, 0, 8);
            try { BundleExtractor.IsBundle(bad, out _); throw new Exception("Accepted bad header offset"); }
            catch (InvalidDataException) { }
            using (var managed = new dnlib.DotNet.ModuleDefUser("ManagedMarker.dll") { Kind = dnlib.DotNet.ModuleKind.Dll })
            {
                managed.Resources.Add(new dnlib.DotNet.EmbeddedResource("marker", bad));
                using var image = new MemoryStream();
                managed.Write(image);
                if (BundleExtractor.IsBundle(image.ToArray(), out _)) throw new Exception("Managed resource marker mistaken for a bundle.");
            }
            Console.WriteLine("PASS: uncompressed/compressed bundle, traversal, absolute path, truncation, offset bounds."); return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckRecoveryCheckpoint(string root)
    {
        string directory=Path.Combine(root,"checkpoint");Directory.CreateDirectory(directory);
        string report=Path.Combine(directory,"result.json");
        var result=new SampleResult {Output="first.exe",Status="partial"};
        RecoveryPipeline.WriteResult(result,directory);
        using(var locked=new System.Threading.ManualResetEventSlim())
        {
            var reader=System.Threading.Tasks.Task.Run(()=>
            {
                using(var stream=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    locked.Set();
                    System.Threading.Thread.Sleep(500);
                }
            });
            locked.Wait();
            result.Output="second.exe";
            RecoveryPipeline.WriteResult(result,directory);
            reader.GetAwaiter().GetResult();
        }
        result.Output="final.exe";result.Status="complete";
        RecoveryPipeline.WriteResult(result,directory);
        var saved=Newtonsoft.Json.JsonConvert.DeserializeObject<SampleResult>(File.ReadAllText(report))!;
        if(saved.Output!="final.exe" || saved.Status!="complete" || File.Exists(report+".pending"))
            throw new Exception("Recovery checkpoint replacement lost data or leaked a pending file.");
        Console.WriteLine("PASS: recovery checkpoints survive a temporary Windows reader lock and retain the final output/status.");
    }

    private static void CheckRebuiltMapping(string root)
    {
        string before=Path.Combine(root,"mapping-before.dll"), after=Path.Combine(root,"mapping-after.dll");
        void WriteFixture(string path,bool reverse,bool omit)
        {
            using var module=new dnlib.DotNet.ModuleDefUser("Mapping.dll");
            var assembly=new dnlib.DotNet.AssemblyDefUser("Mapping",new Version(1,0));assembly.Modules.Add(module);
            var type=new dnlib.DotNet.TypeDefUser("Fixture","Mapping",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
            foreach(string name in reverse?new[]{"Other","Evaluate"}:new[]{"Evaluate","Other"})
            {
                if(omit && name=="Evaluate")continue;
                var method=new dnlib.DotNet.MethodDefUser(name,dnlib.DotNet.MethodSig.CreateStatic(module.CorLibTypes.Int32),
                    dnlib.DotNet.MethodImplAttributes.IL,dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static);
                method.Body=new dnlib.DotNet.Emit.CilBody();
                method.Body.Instructions.Add(dnlib.DotNet.Emit.OpCodes.Ldc_I4_1.ToInstruction());
                method.Body.Instructions.Add(dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction());type.Methods.Add(method);
            }
            module.Write(path);
        }
        WriteFixture(before,false,false);WriteFixture(after,true,false);
        string vm=Path.Combine(root,"mapping-vm.json"), mapped=Path.Combine(root,"mapped.json");
        File.WriteAllText(vm,"[{\"Token\":\"0x06000001\",\"Restored\":true}]");
        if(RebuiltValidation.MapMethods(before,after,vm,mapped)!=1)throw new Exception("Missing mapped method.");
        var token=(string)Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(mapped))[0]["Token"]!;
        if(token!="0x06000002")throw new Exception("Rebuilt mapping reused original token.");
        WriteFixture(after,true,true);
        try {RebuiltValidation.MapMethods(before,after,vm,mapped);throw new Exception("Accepted missing rebuilt VM method.");}
        catch(InvalidDataException) { }
        Console.WriteLine("PASS: rebuilt VM signature mapping tolerates token drift and rejects lost methods.");
    }

    private static void CheckControlFlow()
    {
        using var module = new dnlib.DotNet.ModuleDefUser("flow.dll");
        var assembly = new dnlib.DotNet.AssemblyDefUser("FlowFixture", new Version(1, 0)); assembly.Modules.Add(module);
        var type = new dnlib.DotNet.TypeDefUser("Fixture", "Container", module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public
        }; module.Types.Add(type);
        var method = new dnlib.DotNet.MethodDefUser("Evaluate", dnlib.DotNet.MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32),
            dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static);
        type.Methods.Add(method); method.Body = new dnlib.DotNet.Emit.CilBody();
        var op = dnlib.DotNet.Emit.OpCodes.Pop.ToInstruction();
        var positive = dnlib.DotNet.Emit.OpCodes.Ldc_I4.ToInstruction(42);
        var instructions = new[] {
            dnlib.DotNet.Emit.OpCodes.Ldc_I4.ToInstruction(111),dnlib.DotNet.Emit.OpCodes.Ldc_I4_1.ToInstruction(),
            dnlib.DotNet.Emit.OpCodes.Brtrue.ToInstruction(op),dnlib.DotNet.Emit.OpCodes.Pop.ToInstruction(),
            dnlib.DotNet.Emit.OpCodes.Ldc_I4.ToInstruction(999),dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction(),op,
            dnlib.DotNet.Emit.OpCodes.Ldarg_0.ToInstruction(),dnlib.DotNet.Emit.OpCodes.Brtrue.ToInstruction(positive),
            dnlib.DotNet.Emit.OpCodes.Ldc_I4.ToInstruction(7),dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction(),positive,
            dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction()
        };
        foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
        using var beforeBytes = new MemoryStream(); module.Write(beforeBytes);
        var before = System.Reflection.Assembly.Load(beforeBytes.ToArray()).GetType("Fixture.Container")!.GetMethod("Evaluate")!;
        if (EazFixer.ControlFlowPass.Process(module) == 0) throw new Exception("Control-flow fixture was not processed.");
        assembly.Name = "FlowFixtureAfter";
        using var afterBytes = new MemoryStream(); module.Write(afterBytes);
        var after = System.Reflection.Assembly.Load(afterBytes.ToArray()).GetType("Fixture.Container")!.GetMethod("Evaluate")!;
        foreach (int input in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            int expected = input == 0 ? 7 : 42;
            if ((int)before.Invoke(null, new object[] { input }) != expected || (int)after.Invoke(null, new object[] { input }) != expected)
                throw new Exception("Control-flow behavioral comparison failed.");
        }
        Console.WriteLine("PASS: control-flow transformation preserves fixture results across five inputs.");
    }

    private static void CheckTimeout(string root)
    {
        string log = Path.Combine(root, "timeout.log");
        int code = ProcessRunner.Run(System.Reflection.Assembly.GetExecutingAssembly().Location, new[] { "--test-sleep-tree" }, log, 1);
        if (code != 124) throw new Exception("Timeout did not return 124.");
        string line = File.ReadAllLines(log).First(l => l.StartsWith("Fixture child: "));
        int pid = int.Parse(line.Substring("Fixture child: ".Length));
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(pid);
            if (!child.HasExited) throw new Exception("Timeout left a child process running.");
        }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: worker timeout terminates its descendant process.");
    }

    private static void CheckBundle(string root, bool compressed)
    {
        byte[] fixture = BuildBundle("sub/data.txt", compressed);
        if (!BundleExtractor.IsBundle(fixture, out long header) || header != 128) throw new Exception("Bundle marker");
        string output = Path.Combine(root, compressed ? "compressed" : "plain");
        var paths = BundleExtractor.Extract(fixture, header, output);
        if (paths.Count != 1 || File.ReadAllText(paths[0]) != "hello bundle") throw new Exception("Bundle content");
    }

    private static void ExpectInvalid(byte[] bytes, string output)
    {
        try { BundleExtractor.Extract(bytes, 128, output); throw new Exception("Accepted unsafe bundle path"); }
        catch (InvalidDataException) { }
        if (Directory.Exists(output)) throw new Exception("Extraction wrote before validation");
    }

    private static byte[] BuildBundle(string name, bool compressed)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("hello bundle"), payload = data;
        if (compressed)
        {
            using var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionMode.Compress, true)) deflate.Write(data, 0, data.Length);
            payload = packed.ToArray();
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(128L);
        writer.Write(new byte[] { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32, 0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae });
        stream.Position = 64; writer.Write(payload);
        stream.Position = 128; writer.Write(6U); writer.Write(0U); writer.Write(1); writer.Write("fixture"); writer.Write(new byte[40]);
        writer.Write(64L); writer.Write((long)data.Length); writer.Write(compressed ? (long)payload.Length : 0L); writer.Write((byte)1); writer.Write(name);
        return stream.ToArray();
    }
}
