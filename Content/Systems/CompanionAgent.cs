using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

// Runs one goal as a loop: ask the model for the next tool call, run it in the game, show a grey line,
// feed the real result back, repeat. Long tools (chopping, mining) are the companion's existing tasks;
// the loop waits for their task card to close. Everything runs on the game thread; only the model
// request is asynchronous.
public sealed class CompanionAgent : ModSystem
{
    private static AgentLoop _loop = new("");
    private static string _goal => _loop.Goal;
    private static List<AgentStep> _steps => _loop.Steps;
    private static int _player = -1;
    private static Guid _session;
    private static CheatPermit _cheatPermit;
    private static string _cheatGoal = "";
    private static CompanionRecoverySystem.Owner? _recovery;
    private static Task<string>? _request;
    private static CancellationTokenSource? _cancellation;
    // A long tool in progress: the companion task it started, and when that task closed.
    private static string? _jobTask;
    private static AgentCall? _jobCall;
    private static long _jobClosedAt;
    private static Dictionary<int, int>? _bagBeforeJob;
    // After "ask": the next message from the player answers the question and continues this goal.
    private static string? _question;
    private static DateTime _askedAt;

    internal static bool Thinking => _request != null;
    internal static bool Running => _request != null || _jobTask != null;
    internal static bool AwaitingAnswer => _question != null && DateTime.UtcNow - _askedAt < TimeSpan.FromMinutes(3);

    // A new message from the player: a fresh goal, or the answer to the question we just asked.
    internal static void Start(Player player, string text)
    {
        if(AwaitingAnswer&&_recovery is {} recovering)
        {
            if(!CompanionRecoverySystem.Allows(recovering)){Finish("恢复上下文已失效，请重新说明任务。",Color.Orange);return;}
            if(RecoveryPolicy.IsStop(text)){Finish("已停止恢复，已完成部分保留。",Color.LightBlue);return;}
            _loop.Answer(_question!,text);
            _question=null;RequestNext();return;
        }
        CompanionCollaborationSystem.ExternalCommand();
        CompanionGardenSystem.Cancel("收到新目标，种植／采收已停止，已完成部分保留。");
        CompanionExpeditionSystem.Cancel("收到新目标，挖掘或探路任务已停止。");
        CompanionCombatSystem.Cancel("收到新目标，护卫或清理任务已停止。");
        CraftChainSystem.Cancel("收到新任务，合成链已取消，已完成部分保留。");
        StructureWorkshop.Cancel("收到新任务，施工已取消，已完成部分保留。");
        CompanionConstructionSystem.Cancel("收到新任务，平台备料施工已取消。");
        bool answering = AwaitingAnswer;
        string? question = _question;
        var keep = answering ? _steps.ToList() : new List<AgentStep>();
        string goal = answering ? _goal : text.Trim();
        Cancel();
        _loop = new AgentLoop(goal, keep);
        if(answering)_loop.Answer(question!,text);
        _player = player.whoAmI;
        _session = CompanionMemorySystem.Session;
        _cheatPermit = CheatTools.Authority.Capture();
        _cheatGoal = text.Trim();
        CompanionMemorySystem.Record("agent_started", null, null, new { goal = _goal, answering }, null, null);
        RequestNext();
    }

    internal static void Cancel()
    { ResetLoop(); CompanionRecoverySystem.Invalidate(); }

    private static void ResetLoop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _request = null;
        _jobTask = null;
        _jobCall = null;
        _question = null;
        _loop = new AgentLoop("");
        _recovery=null;
    }

    internal static void Recover(CompanionRecoverySystem.Owner owner,TaskFailureReceipt receipt)
    {
        ResetLoop();_loop=owner.Loop;_loop.Recover(receipt);_recovery=owner;
        _player=owner.Player.whoAmI;_session=owner.Flow.Session;_cheatPermit=owner.Cheat;_cheatGoal=owner.CheatGoal;
        RequestNext();
    }

    public override void PostUpdateWorld() { if (Main.netMode == NetmodeID.SinglePlayer) Pump(); }
    public override void OnWorldUnload() => Cancel();
    public override void ClearWorld() => Cancel();

    private static Player? Owner => _player >= 0 && _player < Main.maxPlayers && Main.player[_player].active && !Main.player[_player].dead
        ? Main.player[_player] : null;

    internal static void Pump()
    {
        if(_recovery is {} recovery)
        {
            if(!CompanionRecoverySystem.Allows(recovery)){Cancel();return;}
            if(Main.gamePaused||ModContent.GetInstance<CompanionUISystem>().WantsPause)return;
        }
        if (_request == null && _jobTask == null) return;
        // Dying only pauses the goal: pick it up again after the respawn.
        if (_session == CompanionMemorySystem.Session && _player >= 0 && _player < Main.maxPlayers
            && Main.player[_player].active && Main.player[_player].dead) return;
        if (_session != CompanionMemorySystem.Session || Owner is not Player player) { Cancel(); return; }
        if (_jobTask != null) { PumpJob(); return; }
        if (!_request!.IsCompleted) return;
        var request = _request;
        _request = null;
        if (request.IsCanceled) return;
        var decision = request.IsFaulted
            ? _loop.OnModel(null, request.Exception!.GetBaseException() is var e && e is FormatException ? null : e.Message)
            : _loop.OnModel(request.Result);
        Act(player, decision);
    }

    private static void Act(Player player, AgentDecision decision)
    {
        if (decision.Call != null)
            CompanionMemorySystem.Record("agent_step", null, null, new { goal = _goal, call = decision.Call }, null, null);
        switch (decision.Kind)
        {
            case AgentOutcome.Ask: RequestNext(); return;
            case AgentOutcome.Finish: Finish(decision.Text, decision.Warn ? Color.Orange : Color.LightGreen); return;
            case AgentOutcome.Question:
                Say(decision.Text, Color.LightBlue);
                _question = decision.Text;
                _askedAt = DateTime.UtcNow;
                return;
            case AgentOutcome.Forward:
                {
                    Note("查资料", "交给资料库");
                    string question = decision.Text;
                    End();
                    CompanionInquirySystem.Submit(player, question);
                    return;
                }
            default: Run(player, decision.Call!); return;
        }
    }

    private static void RequestNext()
    {
        if (Owner is not Player player) { Cancel(); return; }
        if(_recovery is {} recovering&&!CompanionRecoverySystem.Request(recovering))
        {Finish("恢复期限或请求预算已用尽，已停止。已完成部分保留，请查看最后的失败收据。",Color.Orange);return;}
        if (_loop.BeforeRequest() is AgentDecision stop) { Act(player, stop); return; }
        var store = CompanionMemorySystem.Store;
        var recent = store?.Memory.Dialogue.TakeLast(4).Select(l => (l.Role == "player" ? "玩家：" : "你：") + Clip(l.Text, 60)).ToList()
            ?? new List<string>();
        string state;
        try { state = _loop.Prompt(_recovery is {} live?CompanionRecoverySystem.CurrentResources(live):Snapshot(player), recent); }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {Finish("最新状态读取失败，已停止；已完成部分保留："+ex.Message,Color.Orange);return;}
        if(_recovery is {} checkedOwner&&!CompanionRecoverySystem.Allows(checkedOwner)){Cancel();return;}
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(CompanionMemorySystem.WorldToken);
        try { _request = TerrarAI.RequireClient().PlanAgentStepAsync(state, _cancellation.Token, _loop.Schema); }
        catch(Exception ex) { Finish("模型请求未能启动，已停止："+ex.Message,Color.Orange);return; }
        _ = _request.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Run(Player player, AgentCall call)
    {
        if(_loop.ObservationOnly&&!ObservationPolicy.Allows(call))
        {Finish(ObservationPolicy.Blocked,Color.Orange);return;}
        if(_recovery is {} recovery&&(!CompanionRecoverySystem.Allows(recovery)||!RecoveryPolicy.Allows(call)))
        {Finish("恢复权限已失效或操作超出恢复范围，已停止。",Color.Orange);return;}
        using var dispatch=CompanionRecoverySystem.Dispatch(player,_loop,call,_recovery,_cheatPermit,_cheatGoal);
        switch (call.Tool)
        {
            case "replan":
                {
                    var owner=_recovery;
                    if(owner==null){Finish("当前没有待恢复任务。",Color.Orange);return;}
                    string failedTask=owner.Flow.TaskId;
                    string replanResult=CompanionRecoverySystem.Replan(owner);
                    // Native Begin resets this loop and binds the new preview. Never cancel that new lineage.
                    if(owner.Flow.TaskId!=failedTask&&!owner.Flow.Recovering)ResetLoop();
                    else End();
                    CompanionUISystem.Message("Qwen："+replanResult,Color.LightBlue);
                    return;
                }
            case "cheat_world":
                {
                    var request=CheatWorldRequest.FromTool(call.Mode,call.Item);
                    string cheatResult=CheatWorldRequest.Authorizes(_cheatGoal,request)
                        ? CheatTools.World(player,request,_cheatPermit)
                        : "这次原话未明确授权这个世界操作。请单独说明时间、晴雨、Boss名称或把世界出生点设在这里。";
                    End(); Say(cheatResult,Color.LightBlue); return;
                }
            case "garden":
                {
                    string goal=_goal;End();
                    if(GardenRequest.TryParse(goal,out var parsedGarden,out string gardenError))
                        Say(parsedGarden==null?gardenError:CompanionGardenSystem.Begin(player,parsedGarden),Color.LightBlue);
                    else Say(CompanionGardenSystem.Begin(player,new GardenRequest(call.Mode=="plant"?GardenOperation.Plant:GardenOperation.Harvest,
                        call.Item is "草药" or "herbs"?null:GardenRequest.CropName(call.Item),call.Count==0?(call.Mode=="plant"?(GardenRequest.CropName(call.Item)==GardenCrop.Acorn?3:5):20):call.Count,call.Mode=="plant"||call.Count>0)),Color.LightBlue);
                    CompanionUISystem.OpenChat();return;
                }
            case "prepare_eye":
                End(); CompanionUISystem.StartArena(); return;
            case "expedition":
                End(); Say(CompanionExpeditionSystem.Begin(player,new ExpeditionRequest(call.Mode=="underground"?ExpeditionMode.Underground:call.Mode=="tunnel"?ExpeditionMode.Tunnel:ExpeditionMode.Scout,call.Count==0?32:call.Count)), Color.LightBlue); return;
            case "locate":
                if(_recovery!=null)break; // recovery observations return to this goal, not a terminal map handoff
                End(); Say(MapLocationSystem.FromTool(player, call.Item), Color.LightBlue); return;
            case "combat":
                End(); Say(CompanionCombatSystem.Begin(player, call.Mode), Color.LightBlue); return;
            case "craft_chain":
                {
                    string goal = _goal;
                    End();
                    var request = CraftChainRequest.TryParse(goal, out var parsed, out _) && parsed != null ? parsed :
                        new CraftChainRequest(call.Item, Math.Max(1,call.Count), call.Item is "铁盔甲" or "铁套装" or "一套铁盔甲");
                    Say(CraftChainSystem.Begin(player, request), Color.LightBlue);
                    CompanionUISystem.OpenChat(); return;
                }
            case "build":
                {
                    var entry = StructureLibrary.List().FirstOrDefault(e => e.Id == $"preset:npc{Math.Clamp(call.Rooms, 1, 3)}");
                    End();
                    if (entry == null) { Say("找不到小屋蓝图。", Color.Orange); return; }
                    Say($"好，盖「{entry.Name}」。在世界里点一下要放的位置，看清材料和冲突再确认。", Color.LightGreen);
                    CompanionUISystem.StartStructure(entry);
                    return;
                }
            case "platform":
                {
                    string goal = _goal;
                    End();
                    if (PlatformRequest.TryParse(goal, out var request, out string error))
                    {
                        if (request == null) Say(error, Color.Orange);
                        else CompanionUISystem.StartConstruction(request);
                        return;
                    }
                    Say("好，在世界里拖出要铺平台的范围。", Color.LightGreen);
                    CompanionUISystem.StartConstruction();
                    return;
                }
            case "chop":
            case "mine":
                StartJob(player, call);
                return;
        }
        string result;
        try { result = Instant(player, call); }
        catch (Exception ex) { result = "出错了：" + ex.Message; }
        // A station placed / item bought or taken for a craft that was waiting: say whether it can go now.
        if (call.Tool is not "craft" && !AgentProtocol.IsLookup(call.Tool) && AgentProtocol.IsSuccess(result)
            && AgentProtocol.PendingCraft(_steps) is var (want, wantCount) && Crafting.Resolve(want) is int wantType and > 0)
            result += "\n" + Crafting.Progress(player, wantType, wantCount);
        Note(call.Label, result);
        _loop.Record(call, result);
        if (_loop.AfterRun(call, result) is AgentDecision done) { Act(player, done); return; }
        RequestNext();
    }

    // Tools that finish within the tick.
    private static string Instant(Player player, AgentCall call)
    {
        switch (call.Tool)
        {
            case "locate": return MapLocationSystem.FromTool(player,call.Item);
            case "inventory": return Inventory(player);
            case "look": return Look(player);
            case "house_materials": return Crafting.HouseAnswer(player);
            case "chests": return NearbyChests.Describe(player, call.Item);
            case "chest_put":
            case "chest_take":
                {
                    var operation = call.Tool == "chest_put" ? ChestOperation.Deposit : ChestOperation.Withdraw;
                    if (!ChestRequest.Authorizes(_goal, operation, call.Item, call.Count, _steps, Crafting.ResolveExact))
                        return "这次没有明确授权这项箱子存取，或已经完成。请让玩家单独说明物品和数量，例如：从附近箱子取5个铁锭。";
                    return NearbyChests.Transfer(player, new ChestRequest(operation, new[] { new ChestItemRequest(call.Item, call.Count) }));
                }
            case "storage":
                {
                    string extra = ModTools.ImproveGame.Describe(player);
                    string stored = ModTools.Storage.Available || call.Item.Length == 0 ? ModTools.Storage.Describe(call.Item) : "";
                    return string.Join("\n", new[] { NearbyChests.Describe(player, call.Item), stored, extra }.Where(x => x.Length > 0));
                }
            case "npcs": return ModTools.Census.Describe();
            case "spawn_item":
                {
                    int type = Crafting.Resolve(call.Item);
                    return type == 0 ? Crafting.Unknown(call.Item) : CheatTools.SpawnItem(player, type, Math.Max(1, call.Count), _cheatPermit);
                }
            case "structure": return CheatTools.Generate(player, call.Text, _cheatPermit);
            case "shop":
                {
                    int type = Crafting.Resolve(call.Item);
                    return type == 0 ? Crafting.Unknown(call.Item) : ModTools.Shop(type);
                }
            case "buy":
                {
                    int type = Crafting.Resolve(call.Item);
                    if (type == 0) return Crafting.Unknown(call.Item);
                    if (!AgentProtocol.PlayerAgreedToBuy(_goal, _steps))
                        return "要花玩家的钱，先用 ask 问玩家要不要买（说清价格）。" + ModTools.Shop(type);
                    return ModTools.Buy(player, type, Math.Max(1, call.Count)).Message;
                }
            case "recipe":
                {
                    int type = Crafting.Resolve(call.Item);
                    return type == 0 ? Crafting.Unknown(call.Item) : Crafting.Describe(player, type);
                }
            case "craft":
                {
                    int type = Crafting.Resolve(call.Item);
                    if (type == 0) return Crafting.Unknown(call.Item);
                    return Craft(player, type, Math.Max(1, call.Count), 0);
                }
            case "place":
                {
                    int type = Crafting.Resolve(call.Item);
                    return type == 0 ? Crafting.Unknown(call.Item) : PlaceNear(player, type);
                }
            case "control":
                return CompanionCommandService.Execute(player, call.Mode, fromAgent: true);
            default:
                return "这个工具现在用不了。";
        }
    }

    // ---- long tools: the companion's own gathering task ------------------------------------------
    private static void StartJob(Player player, AgentCall call)
    {
        var companion = ChatCoordinator.GetOrCreateCompanion(player);
        if (companion == null) { Finish("同伴不在场，没法去干活。", Color.Orange); return; }
        string ore = call.Ore.Replace("矿", "").Trim();
        string? block = call.Tool == "mine" ? TaskGoal.OreWord("挖" + ore) : null;
        bool isBlock = TaskGoal.IsBlock(block);
        int count = Math.Clamp(call.Count, 1, isBlock ? TaskGoal.MaxBlocks : TaskGoal.MaxCount);
        string command = call.Tool == "chop" ? $"砍{count}棵树" : isBlock ? $"挖{count}块{block}" : $"挖{count}处{ore}矿";
        companion.ReceiveCommand(player, command);
        _jobTask = CompanionTaskTracker.Current?.TaskId;
        _jobCall = call;
        _jobClosedAt = 0;
        _bagBeforeJob = BagCounts(player);
        Note(call.Label, "开始了");
        if (_jobTask == null) { _loop.Record(call, "没能开始"); _jobCall = null; RequestNext(); }
    }

    private static void PumpJob()
    {
        var view = CompanionTaskTracker.Current;
        // Another task took over (the player gave a direct order): this goal is no longer ours to finish.
        if (view == null || view.TaskId != _jobTask) { End(); return; }
        if (!view.Closed) return;
        if (view.EndedByPlayer) { End(); return; }   // stopped / waited / recalled / a new order from the player
        if(view.Phase==CompanionTaskPhase.Failed&&CompanionRecoverySystem.OwnsFailure(view.TaskId))return;
        // The haul is attached a moment after the task closes.
        if (_jobClosedAt == 0) { _jobClosedAt = Main.GameUpdateCount; return; }
        if (Main.GameUpdateCount - _jobClosedAt < 75) return;
        string result = (view.PhaseText + "：" + view.OutcomeText).Trim('：');
        // What actually arrived in the bag (the task's own report comes later and is not in the view).
        if (Owner is Player gatherer && _bagBeforeJob != null)
        {
            var before = _bagBeforeJob;
            var gained = BagCounts(gatherer).Where(p => p.Value > before.GetValueOrDefault(p.Key))
                .OrderByDescending(p => p.Value - before.GetValueOrDefault(p.Key)).Take(5)
                .Select(p => $"{Lang.GetItemNameValue(p.Key)} +{p.Value - before.GetValueOrDefault(p.Key)}（现在共 {p.Value}）").ToList();
            result += "\n" + (gained.Count > 0 ? "背包里多了：" + string.Join("、", gained) : "背包里没多东西。");
        }
        // Say plainly whether the haul now covers the craft that sent us gathering; otherwise the model
        // tends to read "only 2 of 10 trees" as failure and keep chopping.
        if (AgentProtocol.PendingCraft(_steps) is var (item, count) && Owner is Player owner
            && Crafting.Resolve(item) is int type and > 0)
            result += "\n" + Crafting.Progress(owner, type, count);
        _loop.Record(_jobCall!, result);
        _jobTask = null;
        _jobCall = null;
        RequestNext();
    }

    // ---- what the model sees ---------------------------------------------------------------------
    internal static string Snapshot(Player p)
    {
        var world = CompanionWorldInfo.Capture(p);
        var pick = ToolSelector.FindBestTool(p, ToolType.Pickaxe);
        var axe = ToolSelector.FindBestTool(p, ToolType.Axe);
        var near = new List<string>();
        p.AdjTiles();
        if (p.adjTile[TileID.WorkBenches]) near.Add("工作台");
        if (p.adjTile[TileID.Furnaces]) near.Add("熔炉");
        if (p.adjTile[TileID.Anvils] || p.adjTile[TileID.MythrilAnvil]) near.Add("砧");
        return $"{world.Clock} · {world.Biome} · {world.Layer}\n" +
            $"生命 {world.Life}/{world.LifeMax} · 防御 {p.statDefense} · 镐：{pick?.Name ?? "没有"} · 斧：{axe?.Name ?? "没有"}\n" +
            $"玩家身边的制作站：{(near.Count > 0 ? string.Join("、", near) : "没有")}\n" +
            (ModTools.Storage.Available ? "装了魔法存储（storage 能翻，craft 会自动取料）\n" : "") +
            $"附近可用箱子：{NearbyChests.Count(p)}个（半径{NearbyChests.Radius}格；chests可查，craft自动补料）\n" +
            $"作弊模式：{(CheatTools.Enabled ? "开" : "关")}\n" +
            "背包：" + Bag(p, 14) + (CompanionCollaborationSystem.Current is {} project ? "\n【协作计划，以程序记录为准】\n" + project.Describe() : "");
    }

    private static string Bag(Player p, int max)
    {
        var items = p.inventory.Take(58).Where(i => !i.IsAir).GroupBy(i => i.type)
            .Select(g => (name: Lang.GetItemNameValue(g.Key), count: g.Sum(i => i.stack)))
            .OrderByDescending(x => x.count).ToList();
        if (items.Count == 0) return "空";
        return string.Join("、", items.Take(max).Select(x => $"{x.name}×{x.count}")) + (items.Count > max ? $" 等 {items.Count} 种" : "");
    }

    private static Dictionary<int, int> BagCounts(Player p) =>
        p.inventory.Take(58).Where(i => i != null && !i.IsAir).GroupBy(i => i.type).ToDictionary(g => g.Key, g => g.Sum(i => i.stack));

    private static string Inventory(Player p)
    {
        var armor = p.armor.Take(3).Where(i => !i.IsAir).Select(i => i.Name).ToList();
        var accessories = p.armor.Skip(3).Take(7).Where(i => !i.IsAir).Select(i => i.Name).ToList();
        return "背包：" + Bag(p, 40) + "\n盔甲：" + (armor.Count > 0 ? string.Join("、", armor) : "没穿") +
            " · 饰品：" + (accessories.Count > 0 ? string.Join("、", accessories) : "没有");
    }

    private static string Look(Player p)
    {
        var world = CompanionWorldInfo.Capture(p);
        var center = p.Bottom.ToTileCoordinates();
        int trees = TreeHelper.FindTreeBasesNear(center, 28).Count;
        var ores = new Dictionary<string, int>();
        for (int y = center.Y - 20; y <= center.Y + 20; y++)
            for (int x = center.X - 20; x <= center.X + 20; x++)
            {
                if (!WorldGen.InWorld(x, y, 2)) continue;
                var t = Framing.GetTileSafely(x, y);
                if (!t.HasTile || !TileID.Sets.Ore[t.TileType]) continue;
                int item = StructureLibrary.ItemForTile(t.TileType, 0);
                string name = item > 0 ? Lang.GetItemNameValue(item) : "矿";
                ores[name] = ores.GetValueOrDefault(name) + 1;
            }
        return $"{world.Clock} · {world.Weather} · {world.Biome} · {world.Layer}\n" +
            $"附近的树：{trees} 棵 · 看得到的矿：{(ores.Count > 0 ? string.Join("、", ores.Select(o => $"{o.Key} {o.Value} 格")) : "没有")}\n" +
            $"附近友方：{(world.Nearby.Length > 0 ? string.Join("、", world.Nearby.Take(6)) : "没有")} · 附近敌怪：{world.NearbyHostiles}";
    }

    // ---- placing an item next to the player -----------------------------------------------------
    private static string PlaceNear(Player p, int type)
    {
        var sample = ContentSamples.ItemsByType[type];
        if (sample.createTile < 0) return $"{Crafting.Name(type)}不是能放下的东西。";
        int slot = Array.FindIndex(p.inventory, 0, 58, i => !i.IsAir && i.type == type);
        if (slot < 0) return $"背包里没有{Crafting.Name(type)}，先做一个。";
        int tile = sample.createTile, style = sample.placeStyle;
        int feet = (int)(p.Bottom.Y / 16f);
        int px = (int)(p.Center.X / 16f);
        foreach (int dx in new[] { 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8, -8, 10, -10 })
            foreach (int dy in new[] { 0, -1, 1, -2, 2, -3, 3 })
            {
                int x = px + dx, y = feet - 1 + dy;
                if (!WorldGen.InWorld(x, y, 5) || !ClearFootprint(x, y, tile, style)) continue;
                bool placed = false;
                if (TileObjectData.GetTileData(tile, style) != null)
                {
                    if (TileObject.CanPlace(x, y, tile, style, p.direction, out var obj) && TileObject.Place(obj))
                    {
                        TileObjectData.CallPostPlacementPlayerHook(x, y, tile, style, p.direction, obj.alternate, obj);
                        placed = true;
                    }
                }
                else placed = WorldGen.PlaceTile(x, y, tile, mute: false, forced: false, plr: p.whoAmI, style: style);
                if (!placed || !Framing.GetTileSafely(x, y).HasTile || Framing.GetTileSafely(x, y).TileType != tile) continue;
                WorldGen.SquareTileFrame(x, y);
                var item = p.inventory[slot];
                if (--item.stack <= 0) item.TurnToAir();
                return $"放好了：{Crafting.Name(type)}在你{(dx > 0 ? "右" : "左")}边 {Math.Abs(dx)} 格。";
            }
        return $"你旁边找不到能放{Crafting.Name(type)}的平地，换个空旷点的地方再试。";
    }

    // craft, as a player would do it: when only a station is missing, put one down first (the player's own,
    // or one made from the bag on the spot), then make the item. Stations of stations go one level deep.
    private static string Craft(Player player, int type, int count, int depth)
    {
        var made = Crafting.Make(player, type, count);
        if (made.Done || depth > 1) return made.Message;
        var missing = Crafting.MissingStations(player, type, count);
        if (missing.Count == 0) return made.Message;
        var notes = new List<string>();
        foreach (int tile in missing)
        {
            string? placed = null;
            foreach (int station in Crafting.StationItems(player, tile).Take(40))
            {
                bool owned = player.inventory.Take(58).Any(i => !i.IsAir && i.type == station);
                if (!owned)
                {
                    string built = Craft(player, station, 1, depth + 1);
                    if (!built.StartsWith("做好了")) continue;
                }
                string result = PlaceNear(player, station);
                placed = result.StartsWith("放好了") ? $"先{(owned ? "把" : "做了一个")}{Crafting.Name(station)}" + result.Replace("放好了：" + Crafting.Name(station), owned ? "放" : "，放") : null;
                if (placed == null) return made.Message + $"\n想先放个{Crafting.Name(station)}，但{result}";
                break;
            }
            if (placed == null) return made.Message + $"\n（背包里没有{Crafting.Station(tile)}，材料也不够现做一个）";
            notes.Add(placed.TrimEnd('。'));
        }
        var again = Crafting.Make(player, type, count);
        string prep = string.Join("；", notes) + "。";
        return again.Done ? again.Message + " 为此" + prep : prep + again.Message;
    }

    // The object's cells must be empty, or hold only grass, flowers and vines (which a swing would cut anyway).
    // Those are cleared; anything else (blocks, pots, furniture) means this spot is taken.
    private static bool ClearFootprint(int x, int y, int tile, int style)
    {
        var data = TileObjectData.GetTileData(tile, style);
        int w = data?.Width ?? 1, h = data?.Height ?? 1;
        int left = x - (data?.Origin.X ?? 0), top = y - (data?.Origin.Y ?? 0);
        var cut = new List<(int, int)>();
        for (int cx = left; cx < left + w; cx++)
            for (int cy = top; cy < top + h; cy++)
            {
                var t = Framing.GetTileSafely(cx, cy);
                if (!t.HasTile) continue;
                bool breakable = Main.tileCut[t.TileType] || TileID.Sets.BreakableWhenPlacing[t.TileType];
                if (!breakable || t.TileType == TileID.Pots || Main.tileContainer[t.TileType]) return false;
                cut.Add((cx, cy));
            }
        foreach (var (cx, cy) in cut) WorldGen.KillTile(cx, cy, noItem: true);
        return true;
    }

    // ---- talking ---------------------------------------------------------------------------------
    private static void Note(string label, string result) =>
        CompanionUISystem.Publish("· " + label + (result.Length > 0 ? "：" + Clip(result.Replace("\n", " "), 70) : ""), Color.LightGray);

    private static void Say(string text, Color color)
    {
        Remember(text);
        CompanionUISystem.Message("Qwen：" + text, color);
    }

    private static void Remember(string reply) => CompanionMemorySystem.Store?.AddDialogue(_goal, reply);

    private static void Finish(string text, Color color)
    {
        Say(text, color);
        End();
    }

    private static void End()
    {
        CompanionMemorySystem.Record("agent_finished", null, null, new { goal = _goal, steps = _steps.Select(s => s.Call + " → " + s.Result) }, null, null);
        Cancel();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
