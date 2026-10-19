using EazyDevirt.Core.Abstractions;
using EazyDevirt.Devirtualization.Pipeline;

namespace EazyDevirt.Devirtualization;

internal class Devirtualizer
{
    public Devirtualizer(DevirtualizationContext ctx)
    {
        Ctx = ctx;
        Pipeline = new List<StageBase>
        {
            new ResourceParsing(ctx),
            new OpCodeMapping(ctx),
            new MethodDiscovery(ctx),
            new MethodDevirtualizer(ctx),
        };
    }
    
    private DevirtualizationContext Ctx { get; }
    private List<StageBase> Pipeline { get; }

    public bool Run()
    {
        foreach (var stage in Pipeline)
        {
            string label=stage.Name switch {"ResourceParsing"=>"资源与 VM 结构识别","OpCodeMapping"=>"VM 指令映射","MethodDiscovery"=>"VM 方法扫描","MethodDevirtualizer"=>"VM 方法还原",_=>stage.Name};
            Ctx.Progress?.Invoke(label,0,0);
            Ctx.Console.Summary("[VM] "+label+"…");
            Ctx.Console.Info($"Executing {stage.Name}...");
            if (!stage.Run())
            {
                Ctx.Console.Error($"Failed executing {stage.Name}!");
                return false;
            }
            
            Ctx.Console.Success($"Executed {stage.Name}!");
            if(stage is MethodDiscovery)
            {
                string detected=$"[VM] 检测到 {Ctx.VMMethods.Select(m=>m.Parent).Distinct().Count():N0} 个方法、{Ctx.VMMethods.Count:N0} 个 VM 调用点。";
                Ctx.Console.Summary(detected);Ctx.Progress?.Invoke(detected,0,0);
            }
        }

        return true;
    }
}
