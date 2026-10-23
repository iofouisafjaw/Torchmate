using System.Text.Json;
using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class TaskRecoveryTests
{
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-19T00:00:00Z");
    static TaskFailureReceipt Receipt(string id="task-a",bool replan=true)=>new(id,"craft_chain","construction_failed","制作站位置被占用",
        "已推进2/4步；木材已采集，成品尚未验收",new[]{new RecoveryStock(9,"木材",8,12),new RecoveryStock(1,"铁锭",3,2)},"当前工作台可用",replan);
    static TaskRecoverySession Ready(out Guid session)
    {
        session=Guid.NewGuid();var flow=new TaskRecoverySession(session,0,7,"帮我做一个熔炉");
        flow.Bind("task-a",new(session,11));return flow;
    }
    internal static void Run(Action<bool,string> check)
    {
        const string inspect="检查最新资源，告诉我刚才的通道为什么停了，不要继续挖";
        var failedView=new CompanionTaskView("stopped-tunnel","挖过去采矿",CompanionTaskPhase.Failed,
            Result:"目标(55,65)出现线路；已移除11块，矿格0/1，入包尚未核验。",VerifiedEffects:11,Closed:true,
            Reason:"挖掘未完成，请查看具体原因。");
        var inspected=ObservationPolicy.Describe(failedView);
        check(inspected.Contains(failedView.Reason)&&inspected.Contains(failedView.Result),
            "inspection retains detailed failure and partial receipts rather than hiding them behind a generic reason");
        foreach(string text in new[]{inspect,"刚才的任务为什么失败","查看最新资源","不要继续施工","别再挖了"})
            check(ObservationPolicy.Matches(text),"failure follow-up has no new execution grant: "+text);
        foreach(string text in new[]{"规划通道挖铁矿","帮我做一个熔炉","下次不要挖矿","如何停止采矿","如果不要继续挖会怎样"})
            check(!ObservationPolicy.Matches(text),"observation route preserves new work and hypothetical discussion: "+text);
        foreach(string raw in new[]{"""{"tool":"expedition","mode":"tunnel"}""","""{"tool":"mine","count":1}""",
            """{"tool":"replan"}""","""{"tool":"craft_chain","item":"熔炉"}""","""{"tool":"control","mode":"follow"}"""})
        {
            var observe=new AgentLoop(inspect);var decision=observe.OnModel(raw);
            check(observe.ObservationOnly&&decision.Kind==AgentOutcome.Finish&&decision.Warn,
                "fresh agent loop blocks a misbehaving model after recovery has ended: "+raw);
            check(!observe.Schema.GetRawText().Contains("\"replan\"")&&!observe.Schema.GetRawText().Contains("\"expedition\""),
                "inspection schema excludes replanning and expedition tools");
        }
        var answered=new AgentLoop("规划通道挖铁矿");answered.Recover(Receipt());answered.Answer("需要重试吗？",inspect);
        check(answered.ObservationOnly&&answered.OnModel("""{"tool":"replan"}""").Kind==AgentOutcome.Finish,
            "negative answer restricts an already running recovery loop as well as a new turn");
        var observeLook=new AgentLoop(inspect);
        check(observeLook.OnModel("""{"tool":"inventory"}""").Kind==AgentOutcome.Run&&
            observeLook.OnModel("""{"tool":"reply","text":"通道因现场变化停止，尚未完成。"}""").Kind==AgentOutcome.Finish,
            "inspection still permits real inventory queries and a factual reply");
        foreach(var bad in new[]{(Guid.Empty,0,1,"goal"),(Guid.NewGuid(),-1,1,"goal"),(Guid.NewGuid(),0,-1,"goal"),(Guid.NewGuid(),0,1,"")})
        {
            bool rejected=false;try{_ = new TaskRecoverySession(bad.Item1,bad.Item2,bad.Item3,bad.Item4);}catch(ArgumentException){rejected=true;}
            check(rejected,"invalid recovery lineage owner or goal rejected");
        }
        var flow=Ready(out Guid session);
        check(!flow.Recovering&&!flow.Allows(session,11,0,7,true,Now),"preview registration never starts a model recovery");
        foreach(string change in new[]{"session","revision","player","npc","dead","task"})
        {
            var f=Ready(out Guid s);var receipt=Receipt(change=="task"?"other":"task-a");
            check(!f.Fail(receipt,change=="session"?Guid.NewGuid():s,change=="revision"?12:11,change=="player"?1:0,
                change=="npc"?8:7,change!="dead",Now)&&f.Failures==0,"stale failure cannot attach to another owner or task: "+change);
        }
        check(flow.Fail(Receipt(),session,11,0,7,true,Now)&&flow.Latest?.Stock[0].Chests==12,"failure captures typed current stock, progress and original goal");
        check(!flow.Fail(Receipt(),session,11,0,7,true,Now)&&flow.Failures==1,"same failed task cannot restart recovery twice");
        check(!flow.Bind("task-a",new(session,11)),"failed task identity cannot be rebound as a new preview");
        check(!flow.Allows(session,11,0,7,true,Now.AddMinutes(5)),"recovery expires at exact five-minute boundary");
        foreach(string change in new[]{"session","revision","player","npc","dead"})
            check(!flow.Allows(change=="session"?Guid.NewGuid():session,change=="revision"?12:11,change=="player"?1:0,
                change=="npc"?8:7,change!="dead",Now),"model execution revalidates live recovery scope: "+change);
        for(int i=0;i<TaskRecoverySession.MaxModelRequests;i++)check(flow.Request(session,11,0,7,true,Now),"bounded recovery model request "+i);
        check(!flow.Request(session,11,0,7,true,Now)&&flow.Requests==6,"seventh recovery request is refused, no unbounded fallback loop");
        check(flow.Replan(session,11,0,7,true,Now),"first replan consumes lineage budget");
        check(flow.Bind("task-b",new(session,12))&&!flow.Recovering&&flow.Requests==6&&flow.Replans==1,"new preview keeps cumulative budgets and ends model recovery until actual failure");
        check(!flow.Fail(Receipt(),session,12,0,7,true,Now),"late old failure cannot replace the new task receipt");
        check(flow.Fail(Receipt("task-b"),session,12,0,7,true,Now)&&flow.Failures==2,"second child failure can resume same goal");
        check(flow.Replan(session,12,0,7,true,Now)&&!flow.Replan(session,12,0,7,true,Now),"third replan attempt refused even without a preview replacement");
        flow.Bind("task-c",new(session,13));
        check(!flow.Fail(Receipt("task-c"),session,13,0,7,true,Now)&&flow.Cancelled&&flow.Latest?.TaskId=="task-c","third failed child stops lineage but retains last real receipt");
        check(!flow.Bind("task-d",new(session,14)),"cancelled recovery cannot resurrect on a new preview");
        var cancelled=Ready(out var cancelledSession);cancelled.Cancel();
        check(!cancelled.Fail(Receipt(),cancelledSession,11,0,7,true,Now),"player stop before queued failure delivery prevents any resumption");
        var unsafeFlow=Ready(out var unsafeSession);unsafeFlow.Fail(Receipt(replan:false),unsafeSession,11,0,7,true,Now);
        check(unsafeFlow.Request(unsafeSession,11,0,7,true,Now)&&!unsafeFlow.Replan(unsafeSession,11,0,7,true,Now),"manual-only failure permits information gathering without restarting movement/combat");

        foreach(string text in new[]{"算了","不用了。","不做了","别继续了","停止恢复","取消恢复!","不恢复了"})
            check(RecoveryPolicy.IsStop(text),"explicit user cancellation ends recovery: "+text);
        foreach(string text in new[]{"怎么取消恢复","不用买，先查箱子","他说算了","先看背包","恢复停止了吗"})
            check(!RecoveryPolicy.IsStop(text),"unrelated words/questions do not masquerade as cancellation: "+text);
        var receiptText=Receipt().Describe();
        check(receiptText.Contains("制作站位置被占用")&&receiptText.Contains("2/4")&&receiptText.Contains("背包8／附近箱子12")&&
            receiptText.Contains("未通过最终验收")&&!AgentProtocol.IsSuccess(receiptText),"failure receipt is factual and never interpreted as successful completion");
        check(!Receipt(replan:false).Describe().Contains("可用replan")&&Receipt(replan:false).Describe().Contains("玩家重新明确下令"),"manual-only receipt does not advertise automatic restart");

        foreach(var call in new[]{new AgentCall("inventory"),new("look"),new("recipe","熔炉"),new("house_materials"),new("storage"),new("chests"),
            new("npcs"),new("shop","铁锭"),new("locate","箱子"),new("ask",Text:"是否补料后再规划？"),new("reply",Text:"已停止，保留已完成部分。"),
            new("answer"),new("replan"),new("control",Mode:"stop")})
        {
            check(RecoveryPolicy.Allows(call),"recovery allows an information/approval/stop operation: "+call.Label);
            var loop=new AgentLoop("帮我做熔炉");loop.Recover(Receipt());
            var decision=loop.OnModel(call.Tool=="replan"?"""{"tool":"replan"}""":JsonSerializer.Serialize(new{tool=call.Tool,item=call.Item,mode=call.Mode,text=call.Text}));
            check(decision.Kind!=AgentOutcome.Ask,"allowed recovery decision is not blocked by policy: "+call.Tool);
        }
        foreach(var call in new[]{new AgentCall("craft","熔炉",1),new("place","工作台"),new("chest_take","铁锭",5),new("chest_put","木材",10),
            new("buy","铁锭",1),new("chop",Count:1),new("mine",Count:20,Ore:"石块"),new("build",Rooms:1),new("platform"),
            new("craft_chain","熔炉",1),new("garden","太阳花",1,Mode:"plant"),new("prepare_eye"),new("expedition",Mode:"scout"),
            new("combat",Mode:"protect"),new("spawn_item","天顶剑",1),new("structure",Text:"小屋"),new("cheat_world","eye",Mode:"boss"),
            new("control",Mode:"follow"),new("control",Mode:"recall"),new("control",Mode:"wait")})
        {
            var loop=new AgentLoop("帮我做熔炉");loop.Recover(Receipt());
            string raw=JsonSerializer.Serialize(new{tool=call.Tool,item=call.Item,count=call.Count,ore=call.Ore,rooms=call.Rooms,mode=call.Mode,text=call.Text});
            if(call.Tool=="cheat_world")raw="""{"tool":"cheat_world","mode":"boss","item":"eye"}""";
            var decision=loop.OnModel(raw);
            check(!RecoveryPolicy.Allows(call)&&decision.Kind!=AgentOutcome.Run,"untrusted recovery model cannot launch side effects or retarget: "+call.Tool+" "+call.Mode);
            check(loop.Steps.All(s=>!AgentProtocol.IsSuccess(s.Result)),"blocked proposal is not recorded as executed success: "+call.Tool);
        }
        var outside=new AgentLoop("随便聊聊").OnModel("""{"tool":"replan"}""");
        check(outside.Kind==AgentOutcome.Finish&&outside.Warn,"replan has no effect outside explicit failure recovery");
        foreach(string extra in new[]{"item","count","rooms","mode","text","tileX"})
        {
            bool rejected=false;try{AgentProtocol.Parse("{\"tool\":\"replan\",\""+extra+"\":1}");}catch(FormatException){rejected=true;}
            check(rejected,"model cannot inject a new replan scope: "+extra);
        }
        bool duplicateRejected=false;
        try{AgentProtocol.Parse("""{"tool":"replan","tool":"replan"}""");}catch(FormatException){duplicateRejected=true;}
        check(duplicateRejected,"duplicate replan tool properties are rejected");

        // A scripted model uses the real production agent loop: observe failure -> query current stock -> new preview.
        var world=new FakeWorld(new WorldSpec{Bag=new(){{"木材",8}},Chests=new(){{"石块",20}}});
        var recoveringLoop=new AgentLoop("帮我做一个熔炉");recoveringLoop.Recover(Receipt());
        var inventory=recoveringLoop.OnModel("""{"tool":"inventory"}""");
        string result=world.Run(inventory.Call!,recoveringLoop);recoveringLoop.Record(inventory.Call!,result);
        check(inventory.Kind==AgentOutcome.Run&&result.Contains("木材"),"recovery really queries shared offline inventory adapter");
        var boxes=recoveringLoop.OnModel("""{"tool":"chests","item":"石块"}""");
        string boxResult=world.Run(boxes.Call!,recoveringLoop);recoveringLoop.Record(boxes.Call!,boxResult);
        check(boxResult.Contains("20")&&world.Bag.GetValueOrDefault("石块")==0,"information gathering sees chest changes without withdrawing items");
        var retryDecision=recoveringLoop.OnModel("""{"tool":"replan"}""");
        check(retryDecision.Kind==AgentOutcome.Run&&world.Spawned.Count==0,
            "replan is a terminal native handoff, never offline material generation");
        string handoff=world.Run(retryDecision.Call!,recoveringLoop);
        check(handoff.Contains("未创建真实预览")&&handoff.Contains("重新确认")&&world.Bag["木材"]==8&&world.Chests["石块"]==20,
            "offline replan reports simulation limits and does not mutate or fabricate completion");
        string prompt=recoveringLoop.Prompt("实时库存：石块已补齐",Array.Empty<string>());
        check(prompt.Contains("原任务没有完成")&&prompt.Contains("实时库存：石块已补齐")&&prompt.Contains("旧许可作废"),"model receives current stock and explicit renewed-confirmation constraints");
        check(new AgentLoop("goal").Prompt("stock",Array.Empty<string>()).Contains("【长任务失败后的恢复】")==false,"normal agent behavior is not forced into recovery mode");
        var retrySchema=RecoveryPolicy.Schema(true).GetProperty("oneOf").EnumerateArray().ToArray();
        var retryBranch=retrySchema.Single(s=>s.GetProperty("properties").GetProperty("tool").TryGetProperty("const",out var v)&&v.GetString()=="replan");
        check(retryBranch.GetProperty("properties").EnumerateObject().Count()==1&&!retryBranch.GetProperty("additionalProperties").GetBoolean()&&
            retryBranch.GetProperty("required").EnumerateArray().Single().GetString()=="tool","recovery decoding admits only exact no-argument replan, never the empty text field observed in real inference");
        foreach(var branch in retrySchema)
        {
            var tool=branch.GetProperty("properties").GetProperty("tool");
            if(tool.TryGetProperty("enum",out var choices))foreach(var choice in choices.EnumerateArray())
                check(RecoveryPolicy.Allows(new(choice.GetString()!)),"decoding lookup whitelist matches production guard: "+choice);
        }
        var stopBranch=retrySchema.Single(s=>s.GetProperty("properties").GetProperty("tool").TryGetProperty("const",out var v)&&v.GetString()=="control");
        check(stopBranch.GetProperty("properties").GetProperty("mode").GetProperty("const").GetString()=="stop"&&
            stopBranch.GetProperty("required").EnumerateArray().Any(v=>v.GetString()=="mode"),"recovery decoding cannot request follow/wait/recall instead of stop");
        check(!RecoveryPolicy.Schema(false).GetRawText().Contains("replan"),"manual-only and exception failures have no replan decoding branch");
        var changing=new AgentLoop("goal");check(changing.Schema.GetRawText()==AgentProtocol.Schema.GetRawText(),"ordinary agent keeps original decoding schema");
        changing.Recover(Receipt());check(changing.Schema.GetProperty("oneOf").GetArrayLength()==5,"real agent loop selects recovery decoding");
        changing.Recover(Receipt(replan:false));check(changing.Schema.GetProperty("oneOf").GetArrayLength()==4,"latest failure permissions refresh decoding rather than inheriting earlier retry ability");
        var mapLoop=new AgentLoop("挖到地下层");mapLoop.Recover(Receipt(replan:false));
        var mapCall=mapLoop.OnModel("""{"tool":"locate","item":"地下层"}""");mapLoop.Record(mapCall.Call!,"不支持定位地下层；原挖掘仍未完成。");
        check(mapCall.Kind==AgentOutcome.Run,"recovery location query can return an observation to the same loop");
        check(mapLoop.OnModel("""{"tool":"locate","item":"地下层"}""").Kind==AgentOutcome.Ask&&mapLoop.RecoveryDecisionOnly&&!mapLoop.WrapUpMode,
            "a terminal-in-normal-play location lookup is still deduplicated during recovery");
        var mapBlocked=mapLoop.OnModel("""{"tool":"locate","item":"地下层"}""");
        check(mapBlocked.Kind==AgentOutcome.Ask,"decision stage blocks another map lookup even if an endpoint ignores its schema");
        var mapStop=mapLoop.OnModel("""{"tool":"reply","text":"任务已经完成"}""");
        check(mapStop.Kind==AgentOutcome.Finish&&mapStop.Text.Contains("不支持定位地下层")&&mapStop.Text.Contains("尚未通过最终验收")&&!mapStop.Text.Contains("任务已经完成"),"recovery stop is rendered from evidence, not an unverified model completion claim");
        check(!mapLoop.Schema.GetRawText().Contains("inventory")&&!mapLoop.Schema.GetRawText().Contains("replan"),"manual decision schema excludes further queries and replanning");
        mapLoop.Answer("是否先检查？","我已经核对现场，先再查背包");
        check(!mapLoop.RecoveryDecisionOnly&&mapLoop.Schema.GetRawText().Contains("inventory")&&!mapLoop.Schema.GetRawText().Contains("replan"),"human clarification reopens queries without granting retry or spending authority");
        var staleLoop=new AgentLoop("做铁盔甲",Enumerable.Range(0,12).Select(_=>new AgentStep(new AgentCall("inventory").Label,"背包：旧库存","inventory")));
        staleLoop.Steps.Add(new AgentStep("旧合成","做好了：铁锭","craft"));staleLoop.Recover(Receipt());
        check(staleLoop.BeforeRequest()==null&&!staleLoop.Prompt("最新库存",Array.Empty<string>()).Contains("步数快用完了"),
            "recovery has its own bounded budget even when pre-handoff history was long");
        var freshLookup=staleLoop.OnModel("""{"tool":"inventory"}""");
        check(freshLookup.Kind==AgentOutcome.Run,"pre-failure lookup history cannot suppress a current inventory query");
        staleLoop.Record(freshLookup.Call!,"背包：当前只有8木材，未做成盔甲");
        var repeatedLookup=staleLoop.OnModel("""{"tool":"inventory"}""");
        var nextPreview=staleLoop.OnModel("""{"tool":"replan"}""");
        check(repeatedLookup.Kind==AgentOutcome.Ask&&staleLoop.RecoveryDecisionOnly&&!staleLoop.WrapUpMode&&nextPreview.Kind==AgentOutcome.Run,
            "a repeated query retains the original action goal and permits a new preview instead of forcing a reply-only completion");
        check(!staleLoop.Schema.GetRawText().Contains("inventory")&&staleLoop.Schema.GetRawText().Contains("replan"),"retry decision schema keeps only preview, ask and safe stop options");
        var replyBranch=staleLoop.Schema.GetProperty("oneOf").EnumerateArray().Single(s=>s.GetProperty("properties").GetProperty("tool").GetProperty("const").GetString()=="reply");
        check(replyBranch.GetProperty("properties").GetProperty("text").GetProperty("const").GetString()==RecoveryPolicy.StopText,"decoder cannot invent an unaudited completion status in a recovery reply");
        staleLoop.Recover(Receipt());check(!staleLoop.RecoveryDecisionOnly&&staleLoop.Schema.GetRawText().Contains("inventory"),"a later actual failure resets query-decision state without changing session budgets");

        GardenTests(check);
        StructureTests(check);
        ConfirmationTest(check);
    }

    static void GardenTests(Action<bool,string> check)
    {
        var origin=new BuildPoint(100,100);var seeds=new Dictionary<GardenCrop,int>{{GardenCrop.Daybloom,10},{GardenCrop.Moonglow,10}};
        GardenCell Cell(int x,GardenCrop crop=GardenCrop.Daybloom,GardenGrowth growth=GardenGrowth.Empty)=>
            new(new(x,100),crop,growth,GardenBed.Planter,true,$"{x}/{crop}/{growth}");
        var original=GardenPlan.Build(new(GardenOperation.Plant,GardenCrop.Daybloom,3),origin,seeds,new[]{Cell(100),Cell(101),Cell(102)});
        var remaining=original.Remaining(1,seeds,new[]{Cell(100),Cell(101),Cell(102),Cell(103),Cell(101,GardenCrop.Moonglow)});
        check(remaining.Origin==origin&&remaining.Request.Count==2&&remaining.CanExecute&&remaining.Targets.All(c=>c.At.X is 101 or 102&&c.Crop==GardenCrop.Daybloom),
            "garden replan keeps original origin, target crop and only the two unfinished cells");
        var missing=original.Remaining(1,seeds,new[]{Cell(102),Cell(103)});
        check(!missing.CanExecute&&missing.Targets.Count==1&&missing.Targets[0].At.X==102,"missing old target is not silently replaced by another available cell");
        var wrongCrop=original.Remaining(1,seeds,new[]{Cell(101,GardenCrop.Moonglow),Cell(102)});
        check(!wrongCrop.CanExecute,"another crop at the same coordinate does not authorize harvesting/planting a substitute");
        var allowed=original.Targets.Skip(1).Select(c=>(c.At,c.Crop)).ToHashSet();
        var confirmationRefresh=GardenPlan.BuildFixed(remaining.Request,origin,seeds,new[]{Cell(100),Cell(101),Cell(102),Cell(99)},allowed);
        check(confirmationRefresh.Targets.All(c=>c.At.X is 101 or 102)&&confirmationRefresh.CanExecute,
            "confirmation-time stock/world refresh preserves original recovery targets instead of selecting new nearby cells");
        var confirmationBlocked=GardenPlan.BuildFixed(remaining.Request,origin,seeds,new[]{Cell(100),Cell(102),Cell(103)},allowed);
        check(!confirmationBlocked.CanExecute&&confirmationBlocked.Targets.Count==1,
            "confirmation refresh refuses missing fixed target even when new substitute cells exist");
        var noSeed=original.Remaining(1,new Dictionary<GardenCrop,int>(),new[]{Cell(101),Cell(102)});
        check(!noSeed.CanExecute,"fresh garden replan really checks latest seed stock");
        foreach(int cursor in new[]{-1,3,4})
        {
            bool rejected=false;try{original.Remaining(cursor,seeds,new[]{Cell(100)});}catch(ArgumentException){rejected=true;}
            check(rejected,"invalid/completely traversed garden is not repeated wholesale: "+cursor);
        }
        var oldJob=new GardenJob(original,Now);oldJob.Confirm(oldJob.PreviewId,original.Signature,Now);
        var newJob=new GardenJob(remaining,Now);
        check(newJob.State==GardenState.Preview&&newJob.PreviewId!=oldJob.PreviewId&&!newJob.Confirm(oldJob.PreviewId,remaining.Signature,Now),
            "garden replan gets a new confirmation identity and cannot inherit old approval");
    }
    static void StructureTests(Action<bool,string> check)
    {
        var plan=new StructurePlan("test","fixture",2,1,new[]{new StructureStep(0,0,StepKind.Block,1,0,9),new(1,0,StepKind.Block,1,0,9)});
        var site=new BuildSite();var job=new StructureBuildJob(plan);
        while(job.State==StructureJobState.Running)job.Step(site);
        check(job.State==StructureJobState.Completed&&job.Failed==1&&!job.Verified(site),"traversing a blueprint with skipped blocked cells is not verified completion");
        site.Blocked=false;site.Done.Add((1,0));
        check(job.Verified(site),"only current complete footprint can pass final blueprint audit");
        site.Done.Remove((0,0));check(!job.Verified(site),"a previously placed cell removed before audit prevents false completion");
        var retry=new StructureBuildJob(plan);site.Blocked=false;
        while(retry.State==StructureJobState.Running)retry.Step(site);
        check(retry.Reused==1&&retry.Placed==1&&retry.Verified(site),"replanning original footprint reuses surviving work and fills only the missing cell");
    }
    static void ConfirmationTest(Action<bool,string> check)
    {
        var catalog=new Catalog();
        var plan=CraftChainPlanner.Build(new[]{new ChainTarget(2,1)},new Dictionary<int,int>{{1,2}},Array.Empty<int>(),catalog);
        var old=new CraftChainJob(plan,Now);old.Confirm(old.PreviewId,plan.Signature,Now);
        var refreshed=CraftChainPlanner.Build(plan.Targets,new Dictionary<int,int>{{2,1}},Array.Empty<int>(),catalog);
        var next=new CraftChainJob(refreshed,Now);
        check(next.State==ChainState.Preview&&refreshed.Steps.Count==0,"fresh inventory can eliminate a whole craft rather than duplicate completed products");
        check(!next.Confirm(old.PreviewId,refreshed.Signature,Now)&&next.State==ChainState.Preview,"old approval cannot execute even a freshly satisfied plan");
        check(next.Confirm(next.PreviewId,refreshed.Signature,Now),"only a new matching approval can execute the refreshed plan");
    }
    sealed class Catalog:ICraftChainCatalog
    {
        public IEnumerable<ChainRecipe> Recipes(int item)=>item==2?new[]{new ChainRecipe(1,2,1,new[]{new IngredientRequirement(1,2,new[]{1})},Array.Empty<int>())}:Array.Empty<ChainRecipe>();
        public IEnumerable<int> StationItems(int tile)=>Array.Empty<int>();
        public bool Collectable(int item)=>false;
    }
    sealed class BuildSite:IStructureSite
    {
        public bool Blocked=true;
        public HashSet<(int,int)> Done=new();
        public CellState Inspect(StructureStep step)=>Done.Contains((step.Dx,step.Dy))?CellState.Done:step.Dx==1&&Blocked?CellState.Blocked:CellState.Empty;
        public bool Clear(StructureStep step,out string error){error="";return true;}
        public bool Place(StructureStep step,out string error){Done.Add((step.Dx,step.Dy));error="";return true;}
    }
}
