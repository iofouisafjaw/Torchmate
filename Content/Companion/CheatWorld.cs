using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// A switch transition invalidates all earlier permissions, including off -> on transitions.
public readonly record struct CheatPermit(Guid Session, long Revision);
public sealed class CheatAuthority
{
    private Guid _session = Guid.NewGuid();
    private long _revision;
    public bool Enabled { get; private set; }
    public CheatPermit Capture() => Enabled ? new(_session, _revision) : default;
    public bool Allows(CheatPermit permit) => Enabled && permit.Session != Guid.Empty && permit.Session == _session && permit.Revision == _revision;
    public void Toggle() { _revision++; Enabled = !Enabled; }
    public void Reset() { Enabled = false; _session = Guid.NewGuid(); _revision = 0; }
}

public enum CheatWorldOperation { Time, Weather, Boss, Spawn }
public sealed record CheatBoss(string Key, string Name, string[] Aliases, bool Night = false, string Biome = "", int Slots = 5);
public sealed record CheatWorldRequest(CheatWorldOperation Operation, string Value)
{
    public static readonly IReadOnlyList<CheatBoss> Bosses = Array.AsReadOnly(new[] {
        new CheatBoss("king_slime", "史莱姆王", new[]{"史莱姆王","king slime"}),
        new CheatBoss("eye", "克苏鲁之眼", new[]{"克眼","克苏鲁之眼","eye of cthulhu"}, Night:true),
        new CheatBoss("eater", "世界吞噬怪", new[]{"世吞","世界吞噬怪","eater of worlds"}, Biome:"corruption", Slots:100),
        new CheatBoss("brain", "克苏鲁之脑", new[]{"克脑","克苏鲁之脑","brain of cthulhu"}, Biome:"crimson", Slots:25),
        new CheatBoss("queen_bee", "蜂王", new[]{"蜂王","queen bee"}, Biome:"jungle"),
        new CheatBoss("skeletron", "骷髅王", new[]{"骷髅王","skeletron"}, Night:true),
        new CheatBoss("deerclops", "独眼巨鹿", new[]{"独眼巨鹿","巨鹿","deerclops"}, Biome:"snow"),
        new CheatBoss("wall", "血肉墙", new[]{"肉山","血肉墙","wall of flesh"}, Biome:"underworld", Slots:25),
        new CheatBoss("queen_slime", "史莱姆皇后", new[]{"史莱姆皇后","queen slime"}, Biome:"hallow"),
        new CheatBoss("twins", "双子魔眼", new[]{"双子魔眼","双子","the twins"}, Night:true, Slots:2),
        new CheatBoss("destroyer", "毁灭者", new[]{"毁灭者","铁长直","the destroyer"}, Night:true, Slots:90),
        new CheatBoss("prime", "机械骷髅王", new[]{"机械骷髅王","铁吴克","skeletron prime"}, Night:true),
        new CheatBoss("plantera", "世纪之花", new[]{"世纪之花","世花","plantera"}, Biome:"deep_jungle"),
        new CheatBoss("golem", "石巨人", new[]{"石巨人","golem"}, Biome:"temple"),
        new CheatBoss("duke", "猪龙鱼公爵", new[]{"猪龙鱼公爵","猪鲨","duke fishron"}, Biome:"ocean"),
        new CheatBoss("empress", "光之女皇", new[]{"光之女皇","光女","empress of light"}),
        new CheatBoss("cultist", "拜月教邪教徒", new[]{"拜月教邪教徒","拜月教徒","教徒","lunatic cultist"}),
        new CheatBoss("moon_lord", "月亮领主", new[]{"月亮领主","月总","moon lord"}, Slots:10)
    });
    private static string Compact(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", "");
    public static CheatBoss? FindBoss(string text) => Bosses.FirstOrDefault(b => b.Key == text || b.Aliases.Any(a => Compact(a) == Compact(text)));
    public bool Valid => Operation switch {
        CheatWorldOperation.Time => TryMinute(Value, out _),
        CheatWorldOperation.Weather => Value is "rain" or "clear",
        CheatWorldOperation.Boss => Bosses.Any(b => b.Key == Value),
        CheatWorldOperation.Spawn => Value == "here", _ => false
    };
    public static bool TryMinute(string text, out int minute)
    {
        minute = -1;
        var m = Regex.Match(text, @"^(\d{1,2}):(\d{2})$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int h) || !int.TryParse(m.Groups[2].Value, out int n) || h > 23 || n > 59) return false;
        minute = h * 60 + n; return true;
    }
    public static (bool Day, double Ticks) Clock(int minute)
    {
        if (minute < 0 || minute >= 1440) throw new ArgumentOutOfRangeException(nameof(minute));
        bool day = minute >= 270 && minute < 1170;
        return (day, ((minute - (day ? 270 : 1170) + 1440) % 1440) * 60d);
    }
    public static bool TryParse(string text, out CheatWorldRequest? request, out string error)
    {
        request = null; error = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 240 || AgentProtocol.LooksLikeQuestion(text)) return false;
        string s = Compact(text).TrimEnd('。','！','!','.');
        s = Regex.Replace(s, @"^(?:请帮我|请|帮我|麻烦你|麻烦)?(?:用作弊模式|作弊)?", "");
        var time = Regex.Match(s, @"^(?:把)?时间(?:改成|改为|调成|调到|设为|设置为|设置成|调整到|调整为)(.+)$");
        if (!time.Success) time = Regex.Match(s, @"^(?:切换到|调到|改成)(白天|晚上|黎明|清晨|正午|中午|黄昏|傍晚|午夜|半夜|天亮|天黑)$");
        if (time.Success)
        {
            string v = time.Groups[1].Value switch {
                "白天" or "黎明" or "清晨" or "天亮" => "04:30", "正午" or "中午" => "12:00",
                "晚上" or "黄昏" or "傍晚" or "天黑" => "19:30", "午夜" or "半夜" => "00:00", _ => time.Groups[1].Value
            };
            if (TryMinute(v, out int minute)) request = new(CheatWorldOperation.Time, $"{minute/60:00}:{minute%60:00}");
            else error = "时间请用黎明、正午、黄昏、午夜或24小时制HH:mm，例如：时间改成21:00。";
            return true;
        }
        var weather = Regex.Match(s, @"^(?:把)?天气(?:改成|改为|调成|调到|设为|设置为|设置成|调整为)(.+)$");
        string weatherValue = weather.Success ? weather.Groups[1].Value : s;
        string? w = weatherValue switch { "晴天" or "晴朗" or "放晴" or "停雨" or "停止下雨" => "clear", "雨天" or "下雨" or "开始下雨" => "rain", _ => null };
        if (weather.Success || w != null)
        {
            if (w != null) request = new(CheatWorldOperation.Weather, w);
            else error = "目前天气支持晴天和下雨，例如：天气改成晴天。血月、沙尘暴等事件不作为天气修改。";
            return true;
        }
        if (Regex.IsMatch(s, @"^(?:(?:把)?(?:世界)?出生点(?:设在|设置在|设置为|设为|设到|设置到)(?:这里|当前位置|我这里|我脚下)|(?:在这里)?(?:设置|设定)(?:世界)?出生点)$"))
        { request = new(CheatWorldOperation.Spawn,"here"); return true; }
        var boss = Regex.Match(s, @"^(?:召唤|刷出)(?:一个|一只)?(?:boss[:：]?)?(.+?)(?:boss)?$");
        if (boss.Success)
        {
            var b = FindBoss(boss.Groups[1].Value);
            if (b != null) request = new(CheatWorldOperation.Boss,b.Key);
            else error = "请一次指定一个支持的原版Boss：" + string.Join("、",Bosses.Select(b=>b.Name)) + "。不接受NPC编号、模组Boss或批量召唤。";
            return true;
        }
        return false;
    }
    // No model substitution of target, quantity or coordinates. Old dialogue is not authorization.
    public static bool Authorizes(string goal, CheatWorldRequest request) => request.Valid && TryParse(goal, out var parsed, out _) && parsed == request;
    public static CheatWorldRequest FromTool(string mode, string value)
    {
        var operation = mode switch { "time" => CheatWorldOperation.Time, "weather" => CheatWorldOperation.Weather,
            "boss" => CheatWorldOperation.Boss, "spawn" => CheatWorldOperation.Spawn, _ => (CheatWorldOperation)(-1) };
        if(operation==CheatWorldOperation.Time&&TryMinute(value,out int minute)) value=$"{minute/60:00}:{minute%60:00}";
        var request = new CheatWorldRequest(operation, value);
        if (!request.Valid) throw new FormatException("cheat_world needs time(HH:mm), weather(clear/rain), boss(catalog key) or spawn(here).");
        return request;
    }
}

public readonly record struct CheatWorldContext(bool SinglePlayer, bool Alive, bool Paused, bool Day, bool BossActive,
    int FreeNpcSlots, IReadOnlyCollection<string> Biomes);
public readonly record struct CheatSpawnCell(bool Clear, bool SolidFloor, bool Hazard);
public static class CheatWorldRules
{
    public static string? ScopeRefusal(CheatWorldContext state) => !state.SinglePlayer ? "作弊工具目前只支持单人游戏。" :
        !state.Alive ? "请在角色存活时使用作弊工具。" : state.Paused ? "请先关闭暂停／建造预览，再修改世界。" : null;
    public static string? Refusal(CheatWorldRequest request, CheatWorldContext state)
    {
        if (!request.Valid) return "作弊参数无效，请指定支持的操作和目标。";
        if (ScopeRefusal(state) is string scope) return scope;
        if (request.Operation != CheatWorldOperation.Boss) return null;
        var boss = CheatWorldRequest.FindBoss(request.Value)!;
        if (state.BossActive) return "已有Boss在场，请结束当前战斗后再召唤，避免重复或叠加。";
        if (boss.Night && state.Day) return "这个Boss需要夜晚，请先把时间调到晚上再召唤。";
        if (boss.Biome.Length > 0 && !state.Biomes.Contains(boss.Biome)) return boss.Biome switch {
            "corruption" => "请先到腐化之地召唤世界吞噬怪。", "crimson" => "请先到猩红之地召唤克苏鲁之脑。",
            "jungle" => "请先到丛林召唤蜂王。", "snow" => "请先到雪原召唤独眼巨鹿。",
            "underworld" => "请先到地狱召唤血肉墙。", "hallow" => "请先到神圣之地召唤史莱姆皇后。",
            "deep_jungle" => "请先到地下丛林召唤世纪之花。", "temple" => "请先到丛林神庙召唤石巨人。",
            "ocean" => "请先到海边召唤猪龙鱼公爵。", _ => "当前环境不适合召唤。"
        };
        return state.FreeNpcSlots < boss.Slots ? "NPC空位不足，暂时不能完整召唤这个Boss。" : null;
    }
    // The native world-spawn convention places the feet at y*16, with body above that row.
    public static bool SafeSpawn(int x, int y, int width, int height, Func<int,int,CheatSpawnCell> read)
    {
        if (x < 12 || y < 12 || x >= width-12 || y >= height-12) return false;
        for (int xx=x-1; xx<=x+1; xx++)
        {
            for (int yy=y-4; yy<y; yy++) { var c=read(xx,yy); if (!c.Clear || c.Hazard) return false; }
            var floor=read(xx,y); if (!floor.SolidFloor || floor.Hazard) return false;
        }
        return true;
    }
}

public sealed record CheatWorldResult(bool Success, string Message);
public interface ICheatWorldSite
{
    CheatWorldContext Read();
    string? Preflight(CheatWorldRequest request);
    CheatWorldResult Apply(CheatWorldRequest request);
}
public static class CheatWorldExecutor
{
    public const string Off = "作弊开关未打开或本次权限已失效。请打开对话窗口右上角的「作弊」开关后，重新发送指令。";
    public static CheatWorldResult Execute(CheatAuthority authority, CheatPermit permit, CheatWorldRequest request, ICheatWorldSite site)
    {
        if (!authority.Allows(permit)) return new(false,Off);
        try
        {
            string? error = CheatWorldRules.Refusal(request,site.Read()) ?? site.Preflight(request);
            if (error != null) return new(false,error);
            // Revalidate after preflight; no callback/model result may retain a revoked permission.
            if (!authority.Allows(permit)) return new(false,Off);
            error=CheatWorldRules.Refusal(request,site.Read());
            return error != null ? new(false,error) : site.Apply(request);
        }
        catch (Exception ex) { return new(false,"作弊操作异常，已停止，请核对实际世界状态："+ex.Message); }
    }
}
