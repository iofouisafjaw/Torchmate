using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Game = Terraria.Main;

int passed=0;
void Check(bool ok,string text) { if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text); }
void Throws(Action work,string text) {try{work();}catch(ActionParserException){Check(true,text);return;}throw new Exception("FAIL: "+text);}
var workspace=Path.GetFullPath(args.Length>0?args[0]:Directory.GetCurrentDirectory());
var temp=Path.Combine(workspace,".runtime","native-replacement-simulation",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
void Reset()
{
    Game.netMode=NetmodeID.SinglePlayer;Game.GameUpdateCount=0;Game.dayTime=true;Game.gameMenu=Game.gamePaused=false;
    Game.item=Enumerable.Range(0,Game.maxItems).Select(_=>new Item()).ToArray();
    Array.Clear(Game.wallHouse);Array.Clear(Game.tileFrameImportant);Array.Clear(Game.tileCut);
    Framing.Cells.Clear();WorldGen.Kills=0;WorldGen.Protected=false;WorldGen.OnKill=null;
    TerrainRoute.Wet=false;TerrainRoute.Result=AgentActionResult.Pending();TerrainRoute.Created=TerrainRoute.Ticks=0;
    TileLoader.ModMinPick=0;ModContent.Singleton<TerrarAI.TerrarAI_Config>.Value=new();
    new TaskTargetClaims().OnWorldUnload();new CompanionTaskTracker().OnWorldLoad();
    CompanionMemorySystem.Session=Guid.NewGuid();CompanionCommandService.Revision=0;
    CompanionMemorySystem.Store=new CompanionStore(temp,Guid.NewGuid(),CompanionMemorySystem.Session);
    CompanionMemorySystem.Episodes=new(CompanionMemorySystem.Store);CompanionMemorySystem.Events.Clear();
    CompanionRecoverySystem.Closures=0;CompanionRecoverySystem.ClosedView=null;CompanionUISystem.Messages.Clear();GatherTargets.Candidate=null;GatherTargets.Candidates.Clear();
}
Player PlayerWithTools()=>new(){inventory=Enumerable.Range(0,58).Select(i=>i==0?new Item{type=100,stack=1,axe=50}:i==1?new Item{type=101,stack=1,pick=100}:new Item()).ToArray()};
NPC Actor(Point at)=>new(){Center=new(at.X*16+8,at.Y*16+8)};
void Tile(Point at,int kind) {var t=Framing.GetTileSafely(at.X,at.Y);t.HasTile=true;t.TileType=(ushort)kind;}
AgentActionResult Run(AgentAction action,AgentActionContext context,int ticks=2500)
{ for(int i=0;i<ticks;i++){Game.GameUpdateCount++;var r=action.Tick(context);if(r.Status!=AgentActionStatus.Pending)return r;}throw new Exception("Action did not terminate"); }
var target=new Point(20,20);
Reset();var context=new AgentActionContext(Actor(target),PlayerWithTools());
var probe=new Probe();var terminal=Run(probe,context);for(int i=0;i<6;i++)Check(probe.Tick(context)==terminal,"terminal tick has no repeated effect "+i);
Check(probe.Executions==1&&probe.Exits==1,"terminal lifecycle exits exactly once");
probe.Reset();Run(probe,context);Check(probe.Executions==2,"explicit reset permits a new execution");
probe=new();probe.Cancel();probe.Cancel();Check(probe.Tick(context).Status==AgentActionStatus.Failure&&probe.Executions==0,"cancel before first tick has no effects");
probe=new(){Pending=true};Check(Run(probe,context).Status==AgentActionStatus.Failure&&probe.Executions==3,"timeout stops a pending action");
probe=new(){Fault=true};Check(Run(probe,context).Status==AgentActionStatus.Failure&&probe.Exits==1,"exception closes and cleans up action");

foreach(var tool in new[]{ToolType.Pickaxe,ToolType.Axe})
{
    Reset();var p=PlayerWithTools();Check(ToolSelector.FindInventoryTool(p,tool)!=null,"inventory selector finds "+tool);
    int threshold=tool==ToolType.Axe?50:100;Check(ToolSelector.FindInventoryTool(p,tool,threshold)!=null,"minimum tool power is inclusive");
    p.inventory=Enumerable.Range(0,58).Select(_=>new Item()).ToArray();Check(ToolSelector.FindBestTool(p,tool)==null,"no fabricated survival tool");
    ModContent.GetInstance<TerrarAI.TerrarAI_Config>().EnableCreativeMode=true;
    Check(ToolSelector.FindBestTool(p,tool)!=null&&ToolSelector.FindInventoryTool(p,tool)==null,"creative tool stays behind creative flag and inventory-only excludes it");
}
Reset();Check(!ToolSelector.CanMineTile(new Item{type=9,stack=1},TileID.Dirt),"zero pick power is not a pickaxe");
TileLoader.ModMinPick=220;Check(!ToolSelector.CanMineTile(new Item{type=2,stack=1,pick=100},501),"mod tile minimum pick is respected");

foreach(bool tree in new[]{false,true})
{
    Reset();Tile(target,tree?TileID.Trees:TileID.Stone);var p=PlayerWithTools();var n=Actor(target);
    AgentAction a=tree?new ChopAction(target,true):new MineAction(target,true,true);
    var r=Run(a,new(n,p));Check(r.Status==AgentActionStatus.Success&&WorldGen.Kills==1&&a.Mutations.Count==1,"native removal produces one mutation: "+tree);
    a.Tick(new(n,p));Check(WorldGen.Kills==1,"completed gathering is idempotent");
    Reset();Tile(target,tree?TileID.Trees:TileID.Stone);p=PlayerWithTools();n=Actor(target);
    a=tree?new ChopAction(target,true):new MineAction(target,true,true);a.Tick(new(n,p));p.inventory[tree?0:1]=new();
    Check(Run(a,new(n,p)).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"tool removed mid-job stops gathering: "+tree);
    Reset();Tile(target,tree?TileID.Trees:TileID.Stone);a=tree?new ChopAction(target,true):new MineAction(target,true,true);p=PlayerWithTools();n=Actor(target);
    a.Tick(new(n,p));a.Cancel();Check(a.Tick(new(n,p)).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"canceled gathering never resumes: "+tree);
}
Reset();Tile(target,TileID.Stone);var miner=new MineAction(target,true,true);context=new(Actor(target),PlayerWithTools());miner.Tick(context);Tile(target,TileID.Gold);
Check(Run(miner,context).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"changed tile type cannot be destroyed under an old decision");
Reset();Tile(target,TileID.Stone);WorldGen.Protected=true;
Check(Run(new MineAction(target,true,true),new(Actor(target),PlayerWithTools())).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"native world protection blocks mutation");
Reset();Tile(target,TileID.Stone);bool allowed=true;miner=new(target,true,true,_=>allowed);context=new(Actor(target),PlayerWithTools());miner.Tick(context);allowed=false;
Check(Run(miner,context).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"changed expedition guard stops work");
Reset();foreach(int x in new[]{19,20,21})foreach(int y in new[]{19,20,21})Tile(new(x,y),TileID.Stone);
miner=new(target);Check(Run(miner,new(Actor(target),PlayerWithTools())).Status==AgentActionStatus.Success&&miner.Mutations.Count==9,"default mining still covers a 3 by 3 cluster");
Reset();Tile(target,TileID.Stone);Tile(new(19,20),TileID.Gold);miner=new(target,false,true,p=>p==target);
Check(Run(miner,new(Actor(target),PlayerWithTools())).Status==AgentActionStatus.Failure&&miner.Mutations.Count==1&&WorldGen.Kills==1,"partial cluster failure retains evidence and stops outside its guard");
Reset();Tile(target,TileID.Trees);Tile(new(21,20),TileID.Trees);Tile(new(21,19),TileID.Trees);Tile(new(21,18),TileID.Trees);
Check(TreeHelper.FindTreeCenter(target)==new Point(21,20),"tree base canonicalizes to the taller trunk");
Check(TreeHelper.FindTreeBasesNear(new(20,20),3).Contains(new Point(21,20)),"tree scan finds odd-column trunks");
Check(TaskTargetClaims.Acquire(target,1)&&!TaskTargetClaims.Acquire(target,2),"target occupancy excludes a second task");TaskTargetClaims.Release(2);
Check(TaskTargetClaims.Occupied(target),"unrelated cleanup preserves another task lease");TaskTargetClaims.Release(1);Check(!TaskTargetClaims.Occupied(target),"owner cleanup releases lease");
TaskTargetClaims.Acquire(target,1);new TaskTargetClaims().OnWorldUnload();Check(!TaskTargetClaims.Occupied(target),"world unload clears leases");

Reset();Tile(target,TileID.Stone);var shaft=new HellevatorAction(20,target,_=>true);Check(Run(shaft,new(Actor(target),PlayerWithTools())).Status==AgentActionStatus.Success&&shaft.Mutations.Count==1,"bounded shaft delegates one verified tile");
Reset();Tile(target,TileID.Stone);var far=Actor(new(24,20));Check(Run(new HellevatorAction(20,target,_=>true),new(far,PlayerWithTools())).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"off-center shaft refuses remote mining");

Reset();var p0=PlayerWithTools();var n0=Actor(target);string json="{\"action\":{\"type\":\"chop\",\"params\":{\"tileX\":20,\"tileY\":20}}}";
Check(ActionParser.Parse(json,n0,p0).Single().GetTargetTile()==target,"typed parser preserves exact approved coordinates");
Throws(()=>ActionParser.Parse(json.Replace("20","0"),n0,p0),"typed parser rejects world edges without clamping");
Throws(()=>ActionParser.Parse(json.Replace("chop","hellevator"),n0,p0),"unbounded legacy shaft action is not a model capability");
Throws(()=>ActionParser.Parse(json.Replace("chop","build"),n0,p0),"legacy build cannot bypass construction confirmation");
Game.netMode=NetmodeID.MultiplayerClient;Throws(()=>ActionParser.Parse(json,n0,p0),"multiplayer client cannot create actions");
Check(Run(new MineAction(target,true),new(n0,p0)).Status==AgentActionStatus.Failure&&WorldGen.Kills==0,"client-side action ticks cannot mutate");
Reset();var state=default(MovementHelper.MovementState);n0=Actor(target);
MovementHelper.MoveTowards(n0,new(500,500),ref state,new(3,16));MovementHelper.MoveTowards(n0,new(500,500),ref state,new(3,16));
Check(TerrainRoute.Created==1,"route is reused while target and settings are stable");MovementHelper.MoveTowards(n0,new(600,500),ref state,new(3,16));Check(TerrainRoute.Created==2,"changed movement target rebuilds route");
TerrainRoute.Wet=true;MovementHelper.Swim(n0,new(0,2),2.5f);Check(Math.Abs(n0.velocity.Y-1.7f)<.001,"swimming compensates native gravity");MovementHelper.Stop(n0);Check(n0.velocity==Vector2.Zero,"cancel stops water motion");

void Drop(int type,int count,Vector2 position){Game.item[0]=new Item{type=type,stack=count,active=true,position=position};}
Reset();p0=new();n0=Actor(target);foreach(var slot in p0.inventory.Take(50)){slot.type=99;slot.stack=slot.maxStack;}
Drop(7,5,n0.Center);CompanionDrops.Pickup(p0,n0);Check(Game.item[0].active&&Game.item[0].stack==5,"full inventory leaves all drops active");
p0.inventory[0]=new(){type=7,stack=9997,maxStack=9999};CompanionDrops.Pickup(p0,n0);
Check(p0.inventory[0].stack==9999&&Game.item[0].active&&Game.item[0].stack==3,"partial pickup preserves native remainder");
p0.inventory[1]=new();CompanionDrops.Pickup(p0,n0);Check(!Game.item[0].active&&p0.inventory[1].stack==3,"remaining drop can be collected later without duplication");
Drop(8,4,n0.Center+new Vector2(80,0));CompanionDrops.Pickup(p0,n0);Check(Game.item[0].stack==4,"general pickup keeps its 64 pixel radius");
p0.inventory[2]=new();CompanionDrops.PickupMatching(p0,n0,8);Check(!Game.item[0].active,"recipe pickup retains its 96 pixel radius");

AIAgentNPC StartGather(int count=1,bool approval=true)
{
    Reset();var player=PlayerWithTools();Game.player=new[]{player};var npc=new AIAgentNPC();npc.NPC.Center=new(328,328);Game.npc=new[]{npc.NPC};
    if(approval)CompanionMemorySystem.Store!.Memory.Preferences.Add(new("gather_confirmation","required","test","player","turn",DateTimeOffset.UtcNow));
    GatherTargets.Candidate=target;Tile(target,TileID.Trees);npc.ReceiveCommand(player,$"砍{count}棵树");npc.AI();return npc;
}
void TickNpc(AIAgentNPC n,int frames=400){for(int i=0;i<frames;i++){Game.GameUpdateCount++;n.AI();}}
var native=StartGather();Check(native.HasPendingApproval&&WorldGen.Kills==0,"actual NPC waits before gathering");
Check(native.ConfirmPendingAction(new Player{whoAmI=2}).Contains("只有")&&WorldGen.Kills==0,"different player cannot approve");
Check(native.ConfirmPendingAction(Game.player[0]).Contains("只执行"),"actual NPC accepts exact valid approval");TickNpc(native);
Check(WorldGen.Kills==1&&CompanionTaskTracker.Current?.GoalVerified==true,"actual NPC completes counted gathering from mutation evidence");
Check(CompanionMemorySystem.Events.Any(e=>e.Kind=="action_result")&&CompanionRecoverySystem.Closures==1,"actual NPC emits results and closes recovery tracking once");
Check(!TaskTargetClaims.Occupied(target),"finished NPC releases its target");
native=StartGather();CompanionMemorySystem.Store!.Memory.RuleRevision++;Check(native.ConfirmPendingAction(Game.player[0]).Contains("没有等待")&&WorldGen.Kills==0,"changed rules invalidate pending approval");
native=StartGather();native.ConfirmPendingAction(Game.player[0]);CompanionMemorySystem.Store!.Memory.Preferences.Add(new("chopping","forbidden","test","player","turn",DateTimeOffset.UtcNow));TickNpc(native,2);
Check(WorldGen.Kills==0&&CompanionTaskTracker.Current?.Closed==true,"rule denial after approval stops actual NPC before mutation");
native=StartGather();native.ConfirmPendingAction(Game.player[0]);CompanionCommandService.Advance();TickNpc(native,2);Check(WorldGen.Kills==0,"stale execution stamp prevents native mutation");
native=StartGather();Game.dayTime=false;Check(native.ConfirmPendingAction(Game.player[0]).Contains("没有等待")&&WorldGen.Kills==0,"day/night change invalidates approval");
native=StartGather();CompanionMemorySystem.Session=Guid.NewGuid();Check(native.ConfirmPendingAction(Game.player[0]).Contains("没有等待"),"world change invalidates approval");
native=StartGather(2,false);TickNpc(native);Check(WorldGen.Kills==1&&CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},"partial counted task reports incomplete when no new target exists");
Check(native.RecoveryProgress.Contains("1/2")&&CompanionTaskTracker.Current!.Result.Length>0,"partial recovery retains measured progress and status");
native=StartGather(1,false);WorldGen.Protected=true;TickNpc(native);Check(WorldGen.Kills==0&&CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},"protected native target fails without claiming completion");
native=StartGather();native.StopCommand("player_stopped");Check(!native.HasPendingApproval&&WorldGen.Kills==0,"stop removes pending confirmation");
native=StartGather();native.OnKill();Check(!native.HasPendingApproval&&CompanionTaskTracker.Current?.Closed==true,"NPC removal cancels its approval and task");
native=StartGather(1,false);native.ProcessPlanningWhilePaused();Check(WorldGen.Kills==0,"paused planning does not perform native mutations");

// Counted mining must finish honestly, even when native routes reject every
// candidate. No local model is invoked: decisions here are produced by program.
AIAgentNPC StartMining(int candidates,bool approval=false,int count=1) {
 Reset();var p=PlayerWithTools();Game.player=new[]{p};var n=new AIAgentNPC();
 n.NPC.Center=new(328,328);Game.npc=new[]{n.NPC};
 for(int x=40;x<40+candidates;x++){var at=new Point(x,20);GatherTargets.Candidates.Add(at);Tile(at,TileID.Iron);}
 if(approval)CompanionMemorySystem.Store!.Memory.Preferences.Add(new("gather_confirmation","required","test","player","turn",DateTimeOffset.UtcNow));
 TerrainRoute.Result=AgentActionResult.Failure("fixture: no safe approach");
 n.ReceiveCommand(p,$"挖{count}处矿");n.AI();return n;
}
int Events(string kind)=>CompanionMemorySystem.Events.Count(e=>e.Kind==kind);
native=StartMining(8);TickNpc(native,60);
Check(Events("planner_error")==0&&CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},
 "eight unreachable program targets close as gathering shortfall, not a model planning error");
Check(Events("decision_request")==8&&WorldGen.Kills==0,"failed targets are skipped once and keep original decision bound");
Check(CompanionTaskTracker.Current!.Result.Contains("fixture: no safe approach")&&native.RecoveryProgress.Contains("0/1"),
 "unreachable mining receipt retains last failure and verified zero progress");
Check(CompanionRecoverySystem.Closures==1,"bounded mining failure notifies existing recovery exactly once");
Check(CompanionRecoverySystem.ClosedView!.Result.Contains("fixture: no safe approach"),
 "recovery receives the final failure reason at closure, not a later UI update");
native=StartMining(12);TickNpc(native,60);
Check(Events("planner_error")==0&&Events("decision_request")==8&&WorldGen.Kills==0,
 "many unreachable candidates stop with honest gather limit while retaining original bound");
Check(CompanionTaskTracker.Current!.Result.Contains("上限")&&CompanionTaskTracker.Current.Result.Contains("fixture: no safe approach"),
 "attempt cap reports concrete failure rather than implying candidates were exhausted");
native=StartMining(0);TickNpc(native,10);
Check(Events("decision_request")==0&&Events("planner_error")==0&&CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},
 "no exposed candidate stops before requesting any decision");
// Confirmation still creates exactly one mining action for the chosen tile.
Reset();var miningPlayer=PlayerWithTools();Game.player=new[]{miningPlayer};native=new();native.NPC.Center=new(328,328);Game.npc=new[]{native.NPC};
GatherTargets.Candidate=target;Tile(target,TileID.Iron);var neighbour=new Point(21,20);Tile(neighbour,TileID.Stone);
CompanionMemorySystem.Store!.Memory.Preferences.Add(new("gather_confirmation","required","test","player","turn",DateTimeOffset.UtcNow));
native.ReceiveCommand(miningPlayer,"挖1处矿");native.AI();
Check(native.HasPendingApproval&&WorldGen.Kills==0,"counted mining still waits for exact player confirmation");
native.ConfirmPendingAction(miningPlayer);TickNpc(native,500);
Check(WorldGen.Kills==1&&Framing.GetTileSafely(neighbour.X,neighbour.Y).HasTile&&CompanionTaskTracker.Current?.GoalVerified==true,
 "counted ore mining only works the verified target, not an unrelated three-by-three area");
// A shortfall after real progress keeps the removal count and cause.
native=StartMining(9,count:2);var first=GatherTargets.Candidates[0];native.NPC.Center=new(first.X*16+8,first.Y*16+8);
// Let the first target be worked locally; move away before the remaining routes.
WorldGen.OnKill=(_,_)=>native.NPC.Center=new(328,328);TickNpc(native,600);
Check(WorldGen.Kills==1&&native.RecoveryProgress.Contains("1/2")&&CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},
 "mining attempt shortfall preserves verified partial progress without success claim");
Check(Events("planner_error")==0&&CompanionTaskTracker.Current!.Result.Contains("fixture: no safe approach"),
 "partial mining shortfall keeps actionable failure information");
native=StartMining(2,approval:true);Check(native.HasPendingApproval&&WorldGen.Kills==0,"unreachable candidate also cannot bypass gather approval");
native.StopCommand("player_stopped");TickNpc(native,20);Check(WorldGen.Kills==0&&Events("decision_request")==1,
 "stop during mining approval cancels subsequent target attempts");

// Native tunnel adapter: real MineAction / TileWork / safety reader, mocked engine APIs.
Reset();var tunnelPlayer=PlayerWithTools();Game.player=new[]{tunnelPlayer};var tunnelAgent=new AIAgentNPC();
tunnelAgent.NPC.Center=new(328,328);Game.npc=new[]{tunnelAgent.NPC};
foreach(var at in new[]{new Point(20,20),new Point(21,20)})Tile(at,TileID.Stone);
var snapshot=new[]{DiggingSafety.Read(tunnelPlayer,20,20),DiggingSafety.Read(tunnelPlayer,21,20)};
var tunnelPlan=new OreTunnelPlan(new(20,22),snapshot[0],Array.Empty<TunnelPose>(),snapshot,new[]{snapshot[1]});
bool tunnelPermit=true;var nativeTunnel=new NativeOreTunnelSite(tunnelPlayer,tunnelAgent.NPC,tunnelPlan,CompanionCommandService.Capture(),()=>tunnelPermit);
var tunnelJob=new OreTunnelJob(tunnelPlan);nativeTunnel.Job=tunnelJob;
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"native tunnel preview cannot call the mining primitive");
tunnelJob.Approve(nativeTunnel);tunnelPermit=false;
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"revoked native tunnel authority cannot mine");
tunnelPermit=true;CompanionCommandService.Revision++;
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"stale control stamp stops native tunnel edits");CompanionCommandService.Revision--;
var originalSession=CompanionMemorySystem.Session;CompanionMemorySystem.Session=Guid.NewGuid();
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"new world session invalidates native tunnel authority");CompanionMemorySystem.Session=originalSession;
Game.netMode=NetmodeID.MultiplayerClient;
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"multiplayer client cannot execute confirmed tunnel");Game.netMode=NetmodeID.SinglePlayer;
Check(nativeTunnel.Mine(22,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"confirmed tunnel cannot mine outside immutable whitelist");
Tile(new(21,20),TileID.Gold);
Check(nativeTunnel.Mine(20,20,TileID.Stone).State==ExpeditionStepState.Failed&&WorldGen.Kills==0,"a change elsewhere in approved footprint blocks native mutation");
Tile(new(21,20),TileID.Stone);
for(int t=0;t<150&&Framing.GetTileSafely(20,20).HasTile;t++)nativeTunnel.Mine(20,20,TileID.Stone);
Check(WorldGen.Kills==1&&!Framing.GetTileSafely(20,20).HasTile,"confirmed exact native tunnel tile produces actual verified removal");
Check(Framing.GetTileSafely(21,20).HasTile,"native tunnel removal preserves unrelated approved future tile");
// Safety reader shared with the old shaft rejects hazards independently of path selection.
foreach(string hazard in new[]{"liquid","wire","falling","wall","paint","actuator","furniture"}) {
 var cell=Framing.GetTileSafely(25,25);cell.HasTile=true;cell.TileType=TileID.Stone;cell.LiquidAmount=0;cell.RedWire=false;cell.WallType=0;cell.TileColor=0;cell.HasActuator=false;
 Game.tileFrameImportant[TileID.Stone]=false;
 switch(hazard){case "liquid":cell.LiquidAmount=1;break;case "wire":cell.RedWire=true;break;case "falling":cell.TileType=TileID.Sand;break;case "wall":cell.WallType=1;Game.wallHouse[1]=true;break;case "paint":cell.TileColor=1;break;case "actuator":cell.HasActuator=true;break;case "furniture":Game.tileFrameImportant[TileID.Stone]=true;break;}
 var safe=DiggingSafety.Read(tunnelPlayer,25,25);Check(safe.Protected||safe.Hazard,"shared native digging safety rejects "+hazard);
}

var plant=Framing.GetTileSafely(25,25);plant.HasTile=true;plant.TileType=3;plant.RedWire=false;plant.LiquidAmount=0;plant.WallType=0;plant.TileColor=0;plant.HasActuator=false;Game.tileCut[3]=true;
Check(DiggingSafety.ReadTunnel(tunnelPlayer,25,25).Type<0&&plant.HasTile,"passable grass is not excavated as a structural corridor block");
plant.RedWire=true;Check(DiggingSafety.ReadTunnel(tunnelPlayer,25,25).Hazard,"passable plants cannot hide a wire hazard");

// Exercise the real asynchronous NPC request path using in-memory HTTP only.
Reset();var delayed=new DelayedHandler();
var hostField=typeof(TerrarAI.TerrarAI).GetField("_model",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
hostField.SetValue(null,new XAIClient(new HttpClient(delayed)));
native=new();Game.player=new[]{PlayerWithTools()};native.ReceiveCommand(Game.player[0],"你好");
var pendingRequest=(Task<string>)typeof(AIAgentNPC).GetField("_plannerTask",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
CompanionCommandService.Advance();delayed.Response.SetResult(RecordingHandler.Reply());
await pendingRequest;native.AI();
Check(WorldGen.Kills==0&&CompanionMemorySystem.Events.Any(e=>e.Kind=="decision_discarded"),"late model response cannot survive a newer control stamp");
Check(CompanionTaskTracker.Current is {Closed:true,GoalVerified:false},"discarded response closes without a success claim");
hostField.SetValue(null,null);
// Model-driven work retains the original decision limit and failure path.
Reset();var looping=new LoopingHandler();hostField.SetValue(null,new XAIClient(new HttpClient(looping)));
TerrainRoute.Result=AgentActionResult.Success("fixture movement");native=new();Game.player=new[]{PlayerWithTools()};
native.ReceiveCommand(Game.player[0],"你好");TickNpc(native,80);
Check(looping.Requests==8&&Events("planner_error")==1&&WorldGen.Kills==0,
 "genuine model-driven loop still hits the original planning limit");
hostField.SetValue(null,null);


Reset();var settings=ModContent.GetInstance<TerrarAI.TerrarAI_Config>();using(var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,"release/config-compatibility.json"))))
{
    var expected=contract.RootElement.GetProperty("fields");Check(expected.EnumerateObject().Count()==18,"18 existing serialized configuration fields retained");
    foreach(var property in expected.EnumerateObject())
    {
        var info=settings.GetType().GetProperty(property.Name)!;string literal=property.Value.GetProperty("defaultLiteral").GetString()!;
        object wanted=info.PropertyType==typeof(float)?float.Parse(literal.TrimEnd('f'),System.Globalization.CultureInfo.InvariantCulture):JsonSerializer.Deserialize(literal,info.PropertyType)!;
        Check(Equals(info.GetValue(settings),wanted),"configuration default preserved: "+property.Name);
        Check(Equals(info.GetCustomAttributes(typeof(DefaultValueAttribute),false).Cast<DefaultValueAttribute>().Single().Value,wanted),"loader default attribute preserved: "+property.Name);
        var bounds=System.Text.RegularExpressions.Regex.Match(property.Value.GetProperty("attributes").GetString()!,@"Range\(([^,]+),([^\)]+)\)");
        if(bounds.Success)
        {
            var actual=info.GetCustomAttributes(typeof(RangeAttribute),false).Cast<RangeAttribute>().Single();
            float Read(string value)=>float.Parse(value.TrimEnd('f'),System.Globalization.CultureInfo.InvariantCulture);
            Check(actual.Min==Read(bounds.Groups[1].Value)&&actual.Max==Read(bounds.Groups[2].Value),"loader range preserved: "+property.Name);
        }
    }
    var saved=JsonSerializer.Deserialize<TerrarAI.TerrarAI_Config>("{\"Model\":\"qwen3-4b\",\"EnableCreativeMode\":false,\"EnableAutomaticHealing\":false,\"EnableReminders\":false,\"AutoHealingThresholdPercent\":22,\"BaseEndpoint\":\"http://localhost:9090/v1/chat/completions\"}")!;
    Check(!saved.EnableAutomaticHealing&&!saved.EnableReminders&&saved.AutoHealingThresholdPercent==22&&saved.BaseEndpoint.Contains("9090"),"existing saved opt-outs and customized endpoint deserialize unchanged");
    Check(settings.Mode==ConfigScope.ServerSide&&settings.GetType().FullName=="TerrarAI.TerrarAI_Config","config type and scope remain compatible");
}
var handler=new RecordingHandler();using(var model=new XAIClient(new HttpClient(handler)))
{
    await model.PlanAgentStepAsync("{}",CancellationToken.None);
    using var request=JsonDocument.Parse(handler.Body!);
    Check(request.RootElement.GetProperty("model").GetString()=="qwen3-4b","transport retains legacy model alias");
    Check(request.RootElement.GetProperty("max_tokens").GetInt32()==900&&handler.Body!.Contains("json_schema"),"agent facade retains structured decoding and token budget");
    await model.SendChatCompletionAsync("system","user");Check(handler.Requests==2,"basic compatibility request uses shared transport");
}
Console.WriteLine($"Passed {passed} native replacement checks; simulated APIs, no game or model process.");

sealed class Probe:AgentAction
{
    internal int Executions,Exits;internal bool Pending,Fault;
    public override string Name=>"probe";protected override int MaxExecutionTicks=>3;
    protected override AgentActionResult OnTick(AgentActionContext c){Executions++;if(Fault)throw new Exception("synthetic");return Pending?AgentActionResult.Pending():AgentActionResult.Success();}
    protected override void OnExit(AgentActionContext c,AgentActionResult r){Exits++;base.OnExit(c,r);}
}
sealed class RecordingHandler:HttpMessageHandler
{
    internal string? Body;internal int Requests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Requests++;Body=await request.Content!.ReadAsStringAsync(token);
        return Reply();
    }
    internal static HttpResponseMessage Reply()=>new(HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"action\\\":{\\\"type\\\":\\\"say\\\",\\\"params\\\":{\\\"text\\\":\\\"ok\\\"}}}\"}}]}",Encoding.UTF8,"application/json")};
}
sealed class DelayedHandler:HttpMessageHandler
{
    internal readonly TaskCompletionSource<HttpResponseMessage> Response=new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Response.Task;
}

sealed class LoopingHandler:HttpMessageHandler {
 internal int Requests;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
  Requests++;
  string body=JsonSerializer.Serialize(new {choices=new[]{new {message=new {content="{\"action\":{\"type\":\"move\",\"params\":{\"tileX\":20,\"tileY\":20}}}"}}}});
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
 }
}
