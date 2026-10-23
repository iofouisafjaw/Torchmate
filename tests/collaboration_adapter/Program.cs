using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
using Terraria;

int passed = 0;
void Check(bool yes, string name) { if (!yes) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
var collaboration = new CompanionCollaborationSystem(); var recovery = new CompanionRecoverySystem(); var tracker = new CompanionTaskTracker();
void Reset()
{
    collaboration.Unload(); recovery.Unload(); tracker.OnWorldLoad();
    Main.player = new[] { new Player { whoAmI = 0 }, new Player { whoAmI = 1 } }; Main.netMode = 0; Main.gameMenu = Main.gamePaused = false;
    ChatCoordinator.Agent = new(); CompanionMemorySystem.Session = Guid.NewGuid(); CompanionMemorySystem.Events.Clear();
    CompanionCommandService.Revision = 0; CompanionAgent.LastOwner = null; CompanionAgent.LastReceipt = null; CompanionAgent.Resumes = 0;
    Modules.Ids.Clear(); Modules.Previews = Modules.Executions = 0; Modules.ThrowReplan = false;
    StructureWorkshop.Origin = null; StructureWorkshop.ArenaMode = StructureWorkshop.HousingMode = StructureWorkshop.ArenaPassed = StructureWorkshop.HousingPassed = false;
    HousingStructureSite.Completed.Clear(); NearbyChests.Bag.Clear(); NearbyChests.Boxes.Clear(); NearbyChests.Throw = false;
}
string Input(string text)
{
    using var input = CompanionRecoverySystem.Input(Main.LocalPlayer, text);
    Check(CompanionCollaborationSystem.TryInput(Main.LocalPlayer, text, out string result), "native input recognized: " + text); return result;
}
string TaskId() => CompanionTaskTracker.Current!.TaskId;
void Pots() { foreach (int i in new[] { 1001, 1002, 1003, 1004 }) NearbyChests.Bag[i] = i == 1004 ? 10 : 2; }

Reset(); Input("一起准备打克眼"); var project = CompanionCollaborationSystem.Current!;
Check(Modules.Previews == 0 && Modules.Executions == 0 && project.Steps.All(s => s.TaskId.Length == 0), "negotiating never creates approval or consumes resources");
Input("药水我来"); Input("继续协作"); string first = TaskId();
Check(!StructureWorkshop.IncludedPotions && project.Next!.TaskId == first && project.Next.Owner == CollaborationOwner.Companion, "arena excludes separately assigned potions and binds its real child");
Check(CompanionCollaborationSystem.CanExecute(Main.LocalPlayer, first), "only assigned companion work accepts native execution in its current scope");
Check(!project.Complete && Input("核验协作").Contains("同伴执行"), "player cannot self-report completion of companion work");
CompanionTaskTracker.Update(first, CompanionTaskPhase.Executing, result: "placed three platforms");
Check(project.Next!.State == CollaborationState.Running && project.Next.Evidence.Contains("three"), "actual tracker progress reaches shared ledger");
CompanionTaskTracker.SetResult(first, "station blocked; three platforms preserved"); CompanionTaskTracker.Close(first, "construction_failed", 0);
Check(project.Next!.State == CollaborationState.Failed && !project.Complete, "actual failure receipt preserves partial progress");
recovery.PostUpdateWorld(); var owner = CompanionAgent.LastOwner!;
using (CompanionRecoverySystem.Dispatch(Main.LocalPlayer, owner.Loop, new("replan"), owner, owner.Cheat, owner.CheatGoal))
    CompanionRecoverySystem.Replan(owner);
string retry = TaskId();
Check(retry != first && project.Next!.TaskId == retry && !project.Paused && Modules.Executions == 0, "bounded production recovery rebinds project without approval or execution");
CompanionTaskTracker.CompleteVerified(first, "late old callback");
Check(project.Next!.TaskId == retry && !project.Complete, "stale old success cannot complete recovered project");
StructureWorkshop.ArenaPassed = true; CompanionTaskTracker.CompleteVerified(retry, "native platform audit");
Check(project.Next!.Id == "potions" && project.Steps[0].Evidence.Contains("scene"), "verified scene advances to player supply work");
int before = Modules.Previews; Input("继续协作"); Check(Modules.Previews == before, "player supply assignment never starts companion crafting");
Check(Input("我做完了").Contains("未通过") && !project.Complete, "player claim requires fresh actual backpack stock");
Pots(); Input("我做完了"); Check(project.Complete && project.Steps.All(s => s.State == CollaborationState.Verified), "eye preparation requires both audited arena and actual potions");
NearbyChests.Bag[1001] = 0; Check(Input("核验协作").Contains("未通过") && !project.Complete && project.Paused, "final recheck catches supplies consumed since stage completion");

Reset(); Input("扩建2间NPC住房"); project = CompanionCollaborationSystem.Current!;
Input("备料我来"); Input("继续协作");
Check(Modules.Previews == 0, "player housing material work does not start crafting");
Check(Input("我做完了").Contains("未通过"), "housing materials cannot be fabricated by chat");
var blueprint = StructureLibrary.List().Single(e => e.Id == "preset:npc2").Load();
foreach (var pair in blueprint.Materials()) NearbyChests.Bag[pair.Key] = pair.Value;
Input("核验协作"); Input("住房我来"); Input("继续协作"); string playerBuild = TaskId();
Check(CompanionCollaborationSystem.PlayerOwns(playerBuild), "delegated housing is bound to player ownership for native confirmation guard");
Check(!CompanionCollaborationSystem.CanExecute(Main.LocalPlayer, playerBuild), "delegated player work rejects native execution before any game pump");
StructureWorkshop.Preview(new Point(120, 50));
Check(Input("核验协作").Contains("未通过"), "manual construction requires original scene audit");
Input("住房你来");
Check(!project.Complete && project.Next!.Owner == CollaborationOwner.Companion && project.Next.TaskId.Length == 0, "handoff cancels old player preview and revokes binding");
Input("继续协作"); string build = TaskId();
Check(StructureWorkshop.Origin == new Point(120,50) && build != playerBuild, "handoff reuses selected site with a new unapproved task");
int revision = project.Revision; string resize = Input("住房改成三间");
Check(resize.Contains("已经选址") && project.Revision == revision && project.Rooms == 2, "cannot resize into expanded scope after choosing construction site");
StructureWorkshop.HousingPassed = false; CompanionTaskTracker.CompleteVerified(build, "blueprint placed but room invalid");
Check(!project.Complete && project.Next!.State == CollaborationState.Failed, "native housing acceptance must also pass actual room audit");
Input("继续协作"); string rebuild = TaskId(); StructureWorkshop.HousingPassed = true;
CompanionTaskTracker.CompleteVerified(rebuild, "blueprint audit");
Check(project.Complete, "housing project completes only after actual room acceptance");

Reset(); Input("扩建1间NPC住房"); Input("备料我来"); Input("继续协作");
blueprint = StructureLibrary.List().Single(e => e.Id == "preset:npc1").Load();
foreach (var pair in blueprint.Materials()) NearbyChests.Bag[pair.Key] = pair.Value;
Input("核验协作"); Input("继续协作"); StructureWorkshop.Preview(new Point(90,40));
string partial = TaskId();
var builtCells = blueprint.Steps.Where(s => s.Item == 1).Take(5).ToArray();
foreach (var cell in builtCells) HousingStructureSite.Completed.Add(cell);
NearbyChests.Bag[1] = 0;
CompanionTaskTracker.SetResult(partial,"material exhausted after five actual cells"); CompanionTaskTracker.Close(partial,"construction_failed",0);
Input("继续协作");
Check(CompanionCollaborationSystem.Current!.Next!.Id == "materials" && Input("核验协作").Contains("未通过"), "changed live resources reopen supplies instead of pretending historical stock remains");
Input("备料你来"); Input("继续协作");
Check(CraftChainSystem.Targets.Single(t=>t.Item==1).Count == blueprint.Materials()[1]-5, "replacement material plan deducts verified placed cells instead of rebuilding or gathering them twice");
Check(CompanionMemorySystem.Events.Any(e=>e.Kind=="collaboration_event"), "shared project decisions and progress use existing execution log");

Reset(); Input("一起准备打克眼"); Input("继续协作"); string exceptional = TaskId();
CompanionTaskTracker.SetResult(exceptional,"执行异常，副作用待检查"); CompanionTaskTracker.Close(exceptional,"construction_failed",0);
int previewsBeforeUnknown = Modules.Previews;
Check(Input("继续协作").Contains("未知副作用") && Modules.Previews == previewsBeforeUnknown, "unknown side effects never become an automatic new preview");
Reset(); Input("一起准备打克眼"); Input("继续协作");
CompanionCommandService.Advance();
Check(CompanionCollaborationSystem.Current!.Paused, "unrelated control command invalidates cooperative execution");
string unrelated = Modules.Begin("craft_chain");
CompanionTaskTracker.CompleteVerified(unrelated,"unrelated success");
Check(!CompanionCollaborationSystem.Current.Complete, "unrelated success is never adopted by paused project");

Reset(); Input("扩建1间NPC住房"); Input("继续协作"); string supply = TaskId();
Input("住房改成三间"); project = CompanionCollaborationSystem.Current!;
Check(project.Rooms == 3 && project.Next!.TaskId.Length == 0, "mid-supply scope edit stops old work and revokes its preview");
CompanionTaskTracker.CompleteVerified(supply, "old smaller supply success"); Check(!project.Complete && project.Next!.Id == "materials", "old smaller-budget callback cannot satisfy resized project");
Input("继续协作");
Check(CraftChainSystem.Targets.Single(t => t.Item == 2).Count == 96, "resized material plan uses existing three-room blueprint quantities");
Input("暂停协作"); var oldPreview = Modules.PreviewId;
Check(project.Paused, "explicit pause is visible in ledger"); Input("继续协作");
Check(Modules.PreviewId != oldPreview && !project.Paused && Modules.Executions == 0, "resuming requires fresh preview and never implicitly confirms");

foreach (string invalid in new[] { "world", "player", "npc", "death", "ghost", "multiplayer" })
{
    Reset(); Input("一起准备打克眼"); Input("继续协作");
    switch (invalid)
    {
        case "world": CompanionMemorySystem.Session = Guid.NewGuid(); break;
        case "player": Main.player[0] = new() { whoAmI = 0 }; break;
        case "npc": ChatCoordinator.Agent = new(); break;
        case "death": Main.LocalPlayer.dead = true; break;
        case "ghost": Main.LocalPlayer.ghost = true; break;
        case "multiplayer": Main.netMode = 1; break;
    }
    Check(!CompanionCollaborationSystem.CanExecute(Main.LocalPlayer, TaskId()), "authority changes reject old confirmation and execution before next cleanup tick: " + invalid);
    collaboration.PostUpdateWorld();
    Check(CompanionCollaborationSystem.Current == null && Modules.Executions == 0, "project cannot outlive authority change: " + invalid);
}
Reset(); Input("一起准备打克眼"); Input("继续协作");
CompanionTaskTracker.Close(TaskId(), "player_rejected", 0);
Check(CompanionCollaborationSystem.Current!.Paused, "rejecting native preview pauses the composite task too");
Reset(); Input("一起准备打克眼"); Input("竞技场我来"); Input("继续协作");
Check(StructureWorkshop.Origin == null, "player-owned arena first enters explicit site selection before unpausing manual construction");
CompanionTaskTracker.Close(TaskId(), "construction_cancelled", 0);
Check(CompanionCollaborationSystem.Current!.Paused, "cancelling delegated player preview also pauses project and revokes its binding");
Reset(); Input("一起准备打克眼"); Input("继续协作"); Input("不要协作扩建住房了");
Check(CompanionCollaborationSystem.Current!.Paused, "ambiguous negative or unsupported new constraints pause existing work before clarification");
Reset(); Input("一起准备打克眼，别拆家"); Check(CompanionCollaborationSystem.Current == null && Modules.Previews == 0, "unsupported inline constraints are surfaced before starting work");
Console.WriteLine($"All {passed} collaboration adapter checks passed.");
