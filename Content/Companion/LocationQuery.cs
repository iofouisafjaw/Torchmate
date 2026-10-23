using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public enum LocationKind { Ore,Chest,Bed,Danger }
public enum LocationAction { Find,Show,Clear }
public sealed record LocationRequest(LocationKind Kind,string Name="",bool Personal=false,LocationAction Action=LocationAction.Find)
{
    public static bool TryParse(string input,out LocationRequest? request,Func<string,bool>? knownOre=null)
    {
        request=null;if(input.Length>240)return false;
        string text=input.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。','！','!','?','？','.');
        if(text is "清除标记" or "清除地图标记" or "删除地图标记" or "取消地图标记")
        {request=new(LocationKind.Ore,Action:LocationAction.Clear);return true;}
        if(text is "查看标记" or "查看地图标记" or "我的地图标记" or "当前标记")
        {request=new(LocationKind.Ore,Action:LocationAction.Show);return true;}
        if(Regex.IsMatch(text,"不要|别|不用|以后|记住|下次|每次|如果|假如|怎么|如何|然后|并且|顺便|再去|同时|[、和与]"))return false;
        text=Regex.Replace(text,@"^(?:(?:请|帮我|麻烦你|麻烦)\s*)+","");
        string? body=null;
        foreach(string pattern in new[]{
            @"^(?:在地图上)?(?:标出|标记一下|标记|标一下|找一下|找找|寻找|找到|查找|找)(?<body>.+)$",
            @"^(?:把)?(?<body>.+?)(?:标在地图上|标到地图上|标记在地图上|标出来|标记出来|标一下)$",
            @"^(?<body>.+?)(?:的?位置在哪|在哪里|在哪(?:里|儿)?)$",
            @"^(?:附近|周围|这附近)?(?:有没有|有|哪里有|哪儿有)(?<body>.+)$"})
        {var m=Regex.Match(text,pattern);if(m.Success){body=m.Groups["body"].Value.Trim();break;}}
        if(body==null)return false;
        body=Regex.Replace(body,@"^(?:(?:离我最近的|离我最近|最近的|最近|附近的|附近|周围的|周围|一个|一处|一张|一口)\s*)+","");
        body=Regex.Replace(body,@"(?:的?位置)$","").Trim();
        if(body is "箱子" or "宝箱" or "箱")request=new(LocationKind.Chest);
        else if(body is "床" or "床铺")request=new(LocationKind.Bed);
        else if(body is "我的床" or "我的床铺" or "出生点的床" or "我设置出生点的床" or "我的出生点")request=new(LocationKind.Bed,Personal:true);
        else if(body is "矿" or "矿石" or "矿物" or "矿脉")request=new(LocationKind.Ore);
        else if(body.Length<=40&&(Regex.IsMatch(body,@"矿(?:石)?$")||body is "黑曜石" or "陨石"||knownOre?.Invoke(body)==true))
            request=new(LocationKind.Ore,body);
        return request!=null;
    }
}

// Tile units throughout. X/Y identify the object's top-left tile; the marker and
// distance use its actual center. Native identity/geometry is rechecked by the source.
public sealed record MapLocation(LocationKind Kind,string Identity,string Name,int X,int Y,int Type=0,int Width=1,int Height=1,
    bool Known=true,bool Personal=false,int Icon=0)
{
    public double CenterX=>X+Width/2.0;
    public double CenterY=>Y+Height/2.0;
}
public interface ILocationSource
{
    IEnumerable<MapLocation> Candidates(LocationRequest request);
    bool Valid(MapLocation location);
}
public sealed record LocationBearing(string Direction,double Distance)
{
    public string Text=>$"{Direction}约{Distance.ToString("0.#",CultureInfo.InvariantCulture)}格";
}
public static class LocationQueries
{
    public const int Radius=120;
    private static readonly string[] Directions={"右方","右下方","下方","左下方","左方","左上方","上方","右上方"};
    public static LocationBearing Bearing(double fromX,double fromY,MapLocation target)
    {
        if(!double.IsFinite(fromX)||!double.IsFinite(fromY))throw new ArgumentException("位置无效。");
        double dx=target.CenterX-fromX,dy=target.CenterY-fromY,distance=Math.Sqrt(dx*dx+dy*dy);
        if(distance<0.75)return new("脚边",Math.Round(distance,1,MidpointRounding.AwayFromZero));
        double angle=(Math.Atan2(dy,dx)+Math.PI*2)%(Math.PI*2);
        int sector=(int)Math.Floor(angle/(Math.PI/4)+0.5)%8;
        return new(Directions[sector],Math.Round(distance,1,MidpointRounding.AwayFromZero));
    }
    public static MapLocation? Find(LocationRequest request,double x,double y,ILocationSource source)
    {
        if(request.Action!=LocationAction.Find||!double.IsFinite(x)||!double.IsFinite(y))return null;
        bool Accept(MapLocation p)=>p.Kind==request.Kind&&p.X>=0&&p.Y>=0&&p.Width is >=1 and <=16&&p.Height is >=1 and <=16&&
            (p.Known||request.Personal&&p.Personal)&&
            (request.Personal&&p.Personal||Math.Pow(p.CenterX-x,2)+Math.Pow(p.CenterY-y,2)<=Radius*Radius)&&source.Valid(p);
        var result=source.Candidates(request).Where(Accept).DistinctBy(p=>p.Identity)
            .OrderByDescending(p=>request.Personal&&p.Personal)
            .ThenBy(p=>Math.Pow(p.CenterX-x,2)+Math.Pow(p.CenterY-y,2)).ThenBy(p=>p.Y).ThenBy(p=>p.X).ThenBy(p=>p.Identity,StringComparer.Ordinal)
            .FirstOrDefault();
        // A source may change during enumeration or validation. Never publish a stale hit.
        return result!=null&&Accept(result)?result:null;
    }
}

// One marker of each kind, scoped to a player and world visit; it never writes to
// the game's map tiles or exploration data. A failed search replaces that kind too.
public sealed class LocationMarkers
{
    private readonly Dictionary<LocationKind,MapLocation> _locations=new();
    private Guid _session;
    private int _player=-1;
    private void Bind(Guid session,int player)
    {if(_session!=session||_player!=player){_locations.Clear();_session=session;_player=player;}}
    public void Replace(Guid session,int player,LocationKind kind,MapLocation? target)
    {
        Bind(session,player);_locations.Remove(kind);
        if(session!=Guid.Empty&&target!=null&&target.Kind==kind)_locations[kind]=target;
    }
    public IReadOnlyList<MapLocation> Current(Guid session,int player,Func<MapLocation,bool> valid)
    {
        Bind(session,player);
        if(session==Guid.Empty){_locations.Clear();return Array.Empty<MapLocation>();}
        foreach(var key in _locations.Where(x=>!valid(x.Value)).Select(x=>x.Key).ToArray())_locations.Remove(key);
        return _locations.OrderBy(x=>x.Key).Select(x=>x.Value).ToArray();
    }
    public void Clear(){_locations.Clear();_session=Guid.Empty;_player=-1;}
}

public sealed record FurnitureLocation(int X,int Y,int BaseFrameX,int BaseFrameY,int Column,int Row);
public static class FurnitureFrames
{
    // Uses TileObjectData coordinates, including uneven row heights and padding.
    // Both orientations and all horizontal/vertical styles share this decoder.
    public static FurnitureLocation? Decode(int x,int y,int frameX,int frameY,int width,int height,int cellWidth,int padding,
        IReadOnlyList<int> heights,int fullWidth,int fullHeight)
    {
        if(x<0||y<0||frameX<0||frameY<0||width is <1 or >16||height is <1 or >16||cellWidth is <1 or >64||padding is <0 or >16||
            heights.Count<height||heights.Take(height).Any(h=>h is <1 or >64)||fullWidth is <1 or >4096||fullHeight is <1 or >4096)return null;
        int localX=frameX%fullWidth,localY=frameY%fullHeight,step=cellWidth+padding;
        if(localX%step!=0)return null;int column=localX/step,row=-1,offset=0;
        for(int i=0;i<height;i++){if(localY==offset){row=i;break;}offset+=heights[i]+padding;}
        if(column>=width||row<0||x<column||y<row)return null;
        return new(x-column,y-row,frameX-localX,frameY-localY,column,row);
    }
}
