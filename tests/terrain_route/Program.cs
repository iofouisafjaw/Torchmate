using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Systems;
using TerrarAI.Content.Actions;

int checks=0, failures=0;
void Check(bool ok,string label) {checks++;Console.WriteLine((ok?"PASS: ":"FAIL: ")+label);if(!ok)failures++;}
void Reset() {
 Framing.Cells=new Tile[220,120];CompanionMemorySystem.Events.Clear();
 Main.tileSolid[TileID.Dirt]=Main.tileSolid[TileID.Stone]=Main.tileSolid[TileID.ClosedDoor]=true;
 Main.tileSolid[TileID.Platforms]=Main.tileSolidTop[TileID.Platforms]=true;
 for(int x=10;x<210;x++)for(int y=60;y<110;y++)Framing.GetTileSafely(x,y).HasTile=true;
}
void Pond(int left,int right,int depth,int liquid=LiquidID.Water) {
 for(int x=left;x<=right;x++)for(int y=60;y<60+depth;y++) {
  var t=Framing.GetTileSafely(x,y);t.HasTile=false;t.LiquidAmount=255;t.LiquidType=liquid;
 }
}
NPC Actor(float x,float feet)=>new(){position=new Vector2(x-20,feet-40),collideY=true};
void Physics(NPC npc) {
 npc.gravity=MovementHelper.InWater(npc)?.2f:.3f;
 if(!npc.noGravity)npc.velocity.Y=Math.Min(10,npc.velocity.Y+npc.gravity);
 var v=Collision.TileCollision(npc.position,npc.velocity,npc.width,npc.height);
 npc.collideX=Math.Abs(v.X-npc.velocity.X)>.001f;npc.collideY=Math.Abs(v.Y-npc.velocity.Y)>.001f;
 bool wet=MovementHelper.InWater(npc);
 npc.position+=new Vector2(wet&&!npc.collideX?v.X*.5f:v.X,wet&&!npc.collideY?v.Y*.5f:v.Y);if(npc.collideY)npc.velocity.Y=0;
}
bool Execute(NPC n,Vector2 target,string label,bool follow=false) {
 var original=Framing.Cells.Cast<Tile?>().Where(t=>t!=null).Select(t=>(t!.HasTile,t.TileType,t.LiquidAmount,t.LiquidType)).ToArray();
 var route=new TerrainRoute(target,16,3);var f=new FollowNavigation();var player=new Player{Center=target,wet=TerrainRoute.InWater(target-new Vector2(20),40,40)};
 AgentActionResult result=default;bool success=false;
 for(int tick=0;tick<900;tick++) {
  if(follow) {f.Tick(n,player,3);success=Vector2.Distance(n.Center,target)<=50&&!MovementHelper.InWater(n);}
  else {result=route.Tick(n);success=result.Status==AgentActionStatus.Success;}
  if(success||(!follow&&result.Status==AgentActionStatus.Failure))break;
  var before=n.position;Physics(n);
  if(Vector2.Distance(before,n.position)>16)throw new Exception("teleport");
  if(Collision.SolidCollision(n.position,40,40))throw new Exception("solid overlap");
 }
 Check(success,label+(success?"":" @ "+n.Bottom.X+","+n.Bottom.Y+" "+result.Message));
 var after=Framing.Cells.Cast<Tile?>().Where(t=>t!=null).Select(t=>(t!.HasTile,t.TileType,t.LiquidAmount,t.LiquidType)).ToArray();
 // Reads create empty cells in this fixture; compare material tiles, not allocation.
 Check(original.Where(t=>t.HasTile||t.LiquidAmount>0).SequenceEqual(after.Where(t=>t.HasTile||t.LiquidAmount>0)),label+" preserves terrain/liquid");
 return success;
}
// Surface-adjacent dry ledge needs a jump; swimming cannot climb in air.
Reset();Pond(53,67,1);
for(int y=59;y<60;y++)Framing.GetTileSafely(51,y).HasTile=true;
Framing.GetTileSafely(53,60).HasTile=true;
Execute(Actor(54*16+8,960),new(48*16+8,940),"dry rim beside water needs jump over ledge");
// Initial real entity positions need not coincide with 16-pixel graph nodes.
foreach(float offset in new[]{-7.5f,-3.5f,1.5f,6.5f}) {
 Reset();Pond(53,67,1);Execute(Actor(52*16+8+offset,960),new(73*16+8,940),"off-grid pond approach "+offset);
}
foreach(byte amount in new byte[]{65,128,200,231}) {
 Reset();Pond(53,67,1);
 for(int x=53;x<=67;x++)Framing.GetTileSafely(x,60).LiquidAmount=amount;
 Execute(Actor(60*16+8,976-amount/255f*16+.3f),new(48*16+8,940),"part-filled surface start "+amount);
}
Reset();Framing.GetTileSafely(51,59).HasTile=true;
Execute(Actor(50*16+8,944),new(46*16+8,940),"wide body walks off ledge before checking landing");
Reset();Execute(Actor(45*16+8,960),new(75*16+8,940),"dry ground control");
foreach(int direction in new[]{1,-1})foreach(int depth in new[]{1,2,6}) {
 Reset();Pond(53,67,depth);
 var n=Actor((direction==1?48:73)*16+8,960);
 Execute(n,new((direction==1?73:48)*16+8,940),$"pond depth {depth} direction {direction}");
}
Reset();Pond(53,67,1);Execute(Actor(55*16+8,976),new(48*16+8,940),"start in shallow water and climb bank");
Reset();Pond(53,67,6);Execute(Actor(60*16+8,1024),new(73*16+8,940),"start submerged and leave water");
Reset();Framing.GetTileSafely(51,59).HasTile=true;Pond(53,67,1);Framing.GetTileSafely(53,60).HasTile=true;
Execute(Actor(54*16+8,960),new(42*16+8,940),"follow leaves rim and drops off ledge",true);
Reset();Pond(53,67,1);Execute(Actor(51*16+8,960),new(73*16+8,940),"follow across shallow pond",true);
foreach(int liquid in new[]{LiquidID.Lava,LiquidID.Honey,LiquidID.Shimmer}) {
 Reset();Pond(53,67,6,liquid);
 Check(TerrainRoute.Find(Actor(60*16+8,1024),new(73*16+8,940),16,3)==null,"reject non-water liquid "+liquid);
}
Reset();for(int y=35;y<60;y++)Framing.GetTileSafely(60,y).HasTile=true;
Check(TerrainRoute.Find(Actor(50*16+8,960),new(75*16+8,940),16,3)==null,"reject unjumpable wall");
// Reproduce the logged class of failure: ore surrounded by dirt is a
// candidate in the old heuristic despite there being no mining approach.
void Ore(int x,int y,int type) {var t=Framing.GetTileSafely(x,y);t.HasTile=true;t.TileType=(ushort)type;Main.tileSolid[type]=true;}
Point? PickOre(string? word=null,int power=35,ICollection<Point>? skipped=null,Func<Point,bool>? accept=null)=>
 GatherTargets.NextOre(Actor(50*16+8,960),new Point(50,60),0,skipped??new HashSet<Point>(),power,word,accept);
Reset();for(int x=52;x<60;x++)Ore(x,62,TileID.Iron);
Check(PickOre()==null,"eight sealed ore tiles are rejected before any mining decision");
Ore(75,60,TileID.Iron);
Check(PickOre()==new Point(75,60),"skip sealed near ore in favour of an exposed farther target");
Check(PickOre(skipped:new HashSet<Point>{new(75,60)})==null,"do not retry failed exposed target or fall back to sealed ore");
Check(PickOre(accept:_=>false)==null,"shared material protection predicate remains authoritative");
Reset();Ore(52,60,TileID.Cobalt);
Check(PickOre("钴",35)==null,"named ore must obey pick power just like unspecified ore");
Check(PickOre("钴",100)==new Point(52,60),"adequate pick can select exposed named ore");
Reset();Ore(52,60,TileID.Gold);
Check(PickOre("金",35)==null&&GatherTargets.TooHardFor("金",35),"selection and missing-tool hint reuse actual mining threshold");
Reset();Ore(52,60,TileID.Iron);Framing.GetTileSafely(52,60).IsActuated=true;
Check(PickOre()==null,"actuated ore is not a solid gathering target");
Reset();Ore(52,61,TileID.Iron);
var empty=Framing.GetTileSafely(52,60);empty.HasTile=true;empty.IsActuated=true;
Check(PickOre()==new Point(52,61),"actuated neighbour counts as an open approach");
Reset();
for(int x=49;x<=51;x++)Framing.GetTileSafely(x,60).HasTile=false;
for(int x=51;x<=53;x++)for(int y=60;y<80;y++)Framing.GetTileSafely(x,y).HasTile=false;
var unsafeStep=typeof(TerrainRoute).GetMethod("StepLeg",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
 .Invoke(null,new object[]{new Point(50,61),new Point(54,60),40,40,3f});
Check(unsafeStep==null,"short climb edge refuses an unsupported deep gap");
TunnelGeometry.Run(Check);
Console.WriteLine($"Terrain route: {checks-failures}/{checks} passed");return failures==0?0:1;
