// Explicit fake game dependencies. The actual production recovery bridge and task tracker
// are linked by the project; no Terraria DLL, native world or model is loaded by these tests.
using TerrarAI.Content.Companion;

namespace Microsoft.Xna.Framework
{
    public readonly record struct Point(int X, int Y);
    public readonly struct Color {public static Color Orange=>new();public static Color LightBlue=>new();}
}
namespace Terraria
{
    public class Player {public int whoAmI;public bool active=true,dead,ghost;}
    public class NPC {public int whoAmI=7;public bool active=true;}
    public static class Main
    {
        public static int netMode,maxPlayers=2;
        public static bool gameMenu,gamePaused;
        public static Player[] player={new(){whoAmI=0},new(){whoAmI=1}};
        public static Player LocalPlayer=>player[0];
    }
}
namespace Terraria.ID
{
    public static class NetmodeID {public const int SinglePlayer=0;}
    public static class ItemID { public const int IronskinPotion=1001,RegenerationPotion=1002,SwiftnessPotion=1003,LesserHealingPotion=1004; }
}
namespace Terraria.ModLoader
{
    public class ModSystem
    {
        public virtual void PostUpdateWorld(){}public virtual void OnWorldLoad(){}public virtual void OnWorldUnload(){}public virtual void Unload(){}
    }
    public static class ModContent
    {public static T GetInstance<T>()where T:new()=>Instance<T>.Value;private static class Instance<T>where T:new(){public static T Value=new();}}
}
namespace TerrarAI.Content.Systems
{
    using Terraria;
#if !COLLABORATION_ADAPTER
    public static class CompanionCollaborationSystem
    {
        internal static void Observe(CompanionTaskView? view) { }
        internal static void RecoveryBound(string previous, string current) { }
        internal static bool CanExecute(Player player, string? task) => true;
    }
#endif
    public static class CompanionCommandService
    {
        public static long Revision;
        internal static ControlStamp Capture()=>new(CompanionMemorySystem.Session,Revision);
        internal static void Advance()
        {
#if COLLABORATION_ADAPTER
            CompanionCollaborationSystem.ExternalCommand();
            if (CompanionTaskTracker.Current is { Closed:false } task) CompanionTaskTracker.Close(task.TaskId,"construction_cancelled",0);
#endif
            CompanionRecoverySystem.Invalidate();Revision++;
        }
    }
    public sealed class CompanionUISystem
    {
        public bool WantsPause;
        public static List<string> Messages=new();
        public static void Message(string text,Microsoft.Xna.Framework.Color color)=>Messages.Add(text);
        public static void RevealStructureSelection() { }
    }
    public static class CompanionMemorySystem
    {
        public static Guid Session=Guid.NewGuid();
        public static List<(string Kind,object Data)> Events=new();
        public static void Record(string kind,string? task,string? decision,object data)=>Events.Add((kind,data));
    }
    public static class ChatCoordinator
    {
        public sealed class Companion
        {
            public NPC NPC=new();
            public string RecoveryKind(string id)=>Modules.Is("gather",id)?"gather":"";
            public string RecoveryProgress=>"verified one of three removals; not bag pickups";
            public void StopCommand(string reason) { }
        }
        public static Companion? Agent=new();
        public static Companion? FindCompanion()=>Agent;
    }
    public static class CheatTools {public static CheatAuthority Authority=new();}
    public static class Crafting {public static string Name(int type)=>"item"+type;}
    public static class NearbyChests
    {
        public static Dictionary<int,int> Bag=new(),Boxes=new();
        public static bool Throw;
        public static Dictionary<int,int> Stock(Player player,bool includeChests=true)
        {
            if(Throw)throw new InvalidOperationException("stock fixture failure");
            var result=new Dictionary<int,int>(Bag);
            if(includeChests)foreach(var p in Boxes)result[p.Key]=result.GetValueOrDefault(p.Key)+p.Value;
            return result;
        }
    }
    public static class CompanionAgent
    {
        public static int Resumes;
        internal static CompanionRecoverySystem.Owner? LastOwner;
        public static TaskFailureReceipt? LastReceipt;
        public static string Snapshot(Player p)=>"snapshot for player "+p.whoAmI;
        public static void Cancel()=>CompanionRecoverySystem.Invalidate();
        internal static void Recover(CompanionRecoverySystem.Owner owner,TaskFailureReceipt receipt)
        {Resumes++;LastOwner=owner;LastReceipt=receipt;owner.Loop.Recover(receipt);}
    }
    // Fake module Begin/preview boundaries. Their side-effect-free previews let us test actual
    // dispatch/invalidation/rebinding in the production bridge, independent of native placement.
    public static class Modules
    {
        public static Dictionary<string,string> Ids=new();
        public static int Previews,Executions;
        public static Guid PreviewId;
        public static bool ThrowReplan;
        public static string Begin(string kind)
        {
            CompanionCommandService.Advance();CompanionAgent.Cancel();
            string id=Guid.NewGuid().ToString("N");Ids[kind]=id;Previews++;PreviewId=Guid.NewGuid();
            CompanionTaskTracker.Begin(id,"native "+kind+" goal");return "new preview, confirmation required";
        }
        public static string Replan(string kind)
        {if(ThrowReplan)throw new InvalidOperationException("replan fixture");return Begin(kind);}
        public static bool Is(string kind,string id)=>Ids.GetValueOrDefault(kind)==id;
    }
    public static class CraftChainSystem
    {
        public static bool IsTask(string id)=>Modules.Is("craft_chain",id);
        public static string RecoveryProgress=>"confirmed two craft steps; no final audit";
        public static string Replan(Player p)=>Modules.Replan("craft_chain");
        public static ChainTarget[] Targets=Array.Empty<ChainTarget>();
        public static string BeginTargets(Player p,IReadOnlyList<ChainTarget> targets,string goal)
        { Targets=targets.ToArray(); return Modules.Begin("craft_chain"); }
    }
    public static class CompanionConstructionSystem
    {
        public static bool IsTask(string id)=>Modules.Is("platform",id);
        public static string RecoveryProgress=>"placed three platforms";
        public static string Replan(Player p)=>Modules.Replan("platform");
    }
    public static class StructureWorkshop
    {
        public static bool ArenaMode,HousingMode;
        public static bool IsTask(string id)=>Modules.Is("build",id);
        public static string RecoveryProgress=>"placed two fixtures";
        public static string Replan(Player p)=>Modules.Replan("build");
        public static Microsoft.Xna.Framework.Point? Origin;
        public static StructurePlan? Plan;
        public static bool ArenaPassed, HousingPassed, IncludedPotions;
        public static string BeginArena(Player p,bool includePotions=true)
        { ArenaMode=true; HousingMode=false; IncludedPotions=includePotions; Origin=new(100,50); return Modules.Begin("build"); }
        public static string Begin(Player p,StructureLibrary.Entry e)
        { ArenaMode=HousingMode=false; Origin=null; Plan=e.Load(); return Modules.Begin("build"); }
        public static void CollaborationPreview(Microsoft.Xna.Framework.Point at) => Preview(at);
        public static void Replace() { Origin=null; }
        public static void Preview(Microsoft.Xna.Framework.Point at)
        { Origin=at; CompanionTaskTracker.Update(Modules.Ids["build"],CompanionTaskPhase.WaitingForApproval,result:"selected original site"); }
        public static bool CollaborationAudit(Player p,bool arena,Microsoft.Xna.Framework.Point? at,StructurePlan? house,int rooms,out string evidence)
        { bool ok=at!=null&&(arena?ArenaPassed:HousingPassed); evidence=ok?"actual scene checked":"scene incomplete"; return ok; }
    }
    public static class CompanionGardenSystem
    {
        public static GardenJob? Job;
        public static bool IsTask(string id)=>Modules.Is("garden",id);
        public static string RecoveryProgress=>"planted one verified herb";
        public static string Replan(Player p)=>Modules.Replan("garden");
    }
    public static class CompanionExpeditionSystem
    {
        public static bool IsTask(string id)=>Modules.Is("expedition",id);
        public static string Describe()=>"verified six removed tiles; drops not collected";
    }
    public static class CompanionCombatSystem
    {
        public static bool IsTask(string id)=>Modules.Is("combat",id);
        public static string Describe()=>"verified three hits and one kill";
    }
    public static class CompanionDialogueSystem { public static void Cancel() { } }
    public static class CompanionInquirySystem { public static void Cancel() { } }
    public static class StructureLibrary
    {
        public sealed record Entry(string Id,Func<StructurePlan> Load);
        public static List<Entry> List()=>Enumerable.Range(1,3).Select(n=>new Entry("preset:npc"+n,()=>StructurePresets.NpcRooms(n,
            new(1,1,2,2,3,3,4,4,5,5,6,6)))).ToList();
    }
    public sealed class HousingStructureSite(Player player,Microsoft.Xna.Framework.Point origin):IStructureSite
    {
        public static HashSet<StructureStep> Completed=new();
        public CellState Inspect(StructureStep step)=>Completed.Contains(step)?CellState.Done:CellState.Empty;
        public bool Clear(StructureStep s,out string error){error="no clear";return false;}
        public bool Place(StructureStep s,out string error){error="";Completed.Add(s);return true;}
    }
}
