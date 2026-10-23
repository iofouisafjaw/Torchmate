using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

// Uses the existing target selection, walking, doors, swimming, tools and tile actions.
// A successful tile action is never treated as a material receipt.
internal sealed class ProgramGathering(Player player,NPC agent,string task)
{
    internal static readonly IReadOnlyDictionary<int,(string Word,int Tile)> Materials=new Dictionary<int,(string,int)>{
        [ItemID.Wood]=("木材",TileID.Trees),[ItemID.StoneBlock]=("石块",TileID.Stone),
        [ItemID.DirtBlock]=("土块",TileID.Dirt),[ItemID.ClayBlock]=("粘土块",TileID.ClayBlock),
        [ItemID.MudBlock]=("泥块",TileID.Mud),[ItemID.SnowBlock]=("雪块",TileID.SnowBlock),[ItemID.IceBlock]=("冰块",TileID.IceBlock),
        [ItemID.CopperOre]=("铜",TileID.Copper),[ItemID.TinOre]=("锡",TileID.Tin),[ItemID.IronOre]=("铁",TileID.Iron),
        [ItemID.LeadOre]=("铅",TileID.Lead),[ItemID.SilverOre]=("银",TileID.Silver),[ItemID.TungstenOre]=("钨",TileID.Tungsten),
        [ItemID.GoldOre]=("金",TileID.Gold),[ItemID.PlatinumOre]=("铂金",TileID.Platinum)};
    private readonly Point _center=agent.Bottom.ToTileCoordinates();
    private readonly HashSet<Point> _skip=new();
    private AgentAction? _action;
    private CompanionDecision? _decision;
    private ApprovalTicket? _approval;
    private Point _target;
    private int _item,_settle,_before,_trees,_blocks;
    private long _rule;
    private bool _day,_approved;
    private NativeGardener? _herbs;
    private Guid _rawApprovalId;
    internal bool Asking=>_herbs?.Asking==true||_approval!=null;
    internal Guid ApprovalId=>_herbs?.ApprovalId??_rawApprovalId;
    internal bool Moving=>_action!=null||_herbs?.Moving==true;
    private int Count=>player.inventory.Take(50).Where(i=>i.type==_item).Sum(i=>i.stack);
    private bool Space=>player.inventory.Take(50).Any(i=>i.IsAir||i.type==_item&&i.stack<i.maxStack);
    private bool Safe(Point p)
    {
        if(!Materials.TryGetValue(_item,out var material)||!WorldGen.InWorld(p.X,p.Y,10)||Math.Abs(p.X-_center.X)>28||Math.Abs(p.Y-_center.Y)>28)return false;
        var tile=Framing.GetTileSafely(p.X,p.Y);
        if(!tile.HasTile||tile.TileType!=material.Tile)return false;
        if(_item==ItemID.Wood)
        {
            return GatherTargets.SafeForestTree(p,_center);
        }
        bool exposed=false;
        for(int x=p.X-1;x<=p.X+1;x++)for(int y=p.Y-1;y<=p.Y+1;y++)
        {
            var t=Framing.GetTileSafely(x,y);
            if(HousingInspector.Hazard(t)||t.WallType!=WallID.None||t.HasTile&&Main.tileFrameImportant[t.TileType]&&!Main.tileCut[t.TileType])return false;
            if(Math.Abs(x-p.X)+Math.Abs(y-p.Y)==1&&(!t.HasTile||!Main.tileSolid[t.TileType]))exposed=true;
        }
        return exposed;
    }
    private AgentAction NewAction()=>_item==ItemID.Wood?new ChopAction(_target,inventoryToolsOnly:true):new MineAction(_target,singleTile:true,inventoryToolsOnly:true);
    internal ChainGatherResult Tick(int item,out string message)
    {
        message="正在核对采集缺口。";
        if(_item!=item){Stop();_item=item;}
        if(GardenWorld.HerbCrop(item)!=null)
        {
            _herbs??=new(player,agent,new(_center.X,_center.Y),new HashSet<int>(),CompanionCommandService.Capture(),task);
            return _herbs.Harvest(item,out message);
        }
        var store=CompanionMemorySystem.Store;
        if(store==null||!Materials.ContainsKey(item)){message="该材料需手动补齐。";return ChainGatherResult.Failed;}
        if(_decision!=null&&(_rule!=store.Memory.RuleRevision||_day!=Main.dayTime||CompanionPolicy.Check(store.Memory.Preferences,_decision,_approved,Main.dayTime)==PolicyResult.Deny))
        {message="采集规则或昼夜已变化，请重新规划。";return ChainGatherResult.Failed;}
        if(_approval!=null)
        {
            if(!_approval.Matches(CompanionMemorySystem.Session,task,_decision!.Json,DateTimeOffset.UtcNow,_rule,_day))
            {message="本次采集许可已过期，请重新规划。";return ChainGatherResult.Failed;}
            message=$"需采集{Crafting.Name(item)} tile({_target.X},{_target.Y})，等待你同意本次动作。";return ChainGatherResult.WaitingForApproval;
        }
        if(_settle>0)
        {
            Pickup();agent.velocity.X=0;message="拾取掉落物，按实际背包数量核对材料。";
            if(--_settle>0)return ChainGatherResult.Pending;
            _decision=null;
            if(Count<=_before){message="采集后未收到所需掉落物，已停止；请拾取或补料后重新规划。";return ChainGatherResult.Failed;}
            return ChainGatherResult.Finished;
        }
        if(_action==null)
        {
            var tool=ToolSelector.FindInventoryTool(player,item==ItemID.Wood?ToolType.Axe:ToolType.Pickaxe);
            if(tool==null||!Space){message=tool==null?"缺少背包中的采集工具。":"主背包没有材料空间。";return ChainGatherResult.Failed;}
            if(item==ItemID.Wood?_trees>=24:_blocks>=512){message="已达到确认范围内的采集尝试上限。";return ChainGatherResult.Failed;}
            Point? candidate;
            while((candidate=item==ItemID.Wood?GatherTargets.NextTree(agent,_center,0,_skip):GatherTargets.NextOre(agent,_center,0,_skip,tool.pick,Materials[item].Word,Safe)) is Point at&&!Safe(at))_skip.Add(at);
            if(candidate is not Point target){message="起点28格内没有安全、暴露且可到达的"+Crafting.Name(item)+"；已完成部分保留，请补料后重试。";return ChainGatherResult.Failed;}
            _target=target;_skip.Add(target);
            if(item!=ItemID.Wood&&!ToolSelector.CanMineTile(tool,Materials[item].Tile)){message="背包中的镐强度不足。";return ChainGatherResult.Failed;}
            if(item==ItemID.Wood)_trees++;else _blocks++;
            _decision=CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"{(item==ItemID.Wood?"chop":"mine")}\",\"params\":{{\"tileX\":{target.X},\"tileY\":{target.Y}}}}}}}");
            _rule=store.Memory.RuleRevision;_day=Main.dayTime;_approved=false;_before=Count;
            var permission=CompanionPolicy.Check(store.Memory.Preferences,_decision,day:Main.dayTime);
            if(permission==PolicyResult.Deny){message="当前偏好禁止本次采集。";return ChainGatherResult.Failed;}
            if(permission==PolicyResult.Ask)
            {
                _approval=new(CompanionMemorySystem.Session,task,_decision.Json,DateTimeOffset.UtcNow.AddMinutes(2),_rule,_day);
                _rawApprovalId=Guid.NewGuid();
                message=$"需采集{Crafting.Name(item)} tile({target.X},{target.Y})，请同意本次动作。";
                CompanionUISystem.Message(message,Color.LightBlue);return ChainGatherResult.WaitingForApproval;
            }
            _action=NewAction();
        }
        if(!Safe(_target)){message="采集目标或保护范围已变化，未继续挖掘。";return ChainGatherResult.Failed;}
        var result=_action.Tick(new(agent,player));message=result.Message??"正在采集。";
        if(result.Status==AgentActionStatus.Pending)return ChainGatherResult.Pending;
        _action.Cancel();_action=null;agent.velocity.X=0;
        if(result.Status==AgentActionStatus.Failure){_decision=null;return ChainGatherResult.Finished;}
        _settle=90;return ChainGatherResult.Pending;
    }
    internal string Approve()
    {
        if(_herbs!=null)return _herbs.Approve();
        var store=CompanionMemorySystem.Store;
        if(_approval==null||store==null||!_approval.Matches(CompanionMemorySystem.Session,task,_decision!.Json,DateTimeOffset.UtcNow,store.Memory.RuleRevision,Main.dayTime))return "采集许可已失效，请停止后重新规划。";
        if(!Safe(_target)||CompanionPolicy.Check(store.Memory.Preferences,_decision!,true,Main.dayTime)!=PolicyResult.Allow)return "采集条件已变化，没有执行。";
        _approved=true;_approval=null;_action=NewAction();return "已同意本次采集，继续执行。";
    }
    private void Pickup()=>PickupMatching(player,agent,_item);
    internal static void PickupMatching(Player player,NPC agent,int type) => CompanionDrops.PickupMatching(player,agent,type);
    internal void Stop(){_herbs?.Stop();_herbs=null;_rawApprovalId=Guid.Empty;_action?.Cancel();_action=null;_approval=null;_decision=null;_settle=0;agent.velocity.X=0;}
}
