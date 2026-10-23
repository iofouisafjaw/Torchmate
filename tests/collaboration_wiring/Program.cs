using Mono.Cecil;

// Inspect the compiled production assembly without loading Terraria or executing native game code.
string path = args.Length > 0 ? args[0] : Path.GetFullPath("bin/Debug/net8.0/TerrarAI.dll");
using var assembly = AssemblyDefinition.ReadAssembly(path);
var types = assembly.MainModule.GetTypes().ToArray(); int passed = 0;
TypeDefinition Type(string name) => types.Single(t => t.FullName == "TerrarAI.Content." + name);
bool Calls(MethodDefinition m, string type, string method) => m.HasBody && m.Body.Instructions.Any(i =>
    i.Operand is MethodReference r && r.DeclaringType.FullName == "TerrarAI.Content." + type && r.Name == method);
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
void Binding(string from, string method, string target, string call) => Check(Type(from).Methods.Where(m => m.Name == method).Any(m => Calls(m, target, call)), from + "." + method + " -> " + target + "." + call);
Binding("Systems.ChatCoordinator", "HandleInput", "Systems.CompanionCollaborationSystem", "TryInput");
Binding("Systems.CompanionCommandService", "Advance", "Systems.CompanionCollaborationSystem", "ExternalCommand");
Binding("Systems.CompanionAgent", "Start", "Systems.CompanionCollaborationSystem", "ExternalCommand");
Binding("Systems.CompanionAgent", "Start", "Systems.StructureWorkshop", "Cancel");
Binding("Systems.CompanionAgent", "Snapshot", "Systems.CompanionCollaborationSystem", "get_Current");
Binding("Systems.CompanionTaskTracker", "Set", "Systems.CompanionCollaborationSystem", "Observe");
Binding("Systems.CompanionRecoverySystem", "Began", "Systems.CompanionCollaborationSystem", "RecoveryBound");
Binding("Systems.StructureWorkshop", "Confirm", "Systems.CompanionCollaborationSystem", "PlayerOwns");
Binding("Systems.CraftChainSystem", "Confirm", "Systems.CompanionCollaborationSystem", "PlayerOwns");
Binding("Systems.StructureWorkshop", "CanConfirm", "Systems.CompanionCollaborationSystem", "PlayerOwns");
Binding("Systems.StructureWorkshop", "CanPrepareArena", "Systems.CompanionCollaborationSystem", "PlayerOwns");
foreach (var (type, method) in new[] { ("StructureWorkshop", "Confirm"), ("StructureWorkshop", "CanConfirm"), ("StructureWorkshop", "CanPrepareArena"),
    ("StructureWorkshop", "Tick"), ("CraftChainSystem", "Confirm"), ("CraftChainSystem", "PostUpdateWorld") })
    Binding("Systems." + type, method, "Systems.CompanionCollaborationSystem", "CanExecute");
Binding("Systems.StructureWorkshop", "BuildSite", "Systems.HousingStructureSite", ".ctor");
Binding("Systems.StructureWorkshop", "Tick", "Systems.StructureWorkshop", "BuildSite");
Binding("Systems.StructureWorkshop", "Tick", "Systems.StructureWorkshop", "CollaborationFinalAudit");
Binding("Systems.StructureWorkshop", "CollaborationFinalAudit", "Systems.StructureWorkshop", "CollaborationAudit");
Binding("Systems.StructureWorkshop", "CollaborationAudit", "Systems.HousingInspector", "Scan");
Binding("Systems.StructureWorkshop", "CollaborationAudit", "Companion.PlatformBlueprint", "Audit");
Binding("Systems.CraftChainSystem", "BeginTargets", "Systems.CraftChainSystem", "Propose");
Binding("Systems.CraftChainSystem", "Replan", "Systems.CraftChainSystem", "BeginTargets");
Binding("Systems.CompanionCollaborationSystem", "Continue", "Systems.CraftChainSystem", "BeginTargets");
Binding("Systems.CompanionCollaborationSystem", "Continue", "Systems.StructureWorkshop", "BeginArena");
Binding("Systems.CompanionCollaborationSystem", "Continue", "Systems.StructureWorkshop", "Begin");
Binding("Systems.CompanionCollaborationSystem", "Halt", "Systems.CompanionCommandService", "Advance");
Binding("Systems.CompanionCollaborationSystem", "Halt", "Systems.CompanionAgent", "Cancel");
var orchestratorCalls = Type("Systems.CompanionCollaborationSystem").Methods.Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
Check(!orchestratorCalls.Any(r => r.Name is "Confirm" or "Approve" or "SpawnItem" or "Generate" || r.DeclaringType.FullName == "Terraria.WorldGen"),
    "coordinator cannot approve resource mutations or write world tiles");
Check(!orchestratorCalls.Any(r => r.DeclaringType.FullName.Contains("CheatTools") || r.Name == "PlanAgentStepAsync"),
    "coordinator adds neither cheat authority nor a second model execution path");
// Verify the rebuilt native adapters are connected to the production safety path.
foreach (string removed in new[] { "Actions.ActionRegistry", "Actions.BuildAction", "Systems.ActionValidator", "Systems.ActionParameterReader",
    "Systems.WorldContext", "Systems.ModelRouter", "Systems.MultiAgentCoordinator", "NPCs.AIAgentRenderer", "Players.SharedLifePlayer" })
    Check(!types.Any(t => t.FullName == "TerrarAI.Content." + removed), "removed inherited type absent: " + removed);
Binding("NPCs.AIAgentNPC", "TickExecuting", "Companion.CompanionPolicy", "Check");
Binding("NPCs.AIAgentNPC", "TickExecuting", "Actions.AgentAction", "Tick");
Binding("NPCs.AIAgentNPC", "TickExecuting", "Systems.CompanionTaskTracker", "Update");
Binding("NPCs.AIAgentNPC", "ConfirmPendingAction", "Companion.ApprovalTicket", "Matches");
Binding("NPCs.AIAgentNPC", "ConfirmPendingAction", "Systems.ActionParser", "Parse");
Binding("NPCs.AIAgentNPC", "CollectNearbyItems", "Systems.CompanionDrops", "Pickup");
Binding("Systems.ProgramGathering", "PickupMatching", "Systems.CompanionDrops", "PickupMatching");
Check(Type("Actions.HellevatorAction").Methods.Where(m => m.IsConstructor).All(m => !m.IsPublic),
    "shaft has no public unbounded constructor");
Binding("Systems.ChatCoordinator","HandleInput","Systems.CompanionExpeditionSystem","BeginTunnel");
Binding("Systems.CompanionExpeditionSystem","BeginTunnel","Companion.OreTunnelPlan","Create");
Binding("Systems.CompanionExpeditionSystem","ConfirmTunnel","Companion.ApprovalTicket","Matches");
Binding("Systems.CompanionExpeditionSystem","ConfirmTunnel","Companion.OreTunnelJob","Approve");
Binding("Systems.CompanionExpeditionSystem","TickActive","Systems.CompanionExpeditionSystem","TickTunnel");
Binding("Systems.CompanionExpeditionSystem","TickTunnel","Companion.OreTunnelJob","Tick");
Binding("Systems.CompanionExpeditionSystem","TickTunnel","Systems.CompanionTaskTracker","Close");
Binding("Systems.NativeOreTunnelSite","Mine","Actions.AgentAction","Tick");
Binding("Systems.NativeOreTunnelSite","Move","Systems.MovementHelper","MoveTowards");
Binding("Systems.NativeExpeditionSite","Read","Systems.DiggingSafety","Read");
Binding("Systems.NativeOreTunnelSite","Read","Systems.DiggingSafety","ReadTunnel");
Binding("Systems.ChatCoordinator","HandleInput","Companion.ObservationPolicy","Matches");
Binding("Systems.ChatCoordinator","HandleInput","Companion.ObservationPolicy","StopsWork");
Binding("Systems.CompanionAgent","Run","Companion.ObservationPolicy","Allows");
Binding("Systems.CompanionAgent","Start","Companion.AgentLoop","Answer");
Binding("Systems.HousingInspector","PlanRepairs","Companion.HousingRepairLayout","Furniture");
Check(Type("Systems.GardenWorld").Methods.Single(m=>m.Name=="Observe").Body.Instructions.Any(i=>
    i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name=="tileFrameImportant"),
    "garden observation distinguishes cosmetic terrain frames from meaningful crop/furniture frames");
Console.WriteLine($"Passed {passed} compiled production wiring checks.");
