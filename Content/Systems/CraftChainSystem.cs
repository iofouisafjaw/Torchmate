using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrarAI.Content.Systems;

// The model never supplies recipes, ingredients, station coordinates or execution steps.
public sealed partial class CraftChainSystem : ModSystem
{
    internal static CraftChainJob? Job { get; private set; }
    private static NativeChainSite? _site;
    private static string? _task;
    private static string _snapshot="",_layout="",_blocked="",_goal="",_sources="";
    private static ControlStamp _stamp;
    private static int _player=-1,_agent=-1;
    private static Dictionary<int,StructureStep> _placements=new();
    internal static bool Active=>Job?.State is ChainState.Preview or ChainState.Running;
    internal static bool Asking=>Active&&(_site?.Gather.Asking==true||Job?.State==ChainState.Preview&&Job.Plan.CanExecute&&_blocked.Length==0);
    internal static bool Moving=>Job?.State==ChainState.Running&&_site?.Gather.Moving==true;
    internal static Guid PreviewId=>Job?.State==ChainState.Preview?Job.PreviewId:Guid.Empty;
    internal static Guid ConfirmationId=>_site?.Gather.Asking==true?_site.Gather.ApprovalId:PreviewId;
    internal static bool Owns(int id)=>Active&&id==_agent;
    internal static bool IsTask(string id)=>_task==id;
    internal static ChainRecipe DescribeRecipe(Recipe r)=>new(r.RecipeIndex,r.createItem.type,r.createItem.stack,
        NearbyChests.Requirements(r,1),r.requiredTile.Where(t=>t>=0).ToArray());

    internal static string Begin(Player player,CraftChainRequest request)
    {
        if(request.IronSet&&request.Count!=1)return "套装目前一次支持一套铁盔甲。";
        if(Main.netMode!=NetmodeID.SinglePlayer||!player.active||player.dead||CompanionMemorySystem.Session==Guid.Empty)
            return "请在单人世界、角色存活时规划合成。";
        var companion=ChatCoordinator.FindCompanion();if(companion==null)return "请先唤来同伴。";
        var targets=request.IronSet?new[]{new ChainTarget(ItemID.IronHelmet,1),new ChainTarget(ItemID.IronChainmail,1),new ChainTarget(ItemID.IronGreaves,1)}:
            new[]{new ChainTarget(Crafting.ResolveExact(request.Item),request.Count)};
        if(targets.Any(t=>t.Item==0))return "物品名称不明确，请使用游戏中的全名，例如：规划合成1个熔炉。";
        string? previous = _task;
        string result = BeginTargets(player, targets, request.IronSet ? "做一套铁盔甲" : $"合成 {Crafting.Name(targets[0].Item)}×{request.Count}");
        if (_task != previous) _originalRequest = request;
        return result;
    }
    private static ChainTarget[]? _originalTargets;
    internal static string BeginTargets(Player player, IReadOnlyList<ChainTarget> targets, string goal)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || CompanionMemorySystem.Session == Guid.Empty)
            return "请在单人世界、角色存活时规划合成。";
        var companion = ChatCoordinator.FindCompanion(); if (companion == null) return "请先唤来同伴。";
        if (targets.Count is < 1 or > 8 || targets.Any(t => t.Item <= 0 || t.Count is < 1 or > 999)) return "备料目标无效。";
        // Compute first: an invalid request does not interrupt the current task.
        var proposal=Propose(player,targets);
        CompanionCommandService.Advance();CompanionAgent.Cancel();CompanionDialogueSystem.Cancel();CompanionInquirySystem.Cancel();
        companion.StopCommand("craft_chain_requested");CompanionConstructionSystem.Hide();StructureWorkshop.Hide();
        _stamp=CompanionCommandService.Capture();_player=player.whoAmI;_agent=companion.NPC.whoAmI;
        _originalRequest=null; _originalTargets=targets.ToArray();
        _task=Guid.NewGuid().ToString("N");_goal=goal;
        CompanionTaskTracker.Begin(_task,_goal);SetProposal(proposal);
        return Describe();
    }

    internal sealed record Proposal(CraftChainPlan Plan,Dictionary<int,StructureStep> Placements,string Snapshot,string Blocked,string Sources);
    internal static Proposal Propose(Player p,IReadOnlyList<ChainTarget> targets,IEnumerable<StructureStep>? reserved=null)
    {
        p.AdjTiles();var catalog=new NativeChainCatalog(p);
        var plan=CraftChainPlanner.Build(targets,NearbyChests.Stock(p),Enumerable.Range(0,p.adjTile.Length).Where(t=>p.adjTile[t]),catalog);
        var placements=new Dictionary<int,StructureStep>();var reasons=new List<string>();
        foreach(var step in plan.Steps.Where(s=>s.Kind==ChainStepKind.Place))
        {
            var at=FindStation(p,step,placements.Values,(reserved??Array.Empty<StructureStep>()).SelectMany(s=>s.Footprint()).ToHashSet());
            if(at==null)reasons.Add($"{Crafting.Name(step.Item)}附近没有安全空位；请先放好制作站或站到开阔平地");
            else placements[step.Station]=at;
        }
        if(plan.Gather.Where(x=>x.Key!=ItemID.Wood&&ProgramGathering.Materials.ContainsKey(x.Key)).Sum(x=>x.Value)>512||plan.Gather.GetValueOrDefault(ItemID.Wood)>999)
            reasons.Add("采集量超过单次范围（矿石／石土总计512块、木材999）；请先备好部分材料");
        if(plan.Gather.ContainsKey(ItemID.Wood)&&ToolSelector.FindInventoryTool(p,ToolType.Axe)==null)reasons.Add("背包缺少斧头");
        if(plan.Gather.Any(x=>x.Key!=ItemID.Wood&&ProgramGathering.Materials.ContainsKey(x.Key))&&ToolSelector.FindInventoryTool(p,ToolType.Pickaxe)==null)reasons.Add("背包缺少镐");
        var prefs=CompanionMemorySystem.Store?.Memory.Preferences;
        if(plan.Gather.Where(x=>GardenWorld.HerbCrop(x.Key)!=null).Sum(x=>x.Value)>64)reasons.Add("草药缺口超过一次64份采收预算，请先备好部分草药");
        foreach(string action in plan.Gather.Keys.Where(ProgramGathering.Materials.ContainsKey).Select(i=>i==ItemID.Wood?"chop":"mine").Distinct())
        {
            var decision=CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"{action}\",\"params\":{{\"tileX\":100,\"tileY\":100}}}}}}");
            if(prefs!=null&&CompanionPolicy.Check(prefs,decision,day:Main.dayTime)==PolicyResult.Deny)reasons.Add("当前偏好禁止"+(action=="chop"?"砍树":"挖掘"));
        }
        var bag=NearbyChests.Stock(p,false);var total=NearbyChests.Stock(p);
        var inputs=plan.Steps.Where(s=>s.Kind==ChainStepKind.Craft).SelectMany(s=>s.Ingredients!.Keys)
            .Concat(plan.Steps.Where(s=>s.Kind==ChainStepKind.Place).Select(s=>s.Item)).Concat(targets.Select(t=>t.Item)).Distinct();
        var sources=inputs.Select(i=>{
            int need=plan.Required(i,0),fromBag=Math.Min(need,bag.GetValueOrDefault(i)),fromBox=Math.Min(Math.Max(0,need-fromBag),total.GetValueOrDefault(i)-bag.GetValueOrDefault(i));
            return (i,fromBag,fromBox);
        }).Where(x=>x.fromBag+x.fromBox>0).ToArray();
        string sourceText=sources.Length==0?"无可复用库存":string.Join("、",sources.Select(x=>$"{Crafting.Name(x.i)}（背包{x.fromBag}／箱子{x.fromBox}）"));
        return new(plan,placements,NearbyChests.Snapshot(p),string.Join("；",reasons),sourceText);
    }
    private static string Layout(Dictionary<int,StructureStep> placements)=>JsonSerializer.Serialize(placements.OrderBy(x=>x.Key));
    private static void SetProposal(Proposal proposal)
    {
        Job=new(proposal.Plan);_site=null;_placements=proposal.Placements;_layout=Layout(_placements);_snapshot=proposal.Snapshot;_blocked=proposal.Blocked;_sources=proposal.Sources;
        CompanionTaskTracker.Update(_task,CompanionTaskPhase.WaitingForApproval,action:"确认合成链",result:Describe());
        Log("craft_chain_preview",new{Job.PreviewId,plan=Job.Plan.Signature,_placements,_blocked});
    }
    internal static string Confirm(Player player,Guid id)
    {
        if (CompanionCollaborationSystem.PlayerOwns(_task)) return "这项由玩家负责，未开始同伴备料。";
        if (!CompanionCollaborationSystem.CanExecute(player, _task)) return "协作权限或角色已变化，请重新发起任务。";
        if(!Active||player.whoAmI!=_player||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision))return "合成任务已失效。";
        if(_site?.Gather.Asking==true)return id==_site.Gather.ApprovalId?_site.Gather.Approve():"采集目标已变化，请查看最新提示。";
        if(Job!.State!=ChainState.Preview||id!=Job.PreviewId)return "请查看最新合成计划后再确认。";
        var live=Propose(player,Job.Plan.Targets);
        if(DateTimeOffset.UtcNow>=Job.Expires||live.Snapshot!=_snapshot||live.Plan.Signature!=Job.Plan.Signature||Layout(live.Placements)!=_layout||live.Blocked!=_blocked)
        {SetProposal(live);return "背包、箱子、位置、配方或预览时限已变化，已重新规划；请查看新计划后再次确认。\n"+Describe();}
        if(!Job.Plan.CanExecute||_blocked.Length>0)return "请先补齐提示中的材料或条件，再重新规划。\n"+Describe();
        var npc=ChatCoordinator.FindCompanion()?.NPC;
        if(npc?.whoAmI!=_agent)return "同伴已变化，请重新规划。";
        if(!Job.Confirm(id,live.Plan.Signature,DateTimeOffset.UtcNow))return "确认未生效，请重新规划。";
        _site=new(player,npc!,_placements,NearbyChests.Ids(player),_stamp,_task!);
        Log("craft_chain_confirmed",new{id});return "已确认，开始按计划采集、合成和放置制作站；可随时停止，已完成部分保留。";
    }
    internal static string Describe()
    {
        if(Job==null)return "没有合成计划。";
        var p=Job.Plan;string List(IEnumerable<KeyValuePair<int,int>> items)=>string.Join("、",items.Select(x=>$"{Crafting.Name(x.Key)}×{x.Value}"));
        var crafts=p.Steps.Where(s=>s.Kind==ChainStepKind.Craft).GroupBy(s=>s.Item).ToDictionary(g=>g.Key,g=>g.Sum(s=>s.Count*s.Recipe!.Output));
        var costs=p.Steps.Where(s=>s.Kind==ChainStepKind.Craft).SelectMany(s=>s.Ingredients!).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.Value));
        return _goal+"\n优先主背包，再使用确认时附近18格内已关闭、未上锁的箱子。"+
            "\n复用库存："+_sources+
            "\n逐层合成："+(crafts.Count==0?"已有全部成品":List(crafts))+
            "\n各步耗材（含中间件）："+(costs.Count==0?"无":List(costs))+
            "\n补放制作站："+(_placements.Count==0?"无":string.Join("、",_placements.Values.Select(s=>$"{Crafting.Name(s.Item)} tile({s.Dx},{s.Dy})")))+
            "\n需采集："+(p.Gather.Count==0?"无":List(p.Gather))+
            (p.Manual.Count==0?"":"\n需手动补齐："+List(p.Manual))+(_blocked.Length==0?"":"\n条件不足："+_blocked)+
            $"\n步骤 {Job.Cursor}/{p.Steps.Count} · {Job.Reason}"+
            (Job.State==ChainState.Preview&&p.CanExecute&&_blocked.Length==0?"\n确认后执行；采集限同伴起点28格内暴露的矿石、普通树和已开花草药，保留采集前询问偏好。请留在制作站附近。":"");
    }
    public override void PostUpdateWorld()
    {
        if(!Active)return;
        if(_player<0||!CompanionCollaborationSystem.CanExecute(Main.player[_player], _task)||!Main.player[_player].active||Main.player[_player].dead||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)||ChatCoordinator.FindCompanion()?.NPC.whoAmI!=_agent)
        {Cancel("角色、同伴或世界已变化，请重新规划。");return;}
        if(Job!.State!=ChainState.Running)return;
        int before=Job.Cursor;
        try{Job.Step(_site!);}catch(Exception ex)when(ex is not OutOfMemoryException){Job.Cancel(_site,"执行异常，已完成部分保留："+ex.GetType().Name);
            CompanionTaskTracker.SetResult(_task,Job.Reason);CompanionTaskTracker.Close(_task,"construction_failed",0);return;}
        CompanionTaskTracker.Update(_task,Job.Asking?CompanionTaskPhase.WaitingForApproval:CompanionTaskPhase.Executing,
            action:$"合成链 {Job.Cursor}/{Job.Plan.Steps.Count}",result:Job.Reason);
        if(before!=Job.Cursor)Log("craft_chain_step",new{before,after=Job.Cursor,Job.Reason});
        if(Job.State is not (ChainState.Completed or ChainState.Failed))return;
        Log("craft_chain_audit",new{passed=Job.State==ChainState.Completed,Job.Reason,Job.Cursor});
        if(Job.State==ChainState.Completed)CompanionTaskTracker.Finish(_task,Job.Reason,Job.Plan.Targets.Sum(t=>t.Count));
        else{CompanionTaskTracker.SetResult(_task,Job.Reason);CompanionTaskTracker.Close(_task,"construction_failed",0);}
        CompanionUISystem.Message(Job.Reason,Job.State==ChainState.Completed?Color.LightGreen:Color.Orange);
    }
    internal static void Cancel(string reason)
    {
        if(!Active)return;Job!.Cancel(_site,reason);CompanionTaskTracker.SetResult(_task,reason);CompanionTaskTracker.Close(_task,"player_stopped",0);
        Log("craft_chain_cancelled",new{reason,Job.Cursor});
    }
    private static void Log(string kind,object data)
    {try{CompanionMemorySystem.Record(kind,_task,null,data);}catch(Exception ex)when(ex is not OutOfMemoryException){/* Logging cannot undo committed work. */}}
    public override void OnWorldUnload(){Cancel("世界已退出。");Job=null;_site=null;_task=null;}
    public override void Unload(){Job=null;_site=null;_task=null;}

    private static StructureStep? FindStation(Player p,ChainStep step,IEnumerable<StructureStep> reserved,HashSet<(int X,int Y)>? excluded=null)
    {
        var item=ContentSamples.ItemsByType[step.Item];var data=TileObjectData.GetTileData(item.createTile,item.placeStyle);
        if(data==null)return null;
        var feet=p.Bottom.ToTileCoordinates();var occupied=reserved.SelectMany(s=>s.Footprint()).ToHashSet();
        var safe=new HousingStructureSite(p,Point.Zero);
        if(step.Station==TileID.Bottles)
            foreach(var table in reserved.Where(s=>s.Type==TileID.WorkBenches))
                foreach(int x in Enumerable.Range(table.Dx-table.OriginX,table.Width))
                {
                    var bottle=new StructureStep(x+data.Origin.X,table.Dy-table.OriginY-data.Height+data.Origin.Y,
                        StepKind.Furniture,item.createTile,item.placeStyle,step.Item,Width:data.Width,Height:data.Height,OriginX:data.Origin.X,OriginY:data.Origin.Y);
                    if(!bottle.Footprint().Any(c=>occupied.Contains(c)||excluded?.Contains(c)==true)&&safe.Inspect(bottle)==CellState.Empty)return bottle;
                }
        // Keep all stations in the player's real adjacency rectangle; never clear plants or terrain.
        foreach(int dx in Enumerable.Range(-3,7).OrderBy(x=>Math.Abs(x)))
        for(int dy=-1;dy<=1;dy++)
        {
            var s=new StructureStep(feet.X+dx,feet.Y-1+dy,StepKind.Furniture,item.createTile,item.placeStyle,step.Item,
                Width:data.Width,Height:data.Height,OriginX:data.Origin.X,OriginY:data.Origin.Y);
            if(s.Footprint().Any(c=>occupied.Contains(c)||excluded?.Contains(c)==true)||safe.Inspect(s)!=CellState.Empty)continue;
            if(TileObject.CanPlace(s.Dx,s.Dy,s.Type,s.Style,0,out _,onlyCheck:true))return s;
        }
        return null;
    }
}

internal sealed class NativeChainCatalog(Player player):ICraftChainCatalog
{
    private readonly Dictionary<int,ChainRecipe[]> _recipes=new();
    private readonly Dictionary<int,int> _stock=NearbyChests.Stock(player);
    private readonly IReadOnlySet<int> _herbs=GardenWorld.HarvestableItems(player);
    public IEnumerable<int> SupportStations(int tile)
    {
        if(tile!=TileID.Bottles)yield break;
        var at=player.Bottom.ToTileCoordinates();
        for(int x=at.X-4;x<=at.X+4;x++)for(int y=at.Y-3;y<=at.Y+2;y++)
        {var t=Framing.GetTileSafely(x,y);if(t.HasTile&&!t.IsActuated&&Main.tileTable[t.TileType])yield break;}
        yield return TileID.WorkBenches;
    }
    public IEnumerable<ChainRecipe> Recipes(int item)
    {
        if(_recipes.TryGetValue(item,out var found))return found;
        // Raw surface materials are gathered directly; recycling recipes otherwise explode into cycles.
        found=Collectable(item)?Array.Empty<ChainRecipe>():Crafting.RecipesFor(item).Where(r=>r.Conditions.All(c=>c.IsMet()))
            .OrderBy(r=>r.requiredItem.Where(i=>!i.IsAir).Sum(i=>i.stack)).ThenBy(r=>r.RecipeIndex)
            .Select(CraftChainSystem.DescribeRecipe).ToArray();
        _recipes[item]=found;return found;
    }
    public IEnumerable<int> StationItems(int tile)=>_stock.Keys.Where(i=>ContentSamples.ItemsByType[i].createTile==tile)
        .Concat(Crafting.StationItems(player,tile));
    public bool Collectable(int item)=>ProgramGathering.Materials.ContainsKey(item)||_herbs.Contains(item);
}

internal sealed class NativeChainSite(Player player,NPC npc,Dictionary<int,StructureStep> placements,IReadOnlySet<int> boxes,ControlStamp stamp,string task):ICraftChainSite
{
    private readonly Dictionary<int,(Chest Chest,int X,int Y)> _identities=boxes.ToDictionary(id=>id,id=>(Main.chest[id],Main.chest[id].x,Main.chest[id].y));
    internal ProgramGathering Gather{get;}=new(player,npc,task);
    public bool Valid(out string reason)
    {
        reason="角色、同伴、控制指令或世界已变化，已完成部分保留。";
        return Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&player.active&&!player.dead&&npc.active&&stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)
            &&_identities.All(x=>ReferenceEquals(Main.chest[x.Key],x.Value.Chest)&&x.Value.Chest.x==x.Value.X&&x.Value.Chest.y==x.Value.Y);
    }
    public int Stock(int item)=>NearbyChests.Stock(player,allowed:boxes).GetValueOrDefault(item);
    public ChainGatherResult Collect(int item,out string message)=>Gather.Tick(item,out message);
    public void StopGather()=>Gather.Stop();
    public bool Craft(ChainStep step,out string message)
    {
        message="已确认的配方已变化，请重新规划。";
        var recipe=Crafting.RecipesFor(step.Item).FirstOrDefault(r=>r.RecipeIndex==step.Recipe!.Id);
        if(recipe==null||CraftChainSystem.DescribeRecipe(recipe).Signature!=step.Recipe!.Signature)return false;
        return NearbyChests.TryCraft(player,recipe,step.Count,out message,out _,step.Ingredients,boxes);
    }
    public bool Place(ChainStep step,out string message)
    {
        message="制作站放置位置已变化，请重新规划。";
        if(!placements.TryGetValue(step.Station,out var at))return false;
        var site=new HousingStructureSite(player,Point.Zero);
        if(site.Inspect(at)==CellState.Done){player.AdjTiles();return player.adjTile[step.Station];}
        if(site.Inspect(at)!=CellState.Empty)return false;
        if(!Deliver(new[]{new ChainTarget(step.Item,1)},out message))return false;
        if(!site.Place(at,out message))return false;
        player.AdjTiles();if(player.adjTile[step.Station])return true;
        message="制作站已放好，但角色离它太远，已停止；靠近后重新规划。";return false;
    }
    public bool Deliver(IReadOnlyList<ChainTarget> targets,out string message)
    {
        var orders=targets.Select(t=>new ChestItemRequest(Crafting.Name(t.Item),Math.Max(0,t.Count-player.inventory.Take(50).Where(i=>i.type==t.Item).Sum(i=>i.stack))))
            .Where(t=>t.Count>0).ToArray();
        message=orders.Length==0?"所需成品已在主背包。":NearbyChests.Transfer(player,new(ChestOperation.Withdraw,orders),boxes);
        return targets.All(t=>player.inventory.Take(50).Where(i=>i.type==t.Item).Sum(i=>i.stack)>=t.Count);
    }
    public bool Audit(IReadOnlyList<ChainTarget> targets,out string message)
    {
        bool passed=targets.All(t=>player.inventory.Take(50).Where(i=>i.type==t.Item).Sum(i=>i.stack)>=t.Count);
        message=passed?"合成链完成，已核验主背包："+string.Join("、",targets.Select(t=>$"{Crafting.Name(t.Item)}×{t.Count}")):"最终背包核验未通过，已完成部分保留。";
        return passed;
    }
}
