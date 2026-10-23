using System.Text.Json.Serialization;
using TerrarAI.Content.Companion;

namespace TerrarAI.Evals;

// A small stand-in for the game: just enough of Terraria for the agent's tools to answer the way the mod's
// do (same wording where the model reads it). It is not a simulation of the game, only of the tool results.
public sealed class WorldSpec
{
    public Dictionary<string, int> Bag { get; set; } = new();
    public List<string> Near { get; set; } = new();          // stations already beside the player
    public int Trees { get; set; } = 5;
    public Dictionary<string, int> Ores { get; set; } = new(); // "铜": tiles, "石块": blocks reachable nearby
    public bool Cheat { get; set; }
    public List<string> Npcs { get; set; } = new() { "向导" };
    public Dictionary<string, ShopSpec> Shops { get; set; } = new();
    public long Coins { get; set; } = 7100;                  // copper
    public string Time { get; set; } = "白天 10:00";
    public string Biome { get; set; } = "森林";
    public int Enemies { get; set; }
    public bool CanPlace { get; set; } = true;
    public Dictionary<string, int>? Storage { get; set; }    // null: no Magic Storage
    public Dictionary<string, int> Chests { get; set; } = new(); // simplified nearby accessible chest stock
}

public sealed class ShopSpec
{
    public string Npc { get; set; } = "商人";
    public long Price { get; set; } = 100;
    public bool Unlocked { get; set; } = true;
    public bool Here { get; set; } = true;
}

public sealed class FakeWorld
{
    private sealed record Recipe(int Yield, (string Item, int Count)[] Needs, string? Station);

    private static readonly Dictionary<string, Recipe> Recipes = new()
    {
        ["工作台"] = new(1, new[] { ("木材", 10) }, null),
        ["木桌"] = new(1, new[] { ("木材", 8) }, "工作台"),
        ["木椅"] = new(1, new[] { ("木材", 4) }, "工作台"),
        ["木门"] = new(1, new[] { ("木材", 6) }, "工作台"),
        ["木墙"] = new(4, new[] { ("木材", 1) }, "工作台"),
        ["木平台"] = new(2, new[] { ("木材", 1) }, null),
        ["火把"] = new(3, new[] { ("木材", 1), ("凝胶", 1) }, null),
        ["熔炉"] = new(1, new[] { ("石块", 20), ("木材", 4), ("火把", 3) }, "工作台"),
        ["铜锭"] = new(1, new[] { ("铜矿", 3) }, "熔炉"),
        ["铁锭"] = new(1, new[] { ("铁矿", 3) }, "熔炉"),
        ["铁砧"] = new(1, new[] { ("铁锭", 5) }, "工作台"),
        ["箱子"] = new(1, new[] { ("木材", 8), ("铁锭", 2) }, "工作台"),
        ["铜镐"] = new(1, new[] { ("铜锭", 12), ("木材", 3) }, "砧"),
        ["铁镐"] = new(1, new[] { ("铁锭", 12), ("木材", 3) }, "砧"),
        ["木剑"] = new(1, new[] { ("木材", 7) }, "工作台"),
        ["木弓"] = new(1, new[] { ("木材", 10) }, "工作台"),
        ["蠕虫诱饵"] = new(1, new[] { ("腐肉", 15), ("魔矿", 30) }, "恶魔祭坛"),
        ["史莱姆王冠"] = new(1, new[] { ("凝胶", 99), ("金冠", 1) }, "工作台"),
        ["可疑眼球"] = new(1, new[] { ("晶状体", 6) }, "恶魔祭坛"),
        ["泰拉刃"] = new(1, new[] { ("真断钢剑", 1), ("真永夜刃", 1), ("破碎英雄剑", 1) }, "秘银砧"),
        ["生命水晶"] = new(0, Array.Empty<(string, int)>(), null),
    };

    // Names the game would recognise (recipe outputs, materials and a few famous items).
    private static readonly string[] Known = Recipes.Keys.Concat(new[]
    {
        "木材", "凝胶", "石块", "土块", "铜矿", "铁矿", "金矿", "腐肉", "魔矿", "晶状体", "金冠", "铜币", "银币", "金币",
        "铜短剑", "铜斧", "铁剑", "金剑", "铂金宽剑", "永夜刃", "真永夜刃", "断钢剑", "真断钢剑", "破碎英雄剑", "天顶剑",
        "星怒", "光明之魂", "暗影之魂", "史莱姆钩", "钩爪", "赫尔墨斯靴", "云朵瓶", "生命水晶", "治疗药水", "弱效治疗药水",
        "铁砧", "铅砧", "秘银砧", "橡实", "绳", "神圣锭", "泰拉刃", "月亮领主腿", "日耀喷发剑", "至尊灾厄之刃",
        // armour and accessories, so look-alike names get real suggestions as they would in the game
        "铜头盔", "铜链甲", "铜护胫", "铁头盔", "铁链甲", "铁护胫", "金头盔", "金链甲", "金护胫", "暗影头盔", "神圣头盔",
        "日耀头盔", "日耀胸甲", "日耀护腿", "星旋头盔", "星云头盔", "星尘头盔", "天界壳", "天界星盘", "泰拉闪耀靴", "天使之翼",
        "蝴蝶之翼", "神圣盔甲", "暗影盔甲", "精金剑", "狂星之怒", "最后的棱镜", "星旋机枪"
    }).Distinct().ToArray();

    private static readonly HashSet<string> Placeable = new() { "工作台", "熔炉", "铁砧", "木桌", "木椅", "木门", "火把", "箱子", "木平台" };
    private static string StationOf(string item) => item is "铁砧" or "铅砧" ? "砧" : item;
    private static string StationItem(string station) => station == "砧" ? "铁砧" : station;

    private readonly WorldSpec _w;
    public Dictionary<string, int> Bag => _w.Bag;
    public Dictionary<string, int> Chests => _w.Chests;
    public HashSet<string> Near { get; }
    public List<string> Placed { get; } = new();
    public List<string> Spawned { get; } = new();
    public FakeWorld(WorldSpec spec)
    {
        _w = System.Text.Json.JsonSerializer.Deserialize<WorldSpec>(System.Text.Json.JsonSerializer.Serialize(spec))!;
        Near = new HashSet<string>(_w.Near);
    }

    public static bool IsKnown(string name) => Known.Contains(name.Trim());

    private int Have(string item) => Bag.GetValueOrDefault(item);
    private void Add(string item, int n) { Bag[item] = Have(item) + n; if (Bag[item] <= 0) Bag.Remove(item); }

    private static string List(IEnumerable<KeyValuePair<string, int>> items, int max = 40)
    {
        var l = items.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Take(max).Select(p => $"{p.Key}×{p.Value}").ToList();
        return l.Count == 0 ? "空" : string.Join("、", l);
    }

    public string Snapshot() =>
        $"{_w.Time} · {_w.Biome}／普通地形 · 地表\n" +
        $"生命 100/100 · 防御 0 · 镐：{(Have("铜镐") > 0 ? "铜镐" : "铜镐")} · 斧：铜斧\n" +
        $"玩家身边的制作站：{(Near.Count > 0 ? string.Join("、", Near) : "没有")}\n" +
        (_w.Storage != null ? "装了魔法存储（storage 能翻，craft 会自动取料）\n" : "") +
        (_w.Chests.Count > 0 ? "附近可用箱子：1个（chests可查，craft自动补料）\n" : "附近可用箱子：0个\n") +
        $"作弊模式：{(_w.Cheat ? "开" : "关")}\n" +
        "背包：" + List(Bag, 14);

    // Same similar-name hint the mod gives (Crafting.Unknown).
    public static string Unknown(string name)
    {
        name = name.Trim();
        int Common(string a)
        {
            int best = 0;
            for (int i = 0; i < name.Length; i++)
                for (int len = Math.Min(name.Length - i, a.Length); len > best; len--)
                    if (a.Contains(name.Substring(i, len))) { best = len; break; }
            return best;
        }
        int need = name.Length >= 3 ? 2 : 1;
        var near = Known.Select(k => (k, s: Common(k))).Where(x => x.s >= need).OrderByDescending(x => x.s).ThenBy(x => x.k.Length)
            .Take(8).Select(x => x.k).ToList();
        return $"游戏里没有叫「{name}」的物品。" + (near.Count > 0
            ? "名字相近的真实物品：" + string.Join("、", near) + "。要用就照抄其中一个名字，或者 ask 玩家要哪个。"
            : "换个准确的名字，或者 ask 玩家。");
    }

    private (List<string> Lack, bool StationMissing, Recipe R, int Times)? Check(string item, int count)
    {
        if (!Recipes.TryGetValue(item, out var r) || r.Yield == 0) return null;
        int times = (count + r.Yield - 1) / r.Yield;
        int Available(string name) => Have(name) + _w.Chests.GetValueOrDefault(name);
        var lack = r.Needs.Where(n => Available(n.Item) < n.Count * times).Select(n => $"{n.Item} ×{n.Count * times - Available(n.Item)}").ToList();
        return (lack, r.Station != null && !Near.Contains(r.Station), r, times);
    }

    public string Progress(string item, int count)
    {
        var c = Check(item, count);
        if (c is not var (lack, missing, r, _)) return "";
        if (lack.Count == 0 && !missing) return $"现在能做{item}了，下一步 craft 它。";
        var parts = new List<string>();
        if (lack.Count > 0) parts.Add("还差 " + string.Join("、", lack));
        if (missing) parts.Add($"要在{r.Station}旁边");
        return $"做{item}：" + string.Join("；", parts) + "。";
    }

    private string Make(string item, int count)
    {
        var can = Check(item, count);
        string pulled = can is var (lack, missing, _, _) && lack.Count > 0 && !missing ? PullFromStorage(item, count) : "";
        string made = MakeFromBag(item, count);
        return made.StartsWith("做好了") ? made + pulled : made;
    }

    // Like the mod: materials the bag lacks are taken out of Magic Storage first.
    private string PullFromStorage(string item, int count)
    {
        if (_w.Storage == null || !Recipes.TryGetValue(item, out var r) || r.Yield == 0) return "";
        int times = (count + r.Yield - 1) / r.Yield;
        var taken = new List<string>();
        foreach (var (need, n) in r.Needs)
        {
            int lack = n * times - Have(need), there = _w.Storage.GetValueOrDefault(need);
            int take = Math.Min(lack, there);
            if (take <= 0) continue;
            _w.Storage[need] = there - take; Add(need, take);
            taken.Add($"{need} ×{take}");
        }
        return taken.Count == 0 ? "" : "（从魔法存储取了 " + string.Join("、", taken) + "）";
    }

    private string MakeFromBag(string item, int count)
    {
        var c = Check(item, count);
        if (c is not var (lack, missing, r, times)) return $"{item}没有配方，做不了。";
        if (lack.Count == 0 && !missing)
        {
            var taken = new List<string>();
            foreach (var n in r.Needs)
            {
                int amount=n.Count*times,bag=Math.Min(Have(n.Item),amount);Add(n.Item,-bag);
                int stored=amount-bag;
                if(stored>0){_w.Chests[n.Item]-=stored;taken.Add($"{n.Item}×{stored}");}
            }
            Add(item, r.Yield * times);
            return $"做好了：{item} ×{r.Yield * times}，放进你背包了。"+(taken.Count==0?"":"（附近箱子补料："+string.Join("、",taken)+"）");
        }
        var parts = new List<string>();
        if (lack.Count > 0) parts.Add("还差 " + string.Join("、", lack));
        if (missing) parts.Add($"要站在{r.Station}旁边");
        return $"{item} ×{count} 现在做不了：" + string.Join("；", parts) + "。";
    }

    // The mod's craft tool: when only a station is missing, put one down first (owned or made on the spot).
    private string Craft(string item, int count, int depth)
    {
        string made = Make(item, count);
        if (made.StartsWith("做好了") || depth > 1) return made;
        var c = Check(item, count);
        if (c is not var (lack, missing, r, _) || lack.Count > 0 || !missing) return made;
        string station = StationItem(r.Station!);
        bool owned = Have(station) > 0;
        if (!owned && !Craft(station, 1, depth + 1).StartsWith("做好了"))
            return made + $"\n（背包里没有{r.Station}，材料也不够现做一个）";
        string placed = Place(station);
        if (!placed.StartsWith("放好了")) return made + $"\n想先放个{station}，但{placed}";
        string note = $"先{(owned ? "把" : "做了一个")}{station}" + placed.Replace("放好了：" + station, owned ? "放" : "，放").TrimEnd('。');
        string again = Make(item, count);
        return again.StartsWith("做好了") ? again + " 为此" + note + "。" : note + "。" + again;
    }

    private string Place(string item)
    {
        if (Have(item) <= 0) return $"背包里没有{item}，先做一个。";
        if (!Placeable.Contains(item)) return $"{item}不是能放下的东西。";
        if (!_w.CanPlace) return $"你旁边找不到能放{item}的平地，换个空旷点的地方再试。";
        Add(item, -1);
        Near.Add(StationOf(item));
        Placed.Add(item);
        return $"放好了：{item}在你右边 2 格。";
    }

    // Long tools report like the mod: phase, outcome, what arrived in the bag.
    private string Chop(int count)
    {
        count = Math.Clamp(count, 1, TaskGoal.MaxCount);
        int n = Math.Min(count, _w.Trees);
        if (n == 0) return "已取消：附近没找到能做的目标。\n背包里没多东西。";
        _w.Trees -= n;
        Add("木材", n * 12); Add("橡实", n);
        return $"完成：砍了 {n} 棵树" + (n < count ? "；附近没找到别的能走过去的树。" : "") +
            $"\n背包里多了：木材 +{n * 12}（现在共 {Have("木材")}）、橡实 +{n}（现在共 {Have("橡实")}）";
    }

    private string Mine(string ore, int count)
    {
        string word = ore.Replace("矿", "").Trim();
        string? key = TaskGoal.OreWord("挖" + word);
        bool block = TaskGoal.IsBlock(key);
        count = Math.Clamp(count, 1, block ? TaskGoal.MaxBlocks : TaskGoal.MaxCount);
        string? pick = key != null ? (_w.Ores.ContainsKey(key) ? key : null) : _w.Ores.Keys.FirstOrDefault(k => !TaskGoal.IsBlock(k));
        if (pick == null || _w.Ores[pick] <= 0)
            return $"已取消：附近没找到{(key ?? "能挖的矿")}。\n背包里没多东西。";
        int n = Math.Min(count, _w.Ores[pick]);
        _w.Ores[pick] -= n;
        string item = TaskGoal.IsBlock(pick) ? pick : pick + "矿";
        Add(item, n);
        return $"完成：挖了 {n} 处" + (n < count ? "；附近没有更多目标了。" : "") + $"\n背包里多了：{item} +{n}（现在共 {Have(item)}）";
    }

    public string Run(AgentCall call, AgentLoop loop)
    {
        string Item() => call.Item.Trim();
        int Count() => Math.Max(1, call.Count);
        switch (call.Tool)
        {
            case "replan": return loop.RecoveryMode
                ? "离线环境只记录原任务的重规划交接；未创建真实预览、未执行备料或施工，游戏中必须重新确认。"
                : "没有待恢复的失败任务，未重规划。";
            case "cheat_world":
                if (!_w.Cheat) return CheatWorldExecutor.Off;
                var cheat=CheatWorldRequest.FromTool(call.Mode,call.Item);
                return CheatWorldRequest.Authorizes(loop.Goal,cheat)
                    ? "离线环境未修改真实时间／天气、生成Boss或设置出生点。请在游戏中核验原生操作。"
                    : "本次原话未授权该作弊操作。";
            case "garden": return "已提交种植／采收预览，等待玩家确认；离线环境不模拟草药生长、植株放置或真实掉落。";
            case "prepare_eye": return "已提交克眼准备预览，等待确认；离线环境不执行真实地形、家具增益或药水合成。";
            case "craft_chain": return "已提交合成链预览，等待玩家确认；离线工具环境不执行游戏配方链。";
            case "expedition": return "离线环境没有真实地形，未执行挖掘、移动或地图发现。";
            case "locate": return "离线环境没有真实地图，未创建标记。";
            case "combat": return "已提交战斗指令，离线环境不模拟命中或击杀。";
            case "inventory": return "背包：" + List(Bag) + "\n盔甲：没穿 · 饰品：没有";
            case "look":
                var ores = _w.Ores.Where(p => p.Value > 0 && !TaskGoal.IsBlock(p.Key)).Select(p => $"{p.Key}矿 11 格").ToList();
                return $"{_w.Time} · 晴朗 · {_w.Biome}／普通地形 · 地表\n附近的树：{_w.Trees} 棵 · 看得到的矿：{(ores.Count > 0 ? string.Join("、", ores) : "没有")}\n" +
                    $"附近友方：{string.Join("、", _w.Npcs)}、Qwen 同伴Qwen · 附近敌怪：{_w.Enemies}";
            case "house_materials":
                int wood = Have("木材");
                return "一间 NPC 小屋（预设蓝图）要：木墙 ×36、木平台 ×2、木门 ×1、木桌 ×1、木椅 ×1、火把 ×1。\n" +
                    $"这些用 木材 ×27 在工作台做，" + (wood >= 27 ? "你背包里的材料够了。" : $"还要再弄 木材 ×{27 - wood}。");
            case "storage":
                return "附近箱子里："+List(_w.Chests,30)+(_w.Storage==null?"":"\n魔法存储里："+List(_w.Storage,30));
            case "chests": return "附近箱子里："+List(_w.Chests.Where(p=>Item().Length==0||p.Key.Contains(Item())),30);
            case "chest_put":
            case "chest_take":
            {
                int Resolve(string name)=>Array.IndexOf(Known,name)+1;
                bool deposit=call.Tool=="chest_put";var op=deposit?ChestOperation.Deposit:ChestOperation.Withdraw;
                if(!ChestRequest.Authorizes(loop.Goal,op,Item(),call.Count,loop.Steps,Resolve))return "这次没有明确授权这项箱子存取，或已经完成。";
                int amount=deposit&&call.Count==0?Have(Item()):call.Count;
                int available=deposit?Have(Item()):_w.Chests.GetValueOrDefault(Item());
                if(amount<1||available<amount)return "可存取物品不足。";
                Add(Item(),deposit?-amount:amount);_w.Chests[Item()]=_w.Chests.GetValueOrDefault(Item())+(deposit?amount:-amount);
                return (deposit?"存好了：":"取好了：")+Item()+"×"+amount;
            }
            case "npcs":
                return "已经住进来的：" + string.Join("、", _w.Npcs) +
                    "\n还没来的和条件：\n商人：所有玩家物品栏中加起来有超过50 银币\n护士：有玩家拥有超过100点生命，且商人已入住\n" +
                    "爆破专家：有玩家背包里有炸药\n树妖：击败克苏鲁之眼、世界吞噬怪或骷髅王之一";
            case "shop":
            case "buy":
            {
                if (!IsKnown(Item())) return Unknown(Item());
                if (!_w.Shops.TryGetValue(Item(), out var s)) return $"没有 NPC 卖{Item()}。" + (call.Tool == "buy" ? "\n所以现在买不到。" : "");
                string line = $"{s.Npc} 卖{Item()}，{s.Price / 100}银；" + (!s.Unlocked ? "现在还没解锁（进度或条件不够，比如还没打过对应的 Boss）"
                    : s.Here ? "现在就能买" : "条件够了，但这个 NPC 还没住进来");
                if (call.Tool == "shop") return line;
                if (!s.Unlocked || !s.Here) return line + "\n所以现在买不到。";
                if (!AgentProtocol.PlayerAgreedToBuy(loop.Goal, loop.Steps)) return "要花玩家的钱，先用 ask 问玩家要不要买（说清价格）。" + line;
                long total = s.Price * Count();
                if (total > _w.Coins) return $"钱不够：{Count()} 个{Item()}要 {total / 100}银。";
                _w.Coins -= total; Add(Item(), Count());
                return $"从{s.Npc}那买了 {Item()} ×{Count()}，花了 {total / 100}银。";
            }
            case "spawn_item":
                if (!_w.Cheat) return "作弊模式没开。玩家可以点对话窗口右上角的「作弊」开关打开；没开就用正常办法。";
                if (!IsKnown(Item())) return Unknown(Item());
                Add(Item(), Count()); Spawned.Add(Item());
                return $"生成了 {Item()} ×{Count()}，放进你背包了（作弊）。";
            case "structure":
                if (!_w.Cheat) return "作弊模式没开。玩家可以点对话窗口右上角的「作弊」开关打开；没开就用正常办法。";
                return "SavedStructures 文件夹里还没有建筑。用 StructureHelper 的结构魔杖框选一块地方保存，就会出现在这里。";
            case "recipe":
                if (!IsKnown(Item())) return Unknown(Item());
                if (!Recipes.TryGetValue(Item(), out var rec) || rec.Yield == 0) return $"{Item()}没有配方（只能找到、买到或打怪掉落）。";
                return $"{Item()} ×{rec.Yield}：{string.Join("、", rec.Needs.Select(n => $"{n.Item} ×{n.Count}"))}；" +
                    (rec.Station == null ? "徒手就能做" : $"在{rec.Station}旁边做") + "。" + (Progress(Item(), 1) is var p && p.StartsWith("现在能") ? "现在就能做。" : "现在做不了：" + p);
            case "craft":
                return IsKnown(Item()) ? Craft(Item(), Count(), 0) : Unknown(Item());
            case "place":
                return IsKnown(Item()) ? Place(Item()) : Unknown(Item());
            case "chop": return Chop(call.Count);
            case "mine": return Mine(call.Ore, call.Count);
            case "control":
                return call.Mode switch
                {
                    "wait" => "我会留在这里。点击继续跟随或召回就能叫我回来。",
                    "recall" => "同伴已召回，恢复跟随。",
                    "stop" => "已停止任务，同伴恢复跟随。",
                    _ => "好，跟着你。"
                };
            default: return "这个工具现在用不了。";
        }
    }
}
