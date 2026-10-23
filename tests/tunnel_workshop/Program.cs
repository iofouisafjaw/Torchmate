using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Game=Terraria.Main;
int checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);}
Player player=null!;AIAgentNPC agent=null!;
void Reset(){
 new CompanionExpeditionSystem().OnWorldUnload();Game.netMode=0;Game.gameMenu=Game.gamePaused=false;Game.dayTime=true;Game.GameUpdateCount=0;
 Framing.Cells.Clear();WorldGen.Kills=0;WorldGen.Protected=false;WorldGen.OnKill=null;
 Array.Clear(Game.wallHouse);Array.Clear(Game.tileFrameImportant);Array.Clear(Game.tileCut);
 TileID.Sets.Ore[TileID.Iron]=true;ModContent.Singleton<TerrarAI.TerrarAI_Config>.Value=new();
 player=new(){Center=new(808,940)};player.inventory[0]=new(){type=100,stack=1,pick=100};Game.player=new[]{player};
 agent=new();agent.NPC.Center=player.Center;agent.NPC.friendly=true;Game.npc=new[]{agent.NPC};TerrainRoute.Wet=false;TerrainRoute.Arrive=true;TerrainRoute.Result=TerrarAI.Content.Actions.AgentActionResult.Success("fixture arrival");
 CompanionMemorySystem.Session=Guid.NewGuid();CompanionCommandService.Revision=0;
 string temp=Path.Combine(Path.GetTempPath(),"terra-tunnel-"+Guid.NewGuid());
 CompanionMemorySystem.Store=new(temp,Guid.NewGuid(),CompanionMemorySystem.Session);CompanionMemorySystem.Episodes=new(CompanionMemorySystem.Store);
 CompanionMemorySystem.Events.Clear();new CompanionTaskTracker().OnWorldLoad();
 for(int x=15;x<95;x++)for(int y=60;y<92;y++){var t=Framing.GetTileSafely(x,y);t.HasTile=true;t.TileType=TileID.Stone;}
 var ore=Framing.GetTileSafely(55,65);ore.TileType=TileID.Iron;GatherTargets.Candidate=new(55,65);GatherTargets.Candidates.Clear();
}
void Begin()=>CompanionExpeditionSystem.BeginTunnel(player,new("铁"));
Reset();Begin();var id=CompanionExpeditionSystem.ConfirmationId;
Check(CompanionExpeditionSystem.Asking&&id!=Guid.Empty&&WorldGen.Kills==0,"real workshop always asks even when mining preference allows");
for(int t=0;t<100;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
Check(WorldGen.Kills==0&&CompanionExpeditionSystem.Tunnel!.State==OreTunnelState.Preview,"real owner tick cannot excavate before confirmation");
CompanionExpeditionSystem.Confirm(new Player{whoAmI=2},id);
Check(CompanionExpeditionSystem.Asking&&WorldGen.Kills==0,"different player cannot confirm tunnel");
CompanionExpeditionSystem.Confirm(player,Guid.NewGuid());Check(CompanionExpeditionSystem.Asking,"wrong confirmation id cannot authorize");
CompanionExpeditionSystem.Confirm(player,id);
Check(!CompanionExpeditionSystem.Asking&&CompanionExpeditionSystem.Tunnel!.State==OreTunnelState.Running&&CompanionExpeditionSystem.Permitted,
 "real confirmation validates ticket then starts bounded excavation");
for(int t=0;t<170;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
Check(WorldGen.Kills>0,"confirmed workshop executes existing native mining primitive");
int before=WorldGen.Kills;CompanionCommandService.Advance();for(int t=0;t<120;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
Check(WorldGen.Kills==before&&!CompanionExpeditionSystem.Active,"new control cancels workshop and prevents further edits");
Reset();Begin();CompanionExpeditionSystem.Confirm(player,CompanionExpeditionSystem.ConfirmationId);
Game.gamePaused=true;for(int t=0;t<120;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
Check(WorldGen.Kills==0,"confirmed but paused owner cannot excavate");Game.gamePaused=false;
for(int t=0;t<170;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
before=WorldGen.Kills;var changed=CompanionExpeditionSystem.Tunnel!.Plan.Snapshot.First(c=>c.Type>=0&&Framing.GetTileSafely(c.X,c.Y).HasTile);
Framing.GetTileSafely(changed.X,changed.Y).LiquidAmount=1;Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);
Check(before>0&&WorldGen.Kills==before&&CompanionExpeditionSystem.Tunnel.State==OreTunnelState.Failed,
 "new liquid after partial progress stops production owner before more edits");
Check(CompanionTaskTracker.Current is {Closed:true,GoalVerified:false}&&CompanionRecoverySystem.ClosedView?.Result==CompanionTaskTracker.Current.Result,
 "production owner closes recovery with final cause and measured partial progress");
foreach(string change in new[]{"wire","type","tool","rule","day","session","moved","expired"}){
 Reset();Begin();id=CompanionExpeditionSystem.ConfirmationId;
 var c=CompanionExpeditionSystem.Tunnel!.Plan.Snapshot.First(c=>c.Type>=0);
 switch(change){
  case "wire":Framing.GetTileSafely(c.X,c.Y).RedWire=true;break;
  case "type":Framing.GetTileSafely(c.X,c.Y).TileType=TileID.Gold;break;
  case "tool":player.inventory[0]=new();break;
  case "rule":CompanionMemorySystem.Store!.Memory.RuleRevision++;break;
  case "day":Game.dayTime=false;break;
  case "session":CompanionMemorySystem.Session=Guid.NewGuid();break;
  case "moved":agent.NPC.Center+=new Vector2(32,0);break;
  case "expired":var f=typeof(CompanionExpeditionSystem).GetField("_ticket",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;f.SetValue(null,((ApprovalTicket)f.GetValue(null)!) with{Expires=DateTimeOffset.UtcNow.AddMinutes(-1)});break;
 }
 CompanionExpeditionSystem.Confirm(player,id);
 Check(WorldGen.Kills==0&&!CompanionExpeditionSystem.Permitted,"confirmation rejects changed "+change);
}
Reset();CompanionMemorySystem.Store!.Memory.Preferences.Add(new("mining","forbidden","test","player","turn",DateTimeOffset.UtcNow));Begin();
Check(!CompanionExpeditionSystem.Active&&WorldGen.Kills==0,"forbidden mining cannot create a tunnel preview");
Reset();Begin();id=CompanionExpeditionSystem.ConfirmationId;Begin();CompanionExpeditionSystem.Confirm(player,id);
Check(CompanionExpeditionSystem.Asking&&WorldGen.Kills==0,"old preview cannot authorize a newly planned tunnel");
foreach(bool afterApproval in new[]{false,true}){
 Reset();foreach(var t in Framing.Cells.Values.Where(t=>t.TileType==TileID.Stone))t.TileType=TileID.Dirt;
 Begin();id=CompanionExpeditionSystem.ConfirmationId;
 var tunnelPlan=CompanionExpeditionSystem.Tunnel!.Plan;
 var soil=tunnelPlan.Excavation.First(c=>c.Type==TileID.Dirt);
 if(afterApproval)CompanionExpeditionSystem.Confirm(player,id);
 Framing.GetTileSafely(soil.X,soil.Y).TileType=TileID.Grass;
 if(!afterApproval){CompanionExpeditionSystem.Confirm(player,id);Check(!CompanionExpeditionSystem.Permitted&&WorldGen.Kills==0,
  "production approval still rejects a dirt-to-grass scene change before consent");}
 else{
  foreach(var c in tunnelPlan.Excavation.Where(c=>c.Type==TileID.Dirt))Framing.GetTileSafely(c.X,c.Y).TileType=TileID.Grass;
  int stripped=0;WorldGen.OnKill=(x,y)=>{
   var t=Framing.GetTileSafely(x,y);
   if(t.TileType==TileID.Grass){t.HasTile=true;t.TileType=TileID.Dirt;stripped++;}
  };
  for(int t=0;t<170;t++){Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);}
  Check(WorldGen.Kills>0&&CompanionExpeditionSystem.Tunnel!.State==OreTunnelState.Running,
   "production tunnel guard and MineAction accept approved soil conversion without weakening the whitelist");
  Check(stripped>=1&&tunnelPlan.Excavation.Any(c=>!Framing.GetTileSafely(c.X,c.Y).HasTile)&&WorldGen.Kills>CompanionExpeditionSystem.Tunnel!.Removed,
   "a native grass-to-dirt mutation is retried at the same approved cell and is not counted as a removal");
  var intact=CompanionExpeditionSystem.Tunnel!.Plan.Snapshot.First(c=>c.Type>=0&&Framing.GetTileSafely(c.X,c.Y).HasTile);
  before=WorldGen.Kills;Framing.GetTileSafely(intact.X,intact.Y).RedWire=true;Game.GameUpdateCount++;CompanionExpeditionSystem.TickActive(agent);
  Check(WorldGen.Kills==before&&CompanionExpeditionSystem.Tunnel.State==OreTunnelState.Failed,
   "production tunnel still stops for a newly wired tile after accepting a grass update");
 }
}
Console.WriteLine($"Passed {checks} tunnel workshop checks; actual production owner/confirmation/site, simulated game APIs.");
