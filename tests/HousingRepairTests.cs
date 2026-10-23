using TerrarAI.Content.Companion;

static class HousingRepairTests
{
    private const int Wood=9, Gel=23, Bench=18, Chair=34, Torch=8, Wall=93;
    private static RepairRecipe? Recipe(int item)=>item switch {
        Bench=>new(Bench,1,new Dictionary<int,int>{{Wood,10}},false),
        Chair=>new(Chair,1,new Dictionary<int,int>{{Wood,4}},true),
        Torch=>new(Torch,3,new Dictionary<int,int>{{Wood,1},{Gel,1}},false),
        Wall=>new(Wall,4,new Dictionary<int,int>{{Wood,1}},true), _=>null};
    internal static void Run(Action<bool,string> check)
    {
        // Terraria's Wood wall and Torch tile both have ID 4, in separate ID spaces.
        var nativePalette=StructurePresets.NpcRooms(1,new(30,9,4,93,10,25,14,32,15,34,4,8));
        var light=HousingRepairLayout.Furniture(nativePalette,4);
        check(light.Kind==StepKind.Furniture&&light.Item==8&&nativePalette.Steps.Count(s=>s.Type==4)>1,
            "housing repair selects the torch despite every wood wall sharing its numeric ID");
        check(HousingRepairLayout.Furniture(nativePalette,15).Item==34&&HousingRepairLayout.Furniture(nativePalette,14).Item==32,
            "repair reuses the preset's chair and table furniture definitions");
        foreach(var text in new[]{"检查我旁边的房子","这屋子为啥 NPC 不住","修补这间房屋","housing check"})
            check(HousingRequest.Matches(text),"house inspection request is handled locally: "+text);
        foreach(var text in new[]{"盖一间NPC房","怎么修房子","以后记住先检查房屋","不要修房屋","搭平台","查背包"})
            check(!HousingRequest.Matches(text),"house inspection preserves other requests and preferences: "+text);
        var need=new Dictionary<int,int>{{Bench,1},{Chair,1},{Torch,1},{Wall,5}};
        var bag=new Dictionary<int,int>{{Wood,17},{Gel,1}};
        var budget=HousingMaterialBudget.Calculate(need,i=>bag.GetValueOrDefault(i),Recipe);
        check(budget.CanPrepare&&budget.Consumed[Wood]==17&&budget.Consumed[Gel]==1&&budget.Crafts.Count==4,
            "repair budgets a workbench, chair, torch batch and five walls for 17 wood and one gel");
        check(bag[Wood]==17&&bag[Gel]==1,"repair budgeting is read-only before confirmation");
        check(budget.Crafts[0].Recipe.Item==Bench&&!budget.Crafts[0].Recipe.Workbench&&budget.Crafts.Last().Batches==2,
            "budget puts the station first and rounds wall batches up");
        var shortBudget=HousingMaterialBudget.Calculate(need,i=>i==Wood?16:i==Gel?1:0,Recipe);
        check(!shortBudget.CanPrepare&&shortBudget.Missing.Count==1&&shortBudget.Missing[Wood]==1,
            "shared raw materials are reserved once across the entire repair chain");
        var stock=HousingMaterialBudget.Calculate(need,i=>need.GetValueOrDefault(i),Recipe);
        check(stock.CanPrepare&&stock.Crafts.Count==0&&stock.Consumed.Count==0,"carried furniture and walls need no raw materials");
        var mixed=HousingMaterialBudget.Calculate(new Dictionary<int,int>{{Torch,2}},i=>i==Torch?1:i==Wood||i==Gel?1:0,Recipe);
        check(mixed.CanPrepare&&mixed.Crafts.Single().Batches==1&&mixed.Crafts.Single().Recipe.Output==3,
            "torch planning uses stock first and retains native batch excess");
        var noGel=HousingMaterialBudget.Calculate(new Dictionary<int,int>{{Torch,1}},i=>i==Wood?99:0,Recipe);
        check(!noGel.CanPrepare&&noGel.Missing[Gel]==1,"torch crafting reports gel rather than inventing it");
        var noRecipe=HousingMaterialBudget.Calculate(new Dictionary<int,int>{{999,2}},_=>0,Recipe);
        check(!noRecipe.CanPrepare&&noRecipe.Missing[999]==2,"unavailable recipes require the real finished item");
        var changed=HousingMaterialBudget.Calculate(need,i=>i==Wood?0:i==Gel?1:0,Recipe);
        check(!changed.CanPrepare&&changed.Missing[Wood]==17,"rechecking a changed inventory recomputes the full raw shortfall");

        var full=new[]{new WorkshopStack(Wood,10),new WorkshopStack(999,1)};
        var cost=new Dictionary<int,int>{{Wood,1}};
        var blocked=WorkshopInventory.Craft(full,cost,19,2,9999,out _);
        check(blocked==null&&full[0].Count==10&&full[1].Type==999,"full inventory rejects a craft without consuming ingredients or dropping output");
        var freed=WorkshopInventory.Craft(full,new Dictionary<int,int>{{Wood,10}},Bench,1,9999,out _);
        check(freed!=null&&freed[0]==new WorkshopStack(Bench,1)&&freed[1]==full[1]&&full[0].Count==10,
            "consumed ingredients can free an output slot while unrelated items and the source snapshot stay unchanged");
        var split=WorkshopInventory.Craft(new[]{new WorkshopStack(Wood,150),new WorkshopStack(19,9998),default},
            new Dictionary<int,int>{{Wood,150}},19,300,9999,out _);
        check(split!=null&&split.Sum(i=>i.Type==19?i.Count:0)==10298&&split[1].Count==9999&&split[0].Count==299,
            "platform craft regression: 150 wood produces 300 platforms and fills an existing stack before a new slot");
        var insufficient=WorkshopInventory.Craft(new[]{new WorkshopStack(Wood,10),default},
            new Dictionary<int,int>{{Wood,1},{Gel,1}},Torch,3,9999,out _);
        check(insufficient==null,"multi-ingredient craft is atomic when gel is absent after wood was simulated");
        var fromStacks=WorkshopInventory.Craft(new[]{new WorkshopStack(Wood,2),new WorkshopStack(Wood,2),default},
            new Dictionary<int,int>{{Wood,4}},Chair,1,9999,out _);
        check(fromStacks!=null&&fromStacks.Count(i=>i.Type==Chair&&i.Count==1)==1&&!fromStacks.Any(i=>i.Type==Wood),
            "repair ingredients may span multiple inventory stacks");

        var chair=new StructureStep(0,0,StepKind.Furniture,15,0,Chair,Width:1,Height:2,OriginY:1);
        var table=new StructureStep(0,0,StepKind.Furniture,14,0,32,Width:3,Height:2,OriginX:1,OriginY:1);
        var reserved=new HashSet<(int X,int Y)>();
        bool Empty(int x,int y)=>x>0&&x<9&&y>0&&y<5;
        bool Floor(int x,int y)=>y==5;
        var a=HousingRepairLayout.Floor(10,6,chair,Empty,Floor,reserved,(x,y)=>Math.Abs(x-3)+Math.Abs(y-4));
        check(a is {Dx:3,Dy:4}&&a.Footprint().All(p=>p.Y is 3 or 4),"chair origin and full two-cell footprint stay above the actual floor");
        foreach(var p in a!.Footprint())reserved.Add(p);
        var b=HousingRepairLayout.Floor(10,6,table,Empty,Floor,reserved,(x,y)=>Math.Abs(x-3)+Math.Abs(y-4));
        check(b!=null&&!b.Footprint().Any(reserved.Contains)&&b.Footprint().All(p=>p.X>0&&p.X<9),
            "table placement avoids planned chair cells and keeps its entire footprint inside the room");
        var unsupported=HousingRepairLayout.Floor(10,6,table,Empty,(x,y)=>y==5&&x==4,reserved,(_,_)=>0);
        check(unsupported==null,"a table needs support under every footprint column");
        var unsafeRoom=HousingRepairLayout.Floor(10,6,chair,(_,_)=>false,Floor,reserved,(_,_)=>0);
        check(unsafeRoom==null,"unsafe, occupied or non-room cells cannot become repair targets");
        var farBench=HousingRepairLayout.Floor(10,6,table,Empty,Floor,reserved,(_,_)=>0,(_,_)=>false);
        check(farBench==null,"station origin constraints can reject locations too far from the player");
        var small=HousingRepairLayout.Floor(4,4,table,(_,_)=>true,(_,_)=>true,reserved,(_,_)=>0);
        check(small==null,"repair planning does not squeeze a three-wide table through a too-small interior");

        var rawSite=new Site();var safe=new NonDestructiveStructureSite(rawSite);
        rawSite.State=CellState.Clearable;
        check(safe.Inspect(chair)==CellState.Blocked&&!safe.Clear(chair,out _)&&rawSite.Cleared==0,
            "repair adapter blocks the normal construction site's terrain-clearing capability");
        rawSite.State=CellState.Empty;
        check(safe.Inspect(chair)==CellState.Empty,"an empty repair target can be previewed");
        rawSite.State=CellState.Blocked;
        check(!safe.Place(chair,out _)&&rawSite.Placed==0,"an obstacle inserted after inspection prevents placement without overwriting it");
        rawSite.State=CellState.Done;
        var reused=new StructureBuildJob(new("repair","test",10,6,new[]{chair}));reused.Step(safe);reused.Step(safe);
        check(reused.Reused==1&&rawSite.Placed==0,"already repaired cells are reused without spending another item");
        rawSite.State=CellState.Empty;
        var cancelled=new StructureBuildJob(new("repair","test",10,6,new[]{chair,table}));
        cancelled.Step(safe);cancelled.Cancel("player stopped");cancelled.Step(safe);
        check(cancelled.State==StructureJobState.Cancelled&&rawSite.Placed==1,"cancelling a repair preserves work and prevents further placement");
    }
    private sealed class Site:IStructureSite
    {
        public CellState State;public int Placed,Cleared;
        public CellState Inspect(StructureStep s)=>State;
        public bool Clear(StructureStep s,out string error){error="";Cleared++;return true;}
        public bool Place(StructureStep s,out string error){error="";Placed++;return true;}
    }
}
