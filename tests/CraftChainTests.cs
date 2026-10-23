using TerrarAI.Content.Companion;

static class CraftChainTests
{
    const int Wood=1,Stone=2,Gel=3,Torch=4,Bench=5,Furnace=6,Ore=7,Bar=8,Anvil=9,Helmet=10,Mail=11,Greaves=12,Lead=13;
    const int AtBench=100,AtFurnace=101,AtAnvil=102;
    sealed class Catalog:ICraftChainCatalog
    {
        internal readonly List<ChainRecipe> All=new();
        internal readonly Dictionary<int,int[]> Stations=new(){[AtBench]=new[]{Bench},[AtFurnace]=new[]{Furnace},[AtAnvil]=new[]{Anvil}};
        internal readonly HashSet<int> Raw=new(){Wood,Stone,Ore};
        internal readonly Dictionary<int,int[]> Supports=new();
        public IEnumerable<ChainRecipe> Recipes(int item)=>All.Where(r=>r.Item==item);
        public IEnumerable<int> StationItems(int tile)=>Stations.GetValueOrDefault(tile)??Array.Empty<int>();
        public bool Collectable(int item)=>Raw.Contains(item);
        public IEnumerable<int> SupportStations(int tile)=>Supports.GetValueOrDefault(tile)??Array.Empty<int>();
        internal Catalog Add(int item,int output,int station,params (int Item,int Count)[] costs)
        {All.Add(new(All.Count,item,output,costs.Select(c=>new IngredientRequirement(c.Item,c.Count,new[]{c.Item})).ToArray(),station<0?Array.Empty<int>():new[]{station}));return this;}
    }
    static Catalog Iron()=>new Catalog().Add(Bench,1,-1,(Wood,10)).Add(Torch,3,-1,(Wood,1),(Gel,1))
        .Add(Furnace,1,AtBench,(Stone,20),(Wood,4),(Torch,3)).Add(Bar,1,AtFurnace,(Ore,3)).Add(Anvil,1,AtBench,(Bar,5))
        .Add(Helmet,1,AtAnvil,(Bar,20)).Add(Mail,1,AtAnvil,(Bar,30)).Add(Greaves,1,AtAnvil,(Bar,25));
    static readonly ChainTarget[] Suit={new(Helmet,1),new(Mail,1),new(Greaves,1)};
    static Dictionary<int,int> Stock(params (int Item,int Count)[] items)=>items.ToDictionary(x=>x.Item,x=>x.Count);
    static CraftChainPlan Plan(Catalog c,IReadOnlyDictionary<int,int>? stock=null,IEnumerable<int>? stations=null,params ChainTarget[] targets)
        =>CraftChainPlanner.Build(targets.Length==0?Suit:targets,stock??new Dictionary<int,int>(),stations??Array.Empty<int>(),c);
    internal static void Run(Action<bool,string> check)
    {
        foreach(string text in new[]{"做一套铁盔甲","帮我制作1套铁护甲","规划合成一套铁盔甲"})
            check(CraftChainRequest.TryParse(text,out var r,out _)&&r?.IronSet==true,"iron set becomes three concrete targets: "+text);
        foreach(string text in new[]{"规划合成2个熔炉","制作两个熔炉","合成２个熔炉"})
            check(CraftChainRequest.TryParse(text,out var r,out _)&&r==new CraftChainRequest("熔炉",2),"craft target retains exact quantity: "+text);
        foreach(string text in new[]{"怎么做铁盔甲","不要做一套铁盔甲","以后做一套铁盔甲","做一套铁盔甲吗？","做铁盔甲要多少材料"})
            check(!CraftChainRequest.TryParse(text,out _,out _),"questions and preferences cannot start crafting: "+text);
        foreach(string text in new[]{"合成0个熔炉","合成-1个熔炉","合成1.5个熔炉","合成1000个熔炉","做一套金盔甲","做熔炉然后再做铁砧","合成2个铁盔甲"})
            check(CraftChainRequest.TryParse(text,out var r,out var error)&&r==null&&error.Length>0,"invalid or compound craft target rejected: "+text);
        var c=Iron();var stock=Stock((Gel,1));var plan=Plan(c,stock);
        check(!CraftChainRequest.TryParse("帮我做个工作台放旁边",out _,out _),"existing craft-and-place request keeps its established route");
        check(CraftChainRequest.TryParse("帮我做个工作台",out var one,out _)&&one==new CraftChainRequest("工作台"),"single classifier without number means one item");
        check(plan.CanExecute&&plan.Gather.GetValueOrDefault(Ore)==240&&plan.Gather.GetValueOrDefault(Wood)==15&&plan.Gather.GetValueOrDefault(Stone)==20,
            "iron suit includes 75 armor bars plus 5 anvil bars, full furnace and bench raw costs");
        check(stock.Count==1&&stock[Gel]==1&&plan.Steps.Count(s=>s.Kind==ChainStepKind.Place)==3,"preview is read-only and places each shared station once");
        check(plan.Steps.Where(s=>s.Kind==ChainStepKind.Craft&&s.Item==Bar).Sum(s=>s.Count)==80,"intermediate bar batches include station material");
        check(plan.Steps.Where(s=>s.Kind==ChainStepKind.Craft&&s.Item==Torch).Sum(s=>s.Count)==1,"batch output feeds three-torch furnace demand");
        var blocked=Plan(c);
        check(!blocked.CanExecute&&blocked.Manual.Count==1&&blocked.Manual[Gel]==1&&blocked.Gather[Ore]==240,"unsupported monster drop appears as manual shortage and blocks confirmation");
        var readyStations=new[]{AtBench,AtFurnace,AtAnvil};
        var partly=Plan(c,Stock((Helmet,1),(Bar,30),(Ore,15)),readyStations);
        check(partly.CanExecute&&partly.Gather.Count==1&&partly.Gather[Ore]==60&&!partly.Steps.Any(s=>s.Kind==ChainStepKind.Place),
            "owned armor, boxed intermediates, ore and existing stations reduce exact demand before gathering");
        var owned=Plan(c,Stock((Helmet,1),(Mail,1),(Greaves,1)));
        check(owned.CanExecute&&owned.Steps.Count==0,"all owned targets need only delivery and final audit");
        var grouped=new Catalog();grouped.All.Add(new(0,50,1,new[]{new IngredientRequirement(Bar,2,new[]{Bar,Lead}),new IngredientRequirement(Lead,1,new[]{Lead})},Array.Empty<int>()));
        var groups=Plan(grouped,Stock((Bar,1),(Lead,2)),targets:new[]{new ChainTarget(50,1)});
        check(groups.CanExecute&&groups.Steps.Single().Ingredients![Lead]==2&&groups.Steps.Single().Ingredients![Bar]==1,"overlapping groups share the existing max-flow allocator without double spending");
        var surplus=new Catalog().Add(20,3,-1,(Wood,1)).Add(21,1,-1,(20,2)).Add(22,1,-1,(20,1));
        var batch=Plan(surplus,targets:new[]{new ChainTarget(21,1),new ChainTarget(22,1)});
        check(batch.Gather[Wood]==1&&batch.Steps.Count(s=>s.Kind==ChainStepKind.Craft&&s.Item==20)==1,"batch surplus is shared across different final targets");
        var alternatives=new Catalog().Add(30,1,-1,(99,1)).Add(30,1,-1,(Wood,2));
        check(Plan(alternatives,targets:new[]{new ChainTarget(30,1)}).CanExecute,"planner chooses supported recipe instead of an unsupported dependency");
        var cycle=new Catalog().Add(31,1,-1,(32,1)).Add(32,1,-1,(31,1));
        var recycled=Plan(cycle,targets:new[]{new ChainTarget(31,1)});bool rejected=false;
        check(!recycled.CanExecute&&recycled.Manual.GetValueOrDefault(31)==1&&recycled.Steps.Count==0,
            "pure recycling cycles require manual supply and cannot produce an executable plan");
        ArenaRegression(check);
        NativeRecipeProjection(check);
        foreach(var targets in new[]{new[]{new ChainTarget(Helmet,0)},new[]{new ChainTarget(Helmet,1000)},new[]{new ChainTarget(Helmet,1),new ChainTarget(Helmet,2)}})
        {
            rejected=false;try{Plan(c,targets:targets);}catch(ArgumentException){rejected=true;}check(rejected,"invalid target list cannot reach execution");
        }
        var now=DateTimeOffset.UtcNow;var job=new CraftChainJob(plan,now);var site=new Site(Stock((Gel,1)));
        job.Step(site);check(site.Effects==0&&job.Cursor==0,"preview ticks never collect, craft, place or deliver");
        check(!job.Confirm(Guid.NewGuid(),plan.Signature,now)&&!job.Confirm(job.PreviewId,plan.Signature+"changed",now)&&!job.Confirm(job.PreviewId,plan.Signature,now.AddMinutes(2)),"wrong, stale and expired confirmations cannot execute");
        var blockedJob=new CraftChainJob(blocked,now);
        check(!blockedJob.Confirm(blockedJob.PreviewId,blocked.Signature,now),"manual shortages cannot be confirmed");
        check(job.Confirm(job.PreviewId,plan.Signature,now)&&site.Effects==0,"confirming schedules execution without immediate mutation");
        RunJob(job,site);
        check(job.State==ChainState.Completed&&Suit.All(t=>site.BagCount(t.Item)==1)&&site.Stations.SetEquals(readyStations),"full iron chain executes collection, real material transactions, three stations, three pieces and final audit");
        check(site.Gathered.GetValueOrDefault(Ore)==240&&site.Gathered.GetValueOrDefault(Wood)==15&&site.Gathered.GetValueOrDefault(Stone)==20&&site.BagCount(Bar)==0,
            "full chain obeys confirmed raw budget and consumes exactly 80 bars");
        var reusedJob=new CraftChainJob(partly);var reusedSite=new Site(Stock((Helmet,1)),Stock((Bar,30),(Ore,15)),readyStations);
        reusedJob.Confirm(reusedJob.PreviewId,partly.Signature,now);RunJob(reusedJob,reusedSite);
        check(reusedJob.State==ChainState.Completed&&reusedSite.BoxSpent[Bar]==30&&reusedSite.BoxSpent[Ore]==15&&reusedSite.Gathered[Ore]==60,"confirmed chain consumes boxed intermediates before gathering and delivers actual armor");
        var bagFirst=Plan(c,Stock((Bar,75)),readyStations);var bagJob=new CraftChainJob(bagFirst);var bagSite=new Site(Stock((Bar,75)),Stock((Bar,40)),readyStations);
        bagJob.Confirm(bagJob.PreviewId,bagFirst.Signature,now);RunJob(bagJob,bagSite);
        check(bagJob.State==ChainState.Completed&&bagSite.BoxSpent.Count==0,"main bag takes priority over nearby boxes");
        var deliverJob=new CraftChainJob(owned);var deliverSite=new Site(new(),Stock((Helmet,1),(Mail,1),(Greaves,1)));
        deliverJob.Confirm(deliverJob.PreviewId,owned.Signature,now);RunJob(deliverJob,deliverSite);
        check(deliverJob.State==ChainState.Completed&&Suit.All(t=>deliverSite.BagCount(t.Item)==1),"already owned boxed targets are transferred and audited, not recrafted");
        var woodPlan=Plan(new Catalog().Add(40,1,-1,(Wood,3)),targets:new[]{new ChainTarget(40,1)});
        var fake=new Site(new()){FakeGather=true};var fakeJob=new CraftChainJob(woodPlan,maxTicks:3);
        fakeJob.Confirm(fakeJob.PreviewId,woodPlan.Signature,now);for(int i=0;i<4;i++)fakeJob.Step(fake);
        check(fakeJob.State==ChainState.Failed&&fakeJob.Cursor==0&&fake.Crafts==0,"reported harvest success without inventory cannot advance and eventually times out");
        var over=new CraftChainJob(woodPlan);var missingSite=new Site(new());over.Confirm(over.PreviewId,woodPlan.Signature,now);over.Step(missingSite);
        missingSite.Remove(Wood,1);over.Step(missingSite);
        check(over.State==ChainState.Failed&&missingSite.Gathered[Wood]==1,"external material loss cannot silently expand the approved collection budget");
        var treeJob=new CraftChainJob(woodPlan);var treeSite=new Site(new()){Yield=10};treeJob.Confirm(treeJob.PreviewId,woodPlan.Signature,now);RunJob(treeJob,treeSite);
        check(treeJob.State==ChainState.Completed&&treeSite.GatherCalls==1&&treeSite.BagCount(Wood)==7,"whole-tree surplus remains in inventory and skips further collection");
        var stopped=new CraftChainJob(woodPlan);var stopSite=new Site(new());stopped.Confirm(stopped.PreviewId,woodPlan.Signature,now);stopped.Step(stopSite);stopped.Cancel(stopSite,"stop");int effects=stopSite.Effects;stopped.Step(stopSite);
        check(stopped.State==ChainState.Cancelled&&stopSite.Effects==effects&&stopSite.BagCount(Wood)==1,"stop keeps partial materials and prevents all later steps");
        var invalid=new CraftChainJob(woodPlan);var invalidSite=new Site(new()){IsValid=false};invalid.Confirm(invalid.PreviewId,woodPlan.Signature,now);invalid.Step(invalidSite);
        check(invalid.State==ChainState.Failed&&invalidSite.Effects==0,"world/control invalidation stops before any effects");
        var failPlan=Plan(new Catalog().Add(40,1,-1,(Wood,3)),Stock((Wood,3)),targets:new[]{new ChainTarget(40,1)});
        var failJob=new CraftChainJob(failPlan);var full=new Site(Stock((Wood,3))){RejectCraft=true};failJob.Confirm(failJob.PreviewId,failPlan.Signature,now);failJob.Step(full);
        check(failJob.State==ChainState.Failed&&full.BagCount(Wood)==3&&full.BagCount(40)==0,"rejected crafting transaction keeps ingredients and cannot advance");
        var auditJob=new CraftChainJob(owned);var auditSite=new Site(Stock((Helmet,1),(Mail,1),(Greaves,1))){RejectAudit=true};auditJob.Confirm(auditJob.PreviewId,owned.Signature,now);auditJob.Step(auditSite);
        check(auditJob.State==ChainState.Failed,"success requires independent final bag audit");
        var askJob=new CraftChainJob(woodPlan);var askSite=new Site(new()){Ask=true};askJob.Confirm(askJob.PreviewId,woodPlan.Signature,now);askJob.Step(askSite);
        check(askJob.Asking&&askJob.Cursor==0&&askSite.Gathered.Count==0,"whole-chain confirmation preserves per-target harvest approval");
        askSite.Ask=false;RunJob(askJob,askSite);check(askJob.State==ChainState.Completed,"approved harvest resumes the same execution chain");
        check(AgentProtocol.Parse("""{"tool":"craft_chain","item":"铁盔甲"}""").Ends,"model chain tool hands off to confirmation rather than continuing model steps");
        foreach(string raw in new[]{"""{"tool":"craft_chain"}""","""{"tool":"craft_chain","item":"熔炉","count":1000}""","""{"tool":"craft_chain","item":"熔炉","count":1.5}"""})
        {rejected=false;try{AgentProtocol.Parse(raw);}catch(FormatException){rejected=true;}check(rejected,"model cannot supply missing target or truncate invalid chain count");}
        // Random acyclic recipes exercise shared batch surplus and several final goals.
        var random=new Random(181026);
        for(int trial=0;trial<80;trial++)
        {
            var dag=new Catalog();
            for(int type=20;type<26;type++)
            {
                int input=type==20||random.Next(3)==0?Wood:random.Next(20,type);
                dag.Add(type,random.Next(1,5),-1,(input,random.Next(1,5)));
            }
            var ownedStock=Stock((Wood,random.Next(0,12)),(21,random.Next(0,5)));
            var goals=new[]{new ChainTarget(24,random.Next(1,4)),new ChainTarget(25,random.Next(1,4))};
            var generated=Plan(dag,ownedStock,targets:goals);var generatedSite=new Site(ownedStock);var generatedJob=new CraftChainJob(generated);
            generatedJob.Confirm(generatedJob.PreviewId,generated.Signature,DateTimeOffset.UtcNow);RunJob(generatedJob,generatedSite);
            check(generatedJob.State==ChainState.Completed&&goals.All(t=>generatedSite.BagCount(t.Item)>=t.Count)&&
                generatedSite.Gathered.GetValueOrDefault(Wood)<=generated.Gather.GetValueOrDefault(Wood),"acyclic recipe conservation and verified completion, seed181026 case "+trial);
        }
    }
    static void NativeRecipeProjection(Action<bool,string> check)
    {
        using var data=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures/arena-native-projection.json")));
        var d=data.RootElement;var names=new Dictionary<string,int>();
        int Id(string n){if(!names.TryGetValue(n,out int id))names[n]=id=names.Count+1;return id;}
        var c=new Catalog();c.Raw.Clear();c.Stations.Clear();
        foreach(var n in d.GetProperty("raw").EnumerateArray())c.Raw.Add(Id(n.GetString()!));
        foreach(var s in d.GetProperty("stations").EnumerateObject())c.Stations[Id("@"+s.Name)]=s.Value.EnumerateArray().Select(x=>Id(x.GetString()!)).ToArray();
        c.Supports[Id("@玻璃瓶")]=new[]{Id("@工作台")};
        foreach(var recipe in d.GetProperty("recipes").EnumerateArray())
        {
            if(c.Raw.Contains(Id(recipe.GetProperty("item").GetString()!)))continue;
            var requirements=recipe.GetProperty("ingredients").EnumerateArray().Select(i=>{
                string n=i[0].GetString()!;var accepted=d.GetProperty("groups").TryGetProperty(n,out var members)?
                    members.EnumerateArray().Select(x=>Id(x.GetString()!)).ToArray():new[]{Id(n)};
                return new IngredientRequirement(accepted[0],i[1].GetInt32(),accepted);
            }).ToArray();
            c.All.Add(new(recipe.GetProperty("id").GetInt32(),Id(recipe.GetProperty("item").GetString()!),recipe.GetProperty("output").GetInt32(),
                requirements,recipe.GetProperty("stations").EnumerateArray().Select(s=>Id("@"+s.GetString())).ToArray()));
        }
        var targets=d.GetProperty("goals").EnumerateObject().Select(g=>new ChainTarget(Id(g.Name),g.Value.GetInt32())).ToArray();
        var stock=d.GetProperty("stock").EnumerateObject().ToDictionary(s=>Id(s.Name),s=>s.Value.GetInt32());
        var plan=Plan(c,stock,targets:targets);
        check(!plan.CanExecute&&plan.Manual.GetValueOrDefault(Id("生命水晶"))==2&&plan.Manual.GetValueOrDefault(Id("水瓶"))==6,
            "84 exported native recipe dependencies produce a complete arena shortage bill despite recycling loops");
        foreach(var (item,count) in plan.Manual)stock[item]=stock.GetValueOrDefault(item)+count;
        // Execute the exact bill/recipe choices just checked, with the manual
        // supply present. A new preview may select different station variants.
        var ready=new CraftChainPlan(plan.Targets,plan.Steps,new Dictionary<int,int>());var job=new CraftChainJob(ready);var site=new Site(stock);
        job.Confirm(job.PreviewId,ready.Signature,DateTimeOffset.UtcNow);RunJob(job,site);
        check(ready.CanExecute&&job.State==ChainState.Completed&&targets.All(t=>site.BagCount(t.Item)>=t.Count),
            "native recipe projection can execute after its exact manual bill is supplied, without manufacturing missing water or crystals");
    }
    static void ArenaRegression(Action<bool,string> check)
    {
        // Representative 1.4.4.9 arena dependencies from the demo's game-data export.
        // IDs are test-local; water-bottle recipe is unavailable in this dry scene.
        const int Platform=200,Fire=201,Lantern=202,Crystal=203,Chain=204,Glass=205,GlassPlatform=206,GlassWall=207,
            Sand=208,Bottle=209,Water=210,Daybloom=211,Blinkroot=212,Mushroom=213,Cactus=214,
            IronSkin=215,Regen=216,Swift=217,Healing=218,AtBottles=105;
        var c=Iron().Add(Platform,2,-1,(Wood,1)).Add(Fire,1,-1,(Wood,10),(Torch,5))
            .Add(Lantern,1,-1,(Crystal,1),(Chain,4)).Add(Chain,15,AtAnvil,(Bar,1))
            .Add(Glass,1,-1,(GlassPlatform,2)).Add(Glass,1,AtFurnace,(Sand,2)).Add(Glass,1,AtBench,(GlassWall,4))
            .Add(GlassPlatform,2,-1,(Glass,1)).Add(GlassWall,4,AtBench,(Glass,1)).Add(Bottle,2,AtFurnace,(Glass,1))
            .Add(IronSkin,1,AtBottles,(Water,1),(Daybloom,1),(Ore,1))
            .Add(Regen,1,AtBottles,(Water,1),(Daybloom,1),(Mushroom,1))
            .Add(Swift,1,AtBottles,(Water,1),(Blinkroot,1),(Cactus,1))
            .Add(Healing,2,AtBottles,(Mushroom,1),(Gel,2),(Bottle,2));
        c.Raw.UnionWith(new[]{Daybloom,Blinkroot});c.Stations[AtBottles]=new[]{Bottle,230,231,232};c.Supports[AtBottles]=new[]{AtBench};
        foreach(var pair in new[]{(AtBench,Bench),(AtFurnace,Furnace),(AtAnvil,Anvil)})
        {
            c.Stations[pair.Item1]=new[]{pair.Item2,240+pair.Item1,250+pair.Item1,260+pair.Item1};
            foreach(int variant in c.Stations[pair.Item1].Skip(1))c.Add(variant,1,AtBench,(Wood,20),(99,1));
        }
        // Any Wood has eight members; the default is gatherable, other variants aren't.
        for(int i=0;i<c.All.Count;i++)c.All[i]=c.All[i] with{Ingredients=c.All[i].Ingredients.Select(r=>
            r.Type==Wood?r with{Accepted=new[]{Wood,301,302,303,304,305,306,307}}:r).ToArray()};
        var goals=new[]{new ChainTarget(Platform,240),new(Fire,2),new(Lantern,2),new(IronSkin,2),new(Regen,2),new(Swift,2),new(Healing,10)};
        var plan=Plan(c,Stock((Wood,400),(Gel,40),(Stone,40)),targets:goals);
        check(!plan.CanExecute&&plan.Manual[Crystal]==2&&plan.Manual[Water]==6&&plan.Manual[Mushroom]==7&&plan.Manual[Cactus]==2,
            "full arena yields exact manual shortages instead of exhausting search across glass cycles and station variants");
        check(plan.Manual.GetValueOrDefault(Sand)==12&&plan.Manual.Keys.All(i=>i is Crystal or Water or Mushroom or Cactus or Sand),
            "arena bill uses real raw glass ingredients and never invents unavailable station variants");
        var allStock=Stock((Wood,400),(Gel,40),(Stone,40),(Crystal,2),(Water,6),(Mushroom,7),(Cactus,2),(Sand,12));
        var ready=Plan(c,allStock,targets:goals);var site=new Site(allStock);var job=new CraftChainJob(ready);
        job.Confirm(job.PreviewId,ready.Signature,DateTimeOffset.UtcNow);RunJob(job,site);
        check(ready.CanExecute&&job.State==ChainState.Completed&&goals.All(t=>site.BagCount(t.Item)>=t.Count),
            "stocked arena chain actually executes its recipes and station placements and passes independent final inventory audit");
    }
    static void RunJob(CraftChainJob job,Site site){for(int i=0;i<2000&&job.State==ChainState.Running;i++)job.Step(site);}
    sealed class Stack(int type=0,int count=0){internal int Type=type,Count=count;}
    sealed class Items:IContainerItems<Stack>
    {
        public Stack Clone(Stack s)=>new(s.Type,s.Count);public int Type(Stack s)=>s.Count>0?s.Type:0;
        public int Count(Stack s)=>s.Count;public int Limit(Stack s)=>9999;public bool Favorite(Stack s)=>false;
        public bool CanStack(Stack a,Stack b)=>a.Type==b.Type;
        public Stack Split(Stack s,int n){s.Count-=n;return new(s.Type,n);}public int Stack(Stack a,Stack b,int n){a.Count+=n;b.Count-=n;return n;}
        public void Consume(Stack s,int n)=>s.Count-=n;
    }
    sealed class Site:ICraftChainSite
    {
        Stack[] _bag,_box;
        internal readonly HashSet<int> Stations;
        internal Dictionary<int,int> Gathered=new(),BoxSpent=new();
        internal int Effects,Crafts,GatherCalls,Yield=1;
        internal bool IsValid=true,FakeGather,RejectCraft,RejectAudit,Ask;
        internal Site(Dictionary<int,int> bag,Dictionary<int,int>? box=null,IEnumerable<int>? stations=null)
        {_bag=Slots(bag);_box=Slots(box??new());Stations=(stations??Array.Empty<int>()).ToHashSet();}
        static Stack[] Slots(Dictionary<int,int> stock)=>stock.Select(x=>new Stack(x.Key,x.Value)).Concat(Enumerable.Range(0,50-stock.Count).Select(_=>new Stack())).ToArray();
        internal int BagCount(int item)=>_bag.Where(s=>s.Type==item).Sum(s=>s.Count);
        public bool Valid(out string reason){reason="invalid scope";return IsValid;}
        public int Stock(int item)=>_bag.Concat(_box).Where(s=>s.Type==item).Sum(s=>s.Count);
        internal void Remove(int item,int count){foreach(var s in _bag.Where(s=>s.Type==item)){int n=Math.Min(s.Count,count);s.Count-=n;count-=n;}}
        public ChainGatherResult Collect(int item,out string message)
        {
            message="gather";if(Ask)return ChainGatherResult.WaitingForApproval;if(FakeGather)return ChainGatherResult.Finished;
            GatherCalls++;var tx=Tx();if(!tx.Insert(new Stack(item,Yield)))return ChainGatherResult.Failed;
            Commit(tx);Gathered[item]=Gathered.GetValueOrDefault(item)+Yield;return ChainGatherResult.Finished;
        }
        ContainerInventory<Stack> Tx()=>new(_bag,new[]{_box},new Items());
        void Commit(ContainerInventory<Stack> tx){_bag=tx.Bag.ToArray();_box=tx.Boxes.Single().ToArray();Effects++;foreach(var receipt in tx.Receipts)BoxSpent[receipt.Type]=BoxSpent.GetValueOrDefault(receipt.Type)+receipt.Count;}
        public bool Craft(ChainStep s,out string message)
        {
            message="craft rejected";if(RejectCraft||s.Recipe!.Stations.Any(t=>!Stations.Contains(t)))return false;
            var requirements=s.Ingredients!.Select(x=>new IngredientRequirement(x.Key,x.Value,new[]{x.Key})).ToArray();
            var tx=Tx();if(!tx.Craft(requirements,new Stack(s.Item,s.Count*s.Recipe.Output),out message,out _))return false;
            Commit(tx);Crafts++;return true;
        }
        public bool Place(ChainStep s,out string message)
        {if(!Deliver(new[]{new ChainTarget(s.Item,1)},out message))return false;Remove(s.Item,1);Stations.Add(s.Station);Effects++;return true;}
        public bool Deliver(IReadOnlyList<ChainTarget> targets,out string message)
        {
            var orders=targets.Select(t=>new ContainerOrder(t.Item,Math.Max(0,t.Count-BagCount(t.Item)))).Where(t=>t.Count>0).ToArray();
            message="deliver";if(orders.Length==0)return true;var tx=Tx();if(!tx.Transfer(false,orders,out message))return false;Commit(tx);return true;
        }
        public bool Audit(IReadOnlyList<ChainTarget> targets,out string message){message="audited";return !RejectAudit&&targets.All(t=>BagCount(t.Item)>=t.Count);}
        public void StopGather(){}
    }
}
