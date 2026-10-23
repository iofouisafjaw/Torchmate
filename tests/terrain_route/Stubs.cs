// Tile geometry fixture only: no game process, saves, model or resource actions.
using Microsoft.Xna.Framework;
namespace Microsoft.Xna.Framework
{
    public struct Point(int x, int y) : IEquatable<Point>
    { public int X=x,Y=y; public bool Equals(Point p)=>X==p.X&&Y==p.Y; public override bool Equals(object? p)=>p is Point a&&Equals(a); public override int GetHashCode()=>HashCode.Combine(X,Y); public static bool operator==(Point a,Point b)=>a.Equals(b); public static bool operator!=(Point a,Point b)=>!a.Equals(b); }
    public struct Vector2(float x,float y) : IEquatable<Vector2>
    {
        public float X=x,Y=y; public Vector2(float v):this(v,v){} public static Vector2 Lerp(Vector2 a,Vector2 b,float f)=>a+(b-a)*f; public static Vector2 Zero=>new(0,0);
        public float LengthSquared()=>X*X+Y*Y;
        public static Vector2 Normalize(Vector2 p)=>p/(float)Math.Sqrt(p.LengthSquared());
        public static float Distance(Vector2 a,Vector2 b)=>(float)Math.Sqrt(DistanceSquared(a,b));
        public static float DistanceSquared(Vector2 a,Vector2 b)=>(a-b).LengthSquared();
        public static Vector2 operator+(Vector2 a,Vector2 b)=>new(a.X+b.X,a.Y+b.Y);
        public static Vector2 operator-(Vector2 a,Vector2 b)=>new(a.X-b.X,a.Y-b.Y);
        public static Vector2 operator*(Vector2 a,float f)=>new(a.X*f,a.Y*f);
        public static Vector2 operator/(Vector2 a,float f)=>new(a.X/f,a.Y/f);
        public bool Equals(Vector2 p)=>X==p.X&&Y==p.Y; public override bool Equals(object? p)=>p is Vector2 v&&Equals(v); public override int GetHashCode()=>HashCode.Combine(X,Y);
        public static bool operator==(Vector2 a,Vector2 b)=>a.Equals(b); public static bool operator!=(Vector2 a,Vector2 b)=>!a.Equals(b);
    }
    public struct Color
    { public static readonly Color White=new(),LightBlue=new(),LightGreen=new(),Gold=new(),Orange=new(),OrangeRed=new(),LightGoldenrodYellow=new(); }
    public struct Rectangle { public int Y; }
}

namespace Terraria
{
 public static class Main {
  public static int maxTilesX=220,maxTilesY=120,netMode;
  public static bool[] tileFrameImportant=new bool[1024],tileCut=new bool[1024];
  public static bool[] tileSolid=new bool[1024],tileSolidTop=new bool[1024];
 }
 public class Tile { public bool HasTile,IsActuated; public ushort TileType; public byte LiquidAmount; public int LiquidType; public short TileFrameY; public ushort WallType; }
 public static class Framing {
  public static Tile[,] Cells=new Tile[220,120];
  public static Tile GetTileSafely(int x,int y) => x>=0&&y>=0&&x<220&&y<120 ? Cells[x,y]??=new() : new();
 }
 public class NPC {
  public Vector2 position,velocity; public int width=40,height=40,direction=1;
  public bool collideX,collideY,noGravity,noTileCollide; public float gravity=.3f;
  public object? ModNPC;
  public Vector2 Center=>position+new Vector2(width/2f,height/2f);
  public Vector2 Bottom=>position+new Vector2(width/2f,height);
 }
 public class Player {public Vector2 Center;public Vector2 Bottom=>Center+new Vector2(0,20);public bool wet;}
 public static class Coordinates {public static Point ToTileCoordinates(this Vector2 p)=>new((int)(p.X/16),(int)(p.Y/16));}
 public static class WorldGen {
  public static bool InWorld(int x,int y,int margin=0)=>x>=margin&&y>=margin&&x<Main.maxTilesX-margin&&y<Main.maxTilesY-margin;
  public static bool IsLockedDoor(Tile t)=>t.TileFrameY>=594;
  public static bool OpenDoor(int x,int y,int direction) {
   if(IsLockedDoor(Framing.GetTileSafely(x,y)))return false;
   for(int yy=y-1;yy<=y+1;yy++)Framing.GetTileSafely(x,yy).HasTile=false;
   return true;
  }
 }
 public static class NetMessage {public static void SendTileSquare(int a,int x,int y,int size){} }
 // Swept AABB tiles and one-way platforms. Axis aligned geometry is deliberately
 // explicit; this fixture makes no claim about native slopes or liquid drag.
 public static class Collision {
  static bool Solid(Tile t)=>t.HasTile&&!t.IsActuated&&Main.tileSolid[t.TileType]&&!Main.tileSolidTop[t.TileType];
  public static bool SolidCollision(Vector2 p,int w,int h) {
   for(int x=(int)MathF.Floor(p.X/16);x<=(int)MathF.Floor((p.X+w-.001f)/16);x++)
    for(int y=(int)MathF.Floor(p.Y/16);y<=(int)MathF.Floor((p.Y+h-.001f)/16);y++)
     if(Solid(Framing.GetTileSafely(x,y)))return true;
   return false;
  }
  public static Vector2 TileCollision(Vector2 p,Vector2 v,int w,int h) {
   float dx=v.X,dy=v.Y;
   int left=(int)MathF.Floor((p.X+Math.Min(0,dx))/16)-1,right=(int)MathF.Floor((p.X+w+Math.Max(0,dx))/16)+1;
   int top=(int)MathF.Floor((p.Y+Math.Min(0,dy))/16)-1,bottom=(int)MathF.Floor((p.Y+h+Math.Max(0,dy))/16)+1;
   for(int x=left;x<=right;x++)for(int y=top;y<=bottom;y++) {
    var t=Framing.GetTileSafely(x,y); if(!Solid(t))continue;
    if(p.Y+h>y*16+.001f && p.Y<(y+1)*16-.001f) {
     if(dx>0&&p.X+w<=x*16+.001f)dx=Math.Min(dx,x*16-p.X-w);
     if(dx<0&&p.X>=(x+1)*16-.001f)dx=Math.Max(dx,(x+1)*16-p.X);
    }
   }
   for(int x=left;x<=right;x++)for(int y=top;y<=bottom;y++) {
    var t=Framing.GetTileSafely(x,y);
    if(!Solid(t)&&!(t.HasTile&&Main.tileSolidTop[t.TileType]))continue;
    if(p.X+dx+w>x*16+.001f&&p.X+dx<(x+1)*16-.001f) {
     if(dy>0&&p.Y+h<=y*16+.001f)dy=Math.Min(dy,y*16-p.Y-h);
     if(dy<0&&Solid(t)&&p.Y>=(y+1)*16-.001f)dy=Math.Max(dy,(y+1)*16-p.Y);
    }
   }
   return new(dx,dy);
  }
 }
}
namespace Terraria.ID {
 public static class WallID {public const int None=0;}
 public static class LiquidID {public const int Water=0,Lava=1,Honey=2,Shimmer=3;}
 public static class TileID {public const ushort Dirt=0,Stone=1,ClosedDoor=10,Spikes=48,WoodenSpikes=232,Platforms=19,
  Trees=5,Grass=2,Sand=53,Mud=59,ClayBlock=40,SnowBlock=147,IceBlock=161,
  Copper=7,Tin=166,Iron=6,Lead=167,Silver=9,Tungsten=168,Gold=8,Platinum=169,
  Meteorite=37,Demonite=22,Crimtane=204,Hellstone=58,Obsidian=56,Cobalt=107,Palladium=221,
  Mythril=108,Orichalcum=222,Adamantite=111,Titanium=223,Chlorophyte=211,LunarOre=408;
  public static class Sets {public static bool[] Ore=Make();static bool[] Make(){var a=new bool[1024];foreach(int id in new[]{Iron,Gold,Cobalt})a[id]=true;return a;}}}
 public static class NetmodeID {public const int Server=2;}
}
namespace TerrarAI.Content.NPCs {internal class AIAgentNPC {internal float GetJumpMultiplier()=>1;} }
namespace TerrarAI.Content.Actions {
 public enum AgentActionStatus {Pending,Success,Failure}
 public readonly record struct AgentActionResult(AgentActionStatus Status,string? Message=null) {
  public static AgentActionResult Pending(string? s=null)=>new(AgentActionStatus.Pending,s);
  public static AgentActionResult Failure(string s)=>new(AgentActionStatus.Failure,s);
  public static AgentActionResult Success(string s)=>new(AgentActionStatus.Success,s);
 }
}
namespace TerrarAI.Content.Systems {
 internal static class TreeHelper {
  internal static List<Point> FindTreeBasesNear(Point p,int radius)=>new();
  internal static bool DoesTreeExist(Point p)=>false;
 }
 internal static class TaskTargetClaims {internal static bool Occupied(Point p)=>false;}
 internal static class HousingInspector {internal static bool Hazard(Terraria.Tile t)=>false;}
 // Tool thresholds are covered separately by native_replacement, which compiles
 // the production ToolSelector. These values isolate target selection geometry.
 internal static class ToolSelector {
  internal static int GetTileStrength(int type)=>type switch {Terraria.ID.TileID.Iron=>35,Terraria.ID.TileID.Gold=>45,Terraria.ID.TileID.Cobalt=>100,_=>0};
 }
 internal static class ServerAuthority {internal static bool IsServer=>true;}
 internal static class CompanionMemorySystem {
  internal static List<(string Kind,object Data)> Events=new();
  internal static void Record(string kind,object? a,object? b,object data)=>Events.Add((kind,data));
 }
}
