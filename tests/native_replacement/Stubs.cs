// Simulated native API surface; production actions/NPC/transport are compiled unchanged.
using Microsoft.Xna.Framework;
using global::TerrarAI.Content.Actions;
using global::TerrarAI.Content.Companion;
namespace Microsoft.Xna.Framework
{
    public struct Point(int x, int y) : IEquatable<Point>
    { public int X=x,Y=y; public bool Equals(Point p)=>X==p.X&&Y==p.Y; public override bool Equals(object? p)=>p is Point a&&Equals(a); public override int GetHashCode()=>HashCode.Combine(X,Y); public static bool operator==(Point a,Point b)=>a.Equals(b); public static bool operator!=(Point a,Point b)=>!a.Equals(b); }
    public struct Vector2(float x,float y) : IEquatable<Vector2>
    {
        public float X=x,Y=y; public Vector2(float v):this(v,v){} public static Vector2 Zero=>new(0,0);
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
namespace Microsoft.Xna.Framework.Graphics { public class SpriteBatch {} }
namespace Terraria
{
    public static class Main
    {
        public static int netMode,maxTilesX=200,maxTilesY=120,maxItems=12;
        public static long GameUpdateCount;
        public static bool dayTime=true,gameMenu,gamePaused;
        public static double worldSurface=60;public static bool[] tileSolid=new bool[1024],tileSolidTop=new bool[1024];
        public static bool[] wallHouse=new bool[1024],tileFrameImportant=new bool[1024],tileCut=new bool[1024];
        public static NPC[] npc=Array.Empty<NPC>(); public static Player[] player=Array.Empty<Player>();
        public static Item[] item=Enumerable.Range(0,12).Select(_=>new Item()).ToArray(); public static int[] npcFrameCount=new int[1024];
        public static void QueueMainThreadAction(Action call)=>call();
    }
    public class Tile { public bool HasTile,IsActuated,HasActuator,RedWire,BlueWire,GreenWire,YellowWire; public ushort TileType,WallType;public byte LiquidAmount,TileColor,WallColor;public int LiquidType; }
    public static class Framing
    {
        public static Dictionary<(int,int),Tile> Cells=new();
        public static Tile GetTileSafely(int x,int y)
        { if(!Cells.TryGetValue((x,y),out var t))Cells[(x,y)]=t=new();return t; }
    }
    public static class WorldGen
    {
        public static int Kills; public static bool Protected; public static Action<int,int>? OnKill;
        public static bool InWorld(int x,int y,int margin=0)=>x>=margin&&y>=margin&&x<Main.maxTilesX-margin&&y<Main.maxTilesY-margin;
        public static bool CanKillTile(int x,int y)=>!Protected;
        public static void KillTile(int x,int y,bool fail=false,bool effectOnly=false,bool noItem=false)
        { Kills++; if(!Protected)Framing.GetTileSafely(x,y).HasTile=false;OnKill?.Invoke(x,y); }
    }
    public class Item
    {
        public int type,stack,pick,axe,hammer,damage,ammo,useTime=20,maxStack=9999;
        public bool active,consumable,accessory,channel,noUseGraphic,noMelee,Melee=true;
        public Vector2 position; public Vector2 Center=>position;
        public bool IsAir=>type==0||stack<=0;
        public bool CountsAsClass(ModLoader.DamageClass c)=>Melee;
        public void SetDefaults(int id){type=id;stack=1;}
    }
    public class NPC
    {
        public bool active=true,townNPC,friendly,dontTakeDamage,dontTakeDamageFromHostiles,noGravity,noTileCollide,collideY=true,netUpdate;
        public int type,width=40,height=40,whoAmI,lifeMax,aiStyle,damage,direction=1,spriteDirection;
        public float gravity=.3f,stepSpeed,knockBackResist; public double frameCounter;
        public string TypeName="fixture";public T GetGlobalNPC<T>()where T:new()=>new();
        public string GivenName="Qwen"; public float[] ai=new float[4]; public Rectangle frame;
        public Vector2 position,velocity; public ModLoader.ModNPC? ModNPC;
        public Vector2 Center {get=>position+new Vector2(width/2f,height/2f);set=>position=value-new Vector2(width/2f,height/2f);}
        public Vector2 Right=>position+new Vector2(width,height/2f);public Vector2 Bottom=>position+new Vector2(width/2f,height);public Vector2 Top=>position+new Vector2(width/2f,0);
    }
    public class Player
    {
        public bool active=true,dead;public int direction=1,whoAmI;public Vector2 Bottom=>Center+new Vector2(0,20);public string name="tester";public Vector2 Center;
        public Item[] inventory=Enumerable.Range(0,58).Select(_=>new Item()).ToArray();
        public Item GetItem(int id,Item drop,GetItemSettings settings)
        {
            var rest=new Item{type=drop.type,stack=drop.stack,maxStack=drop.maxStack,active=true};
            foreach(var slot in inventory.Take(50))
            {
                if(rest.stack==0)break;
                if(!slot.IsAir&&slot.type!=rest.type)continue;
                int amount=Math.Min(rest.stack,slot.IsAir?rest.maxStack:slot.maxStack-slot.stack);
                if(amount<=0)continue;
                if(slot.IsAir){slot.type=rest.type;slot.stack=0;slot.maxStack=rest.maxStack;}
                slot.stack+=amount;rest.stack-=amount;
            }
            return rest;
        }
    }
    public class GetItemSettings { public static GetItemSettings InventoryEntityToPlayerInventorySettings=new(); }
    public static class Collision {public static bool CanHitLine(Vector2 a,int w,int h,Vector2 b,int w2,int h2)=>true;public static Vector2 TileCollision(Vector2 p,Vector2 v,int w,int h)=>v; public static bool SolidCollision(Vector2 p,int w,int h)=>false; }
    public static class NetMessage { public static void SendTileSquare(int who,int x,int y,int size){} public static void SendData(int type,int number=0){} }
    public static class Lang {public static string GetItemNameValue(int type)=>"item"+type;}
    public static class Utils {public static void DrawBorderString(Microsoft.Xna.Framework.Graphics.SpriteBatch b,string s,Vector2 p,Color c,float scale){} }
}
namespace Terraria.DataStructures {public interface IEntitySource{} }
namespace Terraria.Chat {public static class ChatHelper {public static void BroadcastChatMessage(Terraria.Localization.NetworkText text,Color color){} } }
namespace Terraria.Localization {public class NetworkText {public static NetworkText FromLiteral(string text)=>new();} }
namespace Terraria.GameContent
{
    public class Font {public Vector2 MeasureString(string s)=>new(s.Length,10);}
    public class Asset {public Font Value=new();}
    public static class FontAssets {public static Asset MouseText=new();}
}
namespace Terraria.ID
{
    public static class NetmodeID {public const int SinglePlayer=0,MultiplayerClient=1,Server=2;}
    public static class LiquidID {public const int Water=0,Lava=1;}
    public static class PaintID {public const int None=0;}
    public static class NPCID {public const int None=0,GoblinScout=73;public static class Sets {public static bool[] CountsAsCritter=new bool[1024],ActsLikeTownNPC=new bool[1024],IsTownPet=new bool[1024];}}
    public static class MessageID {public const int SyncItem=21;}
    public static class ItemID {public const int Sign=171,PickaxeAxe=990,TheBreaker=991,TerraBlade=992;}
    public static class TileID
    {
        public const int Grass=2,Mud=59,ClayBlock=40,SnowBlock=147,IceBlock=161,Spikes=48,WoodenSpikes=232,Traps=137,Boulder=138,Explosives=141,Sand=53,Silt=123,Slush=224,Ebonsand=112,Crimsand=234,Pearlsand=116,
            Dirt=0,Stone=1,Trees=5,Copper=7,Tin=166,Iron=6,Lead=167,Silver=9,Tungsten=168,Gold=8,Platinum=169,
            Meteorite=37,Demonite=22,Crimtane=204,Ebonstone=25,Crimstone=203,Hellstone=58,Obsidian=56,Cobalt=107,Palladium=221,
            Mythril=108,Orichalcum=222,Adamantite=111,Titanium=223,Chlorophyte=211,LunarOre=408;
        public static class Sets {public static bool[] Ore=new bool[1024];public static bool[] IsATreeTrunk=Make();private static bool[] Make(){var a=new bool[1024];a[Trees]=true;return a;} }
    }
}
namespace Terraria.ModLoader.Config
{
    public enum ConfigScope {ServerSide,ClientSide}
    public abstract class ModConfig {public abstract ConfigScope Mode{get;}}
    [AttributeUsage(AttributeTargets.Property)] public class RangeAttribute(float min,float max):Attribute {public float Min=min,Max=max;}
}
namespace Terraria.ModLoader
{
    public class Logger {public void Error(string s){}public void Info(string s){}public void Warn(string s){} }
    public class Mod {public Logger Logger=new();public virtual void Load(){}public virtual void Unload(){} }
    public class ModSystem {public virtual void PostUpdateWorld(){}public virtual void OnWorldUnload(){}public virtual void OnWorldLoad(){}public virtual void Unload(){} }
    public class ModNPC
    {
        public Terraria.NPC NPC {get;} public ModNPC(){NPC=new(){ModNPC=this};}
        public int Type=>1; public virtual string Texture=>"";
        public virtual bool CheckActive()=>true; public virtual void SetStaticDefaults(){}public virtual void SetDefaults(){}
        public virtual void FindFrame(int h){}public virtual void OnSpawn(Terraria.DataStructures.IEntitySource s){}public virtual void OnKill(){}
        public virtual void AI(){}public virtual void SendExtraAI(BinaryWriter b){}public virtual void ReceiveExtraAI(BinaryReader b){}
        public virtual void PostDraw(Microsoft.Xna.Framework.Graphics.SpriteBatch b,Vector2 s,Color c){}
    }
    public static class ModContent {public static T GetInstance<T>() where T:new()=>Singleton<T>.Value;public static class Singleton<T>where T:new(){public static T Value=new();} }
    public class DamageClass {public static DamageClass Melee=new();}
    public class ModTile {public int MinPick;}
    public static class TileLoader {public static int ModMinPick;public static ModTile? GetTile(int type)=>type>=500?new(){MinPick=ModMinPick}:null;}
}
namespace TerrarAI.Content.Systems
{
    using Terraria;
    using global::TerrarAI.Content.NPCs;
    internal class TerrainRoute(Vector2 target,float tolerance,float speed)
    {internal static bool Wet,Arrive;internal static AgentActionResult Result=AgentActionResult.Pending();internal static int Created,Ticks;
     internal static object? Find(NPC n,Vector2 target,float tolerance,float speed)=>new object();
     private readonly int _id=++Created;internal AgentActionResult Tick(NPC n){Ticks++;if(Arrive&&Result.Status==AgentActionStatus.Success)n.Center=target;return Result;}internal static bool InWater(Vector2 p,int w,int h)=>Wet;}
    internal class FollowNavigation {internal void Reset(NPC n){}internal string Tick(NPC n,Player p,float s)=>"跟随中";}
    internal static class CompanionAgent {internal static void Cancel(){} }
    internal static class CompanionDialogueSystem {internal static void Cancel(){} }
    internal static class CompanionInquirySystem {internal static void Cancel(){} }
    internal class CompanionEnemyIdentity {public int Serial;}
    internal static class NearbyChests {internal static IEnumerable<object> Locations(double x,double y,int radius)=>Array.Empty<object>();}
    internal class TerrariaLocationSource(Player player) {
     internal MapLocation ChestLocation(object chest)=>new(LocationKind.Chest,"fixture","chest",1,1);
     internal MapLocation OreLocation(int x,int y,int type)=>new(LocationKind.Ore,"fixture","ore",x,y,type);
     internal bool Valid(MapLocation p,bool observed=false)=>true;
    }
    internal static class MapLocationSystem {
     internal static bool RememberObserved(Player p,MapLocation point,Func<MapLocation,bool> valid)=>valid(point);
     internal static string Describe(Player p,MapLocation point)=>point.Name;
    }
    internal static class ChatCoordinator {internal static AIAgentNPC? FindCompanion()=>Main.npc.Select(n=>n.ModNPC).OfType<AIAgentNPC>().FirstOrDefault();}
    internal static class CompanionUISystem {internal static List<string> Messages=new();internal static void Message(string s,Color c)=>Messages.Add(s);}
    internal static class CompanionCommandService
    {internal static long Revision;internal static ControlStamp Capture()=>new(CompanionMemorySystem.Session,Revision);internal static void Advance(){
#if TUNNEL_WORKSHOP_TEST
      CompanionExpeditionSystem.Cancel("new control");
#endif
      Revision++;
     }}
    internal static class CompanionMemorySystem
    {
        internal static CompanionStore? Store;internal static EpisodeRecorder? Episodes;internal static Guid Session=Guid.NewGuid();internal static CancellationToken WorldToken=>CancellationToken.None;
        internal static List<(string Kind,string? Task,string? Decision,object Data)> Events=new();
        internal static void Record(string kind,string? task,string? decision,object data)=>Events.Add((kind,task,decision,data));
        internal static void Observe(string text){}
    }
    internal static class CompanionObservation {internal static object Capture(Player? p,NPC n,Point? at=null,int r=0)=>new {items=p?.inventory.Sum(i=>i.stack)??0};}
    internal static class CompanionPrompt {internal static string Build(NPC n,Player? p,string command,string? result,Point? locked)=>"{}";}
    internal static class GatherTargets
    {
        internal static Point? Candidate;internal static List<Point> Candidates=new();
        internal static (Point,int) Origin(NPC n,Player? p,string a)=> (new(20,20),0);
        internal static Point? NextTree(NPC n,Point p,int side,HashSet<Point> skip)=>Candidates.Concat(Candidate is Point c?new[]{c}:Array.Empty<Point>()).Where(c=>!skip.Contains(c)).Select(c=>(Point?)c).FirstOrDefault();
        internal static Point? NextOre(NPC n,Point p,int side,HashSet<Point> skip,int power,string? ore,Func<Point,bool>? accept=null,bool exposedOnly=true)=>NextTree(n,p,side,skip);
        internal static string? OreLabel(int type)=>"铁矿";
        internal static bool TooHardFor(string? ore,int power)=>false;
    }
    internal static class CompanionRecoverySystem
    { internal static int Closures;internal static CompanionTaskView? ClosedView;internal static void Began(CompanionTaskView v){}internal static void Closed(CompanionTaskView v,string? reason=null){Closures++;ClosedView=v;} }
    internal class CollabView {internal string Describe()=>"collaboration";}
    internal static class CompanionCollaborationSystem {internal static CollabView? Current;internal static void Observe(CompanionTaskView? v){} }
    internal static class CompanionCombatSystem
    {
        internal static bool Fighting,Auto;internal static string Status=>"战斗";internal static bool Owns(int who)=>false;
        internal static void TickActive(AIAgentNPC n){}internal static bool TickAutomatic(AIAgentNPC n,Player p)=>Auto;
        internal static void Cancel(string s){}internal static void ResumeAutomatic(){}internal static string Describe()=>"战斗";
        internal static string AutomaticDescription()=>"自动反击";
        internal static void Draw(NPC n,Microsoft.Xna.Framework.Graphics.SpriteBatch b,Vector2 s,Color c){}
    }
#if !TUNNEL_WORKSHOP_TEST
    internal static class CompanionExpeditionSystem
    {internal static bool Asking;internal static Guid ConfirmationId;internal static string Status=>"探路";internal static bool Owns(int id)=>false;internal static bool IsTask(string id)=>false;
     internal static void TickActive(AIAgentNPC n){}internal static void Cancel(string s){}internal static string Describe()=>"探路";internal static string Confirm(Player p,Guid id)=>"确认";}
#endif
    internal static class CompanionGardenSystem
    {internal static bool Asking,Moving;internal static Guid ConfirmationId;internal static string Status=>"种植";internal static bool Owns(int id)=>false;
     internal static string Describe()=>"种植";internal static string Confirm(Player p,Guid id)=>"确认";}
    internal static class CraftChainSystem
    {internal static bool Asking,Moving;internal static Guid ConfirmationId;internal static bool Owns(int id)=>false;internal static string Describe()=>"合成";internal static string Confirm(Player p,Guid id)=>"确认";}
    internal class SupplyDummy {internal SupplyStage Stage;}
    internal static class CompanionConstructionSystem
    {internal static bool SupplyAsking,SupplyMoving;internal static SupplyDummy? Supply;internal static string Notice=>"";internal static bool Owns(int id)=>false;
     internal static string SupplyDescription()=>"备料";internal static string ConfirmSupplyHarvest(Player p)=>"确认";}
    internal class PlanDummy {internal string Name=>"房子";}
    internal static class StructureWorkshop
    {internal static bool ArenaAsking,ArenaMoving,ArenaMode;internal static Guid ArenaConfirmationId;internal static PlanDummy? Plan;internal static string Notice=>"";internal static bool Owns(int id)=>false;
     internal static string Confirm(Player p,Guid id)=>"确认";}
}
