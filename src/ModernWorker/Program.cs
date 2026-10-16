using EazFixer;
using Newtonsoft.Json;

namespace Unpacker.ModernWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try {
            if(args.Length!=3) return 1;
            var stages=FixerBridge.Process(args[0],args[1]);
            File.WriteAllText(args[2],JsonConvert.SerializeObject(stages,Formatting.Indented));
            return stages.Any(s=>s.Status=="failed")?2:0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
