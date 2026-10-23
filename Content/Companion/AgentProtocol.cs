using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

// The companion as an agent: the model picks one tool per step, the game runs it and reports back,
// until the model replies, asks, or hands a question to the knowledge base. The model never acts on
// the world itself; every tool checks its own preconditions and reports what really happened.
public sealed record AgentCall(string Tool, string Item = "", int Count = 0, string Ore = "", int Rooms = 0,
    string Mode = "", string Text = "")
{
    public bool Ends => Tool is "reply" or "ask" or "answer" or "build" or "platform" or "craft_chain" or "combat" or "locate" or "expedition" or "prepare_eye" or "garden" or "cheat_world" or "replan";

    // Shown to the player as one grey line, and fed back to the model as the step's label.
    public string Label => Tool switch
    {
        "replan" => "重新规划原任务（需再次确认）",
        "cheat_world" => $"世界操作（作弊）：{Mode} {Item}",
        "garden" => Mode=="plant"?$"规划种植：{Item}":$"规划采收：{Item}",
        "prepare_eye" => "规划克眼战前准备",
        "inventory" => "看背包",
        "look" => "看周围",
        "locate" => $"查找位置：{Item}",
        "expedition" => Mode == "underground" ? "挖到地下层" : Mode == "tunnel" ? "向下挖通道" : "短程探路",
        "recipe" => $"查配方：{Item}",
        "house_materials" => "算小屋材料",
        "storage" => Item.Length > 0 ? $"翻仓库：{Item}" : "翻仓库",
        "chests" => Item.Length > 0 ? $"查附近箱子：{Item}" : "查附近箱子",
        "chest_put" => $"存入箱子：{Item} ×{(Count == 0 ? "全部未收藏" : Count.ToString())}",
        "chest_take" => $"取出箱子：{Item} ×{Count}",
        "npcs" => "查城镇 NPC",
        "shop" => $"查谁卖：{Item}",
        "buy" => $"购买：{Item} ×{Math.Max(1, Count)}",
        "spawn_item" => $"生成（作弊）：{Item} ×{Math.Max(1, Count)}",
        "structure" => Text.Length > 0 ? $"生成建筑（作弊）：{Text}" : "列出存好的建筑",
        "craft" => $"合成：{Item} ×{Math.Max(1, Count)}",
        "craft_chain" => $"规划完整合成链：{Item} ×{Math.Max(1, Count)}",
        "place" => $"放置：{Item}",
        "chop" => $"砍树 ×{Math.Max(1, Count)}",
        "mine" => $"挖矿：{(Ore.Length > 0 ? Ore : "附近的矿")} ×{Math.Max(1, Count)}",
        "build" => $"盖小屋 ×{Math.Clamp(Rooms, 1, 3)}",
        "platform" => "搭平台",
        "combat" => Mode == "clear" ? "清理附近小怪" : Mode == "off" ? "停止攻击" : "保护玩家",
        "control" => Mode switch { "wait" => "原地等待", "recall" => "回到你身边", "stop" => "停下", _ => "跟着你" },
        "answer" => "查资料",
        "ask" => "问你",
        _ => "回复"
    };
}

// Tool is the tool that ran ("" for the player's answers): it tells look-ups from steps that change the world.
public sealed record AgentStep(string Call, string Result, string Tool = "");

public static class AgentProtocol
{
    public const int MaxSteps = 12;

    public static readonly string[] Tools = {
        "inventory", "look", "recipe", "house_materials", "storage", "chests", "locate", "chest_put", "chest_take", "npcs", "shop", "buy", "craft", "place",
        "chop", "mine", "build", "platform", "craft_chain", "combat", "expedition", "prepare_eye", "garden", "control", "spawn_item", "structure", "cheat_world", "replan", "answer", "reply", "ask" };

    public const string SystemPrompt = """
        你是泰拉瑞亚里的同伴 Qwen。玩家给你一个目标，你一步一步调用工具去完成。每次只输出一个工具调用（JSON）。
        工具：
        inventory：看玩家背包里的材料、工具和装备。
        look：看周围：附近的树和矿、城镇 NPC、时间、所在环境。
        locate(item)：查矿、箱子、床的位置并在地图上标记，item 填矿名、矿、箱子、床或我的床。只读查玩家120格内已探索区域；我的床优先核对已设置出生点。由程序直接报告方向、直线距离和坐标，提交后结束，不虚构坐标，不采集或取物。
        recipe(item)：查 item 的配方，以及玩家现在能不能做、还差什么。
        house_materials：盖一间 NPC 小屋要什么，玩家还差什么。
        storage(item)：翻附近箱子、魔法存储、大背包和自动钓鱼机里有什么；item 可留空，填了就只找这个。
        chests(item)：只读查询玩家18格内已关闭且未上锁的箱子，item 可留空。
        chest_put(item,count)：指定物品存入附近箱子，count=0表示全部未收藏数量；只有玩家这次明确说要存该物品和数量才用。
        chest_take(item,count)：从附近箱子取指定物品1–9999个；必须照玩家的物品和数量，不得自行增加或重复取。
        npcs：哪些城镇 NPC 已经住进来，没来的各自需要什么条件。
        shop(item)：哪个 NPC 卖 item（包括突变体、炼金师这些模组 NPC）、多少钱、现在能不能买。
        buy(item,count)：用玩家的钱从住在这里的 NPC 那买。只有玩家明确说了要买才用。
        craft(item,count)：用玩家主背包和附近箱子里的材料按游戏配方合成，成品放进玩家背包；自动取实际材料缺口，回报箱子坐标和数量。
        craft_chain(item,count)：完整合成链交给程序规划真实配方、制作站和采集缺口，显示预览，玩家确认后逐步执行；做套装时item填“铁盔甲”。这是规划入口，不表示已经做成。
        place(item)：把玩家背包里的一件可放置物品（工作台、箱子、火把、家具）放在玩家旁边。
        chop(count)：去附近砍 count 棵树（count 是树的棵数，不是木材数；一棵树一般给 5~15 木材，差 10 木材砍 2 棵就够）。
        mine(ore,count)：去附近挖矿，ore 是矿名（铜、铁、金……），不限就留空；也能挖石块、土块、沙块、泥块、粘土块、雪块、冰块，这时 count 是块数（最多 40，比如熔炉要 20 石块就挖 20）。
        build(rooms)：打开建筑工坊盖 NPC 小屋（rooms=1~3 间），玩家在世界里点位置，确认后用背包材料盖。
        garden(mode,item,count)：种植／采收交给程序扫描并预览，确认后逐株执行。mode=plant或harvest，item填橡实、太阳花、月光草、闪耀根、死亡草、幌菊、火焰花、寒颤棘或草药；count为1–64株，可留空（种草药5株，种橡实3株；采收最多20株已开花草药）。不采幼苗／未开花植株，不催熟。药水缺草药时直接craft_chain，程序优先背包和箱子、再采附近已开花草药。提交garden后结束，等待玩家确认，不自行砍树／挖土／造种子。
        prepare_eye：克眼（克苏鲁之眼）战前准备，程序规划三层100格竞技场、篝火、心灯和药水，显示完整材料及制作站缺口，玩家确认后才备料施工；提交后结束，不能宣称已完成，也不自行拆成craft/place或召唤Boss。
        platform：从玩家原话读取层数、每层长度和间距，生成木平台蓝图与材料预览，玩家确认后才备料施工；没有给尺寸时打开手动框选。不要先调用 craft 或 chop 备料。
        combat(mode)：protect 保护玩家并跟随，clear 清理任务起点附近24格小怪，off 停止攻击。仅近身小怪，不攻击城镇NPC、宠物、Boss，不保证玩家免伤。发起后交给程序执行，不口头声称已击杀。跟随时自动反击可在设置关闭；停止或等待会中止战斗。
        expedition(mode,count)：underground 向下挖到地下层，tunnel 挖三格宽竖井（默认32格深，最多160），scout 按玩家发令时朝向向前探路并尝试返回（默认32格，最多64）。程序规划起点与地层、执行保护和采集许可，报告实际观察到的矿、箱子与危险并标记地图。只交接任务，不虚构探索完成，不调用mine逐格挖、不传送。underground不用count，其他模式count可留空。
        control(mode)：只管同伴自己的移动（玩家不会动）：follow 跟着玩家 / wait 原地等 / recall 回到玩家身边 / stop 停下。
        spawn_item(item,count)：【作弊】直接生成物品放进玩家背包。
        structure(text)：【作弊】用 StructureHelper 在玩家身旁生成一个存好的建筑，text 写建筑名；留空就列出有哪些。
        replan：仅用于程序提供的长任务失败恢复，按原目标、原数量／位置重新生成预览，保留已完成部分，玩家再次确认才执行。不填其他字段，提交后结束。
        cheat_world(mode,item)：【作弊】只在本次原话明确要求时使用，一次执行后结束。mode=time，item=24小时制HH:mm（黎明04:30、正午12:00、黄昏19:30、午夜00:00）；mode=weather，item=clear或rain；mode=spawn，item=here，仅设置世界默认出生点，保留个人床；mode=boss，item填名单键 king_slime/eye/eater/brain/queen_bee/skeletron/deerclops/wall/queen_slime/twins/destroyer/prime/plantera/golem/duke/empress/cultist/moon_lord。环境和原生生成可能拒绝，按程序实际回报，不自动改时间／天气、不重复召唤。知识提问、否定、战前准备不是召唤授权；不支持模组Boss、任意坐标／NPC编号、批量及复合操作，请让玩家单独发一条清楚指令。
        answer：攻略和知识问题（某个东西是什么、有什么用、怎么用、Boss 怎么打、掉什么、最终 Boss 是谁、下一步做什么）交给资料库回答。资料库会直接收到玩家的原话，你不用自己回答，text 留空。
        reply(text)：目标完成、做不到或只是聊天时，给玩家最后一句话。
        ask(text)：缺信息或要玩家拿主意时问玩家。
        规则：
        0. 只完成【目标】里玩家这一次说的事。【刚才的对话】只是背景，不要接着做以前的目标。
        1. 工具结果都是真的，按结果决定下一步。失败了就换办法或 ask，不要重复同一个失败的调用。
        2. 玩家要制作物品、套装或完整合成链时优先 craft_chain；不要自行拆分采集、合成或制作站步骤。程序先算背包和附近箱子，缺口再采集，缺不支持的材料会提示。
        3. craft_chain 提交后结束本轮，等玩家确认预览；制作站要求玩家站在旁边。普通 craft 保留单步合成用途，明确同时要求放家具的短任务可用 craft / place。
        4. 小怪护卫和清理由 combat 执行；Boss战、钓鱼、卖东西、搬走箱子实体暂不支持。附近箱子中的指定物品可以存取。存取失败要说明原因，不猜物品、不改数量、不解锁或碰已打开的箱子。
        5. 目标做完了就马上 reply，不要再加别的步骤；control 只在玩家要你跟随、等待、回来或停下时用。
           reply 口语、简短，说做了什么、结果怎样；只说工具结果里真有的事，数量照抄结果。
        6. 玩家只是问问题时：配方和小屋材料用 recipe / house_materials 查完再 reply；攻略类直接 answer。
           查询类工具（inventory、look、recipe、house_materials、storage、chests、npcs、shop）同样的查询只查一次，结果就在【已经做的】里；查到的结果已经能回答玩家，就马上 reply，用自己的话把结果告诉玩家。
        7. 要准备打克眼或克苏鲁之眼时用 prepare_eye；其他 Boss 想查召唤物时：先 shop 查召唤物能不能直接买（装了 Fargo 的话突变体会卖），买不到再 recipe 看怎么做。
        8. 作弊工具只在【现在】写着“作弊模式：开”时用；开着时玩家要东西就直接 spawn_item，要盖存好的建筑就 structure；明确改时间、晴雨、召唤Boss或设置世界出生点用 cheat_world。没开就用正常办法，并告诉玩家右上角有作弊开关。
        9. 物品名必须是游戏里真实的名字。玩家说“随便来个装备”这类不指定的，就挑一个你确定存在的常见物品（铁剑、金头盔、赫尔墨斯靴、天顶剑），生成成功一件就 reply，不要再多试。名字不对时工具会返回相近的真实名字，照抄其中一个；还不行就 ask。
        10. 泰拉瑞亚没有“商店”建筑：买东西找城镇 NPC，问商店就用 npcs 看谁住进来了、谁还没来。
        11. 回复里不要提作弊模式，除非玩家问到或要用作弊工具。structure 只用来生成存好的建筑，不是物品。
        只输出 JSON。/no_think
        """;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["tool"],
         "properties":{
          "tool":{"type":"string","enum":["inventory","look","recipe","house_materials","storage","chests","locate","chest_put","chest_take","npcs","shop","buy","craft","craft_chain","place","chop","mine","build","platform","combat","expedition","prepare_eye","garden","control","spawn_item","structure","cheat_world","replan","answer","reply","ask"]},
          "item":{"type":"string","maxLength":40},
          "count":{"type":"integer","minimum":0,"maximum":9999},
          "ore":{"type":"string","maxLength":20},
          "rooms":{"type":"integer","minimum":1,"maximum":3},
          "mode":{"type":"string","enum":["follow","wait","recall","stop","protect","clear","off","underground","tunnel","scout","plant","harvest","time","weather","boss","spawn"]},
          "text":{"type":"string","maxLength":300}}}
        """).RootElement.Clone();

    public static AgentCall Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Agent step is not an object.");
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        int N(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        string tool = S("tool");
        if (!Tools.Contains(tool)) throw new FormatException("Unknown tool: " + tool);
        var call = new AgentCall(tool, S("item"), Math.Clamp(N("count"), 0, 999), S("ore"), Math.Clamp(N("rooms"), 0, 3), S("mode"), S("text"));
        if (tool is "chest_put" or "chest_take")
        {
            bool present = root.TryGetProperty("count", out var amount);
            if (present && (amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt32(out int _) || N("count") < 0 || N("count") > ChestRequest.MaxCount) ||
                tool == "chest_take" && N("count") < 1) throw new FormatException("Chest quantity must be an exact integer, 1–9999 (deposit 0 means all).");
            call = call with { Count = N("count") };
            if (call.Item.Length == 0 || call.Item.Length > 40) throw new FormatException(tool + " needs an exact item name.");
        }
        // Arguments a tool cannot run without: fail here so the step is retried, not run half-specified.
        if (tool is "recipe" or "craft" or "craft_chain" or "place" or "shop" or "buy" or "spawn_item" && call.Item.Length == 0) throw new FormatException(tool + " needs item.");
        if(tool=="replan"&&(root.EnumerateObject().Count()!=1||root.EnumerateObject().Any(p=>p.Name!="tool")))throw new FormatException("replan accepts no arguments; target and scope come from the failed task.");
        if (tool == "cheat_world")
        {
            _ = CheatWorldRequest.FromTool(call.Mode,call.Item);
            if (root.EnumerateObject().Any(p=>p.Name is not ("tool" or "mode" or "item")) || root.EnumerateObject().GroupBy(p=>p.Name).Any(g=>g.Count()>1)) throw new FormatException("cheat_world accepts only mode and item; no quantities or coordinates.");
        }
        if (tool == "locate" && (call.Item.Length == 0 || call.Item.Length > 40)) throw new FormatException("locate needs a target name of 1–40 characters.");
        if (tool == "craft_chain" && (root.TryGetProperty("count",out var chainCount) && (chainCount.ValueKind != JsonValueKind.Number || !chainCount.TryGetInt32(out int n) || n < 1 || n > 999)))
            throw new FormatException("Craft chain quantity must be 1–999.");
        if (tool is "reply" or "ask" && call.Text.Length == 0) throw new FormatException(tool + " needs text.");
        if (tool == "expedition")
        {
            if(call.Mode is not ("underground" or "tunnel" or "scout"))throw new FormatException("expedition needs underground/tunnel/scout.");
            if(root.TryGetProperty("count",out var distance)&&(distance.ValueKind!=JsonValueKind.Number||!distance.TryGetInt32(out int steps)||steps<0||steps>(call.Mode=="scout"?64:160)))throw new FormatException("Expedition distance outside bounds.");
            call=call with{Count=N("count")};
        }
        if(tool=="garden")
        {
            if(call.Mode is not ("plant" or "harvest")||call.Item is not ("草药" or "herbs")&&GardenRequest.CropName(call.Item)==null)
                throw new FormatException("garden needs plant/harvest and a supported crop.");
            if(call.Mode=="harvest"&&GardenRequest.CropName(call.Item)==GardenCrop.Acorn)throw new FormatException("Harvest herbs only; use chop for trees.");
            if(root.TryGetProperty("count",out var amount)&&(amount.ValueKind!=JsonValueKind.Number||!amount.TryGetInt32(out int plants)||plants<1||plants>64))
                throw new FormatException("Garden count must be 1-64 plants.");
        }
        if (tool == "combat" && call.Mode is not ("protect" or "clear" or "off")) throw new FormatException("combat needs mode protect/clear/off.");
        if (tool == "control" && call.Mode.Length > 0 && call.Mode is not ("follow" or "wait" or "recall" or "stop")) throw new FormatException("control needs mode follow/wait/recall/stop.");
        return call;
    }

    // One user message per step: the goal, a short snapshot, and every step so far with its real result.
    public static string State(string goal, string snapshot, IReadOnlyList<AgentStep> steps, IReadOnlyList<string> recentDialogue,
        bool wrapUp = false, int? budgetedSteps = null)
    {
        var lines = new List<string> { "【目标】" + goal };
        if (recentDialogue.Count > 0) lines.Add("【刚才的对话】\n" + string.Join("\n", recentDialogue));
        lines.Add("【现在】\n" + snapshot);
        if (steps.Count > 0)
            lines.Add("【已经做的】\n" + string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s.Call} → {s.Result}")));
        lines.Add("【再说一遍目标】" + goal);
        lines.Add(wrapUp ? "【现在】不要再调用工具。用 reply 根据上面的结果直接回答玩家：做成了什么、没做成什么、为什么、玩家可以怎么做。"
            : (budgetedSteps??steps.Count) >= MaxSteps - 1 ? "【提醒】步数快用完了，这一步请 reply 总结。"
            : steps.Count > 0 && IsLookup(steps[^1].Tool) ? "【下一步】上一步的结果如果已经回答了玩家，就 reply；否则输出下一个工具调用。"
            : steps.Count > 0 && IsSuccess(steps[^1].Result) ? "【下一步】上一步成功了。玩家要的已经做到就马上 reply 结束，不要重复同样的调用；还差什么才继续。"
            : "【下一步】输出一个工具调用。");
        return string.Join("\n\n", lines);
    }

    // Spending the player's money needs the player's own words: the goal or an answer to our question.
    public static bool PlayerAgreedToBuy(string goal, IReadOnlyList<AgentStep> steps) =>
        System.Text.RegularExpressions.Regex.IsMatch(goal, "买|购") ||
        steps.Any(s => s.Result.StartsWith("玩家回答：") &&
            System.Text.RegularExpressions.Regex.IsMatch(s.Result, "买|购|好|行|可以|同意|确认|嗯|ok|OK"));

    private static readonly System.Text.RegularExpressions.Regex Question = new(
        "[？?]|是啥|是什么|是谁|是哪|哪个|哪些|什么|怎么|如何|为什么|吗$|呢$|掉落|有啥用|有什么用|作用");
    public static bool LooksLikeQuestion(string text) => Question.IsMatch(text.Trim());

    // Read-only tools: running one again before anything changed only repeats the same answer.
    public static readonly string[] Lookups = { "inventory", "look", "recipe", "house_materials", "storage", "chests", "locate", "npcs", "shop" };
    public static bool IsLookup(string tool) => Lookups.Contains(tool);
    // Doing these twice in a row with nothing in between changes nothing either.
    private static bool Idempotent(string tool) => IsLookup(tool) || tool is "control" or "structure";
    // Tools whose result only says something, or that end the goal: never "repeated".
    private static bool Terminal(string tool) => tool is "reply" or "ask" or "answer" or "build" or "platform" or "locate" or "expedition" or "prepare_eye" or "garden" or "cheat_world" or "replan";

    public const string SeenPrefix = "刚查过";
    public const string TriedPrefix = "刚试过";
    public static bool IsSeen(string result) => result.StartsWith(SeenPrefix) || result.StartsWith(TriedPrefix);

    // The step (0-based) whose result this call would only repeat, or -1: the same look-up with nothing done
    // since, or the same action that already failed with nothing having succeeded since.
    public static int AlreadyLooked(IReadOnlyList<AgentStep> steps, AgentCall next, bool observeTerminalLookups=false)
    {
        if (Terminal(next.Tool)&&!(observeTerminalLookups&&IsLookup(next.Tool))) return -1;
        bool lookup = Idempotent(next.Tool);
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            var s = steps[i];
            if (s.Call == next.Label && !IsSeen(s.Result))
                return lookup || !IsSuccess(s.Result) ? i : -1;
            // Something got done (or the player answered): the world may differ now.
            if (!Idempotent(s.Tool) && (s.Tool.Length == 0 || IsSuccess(s.Result))) return -1;
        }
        return -1;
    }

    public static string Seen(int index) => $"{SeenPrefix}，结果跟第 {index + 1} 步一样，没有变化。不要再查了：能回答玩家就 reply，不能就换下一步。";

    public static string Seen(IReadOnlyList<AgentStep> steps, int index, AgentCall next) =>
        IsLookup(next.Tool) || next.Tool is "control" ? Seen(index)
        : $"{TriedPrefix}同样的调用，结果跟第 {index + 1} 步一样（{steps[index].Result.Split('\n')[0]}），中间也没做成别的事。" +
          "不要再重复：换个办法（查商店、翻仓库、去采集、先做缺的东西），或者 reply 告诉玩家卡在哪、怎么办。";

    // The exact action that just succeeded, asked for again right away.
    // (Gathering is exempt: a second trip for what the first one fell short of is normal.)
    public static bool RepeatsSuccess(IReadOnlyList<AgentStep> steps, AgentCall next) =>
        steps.Count > 0 && !Idempotent(next.Tool) && !Terminal(next.Tool) && next.Tool is not ("chop" or "mine") &&
        steps[^1].Call == next.Label && IsSuccess(steps[^1].Result);

    // The same call already made three times in this goal, however spread out: it is going in circles.
    public static bool Overused(IReadOnlyList<AgentStep> steps, AgentCall next) =>
        !Terminal(next.Tool) && steps.Count(s => s.Call == next.Label && !IsSeen(s.Result)) >= 3;

    // How many of `name` the craft still lacks: the newest "还差 … name ×N" (a failed craft, or the progress note a
    // gathering trip ends with), unless a craft has succeeded since. 0 when unknown.
    public static int MissingAmount(IReadOnlyList<AgentStep> steps, string name)
    {
        if (name.Length == 0) return 0;
        var pattern = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(name) + @" ×(\d+)");
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            var s = steps[i];
            if (s.Tool == "craft" && IsSuccess(s.Result) || s.Result.Contains("现在能做")) return 0;
            int at = s.Result.LastIndexOf("还差", StringComparison.Ordinal);
            if (at < 0) continue;
            var m = pattern.Match(s.Result, at);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return 0;
    }

    // The model keeps re-asking for a result it already has. If something got done, that is the answer;
    // otherwise the looked-up result itself is.
    public static string LookupAnswer(IReadOnlyList<AgentStep> steps, AgentCall next, bool observeTerminalLookups=false)
    {
        string done = Accomplished(steps);
        if (done.Length > 0) return done;
        int i = AlreadyLooked(steps, next,observeTerminalLookups);
        return i < 0 ? "" : (IsLookup(next.Tool) ? "我查了一下 —— " : "") + steps[i].Result;
    }

    private static readonly string[] Successes = { "做好了", "放好了", "存好了", "取好了", "买了", "从", "生成了", "砍好了", "挖好了", "完成", "好，", "我会留在这里", "同伴已召回", "已停止任务", "已取消旧任务" };

    public static bool IsSuccess(string result) => Successes.Any(w => result.StartsWith(w));

    // "搞定了：…" from the steps that actually did something, or "" when nothing did.
    public static string Accomplished(IReadOnlyList<AgentStep> steps)
    {
        var done = steps.Where(s => !Idempotent(s.Tool) && s.Tool.Length > 0 && IsSuccess(s.Result))
            .Select(s => s.Result.Split('\n')[0].TrimEnd('。')).ToList();
        if (done.Count == 0) return "";
        string text = "做了这些：" + string.Join("；", done) + "。";
        return PendingCraft(steps) is var (item, _) ? text + $"不过{item}还没做出来。" : text;
    }

    // The craft that last failed (materials, station ...), if nothing has crafted it since: (item, count).
    public static (string Item, int Count)? PendingCraft(IReadOnlyList<AgentStep> steps)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            if (steps[i].Tool != "craft") continue;
            if (IsSuccess(steps[i].Result) || steps[i].Result.Contains("没有配方")) return null;
            var m = System.Text.RegularExpressions.Regex.Match(steps[i].Call, @"^合成：(.+) ×(\d+)$");
            return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : null;
        }
        return null;
    }

    // The same call failing twice in a row would loop: stop it in code rather than trusting the model.
    public static bool Repeats(IReadOnlyList<AgentStep> steps, AgentCall next) =>
        steps.Count >= 2 && steps[^1].Call == next.Label && steps[^2].Call == next.Label;
}
