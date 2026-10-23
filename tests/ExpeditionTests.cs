using System.Text.Json;
using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class ExpeditionTests
{
    static ExpeditionPlan Plan(ExpeditionMode mode=ExpeditionMode.Tunnel,int distance=3,int direction=1)=>new(mode,100,100,mode==ExpeditionMode.Scout?100:100+distance,direction,distance);
    sealed class Site: IExpeditionSite
    {
        internal readonly ExpeditionPlan Plan;
        internal readonly Dictionary<(int,int),DigCell> Tiles=new();
        internal readonly List<(int X,int Y)> Removed=new();
        internal readonly List<ExpeditionObservation> Findings=new();
        internal Func<DigCell,DigCell>? FreshRead;
        internal Func<DigBand,DigBand>? OnBand;
        internal Func<bool>? Commit;
        internal int Moves,Mines,Marks,Scans,Holds,Releases,MineTicks=1;
        internal bool MoveFails,LieMove,LieMine,NoMineProgress,NoFall,HighDestination,MarkAvailable=true;
        internal double X=100.5,Y=100;
        private int _damage;
        private (int,int)? _target;
        internal Site(ExpeditionPlan plan)
        {
            Plan=plan;
            if(plan.Digging)for(int y=plan.StartY;y<plan.EndY;y++)foreach(var p in plan.Row(y))Tiles[p]=new(p.X,p.Y,1,Mineable:true);
        }
        internal DigCell Get(int x,int y)=>Tiles.GetValueOrDefault((x,y),new(x,y));
        public DigCell Read(int x,int y)=>FreshRead?.Invoke(Get(x,y))??Get(x,y);
        public DigBand Band(int row)
        {
            var cells=new List<DigCell>();for(int x=Plan.StartX-2;x<=Plan.StartX+2;x++)for(int y=row-3;y<=row+2;y++)cells.Add(Get(x,y));
            var band=new DigBand(cells);return OnBand?.Invoke(band)??band;
        }
        public ExpeditionStep Mine(int x,int y,int expectedType)
        {
            Mines++;
            if(LieMine)return new(ExpeditionStepState.Done);
            if(NoMineProgress)return new(ExpeditionStepState.Pending);
            if(_target!=(x,y)){_target=(x,y);_damage=0;}
            if(++_damage<MineTicks)return new(ExpeditionStepState.Pending);
            var current=Get(x,y);
            if(!Plan.ContainsDig(x,y)||current.Type!=expectedType||current.Protected||current.Hazard||!current.Mineable||Commit?.Invoke()==false)
                return new(ExpeditionStepState.Failed,"commit denied");
            Tiles.Remove((x,y));Removed.Add((x,y));return new(ExpeditionStepState.Done,Removed:1);
        }
        public ExpeditionStep Move(double centerX,double feetY)
        {
            Moves++;if(MoveFails)return new(ExpeditionStepState.Failed,"blocked");
            if(LieMove)return new(ExpeditionStepState.Done);
            X+=Math.Clamp(centerX-X,-1,1);Y=HighDestination&&Math.Abs(centerX-Plan.CenterX)>0.1?feetY-8:feetY;
            return new(Math.Abs(X-centerX)<=0.1?ExpeditionStepState.Done:ExpeditionStepState.Pending);
        }
        public void Hold()=>Holds++;
        public void Release()=>Releases++;
        public IEnumerable<ExpeditionObservation> Observe(){Scans++;return Findings;}
        public bool Mark(ExpeditionObservation finding){Marks++;return MarkAvailable;}
        internal ExpeditionFrame Frame(long tick,bool valid=true,bool alive=true,bool paused=false,bool permitted=true,string danger="")
        {
            if(Plan.Digging&&!NoFall)
            {
                int floor=Enumerable.Range(Plan.StartY,Plan.Distance+1).FirstOrDefault(y=>Get(Plan.StartX,y).Type>=0,Plan.EndY);
                Y=Math.Max(Y,floor);
            }
            return new(tick,X,Y,valid,alive,paused,permitted,danger);
        }
    }
    static ExpeditionObservation Finding(string id,int x=103,int y=100,LocationKind kind=LocationKind.Ore,string name="铁矿",bool visible=true)=>
        new(new(kind,id,name,x,y),100.5,100.5,visible);
    internal static void Run(Action<bool,string> check)
    {
        foreach(var (text,mode,distance) in new[]{("挖到地下层",ExpeditionMode.Underground,32),("请帮我往下挖到地下层。",ExpeditionMode.Underground,32),
            ("往下挖一条通道",ExpeditionMode.Tunnel,32),("挖一条竖井",ExpeditionMode.Tunnel,32),("向下挖",ExpeditionMode.Tunnel,32),
            ("往下挖16格",ExpeditionMode.Tunnel,16),("向下挖１６格通道",ExpeditionMode.Tunnel,16),
            ("去前面探探",ExpeditionMode.Scout,32),("帮我去前面探探路",ExpeditionMode.Scout,32),("向前探路64格",ExpeditionMode.Scout,64)})
            check(ExpeditionRequest.TryParse(text,out var r,out _)&&r==new ExpeditionRequest(mode,distance),"bounded expedition intent: "+text);
        foreach(string text in new[]{"往下挖0格","往下挖161格","往前探路65格","往下挖9999999999999格"})
            check(ExpeditionRequest.TryParse(text,out var r,out var error)&&r==null&&error.Length>0,"invalid expedition size reports error without executing: "+text);
        foreach(string text in new[]{"不要往下挖","以后去前面探探","怎么挖到地下层","挖到地下层？","去前面探探然后砍树","往下挖和探路", "我的床在哪","挖10个铁矿","挖到地狱","挖到洞穴层",""})
            check(!ExpeditionRequest.TryParse(text,out _,out _),"expedition does not directly execute a question, standing rule, compound or unrelated request: "+text);

        ExpeditionPlan? Make(ExpeditionRequest request,int x=100,int y=100,double surface=120,int facing=1)=>ExpeditionPlan.Create(request,x,y,surface,facing,1000,1000,out _);
        var underground=Make(new(ExpeditionMode.Underground))!;
        check(underground.EndY==123&&underground.Distance==23,"underground endpoint uses actual world surface plus body clearance");
        check(Make(new(ExpeditionMode.Underground),y:130)!.Distance==0,"already underground does not manufacture another long shaft");
        check(Make(new(ExpeditionMode.Underground),surface:280)==null,"underground request refuses over-160 depth before modifying terrain");
        check(Make(new(ExpeditionMode.Scout),facing:-1)!.Direction==-1,"scouting snapshots player facing to the left");
        check(Make(new(ExpeditionMode.Tunnel,160))!.EndY==260,"explicit depth remains bounded to 160");
        foreach(var args in new[]{(X:2,Y:100,Surface:120.0),(X:100,Y:990,Surface:120.0),(X:100,Y:100,Surface:double.NaN)})
            check(Make(new(ExpeditionMode.Tunnel),args.X,args.Y,args.Surface)==null,"invalid world/edge geometry refuses plan");
        check(Make(new(ExpeditionMode.Scout,64),x:30,facing:-1)==null,"forward endpoint cannot cross the world edge");
        var plan=Plan();
        check(plan.Row(100).SequenceEqual(new[]{(99,100),(101,100),(100,100)}),"shaft clears both outer cells before removing the supporting middle cell");
        check(!plan.ContainsDig(98,100)&&!plan.ContainsDig(100,103)&&!plan.ContainsDig(100,99),"shaft excavation is exactly three columns and excludes endpoint floor");
        check(!plan.ContainsActor(double.NaN,100)&&!plan.ContainsActor(106,100)&&!plan.ContainsActor(100.5,107),"excavation actor leash fails closed");
        var site=new Site(plan);var job=new ExpeditionJob(plan);
        for(int tick=0;tick<200&&job.Active;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Completed&&job.Removed==9&&job.ClearedRows==3,"three-layer shaft executes and audits each actual tile removal");
        check(site.Removed.SequenceEqual(Enumerable.Range(100,3).SelectMany(y=>plan.Row(y))),"shaft uses ordered layer-by-layer cuts without breaking below the requested floor");
        check(site.Moves==0&&site.Releases==1,"shaft uses natural descent and releases its tool on completion");
        check(job.Survey.Report(100,100).Contains("不代表区域已安全排查"),"empty survey never claims all danger has been cleared");
        site=new(Plan(ExpeditionMode.Underground,4));job=new(site.Plan);
        for(int tick=0;tick<100&&job.Active;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Completed&&site.Y==site.Plan.EndY&&job.ClearedRows==4,"underground goal is completed only after cleared corridor and actual final depth");
        foreach(string status in new[]{"探完了吗","探路进度怎么样了","发现什么了","你发现了什么？","挖到地下层了吗"})
            check(StatusQuery.IsStatusQuestion(status),"expedition status is a read-only query preserving the job: "+status);

        foreach(var blocked in new[]{new DigCell(98,100,Protected:true),new DigCell(100,99,Hazard:true),new DigCell(99,100,1,Mineable:false)})
        {
            site=new(plan);site.Tiles[(blocked.X,blocked.Y)]=blocked;job=new(plan);job.Tick(site.Frame(0),site);
            check(job.State==ExpeditionState.Failed&&site.Mines==0&&site.Removed.Count==0,"neighbor furniture/wall/wire/hazard or insufficient tool stops before digging: "+blocked);
        }
        foreach(Func<DigBand,DigBand> mutate in new Func<DigBand,DigBand>[] {
            b=>b with{LandingSafe=false},b=>b with{Cells=b.Cells.Skip(1).ToArray()},
            b=>b with{Cells=b.Cells.Take(29).Append(b.Cells[0]).ToArray()}})
        {
            site=new(plan){OnBand=mutate};job=new(plan);job.Tick(site.Frame(0),site);
            check(job.State==ExpeditionState.Failed&&site.Mines==0,"unsafe landing or incomplete/duplicate fresh band never mines");
        }
        site=new(plan){FreshRead=c=>c with{Type=99}};job=new(plan);job.Tick(site.Frame(0),site);
        check(job.State==ExpeditionState.Failed&&site.Mines==0,"target changed after band scan is rechecked immediately before mining");
        site=new(plan){Commit=()=>false};job=new(plan);job.Tick(site.Frame(0),site);
        check(job.State==ExpeditionState.Failed&&job.Removed==0&&site.Removed.Count==0,"adapter final guard rejection cannot generate a removal receipt");
        site=new(plan){LieMine=true};job=new(plan);job.Tick(site.Frame(0),site);
        check(job.State==ExpeditionState.Failed&&job.Removed==0,"successful tool return while tile remains is not successful digging");
        site=new(plan){NoFall=true};job=new(plan);
        for(int tick=0;tick<80;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Running&&site.Removed.All(p=>p.Y<=101),"cannot dig remote rows when the companion has not physically descended");
        site=new(plan);job=new(plan);job.Tick(site.Frame(0),site);
        int partial=job.Removed;site.Tiles[(98,100)]=new(98,100,Protected:true);job.Tick(site.Frame(1),site);
        check(job.State==ExpeditionState.Failed&&job.Removed==partial&&partial==1,"new adjacent furniture between cuts stops the job and preserves partial receipt");
        site=new(plan){MineTicks=5};job=new(plan);job.Tick(site.Frame(0),site);job.Cancel(site,"stop");
        int calls=site.Mines;for(int tick=1;tick<10;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Cancelled&&site.Mines==calls&&job.Removed==0,"stop cancels a partially charged tile and later ticks cannot resume it");
        foreach(var frame in new[]{new ExpeditionFrame(0,100.5,100,Valid:false),new ExpeditionFrame(0,100.5,100,Alive:false),new ExpeditionFrame(0,100.5,100,Permitted:false)})
        {
            site=new(plan);job=new(plan);job.Tick(frame,site);
            check(!job.Active&&site.Mines==0&&site.Moves==0,"stale world/control, dead player or changed mining rules cannot act");
        }
        site=new(plan);job=new(plan);for(int tick=0;tick<800;tick++)job.Tick(site.Frame(tick,paused:true),site);
        check(job.Active&&site.Mines==0&&site.Scans==0,"pause consumes no action, scan or idle budget");
        job.Tick(site.Frame(900),site);calls=site.Mines;job.Tick(site.Frame(900),site);
        check(site.Mines==calls,"duplicate game tick cannot mine twice");
        site=new(plan){NoMineProgress=true};job=new(plan);for(int tick=0;tick<610&&job.Active;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Failed&&job.Reason.Contains("10秒"),"no-progress mining terminates with an honest incomplete result");

        var scout=Plan(ExpeditionMode.Scout,8,-1);site=new(scout);job=new(scout);
        site.Findings.Add(Finding("ore"));site.Findings.Add(Finding("chest",105,100,LocationKind.Chest,"箱子"));
        for(int tick=0;tick<60&&job.Active;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Completed&&site.Mines==0&&Math.Abs(site.X-scout.CenterX)<1.5,"scout moves toward snapshotted left goal, returns and never excavates");
        check(job.Survey.Findings.Count==2&&site.Marks==2&&site.Scans<5,"discovery scan is rate limited and repeated observations do not spam markers");
        site=new(Plan(ExpeditionMode.Scout,8)){HighDestination=true};job=new(site.Plan);
        for(int tick=0;tick<60&&job.Active;tick++)job.Tick(site.Frame(tick),site);
        check(job.State==ExpeditionState.Completed,"scouting can reach a hill destination above the start and still return");
        site=new(scout){MoveFails=true};job=new(scout);job.Tick(site.Frame(0),site);
        check(job.State==ExpeditionState.Failed&&job.Reason.Contains("不可达")&&site.Mines==0,"unreachable route is reported without switching to digging");
        site=new(scout);job=new(scout);
        for(int tick=0;tick<60&&job.State==ExpeditionState.Running;tick++)job.Tick(site.Frame(tick),site);
        site.MoveFails=true;job.Tick(site.Frame(61),site);
        check(job.State==ExpeditionState.Failed&&job.Reason.Contains("返回起点失败"),"return route failure does not claim the scout came back");
        site=new(scout){LieMove=true};job=new(scout);job.Tick(site.Frame(0),site);
        check(job.State==ExpeditionState.Running,"movement Done without the companion near the goal cannot fake exploration completion");
        site=new(scout);site.Findings.Add(Finding("lava",103,100,LocationKind.Danger,"岩浆"));job=new(scout);
        job.Tick(site.Frame(0,danger:"岩浆"),site);
        check(job.State==ExpeditionState.Failed&&site.Moves==0&&job.Survey.Findings.Single().Location.Name=="岩浆", "nearby danger is observed/reported before any forward movement");
        site=new(scout);job=new(scout);job.Tick(new(0,300,100),site);
        check(job.State==ExpeditionState.Failed&&site.Moves==0,"leaving the scout leash stops movement");

        var survey=new ExpeditionSurvey();int marks=0;
        check(!survey.Add(Finding("occluded",visible:false),_=>{marks++;return true;})&&!survey.Add(Finding("far",113),_=>true),"occluded and beyond-12 observations cannot disclose targets");
        check(!survey.Add(Finding("bed",kind:LocationKind.Bed),_=>true),"survey does not invent unsupported discovery categories");
        check(survey.Add(Finding("one"),_=>{marks++;return true;})&&!survey.Add(Finding("same-vein",104),_=>true)&&marks==1,"same vein observations merge before reporting/marking");
        check(survey.Add(Finding("box",105,100,LocationKind.Chest,"箱子"),_=>false)&&survey.Marked==1,"unavailable map layer keeps historical findings without claiming an extra marker");
        check(survey.Report(110,100).Contains("左方")&&survey.Report(90,100).Contains("右方"),"report computes directions from current player rather than companion start");
        var maxSurvey=new ExpeditionSurvey();for(int i=0;i<50;i++)maxSurvey.Add(Finding("n"+i,name:"矿"+i),_=>true);
        check(maxSurvey.Findings.Count==ExpeditionSurvey.Limit,"survey list and map requests remain bounded");
        site=new(scout);for(int i=0;i<30;i++)site.Findings.Add(Finding("m"+i,name:"矿"+i));site.Findings.Add(Finding("trap",103,100,LocationKind.Danger,"陷阱"));
        job=new(scout);job.Tick(site.Frame(0),site);
        check(job.Survey.Findings.Any(p=>p.Location.Kind==LocationKind.Danger),"ore-rich survey cannot crowd danger out of the bounded report");
        var markerStore=new LocationMarkers();var session=Guid.NewGuid();
        foreach(var kind in Enum.GetValues<LocationKind>())markerStore.Replace(session,1,kind,new(kind,kind.ToString(),kind.ToString(),100,100));
        check(markerStore.Current(session,1,_=>true).Count==4&&markerStore.Current(Guid.NewGuid(),1,_=>true).Count==0,"danger reuses scoped map store as fourth category and cannot cross world visits");

        // Vary solid depth, tool cadence, preexisting air and the cancellation
        // boundary. Every removal must be inside the approved 3-column volume.
        var random=new Random(191026);
        for(int run=0;run<100;run++)
        {
            plan=Plan(distance:random.Next(1,9));site=new(plan){MineTicks=random.Next(1,5)};
            foreach(var key in site.Tiles.Keys.ToArray())if(random.Next(5)==0)site.Tiles.Remove(key);
            int solid=site.Tiles.Count;job=new(plan);int cancelAt=run%3==0?random.Next(1,12):int.MaxValue;bool cancelled=false;
            for(int tick=0;tick<400&&job.Active;tick++)
            {if(tick==cancelAt){job.Cancel(site,"random stop");cancelled=true;}job.Tick(site.Frame(tick),site);}
            bool safe=site.Removed.All(at=>plan.ContainsDig(at.X,at.Y))&&job.Removed==site.Removed.Count&&site.Removed.Distinct().Count()==site.Removed.Count;
            check(safe&&(cancelled?job.State==ExpeditionState.Cancelled:job.State==ExpeditionState.Completed&&job.Removed==solid),"random shaft depth/cadence/air/cancel audits actual in-bound removals: "+run);
        }
        site=new(Plan()){NoMineProgress=true};job=new(site.Plan);
        for(int tick=0;tick<=ExpeditionJob.MaxDigTicks+1&&job.Active;tick++)
        {site.X=100.5+(tick%2==0?0.5:-0.5);job.Tick(site.Frame(tick),site);}
        check(job.State==ExpeditionState.Failed&&job.Reason.Contains("时间上限"),"continued movement cannot bypass the whole-job digging time cap");
        site=new(Plan(ExpeditionMode.Scout)){LieMove=true};job=new(site.Plan);
        for(int tick=0;tick<=ExpeditionJob.MaxScoutTicks+1&&job.Active;tick++)
        {site.X=100.5+(tick%2==0?0.5:-0.5);job.Tick(site.Frame(tick),site);}
        check(job.State==ExpeditionState.Failed&&job.Reason.Contains("时间上限")&&site.Mines==0,"scout whole-job time cap cannot be bypassed by moving without arriving");

        foreach(string mode in new[]{"underground","tunnel","scout"})
        {
            var call=AgentProtocol.Parse(JsonSerializer.Serialize(new{tool="expedition",mode}));
            check(call.Mode==mode&&call.Ends&&!AgentProtocol.IsLookup(call.Tool),"model expedition mode hands off to bounded production controller: "+mode);
        }
        foreach(string json in new[]{"""{"tool":"expedition"}""","""{"tool":"expedition","mode":"hell"}""",
            """{"tool":"expedition","mode":"scout","count":65}""","""{"tool":"expedition","mode":"tunnel","count":161}""",
            """{"tool":"expedition","mode":"scout","count":1.5}""","""{"tool":"expedition","mode":"scout","count":-1}"""})
        {bool rejected=false;try{AgentProtocol.Parse(json);}catch(FormatException){rejected=true;}check(rejected,"model cannot exceed expedition modes or exact distance limits");}
        var approval=new ApprovalTicket(session,"task",JsonSerializer.Serialize(Plan()),DateTimeOffset.UtcNow.AddMinutes(2),3,true);
        check(approval.Matches(session,"task",JsonSerializer.Serialize(Plan()),DateTimeOffset.UtcNow,3,true)&&
            !approval.Matches(session,"task",JsonSerializer.Serialize(Plan(distance:4)),DateTimeOffset.UtcNow,3,true),"dig approval binds the entire immutable corridor, not an interchangeable mine tile");
        check(!approval.Matches(Guid.NewGuid(),"task",approval.DecisionJson,DateTimeOffset.UtcNow,3,true)&&
            !approval.Matches(session,"task",approval.DecisionJson,DateTimeOffset.UtcNow,4,true)&&
            !approval.Matches(session,"task",approval.DecisionJson,DateTimeOffset.UtcNow,3,false)&&
            !approval.Matches(session,"task",approval.DecisionJson,DateTimeOffset.UtcNow.AddMinutes(3),3,true),"world, mining policy, day and timeout revoke pending corridor approval");
        var fake=new FakeWorld(new WorldSpec());string result=fake.Run(new("expedition",Mode:"scout"),new("探路"));
        check(result.Contains("没有真实地形")&&result.Contains("未执行"),"offline model fixture does not fabricate scouting or excavation results");
    }
}
