using System;
using System.Linq;
using Terraria;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

public sealed partial class CraftChainSystem
{
    private static CraftChainRequest? _originalRequest;
    internal static string RecoveryProgress=>Job==null?"没有可核实的合成步骤":
        $"确认推进{Job.Cursor}/{Job.Plan.Steps.Count}步，其中制作{Job.Plan.Steps.Take(Job.Cursor).Count(s=>s.Kind==ChainStepKind.Craft)}步、"+
        $"放置制作站{Job.Plan.Steps.Take(Job.Cursor).Count(s=>s.Kind==ChainStepKind.Place)}步。阶段推进不等于最终成品验收；实际物品以最新库存为准。";
    internal static string Replan(Player player)=>_originalRequest != null ? Begin(player,_originalRequest) :
        _originalTargets == null ? "原合成目标已失效，请重新下令。" : BeginTargets(player,_originalTargets,_goal);
}

public sealed partial class CompanionConstructionSystem
{
    internal static string RecoveryProgress=>$"本次新放{Job?.Placed??0}格、复用{Job?.Reused??0}格；"+
        $"备料实际入包木材{_supplySite?.Collected??0}、合成平台{Supply?.Crafted??0}、合成消耗木材{Supply?.WoodConsumed??0}。未通过最终核验的格子不算达标。";
    internal static string Replan(Player player)
    {
        if(Region is not {} region)return "原施工范围已失效，请重新选择。";
        int layers=Layers,spacing=Spacing;int? width=RequestedWidth;
        int before=Revision;string notice=BeginSelection(player);
        if(Revision==before)return notice;
        Layers=layers;Spacing=spacing;RequestedWidth=width;Preview(region);
        return "已保留原范围重新检查，已有合格平台复用；需查看新材料缺口并重新确认。\n"+Describe();
    }
}

public sealed partial class StructureWorkshop
{
    internal static string RecoveryProgress=>ArenaMode&&ArenaJob is {} arena?
        $"本次备料推进{arena.Supply.Cursor}/{arena.Supply.Plan.Steps.Count}步、新放平台{arena.Platforms.Placed}格、设施{arena.Fixtures.Placed}项；尚未通过整体验收。":
        HousingMode?$"本次修补新放{(Job?.Placed??0)+_repairBenchPlaced}项、备料合成{_repairCrafted}件，房屋尚未通过再次检测。":
        $"本次新放{Job?.Placed??0}项、复用{Job?.Reused??0}项、清理{Job?.Cleared??0}格、跳过／失败{Job?.Failed??0}项；最终蓝图未达标。";
    internal static string Replan(Player player)
    {
        if(Origin is not {} origin||Plan==null)return "原蓝图位置已失效，请重新选择。";
        if(ArenaMode)
        {
            bool potions = _arena!.Potions.Count > 0;
            string? before=_task;string notice=BeginArena(player,potions);if(_task==before||!ArenaMode)return notice;
            PreviewArena(origin);return "已按原竞技场位置刷新计划，保留和复用已达标部分；必须重新确认。\n"+DescribeArena();
        }
        if(HousingMode)
        {
            if(Housing is not {} housing)return "原房屋检测位置已失效，请重新检测。";
            string? before=_task;string notice=BeginHousing(player);if(_task==before||!HousingMode)return notice;
            PreviewHousing(housing.Seed);return "已对原房屋再次检测；若需修补，必须重新确认。\n"+DescribeHousing();
        }
        var plan=Plan;
        var entry=new StructureLibrary.Entry("recovery:"+plan.Name,plan.Name,plan.Source,$"{plan.Width}×{plan.Height}",()=>plan);
        string? previous=_task;string result=Begin(player,entry);
        if(_task==previous||Plan!=plan||Phase!=StructurePhase.Placing)return result;
        Preview(origin);return "已按原蓝图和原位置刷新预览，复用已完成部分；必须重新确认。\n"+Describe();
    }
}
