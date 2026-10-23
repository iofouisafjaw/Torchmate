using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Map;
using Terraria.ModLoader;
using Terraria.ObjectData;
using Terraria.UI;

namespace TerrarAI.Content.Systems;

public sealed class MapLocationSystem:ModSystem
{
    private static readonly LocationMarkers Markers=new();
    private sealed record Observation(Guid Session,int Player,string Identity,Func<MapLocation,bool> Valid);
    private static readonly Dictionary<LocationKind,Observation> Observations=new();
    internal static bool RememberObserved(Player player,MapLocation point,Func<MapLocation,bool> valid)
    {
        if(!Ready||player.whoAmI!=Main.myPlayer||!valid(point))return false;
        Observations[point.Kind]=new(CompanionMemorySystem.Session,player.whoAmI,point.Identity,valid);
        Markers.Replace(CompanionMemorySystem.Session,player.whoAmI,point.Kind,point with{Known=true});return true;
    }
    private static void Clear(){Markers.Clear();Observations.Clear();}
    internal static bool Ready=>!Main.dedServ&&!Main.gameMenu&&Main.netMode==NetmodeID.SinglePlayer&&Main.LocalPlayer.active&&
        CompanionMemorySystem.Session!=Guid.Empty&&Main.Map!=null&&Main.Map.MaxWidth>=Main.maxTilesX&&Main.Map.MaxHeight>=Main.maxTilesY;
    internal static bool KnownOre(string name)=>TerrariaLocationSource.OreTypes(new(LocationKind.Ore,name)).Count>0;
    internal static string Query(Player player,LocationRequest request)
    {
        if(!Ready||player.whoAmI!=Main.myPlayer)return "请在本地单人世界、地图加载完毕后查找地点。";
        if(request.Action==LocationAction.Clear){Clear();return "地图标记已清除。";}
        if(request.Action==LocationAction.Show)
        {
            var list=Current();return list.Count==0?"当前没有有效地图标记。":string.Join("\n",list.Select(p=>Describe(player,p)));
        }
        try
        {
            Observations.Remove(request.Kind);
            var source=new TerrariaLocationSource(player);
            if(!source.Supports(request))
            {Markers.Replace(CompanionMemorySystem.Session,player.whoAmI,request.Kind,null);return "暂不能定位这类物品。请指定矿石全名，或说“最近的矿在哪”“附近箱子在哪”“我的床在哪”。";}
            var found=LocationQueries.Find(request,player.Center.X/16.0,player.Center.Y/16.0,source);
            Markers.Replace(CompanionMemorySystem.Session,player.whoAmI,request.Kind,found);
            Record(new{kind=request.Kind.ToString(),request.Name,request.Personal,found=found!=null,target=found,scope=LocationQueries.Radius});
            string fallback=request.Personal&&found?.Personal!=true?"没有找到已设置出生点对应的床；":"";
            if(found==null)return fallback+$"{LocationQueries.Radius}格内已探索区域没有找到{KindName(request)}，旧的同类标记已清除；不代表世界里没有。";
            return fallback+Describe(player,found)+"\n已加入小地图／全屏地图标记，鼠标停在图标上可看更新的方向和距离。"+
                (found.Personal?"这里只核实已设置出生点对应的床仍在，不检测是否满足重生条件。":"搜索范围：玩家周围120格的已探索区域；距离为直线距离，不代表路线可达。")+
                (!Main.mapEnabled?" 当前地图已关闭，请在设置中启用地图查看。":"");
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {Markers.Replace(CompanionMemorySystem.Session,player.whoAmI,request.Kind,null);return "地点查找未完成，未保留旧的同类标记："+ex.GetType().Name;}
    }
    internal static string FromTool(Player player,string item)=>LocationRequest.TryParse("找"+item,out var request,KnownOre)&&request!=null&&request.Action==LocationAction.Find?
        Query(player,request):"定位工具只支持矿、箱子和床；请指定矿名，或使用“矿”“箱子”“床”“我的床”。";
    private static string KindName(LocationRequest r)=>r.Kind==LocationKind.Ore?r.Name.Length>0?r.Name:"矿物":r.Kind==LocationKind.Chest?"箱子":"床";
    internal static string Describe(Player player,MapLocation p)=>$"{p.Name}：{LocationQueries.Bearing(player.Center.X/16.0,player.Center.Y/16.0,p).Text}，tile({p.X},{p.Y})。";
    internal static IReadOnlyList<MapLocation> Current()
    {
        if(!Ready)return Array.Empty<MapLocation>();
        var source=new TerrariaLocationSource(Main.LocalPlayer);
        return Markers.Current(CompanionMemorySystem.Session,Main.myPlayer,p=>Observations.TryGetValue(p.Kind,out var observation)&&observation.Identity==p.Identity?
            observation.Session==CompanionMemorySystem.Session&&observation.Player==Main.myPlayer&&observation.Valid(p):source.Valid(p));
    }
    public override void PostUpdateWorld(){if(Ready&&Main.GameUpdateCount%30==0)_=Current();}
    public override void OnWorldLoad()=>Clear();
    public override void OnWorldUnload()=>Clear();
    public override void Unload()=>Clear();
    private static void Record(object data)
    {try{CompanionMemorySystem.Record("map_location_query",null,null,data);}catch(Exception ex)when(ex is not OutOfMemoryException){/* A failed receipt cannot invalidate an already placed UI marker. */}}
}

// One native overlay layer handles minimap, overlay map and full-screen map using
// tML's own coordinate transform, clipping, zoom and hover result.
public sealed class CompanionLocationMapLayer:ModMapLayer
{
    public override void Draw(ref MapOverlayDrawContext context,ref string text)
    {
        if(!MapLocationSystem.Ready)return;
        foreach(var marker in MapLocationSystem.Current())
        {
            int icon=marker.Icon>0&&marker.Icon<TextureAssets.Item.Length?marker.Icon:
                marker.Kind==LocationKind.Chest?ItemID.Chest:marker.Kind==LocationKind.Bed?ItemID.Bed:marker.Kind==LocationKind.Danger?ItemID.Sign:ItemID.IronOre;
            Main.instance.LoadItem(icon);var texture=TextureAssets.Item[icon].Value;
            float size=22f/Math.Max(texture.Width,texture.Height);
            var color=marker.Kind==LocationKind.Ore?Color.Gold:marker.Kind==LocationKind.Chest?Color.LightCyan:marker.Kind==LocationKind.Danger?Color.OrangeRed:Color.LightGreen;
            var result=context.Draw(texture,new Vector2((float)marker.CenterX,(float)marker.CenterY),color,new SpriteFrame(1,1),size,size*1.2f,Alignment.Center);
            if(result.IsMouseOver)text=MapLocationSystem.Describe(Main.LocalPlayer,marker);
        }
    }
}

internal sealed class TerrariaLocationSource(Player player):ILocationSource
{
    private readonly Dictionary<(int Type,int Style),(string Name,int Icon)> _names=new();
    private static bool BedType(int type)=>type==TileID.Beds||type>=0&&type<TileID.Sets.IsValidSpawnPoint.Length&&TileID.Sets.IsValidSpawnPoint[type];
    private static bool Known(int x,int y)=>WorldGen.InWorld(x,y,2)&&Main.Map.IsRevealed(x,y);
    private static bool Known(MapLocation p)=>new StructureStep(p.X,p.Y,StepKind.Furniture,p.Type,0,0,Width:p.Width,Height:p.Height).Footprint().Any(at=>Known(at.X,at.Y));
    private (string Name,int Icon) ItemName(int type,int style,string fallback,int icon)
    {
        if(_names.TryGetValue((type,style),out var cached))return cached;
        var item=ContentSamples.ItemsByType.Values.Where(i=>i.createTile==type&&i.placeStyle==style).OrderBy(i=>i.type).FirstOrDefault();
        var result=(item==null?fallback:Crafting.Name(item.type),item?.type??icon);_names[(type,style)]=result;return result;
    }
    internal static IReadOnlyList<ushort> OreTypes(LocationRequest request)
    {
        var shared=GatherTargets.LocateOreTypes(request.Name);if(shared.Count>0)return shared;
        int type=Crafting.ResolveExact(request.Name);
        if(type>0&&ContentSamples.ItemsByType.TryGetValue(type,out var item)&&item.createTile>=TileID.Dirt&&item.createTile<TileID.Sets.Ore.Length&&TileID.Sets.Ore[item.createTile])
            return new[]{(ushort)item.createTile};
        return Array.Empty<ushort>();
    }
    public bool Supports(LocationRequest request)=>request.Kind!=LocationKind.Ore||request.Name.Length==0||OreTypes(request).Count>0;
    private static string ChestKey(NearbyChests.Box b)=>$"chest:{b.Id}:{b.Chest.x}:{b.Chest.y}:{RuntimeHelpers.GetHashCode(b.Chest)}";
    internal MapLocation ChestLocation(NearbyChests.Box b)
    {
        string label=string.IsNullOrWhiteSpace(b.Chest.name)?"箱子":"箱子「"+CompanionProtocol.Clip(b.Chest.name.Replace('\n',' ').Replace('\r',' '),40)+"」";
        var tile=Framing.GetTileSafely(b.Chest.x,b.Chest.y);var named=ItemName(tile.TileType,TileObjectData.GetTileStyle(tile),"箱子",ItemID.Chest);
        var result=new MapLocation(LocationKind.Chest,ChestKey(b),label,b.Chest.x,b.Chest.y,tile.TileType,2,2,Icon:named.Icon);
        return result with{Known=Known(result)};
    }
    internal MapLocation OreLocation(int x,int y,int type)
    {
        var named=ItemName(type,0,GatherTargets.OreLabel(type)??"矿物",ItemID.IronOre);
        return new(LocationKind.Ore,$"ore:{type}:{x}:{y}",named.Name,x,y,type,Icon:named.Icon);
    }
    private MapLocation? Bed(int x,int y,bool personal=false,bool labels=true)
    {
        if(!WorldGen.InWorld(x,y,2))return null;
        var tile=Framing.GetTileSafely(x,y);if(!tile.HasTile||tile.IsActuated||!BedType(tile.TileType))return null;
        var data=TileObjectData.GetTileData(tile);if(data==null)return null;
        var frame=FurnitureFrames.Decode(x,y,tile.TileFrameX,tile.TileFrameY,data.Width,data.Height,data.CoordinateWidth,data.CoordinatePadding,
            data.CoordinateHeights,data.CoordinateFullWidth,data.CoordinateFullHeight);
        if(frame==null||!WorldGen.InWorld(frame.X,frame.Y,2)||!WorldGen.InWorld(frame.X+data.Width-1,frame.Y+data.Height-1,2))return null;
        int yOffset=0;
        for(int row=0;row<data.Height;row++)
        {
            for(int col=0;col<data.Width;col++)
            {
                var t=Framing.GetTileSafely(frame.X+col,frame.Y+row);
                if(!t.HasTile||t.IsActuated||t.TileType!=tile.TileType||t.TileFrameX!=frame.BaseFrameX+col*(data.CoordinateWidth+data.CoordinatePadding)||t.TileFrameY!=frame.BaseFrameY+yOffset)return null;
            }
            yOffset+=data.CoordinateHeights[row]+data.CoordinatePadding;
        }
        (string Name,int Icon) named=labels?ItemName(tile.TileType,TileObjectData.GetTileStyle(tile),"床类出生点家具",ItemID.Bed):("床",ItemID.Bed);
        string identity=$"bed:{tile.TileType}:{frame.X}:{frame.Y}:{frame.BaseFrameX}:{frame.BaseFrameY}"+(personal?$":spawn:{player.SpawnX}:{player.SpawnY}":"");
        var result=new MapLocation(LocationKind.Bed,identity,personal?"已设置出生点的"+named.Name:named.Name,frame.X,frame.Y,tile.TileType,data.Width,data.Height,Personal:personal,Icon:named.Icon);
        return result with{Known=Known(result)};
    }
    private MapLocation? PersonalBed(bool labels=true)=>WorldGen.InWorld(player.SpawnX,player.SpawnY,3)?Bed(player.SpawnX,player.SpawnY-1,personal:true,labels:labels):null;
    public IEnumerable<MapLocation> Candidates(LocationRequest request)
    {
        double fromX=player.Center.X/16.0,fromY=player.Center.Y/16.0;
        if(request.Kind==LocationKind.Chest)
        {foreach(var box in NearbyChests.Locations(fromX,fromY,LocationQueries.Radius))yield return ChestLocation(box);yield break;}
        if(request.Personal&&PersonalBed() is MapLocation personal)yield return personal;
        var wanted=request.Kind==LocationKind.Ore&&request.Name.Length>0?OreTypes(request).ToHashSet():null;
        var beds=new HashSet<string>();
        int left=Math.Max(2,(int)Math.Floor(fromX)-LocationQueries.Radius-1),right=Math.Min(Main.maxTilesX-3,(int)Math.Ceiling(fromX)+LocationQueries.Radius+1);
        int top=Math.Max(2,(int)Math.Floor(fromY)-LocationQueries.Radius-1),bottom=Math.Min(Main.maxTilesY-3,(int)Math.Ceiling(fromY)+LocationQueries.Radius+1);
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            if(!Known(x,y))continue;
            var tile=Framing.GetTileSafely(x,y);if(!tile.HasTile||tile.IsActuated)continue;
            if(request.Kind==LocationKind.Ore)
            {
                if(wanted!=null?!wanted.Contains(tile.TileType):tile.TileType>=TileID.Sets.Ore.Length||!TileID.Sets.Ore[tile.TileType])continue;
                yield return OreLocation(x,y,tile.TileType);
            }
            else if(BedType(tile.TileType)&&Bed(x,y) is MapLocation bed&&beds.Add(bed.Identity))yield return bed;
        }
    }
    public bool Valid(MapLocation target)
        =>Valid(target,false);
    internal bool Valid(MapLocation target,bool observed)
    {
        if(!WorldGen.InWorld(target.X,target.Y,2))return false;
        try
        {
            if(target.Kind==LocationKind.Bed)
            {
                var actual=target.Personal?PersonalBed(labels:false):Bed(target.X,target.Y,labels:false);
                return actual!=null&&actual.Identity==target.Identity&&(actual.Personal||Known(actual));
            }
            if(!observed&&!Known(target))return false;
            if(target.Kind==LocationKind.Ore)
            {var t=Framing.GetTileSafely(target.X,target.Y);return t.HasTile&&!t.IsActuated&&t.TileType==target.Type;}
            if(target.Kind!=LocationKind.Chest)return false;
            int id=Chest.FindChest(target.X,target.Y);
            return id>=0&&Main.chest[id] is Chest chest&&NearbyChests.Intact(id,chest)&&ChestKey(new(id,chest))==target.Identity;
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){return false;}
    }
}
