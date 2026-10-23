using TerrarAI.Content.Companion;
static class OreTunnelTests
{
 internal sealed class Site:IOreTunnelSite
 {
  internal readonly Dictionary<(int,int),DigCell> Cells=new();internal readonly List<(int,int)> Removed=new();
  internal double X=50.5,Y=60;internal int Moves,Holds;internal bool Lie,NoProgress;
  public DigCell Read(int x,int y)=>Cells.GetValueOrDefault((x,y),new(x,y,y>=60?1:-1,y>=60));
  public ExpeditionStep Mine(int x,int y,int type)
  {
   if(NoProgress)return new(ExpeditionStepState.Pending);
   if(Math.Pow(X-(x+.5),2)+Math.Pow(Y-1.25-(y+.5),2)>25)throw new Exception("remote excavation");
   Removed.Add((x,y));Cells[(x,y)]=new(x,y);return new(ExpeditionStepState.Done,Removed:Lie?0:1);
  }
  public ExpeditionStep Move(TunnelPose p){Moves++;X=p.X+.5;Y=p.FeetY;return new(ExpeditionStepState.Done);}
  public void Hold()=>Holds++;public void Release()=>Holds++;
  internal ExpeditionFrame Frame(long tick)=>new(tick,X,Y);
 }
 internal static void Run(Action<bool,string> check)
 {
  OreTunnelPlan Plan(Site s,int x=55,int y=65)
  {s.Cells[(x,y)]=new(x,y,6,true);return OreTunnelPlan.Create(new(50,60),s.Read(x,y),s.Read,out _)??throw new Exception("no test plan");}
  void Tick(OreTunnelJob job,Site site,int ticks=2000){for(int t=1;t<=ticks&&job.Active;t++)job.Tick(site.Frame(t),site);}
  foreach(var text in new[]{"规划通道挖铁矿","挖过去采矿","开路采铜矿"})check(OreTunnelRequest.TryParse(text,out _),"recognize explicit ore tunnel: "+text);
  foreach(var text in new[]{"挖1处矿","往下挖一条通道","查附近矿","不要开路挖矿","停止采矿通道","如果挖通道采矿","怎么挖通道采矿"})check(!OreTunnelRequest.TryParse(text,out _),"ordinary gathering/scouting does not authorize a tunnel: "+text);
  var site=new Site();var plan=Plan(site);var job=new OreTunnelJob(plan);Tick(job,site,40);
  check(job.State==OreTunnelState.Preview&&site.Removed.Count==0&&site.Moves==0,"preview cannot mine or move before approval");
  check(plan.Steps.Count>0&&plan.Excavation.Count+1<=OreTunnelPlan.MaxTiles&&plan.Describe.Contains("矿格")&&plan.Describe.Contains((plan.Excavation.Count+1).ToString()),"bounded preview contains concrete corridor and counts");
  check(plan.Excavation.All(c=>c.Type>=0)&&!plan.Excavation.Any(c=>c.X==plan.Target.X&&c.Y==plan.Target.Y),"ore target is separate from corridor edits");
  check(job.Approve(site),"approve unchanged exact footprint");Tick(job,site);
  check(job.State==OreTunnelState.Completed&&job.VerifiedOre==1,"verified tunnel and ore chain completes");
  check(site.Removed.Count==job.Removed&&site.Removed.Distinct().Count()==site.Removed.Count,"every edit has exactly one verified receipt");
  var allowed=plan.Excavation.Append(plan.Target).Select(c=>(c.X,c.Y)).ToHashSet();
  check(site.Removed.All(allowed.Contains),"excavation stays inside preview whitelist");
  int terminal=site.Removed.Count;job.Tick(site.Frame(9999),site);check(site.Removed.Count==terminal,"terminal execution cannot repeat edits");
  foreach(int dx in new[]{-9,-5,5,9})foreach(int depth in new[]{2,4,6})
  {
   site=new();plan=Plan(site,50+dx,60+depth);job=new(plan);job.Approve(site);Tick(job,site);
   check(job.State==OreTunnelState.Completed,$"bounded stair route completes dx={dx} depth={depth}");
  }
  site=new();plan=Plan(site);var cell=plan.Snapshot.First(c=>c.Type>=0);site.Cells[(cell.X,cell.Y)]=cell with{Hazard=true};job=new(plan);
  check(!job.Approve(site)&&site.Removed.Count==0,"new hazard invalidates preview approval");
  foreach(string change in new[]{"hazard","protected","type","tool"})
  {
   site=new();plan=Plan(site);job=new(plan);job.Approve(site);cell=plan.Snapshot.First(c=>c.Type>=0);
   site.Cells[(cell.X,cell.Y)]=change switch{"hazard"=>cell with{Hazard=true},"protected"=>cell with{Protected=true},"tool"=>cell with{Mineable=false},_=>cell with{Type=99}};
   job.Tick(site.Frame(1),site);check(job.State==OreTunnelState.Failed&&site.Removed.Count==0,"runtime change stops before mutation: "+change);
  }
  site=new();plan=Plan(site);job=new(plan);job.Approve(site);job.Tick(site.Frame(1) with{Permitted=false},site);
  check(job.State==OreTunnelState.Failed&&site.Removed.Count==0,"permission revocation stops confirmed tunnel");
  site=new();plan=Plan(site);job=new(plan);job.Approve(site);job.Tick(site.Frame(1) with{Valid=false},site);
  check(job.State==OreTunnelState.Cancelled&&site.Removed.Count==0,"new command/world cancellation prevents edits");
  site=new();plan=Plan(site);job=new(plan);job.Approve(site);job.Tick(site.Frame(1),site);terminal=site.Removed.Count;
  job.Cancel(site,"player stopped");Tick(job,site);check(site.Removed.Count==terminal&&job.Removed==terminal,"stop preserves partial edits without continuing");
  site=new();plan=Plan(site);job=new(plan);job.Approve(site);job.Tick(site.Frame(1) with{Paused=true},site);
  check(site.Removed.Count==0&&site.Moves==0,"paused tunnel never edits or moves");
  job.Tick(site.Frame(2),site);terminal=site.Removed.Count;job.Tick(site.Frame(2),site);check(site.Removed.Count==terminal,"duplicate game tick cannot repeat an edit");
  site=new(){Lie=true};plan=Plan(site);job=new(plan);job.Approve(site);Tick(job,site);
  check(job.State==OreTunnelState.Failed&&job.VerifiedOre==0,"fake action success without mutation receipt is rejected");
  site=new(){NoProgress=true};plan=Plan(site);job=new(plan);job.Approve(site);Tick(job,site);
  check(job.State==OreTunnelState.Failed&&site.Removed.Count==0,"stalled excavation is bounded");
  site=new();check(OreTunnelPlan.Create(new(50,60),new(50,80,6,true),site.Read,out _)==null,"reject target beyond depth bound");
  foreach(bool beforeApproval in new[]{true,false})
  {
   site=new();for(int x=40;x<65;x++)for(int y=60;y<72;y++)site.Cells[(x,y)]=new(x,y,0,true,Substrate:0);
   plan=Plan(site);job=new(plan);cell=plan.Excavation.First();
   if(!beforeApproval)check(job.Approve(site),"approve exact ordinary soil before a grass update");
   site.Cells[(cell.X,cell.Y)]=cell with{Type=2};
   if(beforeApproval)check(!job.Approve(site)&&site.Removed.Count==0,"even dirt/grass changes invalidate an unconfirmed preview");
   else{Tick(job,site);check(job.State==OreTunnelState.Completed&&job.VerifiedOre==1&&site.Removed.All(p=>plan.Excavation.Append(plan.Target).Any(c=>(c.X,c.Y)==p)),
     "confirmed tunnel survives a grass/dirt update and completes within its exact excavation whitelist");}
  }
  foreach(string change in new[]{"hazard","protected","tool","ore","unknown substrate"})
  {
   site=new();for(int x=40;x<65;x++)for(int y=60;y<72;y++)site.Cells[(x,y)]=new(x,y,0,true,Substrate:0);
   plan=Plan(site);job=new(plan);job.Approve(site);cell=plan.Excavation.First();
   site.Cells[(cell.X,cell.Y)]=change switch{
    "hazard"=>cell with{Type=2,Hazard=true},"protected"=>cell with{Type=2,Protected=true},"tool"=>cell with{Type=2,Mineable=false},
    "ore"=>cell with{Type=6,Substrate=null},_=>cell with{Type=2,Substrate=null}};
   job.Tick(site.Frame(1),site);check(job.State==OreTunnelState.Failed&&site.Removed.Count==0,
    "soil equivalence cannot bypass a safety or material change: "+change);
  }
  site=new();site.Cells[(50,59)]=new(50,59,1,true);check(OreTunnelPlan.Create(new(50,60),new(55,65,6,true),site.Read,out _)==null,"require clear initial standing body");
  site=new();for(int x=48;x<=52;x++)site.Cells[(x,60)]=new(x,60);
  check(OreTunnelPlan.Create(new(50,60),new(55,65,6,true),site.Read,out _)==null,"reject unsupported starting floor");
 }
}
