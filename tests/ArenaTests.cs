using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class ArenaTests
{
    // Deliberately small fixture catalog, not a substitute for native recipes.
    const int Wood=1,Platform=2,Fire=3,Heart=4,IronSkin=5,Regen=6,Swift=7,Heal=8,
        Torch=9,Gel=10,Crystal=11,Chain=12,Bar=13,Ore=14,Water=15,Day=16,Mushroom=17,Blink=18,Cactus=19,Bottle=20,Bench=21;
    const int AtBench=100,AtBottle=101,AtAnvil=102;
    static ArenaLayout Layout()=>new(Platform,50,Wood,51,
        new(0,0,StepKind.Furniture,52,0,Fire,Width:3,Height:2,OriginX:1,OriginY:1),
        new(0,0,StepKind.Furniture,53,9,Heart,Width:1,Height:2),
        new[]{new ChainTarget(IronSkin,2),new ChainTarget(Regen,2),new ChainTarget(Swift,2),new ChainTarget(Heal,10)});
    sealed class Catalog:ICraftChainCatalog
    {
        internal bool SupportCycle;
        readonly List<ChainRecipe> _recipes=new();
        internal Catalog()
        {
            Add(Platform,2,-1,(Wood,1));Add(Bench,1,-1,(Wood,10));Add(Torch,3,-1,(Wood,1),(Gel,1));
            Add(Fire,1,-1,(Wood,10),(Torch,5));Add(Chain,15,AtAnvil,(Bar,1));Add(Heart,1,-1,(Crystal,1),(Chain,4));
            Add(IronSkin,1,AtBottle,(Water,1),(Day,1),(Ore,1));Add(Regen,1,AtBottle,(Water,1),(Day,1),(Mushroom,1));
            Add(Swift,1,AtBottle,(Water,1),(Blink,1),(Cactus,1));Add(Heal,2,AtBottle,(Mushroom,1),(Gel,2),(Bottle,2));
        }
        void Add(int item,int output,int station,params (int Item,int Count)[] ingredients)=>_recipes.Add(new(_recipes.Count,item,output,
            ingredients.Select(x=>new IngredientRequirement(x.Item,x.Count,new[]{x.Item})).ToArray(),station<0?Array.Empty<int>():new[]{station}));
        public IEnumerable<ChainRecipe> Recipes(int item)=>_recipes.Where(r=>r.Item==item);
        public IEnumerable<int> StationItems(int tile)=>tile==AtBench?new[]{Bench}:tile==AtBottle?new[]{Bottle}:Array.Empty<int>();
        public IEnumerable<int> SupportStations(int tile)=>tile==AtBottle?new[]{AtBench}:SupportCycle&&tile==AtBench?new[]{AtBottle}:Array.Empty<int>();
        public bool Collectable(int item)=>item is Wood or Ore;
    }
    static Dictionary<int,int> Materials()=>new(){[Wood]=200,[Gel]=14,[Crystal]=2,[Bar]=1,[Ore]=2,[Water]=6,[Day]=4,[Mushroom]=7,[Blink]=2,[Cactus]=2,[Bottle]=11};
    static ChainTarget[] Targets(ArenaLayout layout,Site site)=>layout.Targets(layout.Platforms(new(0,0),site.Platform.Inspect),layout.Fixtures.Steps.Select(site.Fixture.Inspect).ToArray()).ToArray();
    static (ArenaPreparationJob Job,Site Site) Setup(Dictionary<int,int>? bag=null,Dictionary<int,int>? box=null)
    {
        var layout=Layout();var site=new Site(layout,bag??Materials(),box??new());
        var plan=CraftChainPlanner.Build(Targets(layout,site),site.SupplyImpl.All(),new[]{AtAnvil},new Catalog());
        return (new(plan,layout.Platforms(new(0,0),site.Platform.Inspect),layout.Fixtures),site);
    }
    static void RunJob(ArenaPreparationJob job,Site site)
    {for(int i=0;i<2500&&job.State is not (ArenaState.Completed or ArenaState.Failed or ArenaState.Cancelled);i++)job.Step(site);}
    static void Confirm(ArenaPreparationJob job)=>job.Confirm(job.PreviewId,job.Supply.Plan.Signature,DateTimeOffset.UtcNow);
    internal static void Run(Action<bool,string> check)
    {
        foreach(string text in new[]{"帮我准备打克眼","准备打克苏鲁之眼","克眼战前准备","备战克眼","帮我建克眼竞技场"})
            check(ArenaRequest.Matches(text),"arena request: "+text);
        foreach(string text in new[]{"不要准备打克眼","以后准备打克眼","怎么准备打克眼？","准备打克眼要什么","取消克眼准备","帮我准备打骷髅王","保护我","铺三层平台"})
            check(!ArenaRequest.Matches(text),"question, cancellation or unrelated goal does not create arena: "+text);
        var layout=Layout();var scene=new Site(layout,new(),new());var targets=Targets(layout,scene);
        check(targets.Count()==8&&targets.Single(t=>t.Item==Platform).Count==300&&targets.Single(t=>t.Item==Wood).Count==8,"one combined bill contains platform, physical supports, four potions and both furniture types");
        check(layout.Ghost.Steps.SelectMany(s=>s.Footprint()).All(c=>c.X>=0&&c.X<100&&c.Y>=0&&c.Y<16),"all native origin offsets remain within arena footprint");
        var cells=layout.Fixtures.Steps.SelectMany(s=>s.Footprint()).ToHashSet();
        check(new[]{25,75}.All(x=>cells.Contains((x,7))&&cells.Contains((x,8))&&cells.Contains((x,9))&&Enumerable.Range(x-1,3).All(c=>cells.Contains((c,15)))),"heart lanterns have solid ceilings and campfires have three-cell plinths");
        scene.Platform.Cells.UnionWith(layout.Platforms(new(0,0),_=>PlatformCell.Empty).Slots.Take(100).Select(s=>s.At));
        foreach(var s in layout.Fixtures.Steps)scene.Fixture.Cells.UnionWith(s.Footprint());
        var reused=Targets(layout,scene);
        check(reused.Single(t=>t.Item==Platform).Count==200&&reused.Count()==5,"existing complete fixtures and platform row are deducted before supply planning");
        scene.Platform.Blocked=true;bool rejected=false;
        try{Targets(layout,scene);}catch(ArgumentException){rejected=true;}
        check(rejected,"terrain conflicts stop planning rather than request demolition");
        scene.Platform.Blocked=false;scene.Fixture.Blocked=true;rejected=false;
        try{Targets(layout,scene);}catch(ArgumentException){rejected=true;}
        check(rejected,"fixture obstacles cannot become clear-and-rebuild tasks");
        var (job,site)=Setup();
        check(job.Supply.Plan.CanExecute&&job.Supply.Plan.Gather.Count==0,"complete inventory plans without collection");
        check(job.Supply.Plan.Steps.Count(s=>s.Kind==ChainStepKind.Craft&&s.Item==Chain)==1&&job.Supply.Plan.Steps.Single(s=>s.Kind==ChainStepKind.Craft&&s.Item==Chain).Count==1,"native-sized chain batches share surplus between both heart lanterns");
        var placements=job.Supply.Plan.Steps.Where(s=>s.Kind==ChainStepKind.Place).ToArray();
        check(placements.Length==2&&placements[0].Station==AtBench&&placements[1].Station==AtBottle,"carried bottle still plans its support bench before bottle placement");
        check(job.Supply.Plan.Steps.Where(s=>s.Kind==ChainStepKind.Craft&&s.Item==Heal).Sum(s=>s.Count)==5,"ten healing potions use five two-output batches");
        job.Step(site);check(site.Effects==0&&job.State==ArenaState.Preview,"arena preview cannot collect, transfer, craft, place or audit");
        var now=DateTimeOffset.UtcNow;
        check(!job.Confirm(Guid.NewGuid(),job.Supply.Plan.Signature,now)&&!job.Confirm(job.PreviewId,"changed",now)&&!job.Confirm(job.PreviewId,job.Supply.Plan.Signature,job.Supply.Expires),"arena rejects wrong identifier, changed recipe signature and exact TTL boundary");
        var stamp=new ArenaPreviewStamp("bag+box","recipes","station coords","scene","conditions");
        foreach(var changed in new[]{stamp with{Stock="moved"},stamp with{Recipes="changed"},stamp with{Stations="moved"},stamp with{Scene="changed"},stamp with{Blocked="changed"}})
            check(!stamp.Matches(changed,now.AddMinutes(1),now),"arena freshness invalidates changed dependency: "+changed);
        check(stamp.Matches(stamp,now.AddSeconds(1),now)&&!stamp.Matches(stamp,now,now),"unchanged fresh scene accepted, expired preview rejected");
        Confirm(job);job.Step(site,true);check(site.Effects==0,"paused composite cannot advance even after confirmation");
        RunJob(job,site);
        check(job.State==ArenaState.Completed&&site.Platform.Cells.Count==300&&layout.Fixtures.Steps.All(s=>site.Fixture.Inspect(s)==CellState.Done),"confirmed composite executes supply, platforms, solid supports, complete fixtures and final audit");
        check(layout.Potions.All(t=>site.SupplyImpl.Bag.GetValueOrDefault(t.Item)==t.Count)&&site.SupplyImpl.Bag.GetValueOrDefault(Wood)==8&&site.SupplyImpl.Bag.GetValueOrDefault(Chain)==7,"retained potions and exact shared-material conservation survive construction");
        check(site.SupplyImpl.BoxSpent.Count==0&&site.Audits==1&&site.Fixture.Clears==0,"main bag priority, independent audit and no clearing in successful chain");
        var missing=Materials();missing.Remove(Crystal);missing.Remove(Day);
        var (blocked,blockedSite)=Setup(missing);
        check(!blocked.Supply.Plan.CanExecute&&blocked.Supply.Plan.Manual[Crystal]==2&&blocked.Supply.Plan.Manual[Day]==4,"life crystals and herbs list full manual shortages without synthetic substitute");
        check(!blocked.Confirm(blocked.PreviewId,blocked.Supply.Plan.Signature,now)&&blockedSite.Effects==0,"manual shortage prevents all execution including partial construction");
        var partial=Materials();partial[Wood]=40;var boxed=new Dictionary<int,int>{[Wood]=160};
        var (boxes,boxesSite)=Setup(partial,boxed);Confirm(boxes);RunJob(boxes,boxesSite);
        check(boxes.State==ArenaState.Completed&&boxesSite.SupplyImpl.BoxSpent.GetValueOrDefault(Wood)==152&&boxesSite.SupplyImpl.Gathered.Count==0,"nearby box materials satisfy residual need after forty bag wood, before gathering");
        var gathering=Materials();gathering[Wood]=190;
        var (gather,gatherSite)=Setup(gathering);Confirm(gather);gatherSite.SupplyImpl.Ask=true;gather.Step(gatherSite);
        for(int i=0;i<50&&!gather.Supply.Asking&&gather.State==ArenaState.Supplying;i++)gather.Step(gatherSite);
        check(gather.Supply.Asking&&gatherSite.Platform.Cells.Count==0&&gatherSite.SupplyImpl.Gathered.Count==0,"chain approval retains per-target gathering question and cannot start construction early");
        gatherSite.SupplyImpl.Ask=false;RunJob(gather,gatherSite);
        check(gather.State==ArenaState.Completed&&gatherSite.SupplyImpl.Gathered.GetValueOrDefault(Wood)==2,"only two missing wood are gathered within approved shared budget");
        foreach(ArenaState stopAt in new[]{ArenaState.Supplying,ArenaState.Platforms,ArenaState.Fixtures})
        {
            var (stop,stopSite)=Setup();Confirm(stop);for(int i=0;i<1000&&stop.State!=stopAt;i++)stop.Step(stopSite);
            stop.Cancel(stopSite,"player stopped");int effects=stopSite.Effects;stop.Step(stopSite);
            check(stop.State==ArenaState.Cancelled&&stopSite.Effects==effects&&stopSite.SupplyImpl.Stops>0,"stop preserves partial state with no later mutation at "+stopAt);
        }
        foreach(ArenaState invalidAt in new[]{ArenaState.Supplying,ArenaState.Platforms,ArenaState.Fixtures})
        {
            var (invalid,invalidSite)=Setup();Confirm(invalid);for(int i=0;i<1000&&invalid.State!=invalidAt;i++)invalid.Step(invalidSite);
            invalidSite.SupplyImpl.IsValid=false;int effects=invalidSite.Effects;invalid.Step(invalidSite);
            check(invalid.State==ArenaState.Failed&&invalidSite.Effects==effects,"world/player/chest/control invalidation stops next action at "+invalidAt);
        }
        foreach(string fault in new[]{"platform obstacle","fixture obstacle","lying fixture","disabled buff","removed potion","removed platform","audit rejects"})
        {
            var (bad,badSite)=Setup();Confirm(bad);
            for(int i=0;i<1000&&bad.State!=ArenaState.Platforms;i++)bad.Step(badSite);
            if(fault=="platform obstacle")badSite.Platform.Blocked=true;
            else
            {
                for(int i=0;i<1000&&bad.State!=ArenaState.Fixtures;i++)bad.Step(badSite);
                if(fault=="fixture obstacle")badSite.Fixture.Blocked=true;
                if(fault=="lying fixture")badSite.Fixture.Lie=true;
                if(fault=="disabled buff")badSite.Buffs=false;
                if(fault=="removed potion")badSite.SupplyImpl.Bag[Heal]=9;
                if(fault=="removed platform")badSite.Platform.Cells.Remove(new(0,0));
                if(fault=="audit rejects")badSite.RejectAudit=true;
            }
            RunJob(bad,badSite);
            check(bad.State==ArenaState.Failed&&badSite.Fixture.Clears==0,"no false arena success after "+fault);
        }
        foreach(int y in new[]{-1,0,17,18,35,36,54})check(ArenaBuffFrames.Campfire(y)==(y>=0&&y<36),"native campfire active frame range "+y);
        foreach(int y in new[]{0,323,324,341,342,358,359,360})check(ArenaBuffFrames.HeartLantern(y)==(y>=324&&y<=358),"native heart lantern active frame range "+y);
        var catalog=new Catalog{SupportCycle=true};rejected=false;
        try{CraftChainPlanner.Build(new[]{new ChainTarget(Heal,10)},Materials(),new[]{AtAnvil},catalog);}catch(ArgumentException){rejected=true;}
        check(rejected,"support station cycles terminate within bounded search");
        var call=AgentProtocol.Parse("""{"tool":"prepare_eye"}""");
        check(call.Ends&&new AgentLoop("帮我准备打克眼").OnModel("""{"tool":"prepare_eye"}""").Kind==AgentOutcome.Run,"model hands preparation to native program without continuing its own craft steps");
        var fake=new FakeWorld(new WorldSpec());var result=fake.Run(call,new AgentLoop("帮我准备打克眼"));
        check(result.Contains("等待确认")&&fake.Bag.Count==0,"offline evaluation explicitly produces a preview, never synthetic arena or potions");
        var random=new Random(221026);
        for(int trial=0;trial<40;trial++)
        {
            var generated=Layout();var bag=Materials();bag[Wood]=random.Next(120,230);
            var box=new Dictionary<int,int>{[Wood]=random.Next(0,80)};
            var generatedSite=new Site(generated,bag,box);
            var blueprint=generated.Platforms(new(0,0),_=>PlatformCell.Empty);
            foreach(var slot in blueprint.Slots.Where(_=>random.Next(4)==0))generatedSite.Platform.Cells.Add(slot.At);
            foreach(var step in generated.Fixtures.Steps.Where(_=>random.Next(3)==0))generatedSite.Fixture.Cells.UnionWith(step.Footprint());
            var generatedPlan=CraftChainPlanner.Build(Targets(generated,generatedSite),generatedSite.SupplyImpl.All(),new[]{AtAnvil},new Catalog());
            var generatedJob=new ArenaPreparationJob(generatedPlan,generated.Platforms(new(0,0),generatedSite.Platform.Inspect),generated.Fixtures);
            Confirm(generatedJob);RunJob(generatedJob,generatedSite);
            check(generatedJob.State==ArenaState.Completed&&generatedSite.Platform.Cells.Count==300&&generated.Potions.All(t=>generatedSite.SupplyImpl.Bag.GetValueOrDefault(t.Item)>=t.Count)
                &&generatedSite.SupplyImpl.Gathered.GetValueOrDefault(Wood)<=generatedPlan.Gather.GetValueOrDefault(Wood),"combined arena stock, reuse and batch conservation seed221026 case "+trial);
        }
    }
    sealed class SupplySite(Dictionary<int,int> bag,Dictionary<int,int> box):ICraftChainSite
    {
        internal Dictionary<int,int> Bag=new(bag),Box=new(box),BoxSpent=new(),Gathered=new();
        internal HashSet<int> Stations=new(){AtAnvil};internal bool IsValid=true,Ask;internal int Effects,Stops;
        internal Dictionary<int,int> All()=>Bag.Keys.Concat(Box.Keys).Distinct().ToDictionary(i=>i,Stock);
        public bool Valid(out string reason){reason="invalid context";return IsValid;}
        public int Stock(int item)=>Bag.GetValueOrDefault(item)+Box.GetValueOrDefault(item);
        bool Consume(int item,int n)
        {
            if(Stock(item)<n)return false;int take=Math.Min(n,Bag.GetValueOrDefault(item));Bag[item]=Bag.GetValueOrDefault(item)-take;
            int fromBox=n-take;Box[item]=Box.GetValueOrDefault(item)-fromBox;if(fromBox>0)BoxSpent[item]=BoxSpent.GetValueOrDefault(item)+fromBox;return true;
        }
        public ChainGatherResult Collect(int item,out string message)
        {message="gather";if(Ask)return ChainGatherResult.WaitingForApproval;Bag[item]=Bag.GetValueOrDefault(item)+1;Gathered[item]=Gathered.GetValueOrDefault(item)+1;Effects++;return ChainGatherResult.Finished;}
        public bool Craft(ChainStep s,out string message)
        {
            message="craft";if(s.Recipe!.Stations.Any(t=>!Stations.Contains(t))||s.Ingredients!.Any(x=>Stock(x.Key)<x.Value))return false;
            foreach(var cost in s.Ingredients!)Consume(cost.Key,cost.Value);
            Bag[s.Item]=Bag.GetValueOrDefault(s.Item)+s.Count*s.Recipe.Output;Effects++;return true;
        }
        public bool Place(ChainStep s,out string message)
        {message="station";if(s.Station==AtBottle&&!Stations.Contains(AtBench)||!Consume(s.Item,1))return false;Stations.Add(s.Station);Effects++;return true;}
        public bool Deliver(IReadOnlyList<ChainTarget> targets,out string message)
        {
            message="deliver";foreach(var t in targets){int need=Math.Max(0,t.Count-Bag.GetValueOrDefault(t.Item));if(Box.GetValueOrDefault(t.Item)<need)return false;Box[t.Item]=Box.GetValueOrDefault(t.Item)-need;Bag[t.Item]=Bag.GetValueOrDefault(t.Item)+need;if(need>0){Effects++;BoxSpent[t.Item]=BoxSpent.GetValueOrDefault(t.Item)+need;}}return true;
        }
        public bool Audit(IReadOnlyList<ChainTarget> targets,out string message){message="bag audit";return targets.All(t=>Bag.GetValueOrDefault(t.Item)>=t.Count);}
        public void StopGather()=>Stops++;
    }
    sealed class PlatformSite(SupplySite supply):IPlatformSite
    {
        internal HashSet<BuildPoint> Cells=new();internal bool Blocked;internal int Effects;
        public PlatformCell Inspect(BuildPoint p)=>Blocked?PlatformCell.Protected:Cells.Contains(p)?PlatformCell.Platform:PlatformCell.Empty;
        public int Available=>supply.Bag.GetValueOrDefault(Platform);
        public bool TryPlace(BuildPoint p,out string error){error="platform";if(Inspect(p)!=PlatformCell.Empty||Available<1)return false;supply.Bag[Platform]--;Cells.Add(p);Effects++;return true;}
    }
    sealed class FixtureSite(SupplySite supply):IStructureSite
    {
        internal HashSet<(int,int)> Cells=new();internal bool Blocked,Lie;internal int Effects,Clears;
        public CellState Inspect(StructureStep s)=>Blocked?CellState.Blocked:s.Footprint().All(Cells.Contains)?CellState.Done:CellState.Empty;
        public bool Clear(StructureStep s,out string error){Clears++;error="never clear";return false;}
        public bool Place(StructureStep s,out string error){error="fixture";if(supply.Bag.GetValueOrDefault(s.Item)<1)return false;supply.Bag[s.Item]--;Effects++;if(!Lie)Cells.UnionWith(s.Footprint());return true;}
    }
    sealed class Site:IArenaSite
    {
        internal SupplySite SupplyImpl;internal PlatformSite Platform;internal FixtureSite Fixture;
        readonly ArenaLayout _layout;internal bool Buffs=true,RejectAudit;internal int Audits;
        internal Site(ArenaLayout layout,Dictionary<int,int> bag,Dictionary<int,int> box){_layout=layout;SupplyImpl=new(bag,box);Platform=new(SupplyImpl);Fixture=new(SupplyImpl);}
        public ICraftChainSite Supply=>SupplyImpl;public IPlatformSite Platforms=>Platform;public IStructureSite Fixtures=>Fixture;
        internal int Effects=>SupplyImpl.Effects+Platform.Effects+Fixture.Effects;
        public bool Audit(out string reason){Audits++;reason="independent scene + potion audit";return !RejectAudit&&Buffs&&_layout.Platforms(new(0,0),Platform.Inspect).Audit(Platform.Inspect).Passed
            &&_layout.Fixtures.Steps.All(s=>Fixture.Inspect(s)==CellState.Done)&&_layout.Potions.All(t=>SupplyImpl.Bag.GetValueOrDefault(t.Item)>=t.Count);}
    }
}
