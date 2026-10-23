using System;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// What the player asked for, in countable form, so the game (not the model) decides when a
// gathering task is done. Kept independent of Terraria so the rules can be tested in isolation.
public sealed record TaskGoal(string Kind, int Target, bool Explicit)
{
    public const int MaxCount = 8;
    // Plain blocks (stone, dirt, sand ...) come one per tile and recipes want 20 of them at a time.
    public const int MaxBlocks = 40;
    public static readonly TaskGoal None = new("other", 1, false);
    public bool Countable => Kind is "chop" or "mine";
    public string Verb => Kind == "chop" ? "砍" : Kind == "mine" ? "挖" : "做";
    public string Unit => Kind == "chop" ? "棵树" : Kind == "mine" ? "处矿" : "件事";

    private static readonly Regex Counted = new(@"(\d+|[一二两三四五六七八九十几]+)\s*(?:棵|颗|株|个|块|处|堆|片|下)");
    private static readonly Regex Some = new("一些|一点|几下|多砍|多挖");
    private static readonly Regex All = new("所有|全部|都砍|都挖|砍光|挖光|清掉|清理");

    public static TaskGoal Parse(string? command)
    {
        string text = command ?? "";
        string kind = Regex.IsMatch(text, "砍|伐|木头|木材|树") ? "chop"
            : Regex.IsMatch(text, "挖|矿|采") ? "mine" : "other";
        if (kind == "other") return None;
        int max = kind == "mine" && IsBlock(OreWord(text)) ? MaxBlocks : MaxCount;
        var m = Counted.Match(text);
        if (m.Success) return new TaskGoal(kind, Math.Clamp(Number(m.Groups[1].Value), 1, max), true);
        if (All.IsMatch(text)) return new TaskGoal(kind, MaxCount, true);
        if (Some.IsMatch(text)) return new TaskGoal(kind, 3, true);
        return new TaskGoal(kind, 1, false);
    }

    private static readonly Regex Ore = new("铂金|魔矿|猩红矿|陨石|狱石|黑曜石|铜|锡|铁|铅|银|钨|金");
    // Explicit block words, or a bare 石/土/沙/泥/雪/冰 right after 挖/采 ("挖20块石", not "去沙漠挖矿").
    private static readonly (Regex Words, string Block)[] Blocks = {
        (new("石块|石头|石材|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?石(?![漠地原山窟矿])"), "石块"),
        (new("土块|泥土|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?土(?![漠地原山窟矿])"), "土块"),
        (new("沙块|沙子|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?沙(?![漠地原山窟矿])"), "沙块"),
        (new("泥块|淤泥|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?泥(?![漠地原山窟矿潭沼土])"), "泥块"),
        (new("粘土|黏土"), "粘土块"),
        (new("雪块|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?雪(?![漠地原山窟矿])"), "雪块"),
        (new("冰块|[挖采][\\d一二两三四五六七八九十几点些]*[块个]?冰(?![漠地原山窟矿雪])"), "冰块") };

    public static bool IsBlock(string? word) => word is "石块" or "土块" or "沙块" or "泥块" or "粘土块" or "雪块" or "冰块";
    private static readonly Regex NearPlayer = new("我(旁边|附近|这边|身边|边上|脚下|这儿|这里)|这(边|里|儿)的");
    private static readonly Regex Left = new("左(边|侧|面)"), Right = new("右(边|侧|面)");

    // "挖铜矿" -> "铜"; null when no particular ore was named.
    public static string? OreWord(string? command)
    {
        string text = command ?? "";
        if (!Regex.IsMatch(text, "矿|挖|采|陨石|狱石|黑曜石")) return null;
        var m = Ore.Match(text);
        if (m.Success) return m.Value;
        foreach (var (words, block) in Blocks) if (words.IsMatch(text)) return block;
        return null;
    }

    // Where the player wants us to look: "player" (我旁边), "left"/"right" of the player, or "companion".
    public static string Anchor(string? command)
    {
        string text = command ?? "";
        if (Left.IsMatch(text)) return "left";
        if (Right.IsMatch(text)) return "right";
        return NearPlayer.IsMatch(text) ? "player" : "companion";
    }

    private static int Number(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        if (s.Contains('几')) return 3;
        int i = s.IndexOf('十');
        if (i < 0) return Digit(s[^1]);
        int tens = i == 0 ? 1 : Digit(s[i - 1]);
        int ones = i == s.Length - 1 ? 0 : Digit(s[^1]);
        return tens * 10 + ones;
    }

    private static int Digit(char c) => c switch
    {
        '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4, '五' => 5,
        '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => 1
    };
}

// "砍完了吗" / "你砍了几棵" are questions about the running task, not new orders.
// They are answered from the task record without stopping the work or asking the model.
public static class StatusQuery
{
    private static readonly Regex Asks = new(
        @"(完|好|结束|搞定|弄好|做好)了?(吗|没|么|没有)|进度|怎么样了|多少|几(棵|颗|个|块|处)|在(干|做|弄)(嘛|什么|啥)|干(嘛|什么|啥)呢|做到哪|还要多久|还没");
    private static readonly Regex TaskWords = new("砍|挖|采|树|矿|任务|活|进度|完|干|做|弄|好了|打|怪|护卫|保护|探路|发现");
    private static readonly Regex Orders = new("帮我|帮忙|去|再|继续|别|不要|停|快点|给我|以后|建议|应该|下一步|推荐|有多少|附近");

    public static bool IsStatusQuestion(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 40) return false;
        if(Regex.IsMatch(t,@"^(?:你)?(?:探到哪了|发现(?:了)?什么(?:了)?|发现啥了|挖到地下(?:层)?了吗)[?？。]*$"))return true;
        return Asks.IsMatch(t) && TaskWords.IsMatch(t) && !Orders.IsMatch(t);
    }
}

// "下一步干啥" / "克眼怎么打" / "这个有什么用" are knowledge questions. Typed in chat, they go to the
// advice path (game state + Wiki) instead of the task classifier, and the companion keeps working.
public static class AdviceQuery
{
    private static readonly Regex Asks = new(@"[?？]|吗|呢|什么|啥|怎么|如何|哪|多少|几|是不是|能不能|建议|推荐|应该|该|要不要|值不值");
    private static readonly Regex Topics = new(
        @"boss|首领|老板|攻略|配方|合成|制作|材料|掉落|奖励|效果|属性|装备|武器|盔甲|饰品|配饰|药水|下一步|接下来|进度|准备|打法|怎么打|" +
        @"生命水晶|魔力水晶|矿|锭|npc|入住|房子|召唤|事件|血月|困难模式|肉山|克眼|世吞|克脑|骷髅王|蜂王|史莱姆王|血肉墙|有什么用|干啥|做什么|干什么",
        RegexOptions.IgnoreCase);
    private static readonly Regex Orders = new(@"^(帮我|帮忙|去|你去|给我|跟我|跟着|过来|停|别|不要|以后|记住|记得)|帮我(砍|挖|采|建|搭|拿|找|放)|你(去|来)(砍|挖|采)|你(能|会|可以)(帮我?)?(砍|挖|采|建|搭)");

    public static bool IsAdviceQuestion(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length < 2 || t.Length > 120) return false;
        return Asks.IsMatch(t) && Topics.IsMatch(t) && !Orders.IsMatch(t) && !StatusQuery.IsStatusQuestion(t);
    }
}

// "帮我盖个NPC房子" / "建两间房" -> a preset blueprint id; the player still picks the spot and confirms.
public static class BuildRequest
{
    private static readonly Regex Verb = new("建|盖|搭|造|修");
    private static readonly Regex House = new("房|屋|住处|npc|NPC|宿舍|公寓");
    private static readonly Regex Two = new("两间|2间|二间|双|两个房|2个房");
    private static readonly Regex Three = new("三间|3间|三个房|3个房|联排");

    public static string? Preset(string? text)
    {
        string t = text ?? "";
        if (!Verb.IsMatch(t) || !House.IsMatch(t) || Regex.IsMatch(t, "怎么|如何|吗|？|\\?")) return null;
        return Three.IsMatch(t) ? "preset:npc3" : Two.IsMatch(t) ? "preset:npc2" : "preset:npc1";
    }
}
