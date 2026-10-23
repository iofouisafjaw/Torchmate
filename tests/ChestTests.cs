using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class ChestTests
{
    private sealed class Stack(int type=0,int count=0,int limit=10,bool favorite=false,string data="a")
    {public int Type=type,Count=count,Limit=limit;public bool Favorite=favorite;public string Data=data;}
    private sealed class Items:IContainerItems<Stack>
    {
        public Stack Clone(Stack s)=>new(s.Type,s.Count,s.Limit,s.Favorite,s.Data);
        public int Type(Stack s)=>s.Count==0?0:s.Type;
        public int Count(Stack s)=>s.Count;
        public int Limit(Stack s)=>s.Limit;
        public bool Favorite(Stack s)=>s.Favorite;
        public bool CanStack(Stack d,Stack s)=>d.Type==s.Type&&d.Data==s.Data;
        public Stack Split(Stack s,int n){var d=Clone(s);d.Count=n;s.Count-=n;return d;}
        public int Stack(Stack d,Stack s,int n){d.Count+=n;s.Count-=n;return n;}
        public void Consume(Stack s,int n){s.Count-=n;}
    }
    private static ContainerInventory<Stack> Tx(Stack[] bag,params Stack[][] boxes)=>new(bag,boxes,new Items());
    private static int Total(ContainerInventory<Stack> tx,int type)=>tx.Bag.Concat(tx.Boxes.SelectMany(b=>b)).Where(s=>s.Type==type).Sum(s=>s.Count);
    internal static void Run(Action<bool,string> check)
    {
        foreach(var text in new[]{"把20个木材存进附近箱子","帮我存20个木材到附近箱子","存入附近箱子20个木材","把木材×20放进箱子"})
        {
            bool ok=ChestRequest.TryParse(text,out var r,out _);
            check(ok&&r?.Operation==ChestOperation.Deposit&&r.Items.Single()==new ChestItemRequest("木材",20),"exact deposit quantity parsed: "+text);
        }
        foreach(var text in new[]{"把木头都存进附近箱子","把全部木头存入箱子"})
            check(ChestRequest.TryParse(text,out var r,out _)&&r?.Items.Single()==new ChestItemRequest("木头",0),"all deposit does not invent a quantity: "+text);
        check(ChestRequest.TryParse("把20个木材和石块存进附近箱子",out var many,out _)&&many?.Items.Count==2&&many.Items[0].Count==20&&many.Items[1].Count==0,
            "batch deposit retains per-item quantities and all-stock semantics");
        foreach(var text in new[]{"从附近箱子取5个铁锭","去箱子里拿五个铁锭","帮我从附近的箱子里取５个铁锭"})
            check(ChestRequest.TryParse(text,out var r,out _)&&r?.Operation==ChestOperation.Withdraw&&r.Items.Single()==new ChestItemRequest("铁锭",5),"exact withdrawal parsed: "+text);
        check(ChestRequest.TryParse("查附近箱子里的铁锭",out var query,out _)&&query?.Operation==ChestOperation.Query&&query.Items.Single().Name=="铁锭","chest filtered lookup is read-only");
        check(ChestRequest.TryParse("附近箱子有什么",out var all,out _)&&all?.Operation==ChestOperation.Query&&all.Items.Count==0,"chest overview is read-only");
        foreach(var text in new[]{"从箱子取铁锭","从箱子取0个铁锭","从箱子取-1个铁锭","从箱子取1.5个铁锭","从箱子取10000个铁锭","从箱子取5组铁锭","把木材和木材存进箱子","从箱子取5个铁锭来做砧"})
            check(ChestRequest.TryParse(text,out var r,out string error)&&r==null&&error.Length>0,"ambiguous or invalid chest operation stops before transfer: "+text);
        foreach(var text in new[]{"不要把木材存进箱子","怎么从箱子里取5个铁锭","以后木材放箱子","整理背包","帮我做个工作台","把20个木材存入箱子吗？"})
            check(!ChestRequest.TryParse(text,out _,out _),"preferences, questions and unrelated goals do not directly transfer: "+text);
        check(ChestRequest.TryParse("把再生药水存入箱子",out var potion,out _)&&potion?.Items.Single().Name=="再生药水","item names containing 再 are not treated as another action");
        check(ChestRequest.TryParse("把金箱存入箱子",out var chestItem,out _)&&chestItem?.Items.Single().Name=="金箱","a chest item can itself be deposited");
        int Resolve(string n)=>n is "木材" or "木头"?9:n=="铁锭"?22:0;
        check(ChestRequest.Authorizes("从附近箱子取5个铁锭",ChestOperation.Withdraw,"铁锭",5,Array.Empty<AgentStep>(),Resolve),"model transfer must match a specific human order");
        check(!ChestRequest.Authorizes("从附近箱子取5个铁锭",ChestOperation.Withdraw,"铁锭",6,Array.Empty<AgentStep>(),Resolve)&&
              !ChestRequest.Authorizes("查附近箱子里的铁锭",ChestOperation.Withdraw,"铁锭",5,Array.Empty<AgentStep>(),Resolve),"model cannot increase a quantity or turn a lookup into withdrawal");
        check(!ChestRequest.Authorizes("把木头存进箱子",ChestOperation.Deposit,"木材",0,new[]{new AgentStep("存入箱子：木头","存好了：木头×20","chest_put")},Resolve),
            "an alias cannot authorize a second transfer after the original order succeeded");
        check(!ChestRequest.Authorizes("帮我做个工作台",ChestOperation.Withdraw,"木材",10,Array.Empty<AgentStep>(),Resolve),"craft goal does not authorize a model to make a separate arbitrary withdrawal");
        check(AgentProtocol.Parse("{\"tool\":\"chest_take\",\"item\":\"木材\",\"count\":1000}").Count==1000,"model chest counts above 999 are preserved rather than clamped");
        check(AgentProtocol.Parse("{\"tool\":\"chest_put\",\"item\":\"木材\",\"count\":0}").Count==0&&AgentProtocol.IsLookup("chests"),"deposit all and chest lookups have distinct tool semantics");
        foreach(var json in new[]{"{\"tool\":\"chest_take\",\"item\":\"铁锭\"}","{\"tool\":\"chest_take\",\"item\":\"铁锭\",\"count\":0}",
            "{\"tool\":\"chest_take\",\"item\":\"铁锭\",\"count\":1.5}","{\"tool\":\"chest_put\",\"item\":\"木材\",\"count\":10000}"})
        {bool rejected=false;try{AgentProtocol.Parse(json);}catch(FormatException){rejected=true;}check(rejected,"invalid model chest count is rejected");}

        var original=new[]{new Stack(9,6),new Stack(9,4,favorite:true),new Stack(1,8)};
        var tx=Tx(original,new[]{new Stack(9,8),new Stack()},new[]{new Stack(9,2,data:"b"),new Stack()});
        check(tx.Transfer(true,new[]{new ContainerOrder(9,0)},out _)&&tx.Bag[0].Count==0&&tx.Bag[1].Count==4&&tx.Bag[1].Favorite,
            "deposit all skips favorited stacks");
        check(tx.Boxes[0][0].Count==10&&tx.Boxes[0][1].Count==4&&tx.Boxes[1][0].Data=="b"&&tx.Boxes[1][0].Count==2,
            "deposit merges compatible stacks first and preserves differing item metadata");
        check(Total(tx,9)==20&&tx.Receipts.Sum(r=>r.Count)==6&&original[0].Count==6,"deposit conserves items and leaves live input snapshots untouched");
        var take=Tx(new[]{new Stack(9,1),new Stack()},new[]{new Stack(9,2)},new[]{new Stack(9,4)});
        check(take.Transfer(false,new[]{new ContainerOrder(9,5)},out _)&&take.Bag[0].Count==6&&take.Boxes[0][0].Count==0&&take.Boxes[1][0].Count==1,
            "exact withdrawal spans nearby boxes and leaves the excess stored");
        check(take.Receipts.GroupBy(x=>x.Box).Count()==2&&Total(take,9)==7,"withdrawal receipts identify each source box and conserve total stock");
        var sourceBox=new[]{new Stack(9,7)};var fullBag=new[]{new Stack(1,10)};
        var noRoom=Tx(fullBag,sourceBox);
        check(!noRoom.Transfer(false,new[]{new ContainerOrder(9,5)},out _)&&sourceBox[0].Count==7&&fullBag[0].Type==1,
            "full bag cannot withdraw or spill stored items to the world");
        check(!noRoom.ReadyToCommit&&!noRoom.Transfer(false,new[]{new ContainerOrder(9,1)},out _),"a rejected transaction cannot be committed or reused with its partial clone changes");
        var partialSource=new[]{new Stack(9,10),new Stack(22,1)};var partialBox=new[]{new Stack(),new Stack()};
        var batch=Tx(partialSource,partialBox);
        check(!batch.Transfer(true,new[]{new ContainerOrder(9,5),new ContainerOrder(22,2)},out _)&&partialSource[0].Count==10&&partialBox[0].Count==0,
            "failure on the second item leaves the entire live batch unchanged");
        check(!Tx(new[]{new Stack()},sourceBox).Transfer(false,new[]{new ContainerOrder(9,8)},out _)&&sourceBox[0].Count==7,
            "insufficient stored quantity never becomes a partial successful withdrawal");
        check(!Tx(new[]{new Stack(9,5)},new[]{new Stack(9,10)}).Transfer(true,new[]{new ContainerOrder(9,5)},out _),"full boxes reject exact deposits atomically");
        var unique=Tx(new[]{new Stack(7,1,1,data:"prefix-A"),new Stack(7,1,1,data:"prefix-B")},new[]{new Stack(),new Stack()});
        check(unique.Transfer(true,new[]{new ContainerOrder(7,0)},out _)&&unique.Boxes[0][0].Data=="prefix-A"&&unique.Boxes[0][1].Data=="prefix-B",
            "non-stackable items retain individual prefixes and mod metadata");
        check(!Tx(new[]{new Stack()},sourceBox).Transfer(false,new[]{new ContainerOrder(9,0)},out _),"quantity zero is never withdraw-all");
        check(!Tx(new[]{new Stack(9,5)},new[]{new Stack()}).Transfer(true,new[]{new ContainerOrder(9,1),new ContainerOrder(9,2)},out _),
            "duplicate item orders are rejected before planning a batch");
        var output=Tx(new[]{new Stack(8,9),new Stack()});
        check(output.Insert(new Stack(8,3))&&output.Bag[0].Count==10&&output.Bag[1].Count==2,"crafted output uses matching stacks before empty slots");
        check(!Tx(new[]{new Stack(8,10)}).Insert(new Stack(8,3)),"craft output capacity is checked on the transaction snapshot");

        var craftBag=new[]{new Stack(9,2)};var craftBox=new[]{new Stack(9,10)};
        var bench=Tx(craftBag,craftBox);
        check(bench.Craft(new[]{new IngredientRequirement(9,10,new[]{9})},new Stack(18,1),out _,out bool canBench)&&canBench&&
            bench.Bag[0].Type==18&&bench.Bag[0].Count==1&&bench.Boxes[0][0].Count==2&&bench.Receipts.Sum(r=>r.Count)==8,
            "whole craft uses bag first, takes only eight missing wood from chest and uses the freed bag slot");
        check(craftBag[0].Count==2&&craftBox[0].Count==10,"craft computation does not touch live bag or chest before commit");
        var failBag=new[]{new Stack(1,10)};var failBox=new[]{new Stack(9,10)};
        var failCraft=Tx(failBag,failBox);
        check(!failCraft.Craft(new[]{new IngredientRequirement(9,10,new[]{9})},new Stack(18,1),out _,out bool ready)&&ready&&
            failBox[0].Count==10&&failBag[0].Type==1,"a complete chest-funded craft with no output space leaves both live inventories intact");
        check(bench.ReadyToCommit&&!failCraft.ReadyToCommit,"only a successful whole craft authorizes a commit");
        var torchBox=new[]{new Stack(23,1),new Stack(9,5)};
        var torchCraft=Tx(new[]{new Stack(9,1),new Stack()},torchBox);
        check(torchCraft.Craft(new[]{new IngredientRequirement(9,1,new[]{9}),new IngredientRequirement(23,1,new[]{23})},new Stack(8,3),out _,out _)&&
            torchCraft.Boxes[0][1].Count==5&&torchCraft.Receipts.Single()==new ContainerReceipt(0,23,1)&&torchCraft.Bag.Sum(s=>s.Type==8?s.Count:0)==3,
            "multi-ingredient craft preserves excess chest wood, sources gel once and retains the full recipe yield");
        var missingCraft=Tx(new[]{new Stack(9,1),new Stack()},new[]{new Stack(9,3)});
        check(!missingCraft.Craft(new[]{new IngredientRequirement(9,1,new[]{9}),new IngredientRequirement(23,1,new[]{23})},new Stack(8,3),out _,out bool enough)&&
            !enough&&missingCraft.Receipts.Count==0&&missingCraft.Bag[0].Count==1,"missing gel prevents any ingredient consumption in a proposed chest craft");

        var iron=IngredientAllocation.Plan(new Dictionary<int,int>{{22,2},{704,3}},new[]{new IngredientRequirement(22,5,new[]{22,704})});
        check(iron.Missing.Count==0&&iron.Used[22]==2&&iron.Used[704]==3,"iron-or-lead recipe group can combine bag and stored material types");
        var shared=IngredientAllocation.Plan(new Dictionary<int,int>{{1,1},{2,1}},new[]{new IngredientRequirement(1,1,new[]{1,2}),new IngredientRequirement(1,1,new[]{1})});
        check(shared.Missing.Count==0&&shared.Used.Count==2,"flexible ingredient can be reassigned so a strict ingredient keeps its required type");
        var doubleSpend=IngredientAllocation.Plan(new Dictionary<int,int>{{1,1}},new[]{new IngredientRequirement(1,1,new[]{1,2}),new IngredientRequirement(2,1,new[]{1,2})});
        check(doubleSpend.Missing.Values.Sum()==1&&doubleSpend.Used[1]==1,"overlapping recipe groups cannot count a single ingredient twice");
        var bagFirst=IngredientAllocation.Plan(new Dictionary<int,int>{{2,3},{1,5}},new[]{new IngredientRequirement(1,2,new[]{1,2})});
        check(bagFirst.Used.Count==1&&bagFirst.Used[2]==2,"available bag types precede extra stored alternatives");
        var rng=new Random(180026);
        for(int trial=0;trial<100;trial++)
        {
            var stock=Enumerable.Range(1,3).ToDictionary(i=>i,_=>rng.Next(4));
            var req=Enumerable.Range(0,rng.Next(1,4)).Select(_=>{
                int type=rng.Next(1,4);var accepted=Enumerable.Range(1,3).Where(_=>rng.Next(2)==0).Append(type).Distinct().ToArray();
                return new IngredientRequirement(type,rng.Next(1,3),accepted);
            }).ToArray();
            var allocation=IngredientAllocation.Plan(stock,req);
            var units=req.SelectMany(r=>Enumerable.Repeat(r.Accepted,r.Count)).ToArray();
            bool Brute(int n,Dictionary<int,int> available)
            {
                if(n==units.Length)return true;
                foreach(int t in units[n])if(available[t]>0)
                {available[t]--;if(Brute(n+1,available)){available[t]++;return true;}available[t]++;}
                return false;
            }
            check((allocation.Missing.Count==0)==Brute(0,new(stock))&&allocation.Used.All(p=>p.Value<=stock[p.Key])&&
                allocation.Used.Values.Sum()+allocation.Missing.Values.Sum()==req.Sum(r=>r.Count),
                "recipe-group allocation matches exhaustive reference and conserves stock: "+trial);
        }
        var fake=new FakeWorld(new WorldSpec{Bag=new(),Chests=new(){{"木材",10}}});
        string made=fake.Run(new AgentCall("craft","工作台",1),new AgentLoop("帮我做个工作台"));
        check(made.StartsWith("做好了")&&fake.Bag.GetValueOrDefault("工作台")==1&&fake.Chests["木材"]==0,
            "offline agent tool fixture matches automatic chest-funded crafting");
        var ironWorld=new FakeWorld(new WorldSpec{Bag=new(),Chests=new(){{"铁锭",7}}});
        var takeLoop=new AgentLoop("从附近箱子取5个铁锭");var takeCall=new AgentCall("chest_take","铁锭",5);
        string taken=ironWorld.Run(takeCall,takeLoop);takeLoop.Record(takeCall,taken);
        check(taken.StartsWith("取好了")&&ironWorld.Bag["铁锭"]==5&&ironWorld.Chests["铁锭"]==2,
            "offline agent fixture performs an explicitly authorized quantity transfer");
        check(!ironWorld.Run(takeCall,takeLoop).StartsWith("取好了")&&ironWorld.Chests["铁锭"]==2,
            "offline agent fixture rejects a repeat transfer after success");
        ironWorld.Run(new AgentCall("chests","铁锭"),new AgentLoop("查附近箱子里的铁锭"));
        check(ironWorld.Chests["铁锭"]==2&&ironWorld.Bag["铁锭"]==5,"offline chest lookup preserves inventory state");
    }
}
