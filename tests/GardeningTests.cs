using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class GardeningTests
{
    static GardenCell Cell(int x,int y,GardenCrop crop=GardenCrop.Daybloom,GardenGrowth growth=GardenGrowth.Empty,GardenBed bed=GardenBed.Planter,bool safe=true)
        =>new(new(x,y),crop,growth,bed,safe,$"{x},{y}:{crop}:{growth}:{bed}:{safe}");
    static GardenPlan Plan(GardenRequest request,params GardenCell[] cells)=>GardenPlan.Build(request,new(0,0),Enum.GetValues<GardenCrop>().ToDictionary(c=>c,_=>64),cells);
    internal static void Run(Action<bool,string> check)
    {
        foreach(var (text,crop,count) in new[]{("种3颗橡实",GardenCrop.Acorn,3),("帮我种三棵橡实",GardenCrop.Acorn,3),("种5株太阳花",GardenCrop.Daybloom,5),("种月光草种子2粒",GardenCrop.Moonglow,2),("播种４株寒颤棘",GardenCrop.Shiverthorn,4)})
            check(GardenRequest.TryParse(text,out var request,out _)&&request==new GardenRequest(GardenOperation.Plant,crop,count),"bounded native garden parse: "+text);
        check(GardenRequest.TryParse("种草药",out var herbs,out _)&&herbs==new GardenRequest(GardenOperation.Plant,null,5),"bare herbs plant five available herb seeds, not acorns");
        check(GardenRequest.TryParse("采收附近的草药",out var harvest,out _)&&harvest==new GardenRequest(GardenOperation.Harvest,null,20,false),"unquantified harvest means at most twenty blooming plants");
        foreach(string text in new[]{"采收已开花的草药","采收附近已开花草药","采收5株已开花的太阳花"})
            check(GardenRequest.TryParse(text,out var flowering,out _)&&flowering?.Operation==GardenOperation.Harvest,"flowering qualifier does not become a malformed count: "+text);
        check(GardenRequest.TryParse("收割10株闪耀根",out var exact,out _)&&exact==new GardenRequest(GardenOperation.Harvest,GardenCrop.Blinkroot,10),"explicit harvest count remains exact and is measured in plants");
        foreach(string text in new[]{"种0颗橡实","种65株草药","种1.5株太阳花","种-1株月光草","种太阳花和月光草","种全部草药","采橡实"})
            check(GardenRequest.TryParse(text,out var invalid,out string error)&&invalid==null&&error.Length>0,"invalid garden request cannot be silently truncated: "+text);
        foreach(string text in new[]{"怎么种太阳花","不要种橡实","以后采收草药先问我","草药开花了吗？","取消采收草药","如果种月光草会怎样","合成2瓶再生药水","砍3棵树"})
            check(!GardenRequest.TryParse(text,out _,out _),"question, preference or unrelated command does not start gardening: "+text);
        foreach(var crop in Enum.GetValues<GardenCrop>())
        {
            var right=crop switch{GardenCrop.Acorn or GardenCrop.Daybloom=>GardenBed.Grass,GardenCrop.Moonglow=>GardenBed.JungleGrass,GardenCrop.Blinkroot=>GardenBed.DirtMud,GardenCrop.Deathweed=>GardenBed.Evil,GardenCrop.Waterleaf=>GardenBed.Sand,GardenCrop.Fireblossom=>GardenBed.Ash,_=>GardenBed.SnowIce};
            check(GardenRules.Plantable(Cell(0,0,crop,bed:right)),"matching natural bed accepts "+crop);
            check(!GardenRules.Plantable(Cell(0,0,crop,bed:GardenBed.None))&&!GardenRules.Plantable(Cell(0,0,crop,bed:right,safe:false)),"protected or unsupported bed rejects "+crop);
            foreach(var bed in new[]{GardenBed.Planter,GardenBed.Pot})
                check(GardenRules.Plantable(Cell(0,0,crop,bed:bed))==(crop!=GardenCrop.Acorn),"pot/planter compatibility "+crop+" "+bed);
            foreach(var growth in new[]{GardenGrowth.Immature,GardenGrowth.Mature,GardenGrowth.Sapling,GardenGrowth.Blocked})
                check(!GardenRules.Plantable(Cell(0,0,crop,growth,right))&&!GardenRules.Harvestable(Cell(0,0,crop,growth,right)),"never overwrite or prematurely harvest "+crop+" "+growth);
        }
        foreach(var crop in Enum.GetValues<GardenCrop>())
            check(GardenRules.Harvestable(Cell(0,0,crop,GardenGrowth.Blooming))==(crop!=GardenCrop.Acorn),"only actual blooming herb stages qualify: "+crop);
        check(!GardenRules.Harvestable(Cell(0,0,growth:GardenGrowth.Blooming,safe:false)),"protected blooming herb remains untouched");
        var seeds=new Dictionary<GardenCrop,int>{[GardenCrop.Daybloom]=2,[GardenCrop.Moonglow]=1,[GardenCrop.Acorn]=30};
        var observations=new[]{Cell(2,0),Cell(1,0),Cell(3,0),Cell(1,0,GardenCrop.Moonglow),Cell(4,0,GardenCrop.Moonglow),Cell(0,0,GardenCrop.Acorn,bed:GardenBed.Grass)};
        var mixed=GardenPlan.Build(new(GardenOperation.Plant,null,3),new(0,0),seeds,observations);
        check(mixed.CanExecute&&mixed.Targets.Count==3&&mixed.Targets.Select(t=>t.At).Distinct().Count()==3&&mixed.Targets.All(t=>t.Crop!=GardenCrop.Acorn),"mixed seeds use independent budgets and do not spend two seeds on one physical cell");
        check(seeds[GardenCrop.Daybloom]==2&&seeds[GardenCrop.Moonglow]==1,"seed plan does not reserve or mutate player stock during preview");
        var shortSeeds=GardenPlan.Build(new(GardenOperation.Plant,GardenCrop.Daybloom,4),new(0,0),seeds,observations);
        check(!shortSeeds.CanExecute&&shortSeeds.Targets.Count==2&&shortSeeds.Blocked.Contains("要求4株"),"not enough seed stock blocks all planting until refreshed");
        var shortBeds=GardenPlan.Build(new(GardenOperation.Plant,GardenCrop.Daybloom,4),new(0,0),new Dictionary<GardenCrop,int>{{GardenCrop.Daybloom,20}},new[]{Cell(0,0)});
        check(!shortBeds.CanExecute&&shortBeds.Targets.Count==1,"not enough compatible empty beds is not reported as full success");
        var trees=Plan(new(GardenOperation.Plant,GardenCrop.Acorn,4),Enumerable.Range(0,16).Select(x=>Cell(x,0,GardenCrop.Acorn,bed:GardenBed.Grass)).ToArray());
        check(trees.CanExecute&&trees.Targets.Select(t=>t.At.X).SequenceEqual(new[]{0,4,8,12}),"tree plan reserves four-column spacing without clearing existing terrain");
        var boundary=Plan(new(GardenOperation.Harvest,null,64,false),Cell(28,28,growth:GardenGrowth.Blooming),Cell(29,0,growth:GardenGrowth.Blooming),Cell(0,-29,growth:GardenGrowth.Blooming));
        check(boundary.CanExecute&&boundary.Targets.Count==1&&boundary.Targets[0].At==new BuildPoint(28,28),"target radius stays inside the existing twenty-eight tile square");
        var ripe=new[]{Cell(0,0,growth:GardenGrowth.Immature),Cell(1,0,growth:GardenGrowth.Mature),Cell(2,0,growth:GardenGrowth.Blooming),Cell(3,0,growth:GardenGrowth.Blooming,safe:false)};
        var harvestPlan=Plan(new(GardenOperation.Harvest,null,20,false),ripe);
        check(harvestPlan.CanExecute&&harvestPlan.Targets.Count==1,"default harvest only selects available safe blooming plants and keeps immature/mature plants");
        check(!Plan(new(GardenOperation.Harvest,null,2),ripe).CanExecute,"explicit larger harvest cannot silently become a smaller completed goal");
        var site=new Site(mixed,seeds);var job=new GardenJob(mixed);var now=DateTimeOffset.UtcNow;
        job.Tick(site);check(site.Effects==0&&job.Cursor==0,"garden preview cannot transfer seeds, plant, harvest or collect");
        check(!job.Confirm(Guid.NewGuid(),mixed.Signature,now)&&!job.Confirm(job.PreviewId,"stale scene",now)&&!job.Confirm(job.PreviewId,mixed.Signature,job.Expires),"garden confirmation rejects wrong ID, changed plan and exact expiration boundary");
        check(job.Confirm(job.PreviewId,mixed.Signature,now)&&site.Effects==0,"confirm schedules but does not synchronously consume seeds");
        job.Tick(site,true);check(site.Effects==0,"paused confirmed garden cannot mutate or advance");
        Run(job,site);
        check(job.State==GardenState.Completed&&job.Planted==3&&site.Plants.Count==3&&site.Seeds.Values.Sum()==30,"mixed herb planting consumes exactly three actual seeds and leaves acorns");
        var blocked=new GardenJob(shortSeeds);check(!blocked.Confirm(blocked.PreviewId,shortSeeds.Signature,now),"insufficient seeds are not confirmable");
        var hs=new Site(harvestPlan,new Dictionary<GardenCrop,int>());var hj=new GardenJob(harvestPlan);hj.Confirm(hj.PreviewId,harvestPlan.Signature,now);hs.Ask=true;hj.Tick(hs);
        check(hj.Asking&&hs.Effects==0&&hj.Cursor==0,"existing per-target collection approval is retained after whole-plan confirmation");
        hs.Ask=false;Run(hj,hs);
        check(hj.State==GardenState.Completed&&hj.Herbs==2&&hj.Seeds==3&&hs.Plants.Count==0,"native-sized harvest fixture only counts actual herb and seed receipts");
        foreach(string failure in new[]{"scope","seed removed","target changed","lying placement","no herb drop","no removal","unreachable","full bag","late plant removal"})
        {
            bool picking=failure is "no herb drop" or "no removal" or "full bag";
            var p=picking?harvestPlan:mixed;var s=new Site(p,seeds);var j=new GardenJob(p);j.Confirm(j.PreviewId,p.Signature,now);
            if(failure=="scope")s.IsValid=false;
            if(failure=="seed removed")s.Seeds.Clear();
            if(failure=="target changed")s.Changed=true;
            if(failure=="lying placement")s.Lie=true;
            if(failure=="no herb drop")s.HerbYield=0;
            if(failure=="no removal")s.RemovePlant=false;
            if(failure=="unreachable"||failure=="full bag")s.FailWork=true;
            if(failure=="late plant removal"){j.Tick(s);s.Plants.Clear();}
            Run(j,s);
            check(j.State==GardenState.Failed,"garden never claims success after "+failure);
        }
        foreach(bool afterPartial in new[]{false,true})
        {
            var s=new Site(mixed,seeds);var j=new GardenJob(mixed);j.Confirm(j.PreviewId,mixed.Signature,now);if(afterPartial)j.Tick(s);
            j.Cancel(s,"stop");int effects=s.Effects;j.Tick(s);
            check(j.State==GardenState.Cancelled&&s.Effects==effects&&j.Planted==(afterPartial?1:0),"cancel preserves partial plants and blocks delayed actions, partial="+afterPartial);
        }
        var preference=new LearnedPreference("gather_confirmation","required","test","player","turn",DateTimeOffset.UtcNow,"any");
        check(CompanionPolicy.CheckGatherConfirmation(new[]{preference},day:true)==PolicyResult.Ask&&CompanionPolicy.CheckGatherConfirmation(new[]{preference},true,true)==PolicyResult.Allow,"herb harvest reuses collection approval policy");
        check(CompanionPolicy.CheckGatherConfirmation(new[]{new LearnedPreference("mining","forbidden","test","player","turn",DateTimeOffset.UtcNow,"any")},day:true)==PolicyResult.Allow,"mining ban does not invent an unrelated herb-harvest ban");
        var timed=preference with{When="night"};
        check(CompanionPolicy.CheckGatherConfirmation(new[]{timed},day:true)==PolicyResult.Allow&&CompanionPolicy.CheckGatherConfirmation(new[]{timed},day:false)==PolicyResult.Ask&&CompanionPolicy.CheckGatherConfirmation(new[]{timed})==PolicyResult.Ask,"herb harvest respects day/night and unknown-time gather confirmation");
        var call=AgentProtocol.Parse("""{"tool":"garden","mode":"plant","item":"太阳花","count":5}""");
        check(call.Ends&&AgentProtocol.Parse("""{"tool":"garden","mode":"harvest","item":"草药"}""").Ends,"model gardening is a bounded terminal preview handoff");
        foreach(string raw in new[]{"""{"tool":"garden","mode":"grow","item":"太阳花"}""","""{"tool":"garden","mode":"plant","item":"不存在"}""","""{"tool":"garden","mode":"harvest","item":"橡实"}""","""{"tool":"garden","mode":"plant","item":"太阳花","count":1.5}""","""{"tool":"garden","mode":"plant","item":"太阳花","count":65}""","""{"tool":"garden","mode":"plant","item":"太阳花","count":0}"""})
        {bool rejected=false;try{AgentProtocol.Parse(raw);}catch(FormatException){rejected=true;}check(rejected,"model cannot create unsupported crop, harvest tree or truncate quantity: "+raw);}
        var fake=new FakeWorld(new WorldSpec());string message=fake.Run(call,new AgentLoop("种5株太阳花"));
        check(message.Contains("等待玩家确认")&&fake.Bag.Count==0,"offline garden tool explicitly creates no synthetic plants, herbs or seeds");
        PotionSupply(check);
        var random=new Random(231026);
        for(int trial=0;trial<60;trial++)
        {
            var stock=Enum.GetValues<GardenCrop>().ToDictionary(c=>c,_=>random.Next(0,12));
            var candidates=Enumerable.Range(0,80).Select(_=>Cell(random.Next(-30,31),random.Next(-30,31),(GardenCrop)random.Next(1,8),
                (GardenGrowth)random.Next(0,4),GardenBed.Planter,random.Next(5)!=0)).ToArray();
            var req=new GardenRequest(trial%2==0?GardenOperation.Plant:GardenOperation.Harvest,null,random.Next(1,10),false);
            var planned=GardenPlan.Build(req,new(0,0),stock,candidates);
            bool conserved=planned.Targets.Count<=req.Count&&planned.Targets.Select(c=>c.At).Distinct().Count()==planned.Targets.Count&&planned.Targets.All(c=>GardenRules.InRange(c.At,new(0,0)));
            conserved&=req.Operation==GardenOperation.Plant?planned.Targets.All(GardenRules.Plantable)&&planned.Targets.GroupBy(c=>c.Crop).All(g=>g.Count()<=stock[g.Key]):planned.Targets.All(GardenRules.Harvestable);
            check(conserved,"generated garden planning conserves seeds, range and growth rules seed231026 case "+trial);
        }
    }
    static void Run(GardenJob job,Site site){for(int i=0;i<200&&job.State==GardenState.Running;i++)job.Tick(site);}
    sealed class Site:IGardenSite
    {
        internal Dictionary<GardenCrop,int> Seeds;internal HashSet<BuildPoint> Plants=new();
        internal int Effects,HerbYield=2;internal bool IsValid=true,Changed,Lie,Ask,FailWork,RemovePlant=true;
        internal Site(GardenPlan p,IReadOnlyDictionary<GardenCrop,int> seeds){Seeds=new(seeds);if(p.Request.Operation==GardenOperation.Harvest)Plants.UnionWith(p.Targets.Select(t=>t.At));}
        public bool Valid(out string reason){reason="invalid scope";return IsValid;}
        public GardenStep Work(GardenCell target,GardenOperation operation,out GardenReceipt receipt,out string message)
        {
            receipt=new(0,0,0);message="actual transaction";if(Changed||FailWork)return GardenStep.Failed;if(Ask)return GardenStep.Asking;
            if(operation==GardenOperation.Plant)
            {if(Seeds.GetValueOrDefault(target.Crop)<1)return GardenStep.Failed;Seeds[target.Crop]--;Effects++;if(!Lie)Plants.Add(target.At);receipt=new(1,0,0);}
            else{Effects++;if(RemovePlant)Plants.Remove(target.At);receipt=new(0,HerbYield,3);}return GardenStep.Done;
        }
        public bool Planted(GardenCell target)=>Plants.Contains(target.At);
        public bool Removed(GardenCell target)=>!Plants.Contains(target.At);
        public void Stop(){}
    }
    // Actual shared planner/job exercise a potion chain with bag + box priority,
    // real receipts, overshooting plant yield and no implicit growth wait.
    static void PotionSupply(Action<bool,string> check)
    {
        const int Herb=100,Water=101,Potion=102;
        var catalog=new PotionCatalog();
        var ready=CraftChainPlanner.Build(new[]{new ChainTarget(Potion,3)},new Dictionary<int,int>{{Herb,2},{Water,3}},Array.Empty<int>(),catalog);
        check(ready.CanExecute&&ready.Gather.GetValueOrDefault(Herb)==1,"potion recipe reuses two owned herbs before planning only one missing herb");
        var site=new PotionSite();var job=new CraftChainJob(ready);job.Confirm(job.PreviewId,ready.Signature,DateTimeOffset.UtcNow);
        for(int i=0;i<30&&job.State==ChainState.Running;i++)job.Step(site);
        check(job.State==ChainState.Completed&&site.Potions==3&&site.Herbs==2&&site.Calls==1,"one blooming plant receipt supplies missing potion herb; surplus is retained without extra harvest");
        var boxPlan=CraftChainPlanner.Build(new[]{new ChainTarget(Potion,3)},new Dictionary<int,int>{{Herb,3},{Water,3}},Array.Empty<int>(),catalog);
        check(boxPlan.Gather.Count==0,"combined bag/chest stock avoids harvesting an already supplied herb");
        catalog.Ripe=false;var none=CraftChainPlanner.Build(new[]{new ChainTarget(Potion,3)},new Dictionary<int,int>{{Water,3}},Array.Empty<int>(),catalog);
        check(!none.CanExecute&&none.Manual[Herb]==3,"no blooming plant leaves an explicit manual potion deficit; seeds are not counted as grown herbs");
        var dry=new PotionSite{FakeHarvest=true};var noYield=new CraftChainJob(ready,maxTicks:4);noYield.Confirm(noYield.PreviewId,ready.Signature,DateTimeOffset.UtcNow);
        for(int i=0;i<5;i++)noYield.Step(dry);
        check(noYield.State==ChainState.Failed&&dry.Potions==0,"plant removal without actual herb inventory cannot advance the potion chain");
    }
    sealed class PotionCatalog:ICraftChainCatalog
    {
        internal bool Ripe=true;
        public IEnumerable<ChainRecipe> Recipes(int item)=>item==102?new[]{new ChainRecipe(1,102,1,new[]{new IngredientRequirement(100,1,new[]{100}),new IngredientRequirement(101,1,new[]{101})},Array.Empty<int>())}:Array.Empty<ChainRecipe>();
        public IEnumerable<int> StationItems(int tile)=>Array.Empty<int>();public bool Collectable(int item)=>item==100&&Ripe;
    }
    sealed class PotionSite:ICraftChainSite
    {
        internal int Herbs=2,Water=3,Potions,Calls;internal bool FakeHarvest;
        public bool Valid(out string reason){reason="";return true;}
        public int Stock(int item)=>item==100?Herbs:item==101?Water:Potions;
        public ChainGatherResult Collect(int item,out string message){message="blooming herb";Calls++;if(!FakeHarvest)Herbs+=3;return ChainGatherResult.Finished;}
        public bool Craft(ChainStep s,out string message){message="potions";if(Herbs<s.Count||Water<s.Count)return false;Herbs-=s.Count;Water-=s.Count;Potions+=s.Count;return true;}
        public bool Place(ChainStep s,out string message){message="not needed";return false;}
        public bool Deliver(IReadOnlyList<ChainTarget> t,out string message){message="delivered";return Potions>=3;}
        public bool Audit(IReadOnlyList<ChainTarget> t,out string message){message="actual potions";return Potions>=3;}
        public void StopGather(){}
    }
}
