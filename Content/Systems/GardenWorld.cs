using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrarAI.Content.Systems;

internal static class GardenWorld
{
    internal sealed record Crop(int Seed,int Herb,int Style);
    internal static readonly IReadOnlyDictionary<GardenCrop,Crop> Crops=new Dictionary<GardenCrop,Crop> {
        [GardenCrop.Acorn]=new(ItemID.Acorn,0,0),[GardenCrop.Daybloom]=new(ItemID.DaybloomSeeds,ItemID.Daybloom,0),
        [GardenCrop.Moonglow]=new(ItemID.MoonglowSeeds,ItemID.Moonglow,1),[GardenCrop.Blinkroot]=new(ItemID.BlinkrootSeeds,ItemID.Blinkroot,2),
        [GardenCrop.Deathweed]=new(ItemID.DeathweedSeeds,ItemID.Deathweed,3),[GardenCrop.Waterleaf]=new(ItemID.WaterleafSeeds,ItemID.Waterleaf,4),
        [GardenCrop.Fireblossom]=new(ItemID.FireblossomSeeds,ItemID.Fireblossom,5),[GardenCrop.Shiverthorn]=new(ItemID.ShiverthornSeeds,ItemID.Shiverthorn,6) };
    internal static GardenCrop? HerbCrop(int item)=>Crops.Where(c=>c.Value.Herb==item&&item>0).Select(c=>(GardenCrop?)c.Key).FirstOrDefault();
    internal static StructureStep PlantStep(GardenCell cell)
    {
        var item=ContentSamples.ItemsByType[Crops[cell.Crop].Seed];var data=TileObjectData.GetTileData(item.createTile,item.placeStyle);
        return new(cell.At.X,cell.At.Y,StepKind.Furniture,item.createTile,item.placeStyle,item.type,
            Width:data?.Width??1,Height:data?.Height??1,OriginX:data?.Origin.X??0,OriginY:data?.Origin.Y??0);
    }
    private static GardenBed Bed(int type)=>type switch {
        TileID.Grass or TileID.HallowedGrass=>GardenBed.Grass,TileID.JungleGrass=>GardenBed.JungleGrass,
        TileID.Dirt or TileID.Mud=>GardenBed.DirtMud,TileID.CorruptGrass or TileID.CrimsonGrass or TileID.Ebonstone or TileID.Crimstone=>GardenBed.Evil,
        TileID.Sand or TileID.Pearlsand=>GardenBed.Sand,TileID.Ash or TileID.AshGrass=>GardenBed.Ash,
        TileID.SnowBlock or TileID.IceBlock or TileID.CorruptIce or TileID.FleshIce or TileID.HallowedIce=>GardenBed.SnowIce,
        TileID.ClayPot=>GardenBed.Pot,TileID.PlanterBox=>GardenBed.Planter,_=>GardenBed.None };
    internal static GardenCell Observe(BuildPoint at,GardenCrop crop)
    {
        var probe=new GardenCell(at,crop,GardenGrowth.Blocked,GardenBed.None,false,"");var step=PlantStep(probe);
        var footprint=step.Footprint().ToArray();int bottom=footprint.Max(p=>p.Y);
        var ground=Framing.GetTileSafely(at.X,bottom+1);
        bool safe=true;var stamp=new StringBuilder();
        // Include footprint, soil, pot base and neighbouring tree clearance in the approval stamp.
        int left=footprint.Min(p=>p.X)-(crop==GardenCrop.Acorn?2:0),right=footprint.Max(p=>p.X)+(crop==GardenCrop.Acorn?2:0);
        int top=footprint.Min(p=>p.Y);
        for(int x=left;x<=right;x++)for(int y=top;y<=bottom+2;y++)
        {
            if(!WorldGen.InWorld(x,y,10)){safe=false;continue;}
            var t=Framing.GetTileSafely(x,y);
            if(HousingInspector.Hazard(t)||crop==GardenCrop.Acorn&&t.WallType!=WallID.None)safe=false;
            if(crop==GardenCrop.Acorn&&y<=bottom&&t.HasTile&&!footprint.Contains((x,y)))safe=false;
            // Adjacent planting reframes ordinary grass/dirt textures. Those visual
            // frame changes do not change permission; furniture/crop styles still do.
            bool framed=t.HasTile&&Main.tileFrameImportant[t.TileType];
            stamp.Append(x).Append(',').Append(y).Append(':').Append(t.HasTile).Append(',').Append(t.HasTile?t.TileType:0)
                .Append(',').Append(framed?t.TileFrameX:0).Append(',').Append(framed?t.TileFrameY:0).Append(',').Append(t.WallType)
                .Append(',').Append(t.Slope).Append(',').Append(t.IsHalfBlock).Append(',').Append(t.IsActuated).Append(',').Append(t.HasActuator)
                .Append(',').Append(t.LiquidAmount).Append(',').Append(t.LiquidType).Append(',').Append(t.RedWire).Append(t.BlueWire).Append(t.GreenWire).Append(t.YellowWire).Append(',').Append(t.TileColor).Append(',').Append(t.WallColor).Append(';');
        }
        GardenGrowth growth=GardenGrowth.Empty;
        if(crop==GardenCrop.Acorn)
        {
            if(footprint.Any(p=>Framing.GetTileSafely(p.X,p.Y).HasTile))growth=GardenGrowth.Blocked;
            foreach(int x in footprint.Select(p=>p.X).Distinct())
            {var g=Framing.GetTileSafely(x,bottom+1);if(!g.HasTile||Bed(g.TileType)!=GardenBed.Grass||g.IsHalfBlock||g.Slope!=SlopeType.Solid||HousingInspector.Hazard(g))safe=false;}
            if(growth==GardenGrowth.Empty&&safe&&!TileObject.CanPlace(at.X,at.Y,step.Type,step.Style,0,out _,onlyCheck:true))safe=false;
        }
        else
        {
            var t=Framing.GetTileSafely(at.X,at.Y);
            if(t.HasTile)growth=t.TileFrameX==Crops[crop].Style*18&&t.TileFrameY==0?t.TileType switch {
                TileID.ImmatureHerbs=>GardenGrowth.Immature,TileID.MatureHerbs=>GardenGrowth.Mature,TileID.BloomingHerbs=>GardenGrowth.Blooming,_=>GardenGrowth.Blocked }:GardenGrowth.Blocked;
        }
        safe&=ground.HasTile&&!ground.IsActuated&&!ground.IsHalfBlock&&ground.Slope==SlopeType.Solid;
        return new(at,crop,growth,Bed(ground.TileType),safe,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))));
    }
    internal static IEnumerable<GardenCell> Scan(GardenRequest request,BuildPoint origin)
    {
        var crops=request.Crop is {} crop?new[]{crop}:Crops.Keys.Where(c=>c!=GardenCrop.Acorn).ToArray();
        for(int x=origin.X-GardenRequest.Radius;x<=origin.X+GardenRequest.Radius;x++)
        for(int y=origin.Y-GardenRequest.Radius;y<=origin.Y+GardenRequest.Radius;y++)
        {
            if(!WorldGen.InWorld(x,y,12))continue;
            var tile=Framing.GetTileSafely(x,y);
            if(request.Operation==GardenOperation.Harvest&&(!tile.HasTile||tile.TileType!=TileID.BloomingHerbs))continue;
            if(request.Operation==GardenOperation.Plant&&tile.HasTile)continue;
            if(!Framing.GetTileSafely(x,y+1).HasTile)continue;
            foreach(var c in crops)yield return Observe(new(x,y),c);
        }
    }
    internal static IReadOnlySet<int> HarvestableItems(Player player)
    {
        var npc=ChatCoordinator.FindCompanion()?.NPC;if(npc==null)return new HashSet<int>();
        var origin=npc.Bottom.ToTileCoordinates();
        return Scan(new(GardenOperation.Harvest,null,64,false),new(origin.X,origin.Y)).Where(GardenRules.Harvestable)
            .Select(c=>Crops[c.Crop].Herb).ToHashSet();
    }
    internal static bool Planted(GardenCell target)
    {
        if(target.Crop!=GardenCrop.Acorn)
        {var live=Observe(target.At,target.Crop);return live.Safe&&live.Growth is GardenGrowth.Immature or GardenGrowth.Mature or GardenGrowth.Blooming;}
        var step=PlantStep(target);var data=TileObjectData.GetTileData(step.Type,step.Style);
        if(data==null)return false;
        int bottom=step.Footprint().Max(p=>p.Y);
        // A previously verified sapling may have naturally grown while later targets
        // were being planted. A real tree at its planted base also satisfies the goal.
        if(step.Footprint().Where(p=>p.Y==bottom).Any(p=>{
            var t=Framing.GetTileSafely(p.X,p.Y);var g=Framing.GetTileSafely(p.X,p.Y+1);
            return t.HasTile&&t.TileType==TileID.Trees&&!HousingInspector.Hazard(t)&&g.HasTile&&Bed(g.TileType)==GardenBed.Grass;}))return true;
        FurnitureLocation? root=null;
        foreach(var (x,y) in step.Footprint())
        {
            var t=Framing.GetTileSafely(x,y);
            if(!t.HasTile||t.TileType!=TileID.Saplings||HousingInspector.Hazard(t))return false;
            var frame=FurnitureFrames.Decode(x,y,t.TileFrameX,t.TileFrameY,data.Width,data.Height,data.CoordinateWidth,data.CoordinatePadding,
                data.CoordinateHeights,data.CoordinateFullWidth,data.CoordinateFullHeight);
            if(frame==null||frame.X!=step.Dx-step.OriginX||frame.Y!=step.Dy-step.OriginY)return false;
            if(root!=null&&(root.BaseFrameX!=frame.BaseFrameX||root.BaseFrameY!=frame.BaseFrameY))return false;
            root=frame;
        }
        return true;
    }
}

// One worker is used both by direct garden tasks and by quantitative recipe gathering.
// Native drops and the existing GetItem transfer supply herbs and seeds; no item is manufactured.
internal sealed class NativeGardener(Player player,NPC agent,BuildPoint origin,IReadOnlySet<int> boxes,ControlStamp stamp,string task):IGardenSite
{
    private readonly Dictionary<int,Chest> _boxes=boxes.ToDictionary(id=>id,id=>Main.chest[id]);
    private GardenCell? _current;
    private GardenOperation _operation;
    private Vector2? _destination;
    private MovementHelper.MovementState _movement=MovementHelper.MovementState.Create(20);
    private ApprovalTicket? _approval;
    private bool _approved,_day,_permissionCaptured,_removed;
    private long _rule;
    private int _age,_settle,_beforeHerbs,_beforeSeeds,_attempts;
    internal bool Asking=>_approval!=null;
    internal bool Moving=>_destination!=null;
    internal Guid ApprovalId{get;private set;}
    private string Decision=>_current==null?"":$"harvest:{_current.At}:{_current.Crop}:{_current.Stamp}";
    private int Count(int item)=>item<1?0:player.inventory.Take(50).Where(i=>i.type==item).Sum(i=>i.stack);
    private bool Space(int item)=>player.inventory.Take(50).Any(i=>i.IsAir||i.type==item&&i.stack<i.maxStack);
    public bool Valid(out string reason)
    {
        reason="角色、同伴、箱子、世界或控制指令已变化，已完成部分保留。";
        return Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&player.active&&!player.dead&&agent.active
            &&ChatCoordinator.FindCompanion()?.NPC==agent&&stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)
            &&_boxes.All(x=>ReferenceEquals(Main.chest[x.Key],x.Value));
    }
    public GardenStep Work(GardenCell target,GardenOperation operation,out GardenReceipt receipt,out string message)
    {
        receipt=new(0,0,0);message="正在接近作物。";
        if(!Valid(out message)||!GardenRules.InRange(target.At,origin))return GardenStep.Failed;
        if(_current!=target||_operation!=operation)
        {Stop();_current=target;_operation=operation;_age=0;_removed=false;_permissionCaptured=false;}
        if(_approval==null&&++_age>1200){message="作物不可达或拾取超时，已停止。";return GardenStep.Failed;}
        var crop=GardenWorld.Crops[target.Crop];
        if(_settle>0)
        {
            MovementHelper.Hold(agent);Pickup(crop.Herb);Pickup(crop.Seed);
            if(--_settle>0)return GardenStep.Pending;
            int herbs=Math.Max(0,Count(crop.Herb)-_beforeHerbs),seeds=Math.Max(0,Count(crop.Seed)-_beforeSeeds);
            receipt=new(0,herbs,seeds);message=$"采收后实际进入主背包：{Crafting.Name(crop.Herb)}×{herbs}，{Crafting.Name(crop.Seed)}×{seeds}。";
            return herbs>0&&Removed(target)?GardenStep.Done:GardenStep.Failed;
        }
        var live=GardenWorld.Observe(target.At,target.Crop);
        if(live.Stamp!=target.Stamp||!(operation==GardenOperation.Plant?GardenRules.Plantable(live):GardenRules.Harvestable(live)))
        {message="植株、开花状态、土壤或保护条件已变化，请重新规划；没有覆盖现场。";return GardenStep.Failed;}
        if(operation==GardenOperation.Harvest)
        {
            var store=CompanionMemorySystem.Store;if(store==null){message="世界偏好未就绪。";return GardenStep.Failed;}
            if(!_permissionCaptured){_rule=store.Memory.RuleRevision;_day=Main.dayTime;_permissionCaptured=true;}
            if(_rule!=store.Memory.RuleRevision||_day!=Main.dayTime){message="采收规则或昼夜变化，请重新规划。";return GardenStep.Failed;}
            if(_approval!=null)
            {
                if(!_approval.Matches(CompanionMemorySystem.Session,task,Decision,DateTimeOffset.UtcNow,_rule,_day))
                {message="采收许可已过期。";return GardenStep.Failed;}
                message=$"采收{GardenRequest.Name(target.Crop)} tile({target.At.X},{target.At.Y})，等待同意本次动作。";return GardenStep.Asking;
            }
            if(CompanionPolicy.CheckGatherConfirmation(store.Memory.Preferences,_approved,Main.dayTime)==PolicyResult.Ask)
            {
                _approval=new(CompanionMemorySystem.Session,task,Decision,DateTimeOffset.UtcNow.AddMinutes(2),_rule,_day);ApprovalId=Guid.NewGuid();
                message=$"采收{GardenRequest.Name(target.Crop)} tile({target.At.X},{target.At.Y})，请同意本次动作。";
                CompanionUISystem.Message(message,Color.LightBlue);return GardenStep.Asking;
            }
            if(!Space(crop.Herb)||!Space(crop.Seed)){message="主背包缺少草药或种子空间，未采收。";return GardenStep.Failed;}
        }
        var at=new Vector2(target.At.X*16+8,target.At.Y*16+8);
        if(Vector2.Distance(agent.Center,at)>72||!Collision.CanHitLine(agent.Center,1,1,at,1,1))
        {
            if(_destination==null)
            {
                var candidates=Enumerable.Range(target.At.X-3,7).SelectMany(x=>Enumerable.Range(target.At.Y-3,8)
                    .Select(y=>new Vector2(x*16+8,y*16-agent.height/2f)))
                    .Where(p=>GardenRules.InRange(new((int)(p.X/16),(int)((p.Y+agent.height/2f)/16)),origin))
                    .Where(p=>Vector2.Distance(p,at)<=68&&!Collision.SolidCollision(p-new Vector2(agent.width/2f,agent.height/2f),agent.width,agent.height)
                        &&Collision.TileCollision(p-new Vector2(agent.width/2f,agent.height/2f),new(0,1),agent.width,agent.height).Y<0.5f
                        &&Collision.CanHitLine(p,1,1,at,1,1)).OrderBy(p=>Vector2.DistanceSquared(p,agent.Center));
                foreach(var p in candidates)if(TerrainRoute.Find(agent,p,20,3.5f)!=null){_destination=p;break;}
                if(_destination==null){message="附近没有可走到且能够接触作物的落脚点。";return GardenStep.Failed;}
            }
            var result=MovementHelper.MoveTowards(agent,_destination.Value,ref _movement,MovementHelper.MovementSettings.Create(3.5f,20));
            message=result.Message??"正在接近作物。";return result.Status==AgentActionStatus.Failure?GardenStep.Failed:GardenStep.Pending;
        }
        MovementHelper.Stop(agent);_destination=null;
        if(operation==GardenOperation.Plant)
        {
            int before=Count(crop.Seed);
            if(before<1)
            {
                message=NearbyChests.Transfer(player,new(ChestOperation.Withdraw,new[]{new ChestItemRequest(Crafting.Name(crop.Seed),1)}),boxes);
                if(Count(crop.Seed)<1)return GardenStep.Failed;
            }
            // Reuse the existing inventory-reserving, native non-forced furniture placement.
            before=Count(crop.Seed);
            var placed=new HousingStructureSite(player,Point.Zero).Place(GardenWorld.PlantStep(target),out message);
            bool verified=GardenWorld.Planted(target)&&Count(crop.Seed)==before-1;
            receipt=new(verified?1:0,0,0);
            if(!placed||!verified){message="种植未通过原生植株及实际扣种子检查，已完成部分保留。";return GardenStep.Failed;}
            message=$"种下{GardenRequest.Name(target.Crop)} tile({target.At.X},{target.At.Y})；自然生长。";return GardenStep.Done;
        }
        if(!WorldGen.CanKillTile(target.At.X,target.At.Y)){message="原生规则或模组阻止采收，未移除。";return GardenStep.Failed;}
        _beforeHerbs=Count(crop.Herb);_beforeSeeds=Count(crop.Seed);
        WorldGen.KillTile(target.At.X,target.At.Y,noItem:false);
        _removed=!Framing.GetTileSafely(target.At.X,target.At.Y).HasTile;
        if(!_removed){message="草药没有被原生采收移除，未记成功。";return GardenStep.Failed;}
        _settle=60;message="已采收，正在拾取真实掉落。";return GardenStep.Pending;
    }
    internal ChainGatherResult Harvest(int item,out string message)
    {
        var crop=GardenWorld.HerbCrop(item);if(crop==null){message="不支持的草药。";return ChainGatherResult.Failed;}
        if(_current==null)
        {
            if(_attempts++>=64){message="草药采收达到64株尝试上限。";return ChainGatherResult.Failed;}
            _current=GardenWorld.Scan(new(GardenOperation.Harvest,crop,1),origin).Where(GardenRules.Harvestable)
                .OrderBy(c=>Math.Abs(c.At.X-origin.X)+Math.Abs(c.At.Y-origin.Y)).FirstOrDefault();
            if(_current==null){message="起点28格内没有该种已开花草药；保留幼苗／未开花植株，请等待自然生长或手动补料。";return ChainGatherResult.Failed;}
            _operation=GardenOperation.Harvest;
        }
        var result=Work(_current,GardenOperation.Harvest,out _,out message);
        if(result==GardenStep.Done){_current=null;Stop();}
        return result switch {GardenStep.Done=>ChainGatherResult.Finished,GardenStep.Asking=>ChainGatherResult.WaitingForApproval,GardenStep.Failed=>ChainGatherResult.Failed,_=>ChainGatherResult.Pending};
    }
    internal string Approve()
    {
        var store=CompanionMemorySystem.Store;
        if(_approval==null||store==null||!Valid(out _)||!_approval.Matches(CompanionMemorySystem.Session,task,Decision,DateTimeOffset.UtcNow,store.Memory.RuleRevision,Main.dayTime)
            ||_current==null||GardenWorld.Observe(_current.At,_current.Crop).Stamp!=_current.Stamp)return "采收目标、规则或许可已变化，请重新规划。";
        _approved=true;_approval=null;return "已同意本次草药采收。";
    }
    private void Pickup(int type)=>ProgramGathering.PickupMatching(player,agent,type);
    public bool Planted(GardenCell target)=>GardenWorld.Planted(target);
    public bool Removed(GardenCell target)=>_removed&&!Framing.GetTileSafely(target.At.X,target.At.Y).HasTile;
    public void Stop()
    {_approval=null;ApprovalId=Guid.Empty;_approved=false;_permissionCaptured=false;_settle=0;_age=0;_current=null;_destination=null;_movement.Reset(20);MovementHelper.Stop(agent);}
}
