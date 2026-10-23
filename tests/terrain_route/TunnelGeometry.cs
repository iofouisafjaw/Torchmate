using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
internal static class TunnelGeometry
{
 sealed class Site(NPC actor):IOreTunnelSite
 {
  private MovementHelper.MovementState _movement;
  public DigCell Read(int x,int y){var t=Framing.GetTileSafely(x,y);return new(x,y,t.HasTile?t.TileType:-1,t.HasTile);}
  public ExpeditionStep Mine(int x,int y,int type)
  {
   if(Vector2.Distance(actor.Center,new(x*16+8,y*16+8))>80)throw new Exception("tunnel mine out of reach");
   if(!MovementHelper.IsOnGround(actor))return new(ExpeditionStepState.Pending);
   Framing.GetTileSafely(x,y).HasTile=false;return new(ExpeditionStepState.Done,Removed:1);
  }
  public ExpeditionStep Move(TunnelPose p)
  {
   var r=MovementHelper.MoveTowards(actor,new(p.X*16+8,p.FeetY*16-20),ref _movement,MovementHelper.MovementSettings.Create(2.5f,3));
   return new(r.Status==TerrarAI.Content.Actions.AgentActionStatus.Success?ExpeditionStepState.Done:r.Status==TerrarAI.Content.Actions.AgentActionStatus.Failure?ExpeditionStepState.Failed:ExpeditionStepState.Pending,r.Message??"");
  }
  public void Hold()=>MovementHelper.Hold(actor);
  public void Release(){MovementHelper.Stop(actor);_movement=default;}
 }
 internal static void Run(Action<bool,string> check)
 {
  foreach(int direction in new[]{-1,1})foreach(int depth in new[]{3,5,8})
  {
   Framing.Cells=new Tile[220,120];for(int x=10;x<210;x++)for(int y=60;y<110;y++)Framing.GetTileSafely(x,y).HasTile=true;
   var n=new NPC{position=new(50*16+8-20,960-40),collideY=true};var site=new Site(n);
   var target=Framing.GetTileSafely(50+direction*7,60+depth);target.TileType=TileID.Iron;
   var plan=OreTunnelPlan.Create(new(50,60),site.Read(50+direction*7,60+depth),site.Read,out string error);
   if(plan==null){check(false,"geometry tunnel plan: "+error);continue;}
   var job=new OreTunnelJob(plan);job.Approve(site);
   for(int tick=1;tick<6000&&job.Active;tick++)
   {
    job.Tick(new(tick,n.Center.X/16.0,n.Bottom.Y/16.0),site);
    n.velocity.Y=Math.Min(10,n.velocity.Y+.3f);var v=Collision.TileCollision(n.position,n.velocity,40,40);
    n.collideY=Math.Abs(v.Y-n.velocity.Y)>.001f;n.collideX=Math.Abs(v.X-n.velocity.X)>.001f;
    n.position+=v;if(n.collideY)n.velocity.Y=0;
    if(Collision.SolidCollision(n.position,40,40))throw new Exception("tunnel solid overlap");
   }
   check(job.State==OreTunnelState.Completed,$"production motor traverses excavated stairs dir={direction} depth={depth}: {job.Reason} at {n.Bottom.X},{n.Bottom.Y}");
   // A normal terrain route must also be able to get back out after digging.
   check(TerrainRoute.Find(n,new(50*16+8,940),16,3)!=null,"excavated staircase retains a route to entry");
   var exit=new TerrainRoute(new(50*16+8,940),16,3);bool returned=false;string exitReason="";
   for(int tick=0;tick<3000;tick++) {
    var r=exit.Tick(n);exitReason=r.Message??"";
    if(r.Status==TerrarAI.Content.Actions.AgentActionStatus.Success){returned=true;break;}
    if(r.Status==TerrarAI.Content.Actions.AgentActionStatus.Failure)break;
    n.velocity.Y=Math.Min(10,n.velocity.Y+.3f);var v=Collision.TileCollision(n.position,n.velocity,40,40);
    n.collideY=Math.Abs(v.Y-n.velocity.Y)>.001f;n.collideX=Math.Abs(v.X-n.velocity.X)>.001f;n.position+=v;if(n.collideY)n.velocity.Y=0;
   }
   check(returned,"production motor climbs back out: "+exitReason);
  }
 }
}
