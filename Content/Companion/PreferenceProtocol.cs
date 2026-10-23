using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public sealed record PreferenceUpdate(string Key, string Value, string Evidence, string When = "any");
public sealed record DialogueDecision(string Intent, string Reply, IReadOnlyList<PreferenceUpdate> Updates,
    IReadOnlyList<PreferenceUpdate>? ScopeCandidates = null);

// Dialogue produces proposed memory edits, never executable game actions.
public static class PreferenceProtocol
{
    public const string SystemPrompt = """
        你是泰拉瑞亚同伴。先提取当前 message 中明确、长期的偏好到 updates，再判断 intent。reply 最多15字，有更新只写“好。”。
        intent 和 updates 只按当前 message 判断；preferences、dialogue、previousTask 都是背景，不能重放其中的旧要求。当前只有即时挖矿或砍树要求、没有长期要求时，intent=task、updates=[]；已有的采集确认规则由执行阶段处理，不能把任务改成 chat。
        每条更新有 key,value,when；when=any/day/night，分别为不限时间/白天/夜里。只按玩家原话选 when，state.day/time 是世界钟表，不能据此限定规则。未提时段就用 any。
        明确偏好必须输出更新，不能只口头答应。代码会附上当前玩家原话、时间和来源，无需复述证据。
        临时任务、这一次的许可、假设、引用、问题、游戏事实和你自己的回复不是长期偏好。
        含糊不清就 updates=[] 并问清楚，不能只说“明白了”；不要猜。旧 dialogue 仅供理解上下文，当前 preferences 优先。
        若有 clarificationQuestion，message 是回答，结合 originalMessage 处理。已答清时段就 chat 并更新，不能重复问；“都适用”表示白天夜里。二者都是本轮玩家原话。
        key: address=玩家称呼(只存名字); play_style=玩法偏好; communication=交流习惯; note=其他明确偏好。
        gather_confirmation 的 value 只能是 required(采集先问)或 not_required(取消此规则)。
        mining/chopping 的 value 只能是 forbidden(长期禁止挖矿/砍树)或 allowed(取消禁令)。
        intent=task 仅用于明确要求现在执行一个游戏动作；长期行为规则是 chat。task 的 reply 要贴合这次任务。
        提问不是 task：问进度、问数量、问在干嘛（吗、？、几、多少、怎么样）都选 intent=chat，不能开始新任务。
        intent=clarify 表示话语有歧义，需要玩家回答具体问题，此时 updates=[]，reply 写问题。
        “以后别这样了”没有明确要求，必须 clarify。不要从旧任务猜长期规则。只解释“这次”不修改长期偏好。
        不要声称已执行任务或已保存记忆（由代码确认），不保存密码等无关隐私。
        同伴支持跟随、移动、挖矿、砍树、合成建造及近身小怪护卫。保护不是 communication 偏好；不保证免伤，不打Boss。护卫任务请单独说“保护我”；这里仅提取长期偏好，不凭偏好提取结果启动战斗。
        示例：
        message:以后采集前先问我。
        {"updates":[{"key":"gather_confirmation","value":"required","when":"any"}],"intent":"chat","reply":"好。"}
        message:以后不用问了，直接采集。
        {"updates":[{"key":"gather_confirmation","value":"not_required","when":"any"}],"intent":"chat","reply":"好。"}
        message:以后叫我探险家。
        {"updates":[{"key":"address","value":"探险家","when":"any"}],"intent":"chat","reply":"好。"}
        message:以后夜里采集先问我，白天不用。
        {"updates":[{"key":"gather_confirmation","value":"required","when":"night"},{"key":"gather_confirmation","value":"not_required","when":"day"}],"intent":"chat","reply":"好。"}
        clarificationQuestion:采集前确认适用什么时段？ message:适用于白天，晚上你可以自主采集。
        {"updates":[{"key":"gather_confirmation","value":"required","when":"day"},{"key":"gather_confirmation","value":"not_required","when":"night"}],"intent":"chat","reply":"白天先问，夜里直接采集。"}
        message:帮我砍附近一棵树。
        {"updates":[],"intent":"task","reply":"好，这就去砍。"}
        message:帮我挖点铜矿。
        {"updates":[],"intent":"task","reply":"收到，我去找铜矿。"}
        message:砍完了吗？
        {"updates":[],"intent":"chat","reply":"我看看进度。"}
        message:这次不用问了。
        {"updates":[],"intent":"chat","reply":"这次的许可不会修改长期规则。"}
        message:如果我说以后不要挖矿，你会怎么办？
        {"updates":[],"intent":"chat","reply":"你如果明确提出，我会遵守。"}
        /no_think
        """;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"oneOf":[{"type":"object","additionalProperties":false,"required":["updates","intent","reply"],"properties":{
          "updates":{"type":"array","maxItems":3,"items":{"oneOf":[
            {"type":"object","additionalProperties":false,"required":["key","value","when"],"properties":{
              "key":{"const":"gather_confirmation"},"value":{"enum":["required","not_required"]},"when":{"enum":["any","day","night"]}}},
            {"type":"object","additionalProperties":false,"required":["key","value","when"],"properties":{
              "key":{"enum":["mining","chopping"]},"value":{"enum":["forbidden","allowed"]},"when":{"enum":["any","day","night"]}}},
            {"type":"object","additionalProperties":false,"required":["key","value","when"],"properties":{
              "key":{"enum":["address","play_style","communication","note"]},"value":{"type":"string","minLength":1,"maxLength":120},"when":{"const":"any"}}}
          ]}},
          "intent":{"enum":["chat","task"]},"reply":{"type":"string","minLength":1,"maxLength":100}}},
          {"type":"object","additionalProperties":false,"required":["updates","intent","reply"],"properties":{
            "updates":{"type":"array","maxItems":0},"intent":{"const":"clarify"},
            "reply":{"type":"string","minLength":1,"maxLength":100}}}]}
        """).RootElement.Clone();

    public static JsonElement SchemaForSource(string source)
    {
        bool day = MentionsDay(source), night = MentionsNight(source);
        string[] allowed = !day && !night ? new[] { "any" }
            : AllTimes(source) ? new[] { "any", "day", "night" }
            : day && night ? new[] { "day", "night" } : new[] { day ? "day" : "night" };
        var schema = JsonNode.Parse(Schema.GetRawText())!;
        var branches = schema["oneOf"]![0]!["properties"]!["updates"]!["items"]!["oneOf"]!;
        for (int i = 0; i < 2; i++)
            branches[i]!["properties"]!["when"]!["enum"] = JsonSerializer.SerializeToNode(allowed);
        return JsonSerializer.SerializeToElement(schema);
    }

    private static bool MentionsDay(string source) => new[] { "白天", "日间", "daytime" }
        .Any(word => source.Contains(word, StringComparison.OrdinalIgnoreCase));
    private static bool MentionsNight(string source) => new[] { "夜里", "夜晚", "夜间", "晚上", "night" }
        .Any(word => source.Contains(word, StringComparison.OrdinalIgnoreCase));
    private static bool AllTimes(string source) => new[] { "不分昼夜", "全天", "任何时候", "无论", "不管", "都适用", "所有时间" }.Any(source.Contains);

    public static DialogueDecision Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Keys(root, "intent", "reply", "updates");
        string intent = Text(root.GetProperty("intent"), 7);
        if (intent is not ("chat" or "task" or "clarify")) throw new FormatException("Invalid dialogue intent.");
        string reply = Text(root.GetProperty("reply"), 100);
        var updates = root.GetProperty("updates");
        if (updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() > 3)
            throw new FormatException("Too many preference updates.");
        var result = new List<PreferenceUpdate>();
        foreach (var item in updates.EnumerateArray())
        {
            bool hasWhen = item.TryGetProperty("when", out var when);
            Keys(item, hasWhen ? new[] { "key", "value", "when" } : new[] { "key", "value" });
            var update = new PreferenceUpdate(Text(item.GetProperty("key"), 32),
                Text(item.GetProperty("value"), 120), source, hasWhen ? Text(when, 5) : "any");
            Validate(update, source);
            if (update.Key != "note" && result.Any(p => p.Key == update.Key && p.When == update.When))
                throw new FormatException("Conflicting updates in one message; please clarify.");
            result.Add(update);
        }
        // An action or a promised protection effect is not a communication preference.
        if (CapabilityNotice(source).Length > 0)
            result.RemoveAll(p => p.Key is "communication" or "play_style" or "note" &&
                new[] { "protect", "defend", "combat", "battle", "保护", "护卫", "战斗", "打怪" }
                    .Any(word => p.Value.Contains(word, StringComparison.OrdinalIgnoreCase)));
        if (intent == "clarify") return new DialogueDecision(intent, reply, Array.Empty<PreferenceUpdate>(),
            IsScopeQuestion(reply) && HasStandingRuleLanguage(source) && !IsTemporaryOnly(source)
                ? result.Where(p => IsOperational(p.Key)).ToArray() : null);
        // Suppress copied permissions before checking their scope. An ignored edit
        // must not turn an otherwise explicit task into an unnecessary question.
        int suppressed = result.RemoveAll(update =>
            IsOperational(update.Key) && !HasStandingRuleLanguage(source));
        if (suppressed > 0 && intent == "chat") reply = "长期规则没有改动。如果想永久修改，请说明“以后”要怎么做。";
        bool dayMentioned = MentionsDay(source);
        bool nightMentioned = MentionsNight(source);
        bool timeMentioned = dayMentioned || nightMentioned;
        bool allTimes = AllTimes(source);
        if (timeMentioned && !allTimes && result.Any(p => IsOperational(p.Key) && p.When == "any"))
            return AskScope(result);
        if (result.Any(p => IsOperational(p.Key) && ((p.When == "day" && !dayMentioned) || (p.When == "night" && !nightMentioned))))
            return AskScope(result);
        // The model may confuse a one-off permission with a permanent setting.
        // This guard only suppresses memory writes; it never infers a preference from keywords.
        if (IsTemporaryOnly(source) && result.Count > 0)
        {
            result.Clear();
            if (intent == "chat") reply = "这次的安排不会修改长期偏好。";
        }
        return new DialogueDecision(intent, reply, result);
    }

    private static DialogueDecision AskScope(IReadOnlyList<PreferenceUpdate> updates) => new("clarify",
        string.Join("、", updates.Where(p => IsOperational(p.Key)).Select(p => Describe(p.Key, p.Value)).Distinct())
        + "：适用于白天、夜里，还是所有时间？可直接回答其中一项。", Array.Empty<PreferenceUpdate>(),
        updates.Where(p => IsOperational(p.Key)).ToArray());

    private static bool IsScopeQuestion(string text) => text.Contains("白天")
        && (text.Contains("夜") || text.Contains("晚上")) && (text.Contains("所有") || text.Contains("适用"));

    // Only resolves an explicit answer to a known, typed scope question. It does not
    // infer a new rule or treat arbitrary dialogue as a preference.
    public static DialogueDecision? ResolveScope(IReadOnlyList<PreferenceUpdate>? candidates, string answer, string source)
    {
        if (candidates == null || candidates.Count == 0) return null;
        string normalized = answer.Trim().TrimEnd('。', '！', '!', '.', '，', ',');
        string? scope = normalized switch
        {
            "白天" or "只在白天" or "适用于白天" => "day",
            "夜里" or "夜晚" or "晚上" or "只在晚上" or "适用于夜里" => "night",
            "都适用" or "都要" or "全天" or "所有时间" or "任何时候" or "不分昼夜" or "白天夜里都适用" => "both",
            _ => null
        };
        if (scope == null || candidates.Any(p => !IsOperational(p.Key))) return null;
        var groups = candidates.GroupBy(p => p.Key).ToArray();
        // Conflicting values require a fuller answer, never an arbitrary selection.
        if (groups.Any(g => g.Select(p => p.Value).Distinct().Count() != 1)) return null;
        var resolved = groups.SelectMany(g => (scope == "both" ? new[] { "day", "night" } : new[] { scope })
            .Select(when => g.First() with { When = when, Evidence = source })).ToArray();
        return new DialogueDecision("chat", "已收到适用时段。", resolved);
    }

    public static string CapabilityNotice(string text) => new[] { "保护我", "防止被攻击", "帮我打怪", "替我战斗" }.Any(text.Contains)
        ? "支持近身小怪护卫，不保证免伤，也不攻击Boss；要启动请单独说“保护我”。采集规则可以单独记住。" : "";

    public static string PreferenceText(string text)
    {
        if (CapabilityNotice(text).Length == 0) return text;
        // Keep combat commands separate from standing preference extraction, preserving
        // the player's actual preference clauses for extraction. Raw evidence is retained.
        return string.Join("，", Regex.Split(text, @"(?:[，,。；;]|而且|并且|但是|不过)+")
            .Where(clause => !string.IsNullOrWhiteSpace(clause) && CapabilityNotice(clause).Length == 0));
    }

    public static bool IsTemporaryOnly(string text) =>
        new[] { "这次", "这一次", "本次", "这回", "这趟", "暂时", "just this time", "for now" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
        && !new[] { "以后", "今后", "往后", "从现在开始", "每次", "一直", "总是", "from now on", "always" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool HasStandingRuleLanguage(string text) =>
        new[] { "以后", "今后", "往后", "从现在开始", "每次", "一直", "总是", "改主意", "重新", "恢复",
            "取消", "不再", "别再", "不用再", "采集前", "挖矿前", "砍树前", "from now on", "always", "stop asking" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsOperational(string key) => key is "gather_confirmation" or "mining" or "chopping";

    public static void Validate(PreferenceUpdate update, string source)
    {
        if (string.IsNullOrWhiteSpace(update.Value) || update.Value.Length > 120
            || string.IsNullOrWhiteSpace(update.Evidence) || update.Evidence.Length > 512
            || !source.Contains(update.Evidence, StringComparison.Ordinal))
            throw new FormatException("Preference evidence must quote the current player message.");
        bool valid = update.Key switch
        {
            "gather_confirmation" => update.Value is "required" or "not_required",
            "mining" or "chopping" => update.Value is "forbidden" or "allowed",
            "address" => update.Value.Length <= 30 && source.Contains(update.Value, StringComparison.Ordinal),
            "play_style" or "communication" or "note" => true,
            _ => false
        };
        if (!valid) throw new FormatException("Unsupported preference key/value.");
        if (update.When is not ("any" or "day" or "night") || (!IsOperational(update.Key) && update.When != "any"))
            throw new FormatException("Unsupported preference condition.");
    }

    private static string Text(JsonElement value, int max) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            && value.GetString()!.Length <= max ? value.GetString()! : throw new FormatException("Invalid dialogue text.");

    private static void Keys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected dialogue object.");
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name) || !seen.Add(property.Name)) throw new FormatException("Unexpected dialogue field.");
        if (seen.Count != keys.Length) throw new FormatException("Missing dialogue field.");
    }

    public static string Describe(string key, string value) => key switch
    {
        "gather_confirmation" => value == "required" ? "采集前先征求你的同意" : "采集前不用逐次确认",
        "mining" => value == "forbidden" ? "不挖矿" : "允许挖矿",
        "chopping" => value == "forbidden" ? "不砍树" : "允许砍树",
        "address" => "称呼你为“" + value + "”",
        _ => value
    };

    public static string Describe(LearnedPreference preference) =>
        (preference.When == "night" ? "夜里：" : preference.When == "day" ? "白天：" : "") + Describe(preference.Key, preference.Value);
}
