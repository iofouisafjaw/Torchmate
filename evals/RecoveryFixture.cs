using System.Text.Json;
using TerrarAI.Content.Companion;

namespace TerrarAI.Evals;

// Fixture data is not a Terraria catalog. The real pure planners/jobs run against these explicit sites.
public sealed class RecoveryFixture
{
    private readonly RecoveryCase _case;
    public FakeWorld World{get;}
    private static readonly string[] Names={"","木材","石块","火把","工作台","熔炉","铁锭","铁头盔","铁链甲","铁护胫","木椅","木墙","木门","木桌","木平台","凝胶","太阳花种子","太阳花","铁砧"};
    public RecoveryFixture(RecoveryCase c){_case=c;World=new(c.World);}
    private int Have(int id)=>World.Bag.GetValueOrDefault(Names[id])+World.Chests.GetValueOrDefault(Names[id]);
    private void Add(int id,int n)=>World.Bag[Names[id]]=World.Bag.GetValueOrDefault(Names[id])+n;
    private bool Take(int id,int n)
    {
        if(Have(id)<n)return false;
        int bag=Math.Min(n,World.Bag.GetValueOrDefault(Names[id]));Add(id,-bag);
        if(n>bag)World.Chests[Names[id]]=World.Chests.GetValueOrDefault(Names[id])-(n-bag);return true;
    }
    public TaskFailureReceipt Receipt()=>new("fixture-task",_case.Family,"fixture_failure",_case.Reason,_case.Progress,
        World.Bag.Keys.Concat(World.Chests.Keys).Distinct().OrderBy(n=>n).Select(n=>new RecoveryStock(Array.IndexOf(Names,n),n,World.Bag.GetValueOrDefault(n),World.Chests.GetValueOrDefault(n))).ToArray(),
        Snapshot(),_case.CanReplan);
    public string Snapshot()=>World.Snapshot()+"\n最新附近箱子："+string.Join("、",World.Chests.Select(p=>$"{p.Key}×{p.Value}"));
    public string StockSignature()=>JsonSerializer.Serialize(new{World.Bag,World.Chests,World.Placed,World.Spawned});
    public string QueryOrRun(AgentCall call,AgentLoop loop)
    {
        if(call.Tool=="recipe"&&call.Item=="铁盔甲")return "离线配方夹具：铁头盔20铁锭、铁链甲30铁锭、铁护胫25铁锭；在铁砧旁制作，已有成品计入原目标。";
        return World.Run(call,loop);
    }
    public bool MatchesOriginal(AgentCall call)=>_case.CanReplan&&(_case.Family switch {
        "craft_chain"=>call.Tool=="craft_chain"&&(CraftChainRequest.TryParse(_case.Goal,out var original,out _)&&original!=null?
            original.IronSet?_case.Target=="铁盔甲":original.Item==_case.Target&&original.Count==_case.Count:
            call.Item==_case.Target&&(call.Count==0||call.Count==_case.Count)),
        "platform"=>call.Tool=="platform",
        "build"=>call.Tool=="build"&&call.Rooms is 0 or 1,
        // A new generic garden start chooses new cells; only native replan preserves unfinished targets.
        _=>false});

    public bool ConfirmAndVerify()
    {
        if(!_case.CanReplan||!_case.Approve)return false;
        if(_case.Family=="craft_chain")
        {
            var targets=_case.Target=="铁盔甲"?new[]{new ChainTarget(7,1),new(8,1),new(9,1)}:
                new[]{new ChainTarget(Array.IndexOf(Names,_case.Target),_case.Count)};
            int[] stations=World.Near.Select(n=>n=="工作台"?100:n=="熔炉"?101:n is "铁砧" or "砧"?102:-1).Where(n=>n>=0).ToArray();
            var plan=CraftChainPlanner.Build(targets,Enumerable.Range(1,Names.Length-1).ToDictionary(i=>i,Have),stations,new Catalog(this));
            var job=new CraftChainJob(plan);if(!job.Confirm(job.PreviewId,plan.Signature,DateTimeOffset.UtcNow))return false;
            var site=new ChainSite(this);
            for(int n=0;n<3000&&job.State==ChainState.Running;n++)job.Step(site);
            return job.State==ChainState.Completed&&targets.All(t=>World.Bag.GetValueOrDefault(Names[t.Item])>=t.Count);
        }
        if(_case.Family is "platform" or "build")
        {
            var plan=_case.Family=="platform"?new StructurePlan("original three-row platform","fixture",100,13,
                Enumerable.Range(0,3).SelectMany(y=>Enumerable.Range(0,100).Select(x=>new StructureStep(x,y*6,StepKind.Block,19,0,14))).ToArray()):
                StructurePresets.NpcRooms(1,new(30,1,4,11,10,12,14,13,15,10,4,3));
            var site=new BuildSite(this,plan);var job=new StructureBuildJob(plan);
            while(job.State==StructureJobState.Running)job.Step(site);
            return job.Verified(site)&&site.BeforeCount+job.Placed==plan.Steps.Count;
        }
        if(_case.Family=="garden")
        {
            var cells=Enumerable.Range(0,3).Select(x=>new GardenCell(new(100+x,100),GardenCrop.Daybloom,GardenGrowth.Empty,GardenBed.Planter,true,"fixture")).ToArray();
            var seeds=new Dictionary<GardenCrop,int>{{GardenCrop.Daybloom,3}};
            var original=GardenPlan.Build(new(GardenOperation.Plant,GardenCrop.Daybloom,3),new(100,100),seeds,cells);
            var remaining=original.Remaining(1,seeds,cells);var job=new GardenJob(remaining);
            if(!job.Confirm(job.PreviewId,remaining.Signature,DateTimeOffset.UtcNow))return false;
            var site=new GardenSite(this);for(int n=0;n<20&&job.State==GardenState.Running;n++)job.Tick(site);
            return job.State==GardenState.Completed&&job.Planted==2&&site.Plants.SetEquals(new[]{new BuildPoint(101,100),new(102,100)});
        }
        return false;
    }
    private sealed class Catalog(RecoveryFixture world):ICraftChainCatalog
    {
        public IEnumerable<ChainRecipe> Recipes(int item)
        {
            var spec=item switch {
                4=>(new[]{(1,10)},Array.Empty<int>(),1),
                5=>(new[]{(2,20),(1,4),(3,3)},new[]{100},1),
                7=>(new[]{(6,20)},new[]{102},1),8=>(new[]{(6,30)},new[]{102},1),9=>(new[]{(6,25)},new[]{102},1),
                10=>(new[]{(1,4)},new[]{100},1),18=>(new[]{(6,5)},new[]{100},1),
                3=>(new[]{(1,1),(15,1)},Array.Empty<int>(),3),
                _=>(Array.Empty<(int,int)>(),Array.Empty<int>(),1)};
            return spec.Item1.Length==0?Array.Empty<ChainRecipe>():new[]{new ChainRecipe(item,item,spec.Item3,
                spec.Item1.Select(i=>new IngredientRequirement(i.Item1,i.Item2,new[]{i.Item1})).ToArray(),spec.Item2)};
        }
        public IEnumerable<int> StationItems(int tile)=>tile==100?new[]{4}:tile==101?new[]{5}:tile==102?new[]{18}:Array.Empty<int>();
        public bool Collectable(int item)=>item==1&&world._case.World.Trees>0||item==2&&world._case.World.Ores.GetValueOrDefault("石块")>0;
    }
    private sealed class ChainSite(RecoveryFixture world):ICraftChainSite
    {
        public bool Valid(out string reason){reason="offline approved fixture";return true;}
        public int Stock(int id)=>world.Have(id);
        public ChainGatherResult Collect(int id,out string message){world.Add(id,1);message="actual fixture stock delta";return ChainGatherResult.Finished;}
        public bool Craft(ChainStep step,out string message)
        {
            message="fixture craft";if(step.Ingredients!.Any(p=>world.Have(p.Key)<p.Value))return false;
            foreach(var p in step.Ingredients!)world.Take(p.Key,p.Value);world.Add(step.Item,step.Count*step.Recipe!.Output);return true;
        }
        public bool Place(ChainStep step,out string message){message="fixture station";return world.Take(step.Item,1);}
        public bool Deliver(IReadOnlyList<ChainTarget> targets,out string message)
        {message="fixture delivery";foreach(var t in targets){int need=Math.Max(0,t.Count-world.World.Bag.GetValueOrDefault(Names[t.Item]));if(!world.Take(t.Item,need))return false;world.Add(t.Item,need);}return true;}
        public bool Audit(IReadOnlyList<ChainTarget> targets,out string message){message="fixture actual inventory audit";return targets.All(t=>world.World.Bag.GetValueOrDefault(Names[t.Item])>=t.Count);}
        public void StopGather(){}
    }
    private sealed class BuildSite:IStructureSite
    {
        private readonly RecoveryFixture _world;private readonly HashSet<(int,int,StepKind)> _done=new();public int BeforeCount;
        public BuildSite(RecoveryFixture world,StructurePlan plan){_world=world;foreach(var s in plan.Steps.Take(plan.Steps.Count/2))_done.Add((s.Dx,s.Dy,s.Kind));BeforeCount=_done.Count;}
        public CellState Inspect(StructureStep s)=>_done.Contains((s.Dx,s.Dy,s.Kind))?CellState.Done:CellState.Empty;
        public bool Clear(StructureStep s,out string error){error="";return true;}
        public bool Place(StructureStep s,out string error){error="材料不足";if(!_world.Take(s.Item,1))return false;_done.Add((s.Dx,s.Dy,s.Kind));error="";return true;}
    }
    private sealed class GardenSite(RecoveryFixture world):IGardenSite
    {
        public HashSet<BuildPoint> Plants=new();
        public bool Valid(out string reason){reason="fixture scope";return true;}
        public GardenStep Work(GardenCell c,GardenOperation operation,out GardenReceipt receipt,out string message)
        {receipt=new(0,0,0);message="fixture plant";if(!world.Take(16,1))return GardenStep.Failed;Plants.Add(c.At);receipt=new(1,0,0);return GardenStep.Done;}
        public bool Planted(GardenCell c)=>Plants.Contains(c.At);public bool Removed(GardenCell c)=>false;public void Stop(){}
    }
}
