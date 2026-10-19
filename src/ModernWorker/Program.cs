using EazFixer;
using EazyDevirt;
using Newtonsoft.Json;

namespace Unpacker.ModernWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        // Harmony's net8 build also runs on newer hosts. Select its JSON patch
        // metadata path before the first patch; BinaryFormatter is removed on 9+.
        AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization",false);
        try {
            WorkCache.Initialize();
            if(args.Length==1 && args[0]=="--test-progress")
            {
                ProgressEvents.Send(3,"info","[密钥] 中文日志合成测试：Int32 = 12345；区域 2；原始校验通过（测试消息）。");
                return 0;
            }
            if(args.Length==1 && args[0]=="--self-test")
            {
                CipherStreamTests.Run();
                try {StacktracePatcher.Patch();}
                finally {StacktracePatcher.UnPatch();}
                Console.WriteLine("PASS: modern stack-frame patch/unpatch without BinaryFormatter.");
                VmRecoveryTests.Run();
                Console.WriteLine("PASS: modern VM tests and stack-frame patch/unpatch without BinaryFormatter.");
                return 0;
            }
            if(args.Length==4 && args[0]=="--vm-worker")
            {
                var progress=new ProgressEvents.Counter(3);
                File.WriteAllText(args[3],VmBridge.Process(args[1],args[2],(message,current,total)=>progress.Report(message,current,total)).ToString());
                return 0;
            }
            if(args.Length>=4 && args[0]=="--validate-worker")
                return ClrValidator.Run(args[1],args[2],args[3],args.Length>4?int.Parse(args[4]):5,args.Length>5?args[5]:null);
            if(args.Length>=4 && args[0]=="--strings-worker")
            {
                using var dependencies=new DependencyLoadScope(args.Length>4?args[4]:args[1],args[1]);
                var post=FixerBridge.Process(args[1],args[2],true);
                File.WriteAllText(args[3],JsonConvert.SerializeObject(post,Formatting.Indented));
                return post.Any(s=>s.Status is "failed" or "partial")?2:0;
            }
            if(args.Length!=3) return 1;
            using var scope=new DependencyLoadScope(args[0]);
            var stages=FixerBridge.Process(args[0],args[1]);
            File.WriteAllText(args[2],JsonConvert.SerializeObject(stages,Formatting.Indented));
            return stages.Any(s=>s.Status=="failed")?2:0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
