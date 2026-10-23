using System.Text.Json;
using TerrarAI.Content.Companion;

static class LearningLoopTests
{
    const string Teaching = "以后夜里采集先问我，白天不用。";
    const string Extraction = """{"intent":"chat","reply":"好。","updates":[{"key":"gather_confirmation","value":"required","when":"night"},{"key":"gather_confirmation","value":"not_required","when":"day"}]}""";
    static CompanionDecision Mine(string mode = "act", int x = 100) => CompanionProtocol.Parse(
        JsonSerializer.Serialize(new { mode, action = new { type = "mine", @params = new { tileX = x, tileY = 50 } } }));

    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "terra-loop-" + Guid.NewGuid());
        try
        {
            var world = Guid.NewGuid();
            var store = new CompanionStore(root, world);
            var updates = PreferenceProtocol.Parse(Extraction, Teaching).Updates;
            store.ApplyPreferences(updates, Teaching, "teach");
            check(store.Memory.Preferences.Count == 2 && store.Memory.RuleRevision == 2, "day and night rules coexist with revision");
            check(store.PromptRules().GetArrayLength() == 2 && !store.PromptMemory(includeRules: false).Contains("gather_confirmation"),
                "planner receives typed conditions without duplicate rule prose");
            check(CompanionPolicy.Check(store.Memory.Preferences, Mine(), day: true) == PolicyResult.Allow
                && CompanionPolicy.Check(store.Memory.Preferences, Mine(), day: false) == PolicyResult.Ask, "same action changes policy with day/night");
            check(CompanionPolicy.Check(store.Memory.Preferences, Mine(), day: null) == PolicyResult.Ask, "unknown context does not grant conditional permission");
            store = new CompanionStore(root, world);
            check(store.PromptMemory().Contains("@night:required") && store.PromptMemory().Contains("@day:not_required")
                && store.Memory.RuleRevision == 2, "conditional rules and revision survive reload");
            store.ApplyPreferences(new[] { new PreferenceUpdate("gather_confirmation", "required", "以后都先问我") }, "以后都先问我", "general");
            check(CompanionPolicy.Check(store.Memory.Preferences, Mine(), day: true) == PolicyResult.Allow, "specific day rule overrides general confirmation");
            store.ApplyPreferences(new[] { new PreferenceUpdate("mining", "forbidden", "以后不要挖矿") }, "以后不要挖矿", "deny");
            check(CompanionPolicy.Check(store.Memory.Preferences, Mine(), approved: true, day: true) == PolicyResult.Deny, "global prohibition survives conditional permission and approval");
            long revision = store.Memory.RuleRevision;
            store.ApplyPreferences(updates, Teaching, "repeat");
            check(store.Memory.RuleRevision == revision, "deduplicated rules do not invalidate approvals");
            store.ForgetPreference(store.Memory.Preferences.FindIndex(p => p.Key == "mining"));
            check(store.Memory.RuleRevision == revision + 1, "forgetting operational memory invalidates approvals");
            var now = DateTimeOffset.UtcNow;
            var session = Guid.NewGuid();
            var approval = new ApprovalTicket(session, "task", Mine("ask").Json, now.AddMinutes(2), 10, false);
            check(approval.Matches(session, "task", Mine("ask").Json, now, 10, false)
                && !approval.Matches(session, "task", Mine("ask").Json, now, 10, true)
                && !approval.Matches(session, "task", Mine("ask").Json, now, 11, false), "approval binds to time and rule revision");
            var clarification = new ClarificationTicket(session, 1, "episode", "以后别这样", "具体？", "挖铜矿", false, 10, false, now.AddMinutes(2));
            check(clarification.Matches(session, 1, 10, false, now)
                && !clarification.Matches(Guid.NewGuid(), 1, 10, false, now)
                && !clarification.Matches(session, 2, 10, false, now)
                && !clarification.Matches(session, 1, 11, false, now)
                && !clarification.Matches(session, 1, 10, true, now)
                && !clarification.Matches(session, 1, 10, false, now.AddMinutes(3)), "clarification cannot cross player, world, context, or expiry");
            string flat = Extraction.Replace("\"night\"", "\"any\"");
            check(PreferenceProtocol.Parse(flat, Teaching).Intent == "clarify", "lost time condition triggers clarification instead of global overwrite");
            check(PreferenceProtocol.Parse(Extraction, "以后采集先问我").Intent == "clarify", "invented time condition cannot enter memory");
            check(PreferenceProtocol.Parse(Extraction, "以后白天采集先问我").Intent == "clarify", "one mentioned time cannot authorize edits to another time");
            var scopeEnum = PreferenceProtocol.SchemaForSource("以后采集前先问我").GetProperty("oneOf")[0].GetProperty("properties")
                .GetProperty("updates").GetProperty("items").GetProperty("oneOf")[0].GetProperty("properties").GetProperty("when").GetProperty("enum");
            check(scopeEnum.GetArrayLength() == 1 && scopeEnum[0].GetString() == "any",
                "generation grammar cannot invent a time condition absent from player evidence");
            var scopeQuestion = PreferenceProtocol.Parse("""{"updates":[{"key":"gather_confirmation","value":"required","when":"day"}],"intent":"chat","reply":"好。"}""",
                "保护我防止被攻击，而且以后采集的时提前跟我说一下");
            check(scopeQuestion.Intent == "clarify" && scopeQuestion.Updates.Count == 0 && scopeQuestion.ScopeCandidates?.Count == 1,
                "rejected invented scope retains a typed question without applying its proposal");
            var allAnswer = PreferenceProtocol.ResolveScope(scopeQuestion.ScopeCandidates, "都适用", "以后采集前先问我\n玩家补充：都适用");
            check(allAnswer?.Intent == "chat" && allAnswer.Updates.Count == 2
                && allAnswer.Updates.All(p => p.Value == "required")
                && allAnswer.Updates.Select(p => p.When).Order().SequenceEqual(new[] { "day", "night" }),
                "explicit all-times scope answer resolves both periods without asking the model again");
            var scopeStore = new CompanionStore(root, Guid.NewGuid());
            scopeStore.ApplyPreferences(allAnswer!.Updates, "以后采集前先问我\n玩家补充：都适用", "scope-answer");
            check(CompanionPolicy.Check(scopeStore.Memory.Preferences, Mine(), day: true) == PolicyResult.Ask
                && CompanionPolicy.Check(scopeStore.Memory.Preferences, Mine(), day: false) == PolicyResult.Ask,
                "resolved scope actually constrains future actions in both periods");
            check(PreferenceProtocol.ResolveScope(scopeQuestion.ScopeCandidates, "白天", "以后采集前先问我\n玩家补充：白天")?.Updates.Single().When == "day"
                && PreferenceProtocol.ResolveScope(scopeQuestion.ScopeCandidates, "晚上", "以后采集前先问我\n玩家补充：晚上")?.Updates.Single().When == "night",
                "single-period scope answers bind only the chosen period");
            check(PreferenceProtocol.ResolveScope(null, "都适用", "都适用") == null
                && PreferenceProtocol.ResolveScope(scopeQuestion.ScopeCandidates, "可能吧", "可能吧") == null
                && PreferenceProtocol.ResolveScope(updates, "都适用", "都适用") == null,
                "scope shorthand cannot invent rules, guess ambiguous answers, or collapse conflicting values");
            const string answeredSource = "保护我防止被攻击，而且以后采集的时提前跟我说一下\n玩家补充：适用于白天，晚上你可以自主采集，但是保护我的命令任何情况";
            var answered = PreferenceProtocol.Parse(Extraction.Replace("\"night\"", "\"swap\"").Replace("\"day\"", "\"night\"").Replace("\"swap\"", "\"day\""), answeredSource);
            check(answered.Intent == "chat" && answered.Updates.Any(p => p.When == "night" && p.Value == "not_required"),
                "screenshot clarification answer accepts separate day and night rules");
            check(PreferenceProtocol.CapabilityNotice(answeredSource).Contains("近身小怪护卫")
                && PreferenceProtocol.CapabilityNotice("以后采集前先问我") == "", "mixed protection requests explain the supported melee scope and separate preference extraction");
            var imaginaryProtection = PreferenceProtocol.Parse("""{"updates":[{"key":"communication","value":"protected","when":"any"},{"key":"gather_confirmation","value":"required","when":"any"}],"intent":"chat","reply":"我会保护你。"}""", "保护我防止被攻击，而且以后采集时提前问我");
            check(imaginaryProtection.Updates.Count == 1 && imaginaryProtection.Updates[0].Key == "gather_confirmation",
                "combat promises cannot be stored as communication preferences");
            check(PreferenceProtocol.PreferenceText("保护我防止被攻击，而且以后采集的时提前跟我说一下") == "以后采集的时提前跟我说一下"
                && PreferenceProtocol.PreferenceText("适用于白天，晚上你可以自主采集，但是保护我的命令任何情况") == "适用于白天，晚上你可以自主采集"
                && PreferenceProtocol.PreferenceText("以后夜里采集先问我，白天不用") == "以后夜里采集先问我，白天不用",
                "combat commands are routed separately without erasing supported preferences or time answers");
            var copied = PreferenceProtocol.Parse("""{"updates":[{"key":"mining","value":"allowed","when":"night"}],"intent":"task","reply":"我先看看目标。"}""", "请挖 tile(2099,210) 的铜矿，挖完这个目标后结束。");
            check(copied.Intent == "task" && copied.Updates.Count == 0, "copied scoped permission is dropped without turning a clear task into clarification");
            check(PreferenceProtocol.Parse("""{"intent":"clarify","reply":"你想改什么？","updates":[]}""", "以后别这样了").Updates.Count == 0, "ambiguous correction cannot mutate memory");
            var forced = DecisionMeasurement.Measure(Mine(), PolicyResult.Ask);
            var needless = DecisionMeasurement.Measure(Mine("ask"), PolicyResult.Allow);
            check(forced.ForcedAsk && !forced.ModelAsked && needless.UnnecessaryAsk, "measurement separates model choice from guard intervention");
            foreach (string type in new[] { "move", "say", "complete" })
            {
                string json = type == "move" ? """{"mode":"ask","action":{"type":"move","params":{"tileX":1,"tileY":2}}}"""
                    : type == "say" ? """{"mode":"ask","action":{"type":"say","params":{"text":"同意吗"}}}"""
                    : """{"mode":"ask","action":{"type":"complete","params":{"message":"结束"}}}""";
                bool rejected = false;
                try { CompanionProtocol.Parse(json); } catch (FormatException) { rejected = true; }
                check(rejected, "approval mode requires a concrete collection action: " + type);
            }
            check(!new TileMutation(1, 2, null, null).Removed && new TileMutation(1, 2, 7, null).Removed
                && !new TileMutation(1, 2, 7, 7).Removed, "empty or unchanged tiles are not verified effects");
            var episodes = new EpisodeRecorder(store);
            string episode = episodes.Begin("turn", "挖矿", new { day = false });
            episodes.Link(episode, "turn", "task", "挖矿");
            episodes.Record("action_result", "task", "decision", new { candidate = false });
            episodes.Close(episode, "stopped");
            episodes.Close(episode, "duplicate");
            var rows = File.ReadAllLines(Path.Combine(store.DirectoryPath, "learning.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
            check(rows.Length == 3 && rows.All(d => d.RootElement.GetProperty("episodeId").GetString() == episode), "episode ties request, task result, and single close together");
            check(rows[1].RootElement.GetProperty("turnId").GetString() == "turn"
                && rows[1].RootElement.GetProperty("decisionId").GetString() == "decision"
                && !rows[2].RootElement.GetProperty("data").GetProperty("goalVerified").GetBoolean(), "episode retains provenance without self-claimed task success");
            foreach (var row in rows) row.Dispose();
            check(new CompanionStore(root, world).Memory.LastTask?.EpisodeId == episode, "previous task linkage survives reload for delayed feedback");
            var legacy = Guid.NewGuid();
            string folder = Path.Combine(root, legacy.ToString("N")); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "memory.json"), JsonSerializer.Serialize(new { version = 1, worldId = legacy.ToString("N"), notes = Array.Empty<string>(), recentEvents = Array.Empty<string>(),
                preferences = new[] { new { key = "gather_confirmation", value = "required", evidence = "以前", source = "以前", turnId = "old", updatedAt = now } } }));
            check(new CompanionStore(root, legacy).Memory.Preferences.Single().When == "any", "version 0.3 preferences migrate as unconditional rules");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task ReportedDialogue(LocalModelClient client, ModelOptions options, Action<bool, string> check)
    {
        const string original = "保护我防止被攻击，而且以后采集的时提前跟我说一下";
        const string answer = "适用于白天，晚上你可以自主采集，但是保护我的命令任何情况";
        const string question = "这条规则适用于白天、夜里，还是所有时间？";
        var results = new List<object>();
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (bool resolving in new[] { false, true })
        {
            string state = JsonSerializer.Serialize(new { message = PreferenceProtocol.PreferenceText(resolving ? answer : original),
                originalMessage = resolving ? PreferenceProtocol.PreferenceText(original) : null, clarificationQuestion = resolving ? question : null,
                mode = "conversation", previousTask = "", preferences = "",
                dialogue = resolving ? new[] { new { Role = "player", text = PreferenceProtocol.PreferenceText(original) }, new { Role = "companion", text = question } } : Array.Empty<object>() }, json);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string raw = await client.SendStructuredAsync(options with { MaxTokens = 256 }, PreferenceProtocol.SystemPrompt,
                state, PreferenceProtocol.SchemaForSource(resolving ? original + "\n玩家补充：" + answer : original), "companion_dialogue", default);
            Console.WriteLine($"REPORTED raw {resolving} {timer.Elapsed.TotalSeconds:F1}s: {raw}");
            string source = resolving ? original + "\n玩家补充：" + answer : original;
            var decision = PreferenceProtocol.Parse(raw, source);
            bool passed = decision.Intent == "chat" && (resolving
                ? decision.Updates.Count == 2 && decision.Updates.Any(p => p.Key == "gather_confirmation" && p.When == "day" && p.Value == "required")
                    && decision.Updates.Any(p => p.Key == "gather_confirmation" && p.When == "night" && p.Value == "not_required")
                : decision.Updates.Count == 1 && decision.Updates[0].Key == "gather_confirmation"
                    && decision.Updates[0].When == "any" && decision.Updates[0].Value == "required");
            results.Add(new { resolving, state, raw, passed, seconds = timer.Elapsed.TotalSeconds });
            Console.WriteLine($"REPORTED {resolving} {timer.Elapsed.TotalSeconds:F1}s: {raw}");
            Directory.CreateDirectory(".runtime");
            File.WriteAllText(".runtime/clarification-live-results.json", JsonSerializer.Serialize(results, json));
            check(passed, resolving ? "live screenshot answer resolves day/night without repeating the question"
                : "live screenshot mixed protection request learns supported rule without inventing day scope");
        }
    }

    // Development cases, not a held-out benchmark. No dialogue/old state is provided at recall.
    internal static async Task Live(LocalModelClient client, ModelOptions options, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "terra-live-loop-" + Guid.NewGuid());
        var results = new List<object>();
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
        async Task<DialogueDecision> Teach(string text, object? extra = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var raw = await client.SendStructuredAsync(options with { MaxTokens = 256 }, PreferenceProtocol.SystemPrompt,
                JsonSerializer.Serialize(extra ?? new { message = text, preferences = "", dialogue = Array.Empty<string>() }, json), PreferenceProtocol.SchemaForSource(text), "companion_dialogue", default);
            var result = PreferenceProtocol.Parse(raw, text);
            Console.WriteLine($"PAHF teach {timer.Elapsed.TotalSeconds:F1}s: {raw}");
            results.Add(new { kind = "teaching", text, raw, seconds = timer.Elapsed.TotalSeconds });
            return result;
        }
        async Task Probe(string label, CompanionStore store, bool day, string expected, int x, string action = "mine")
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string state = JsonSerializer.Serialize(new { task = action == "mine" ? $"挖 tile({x},50) 的铜矿" : $"砍 tile({x},50) 的树", day, time = day ? "day" : "night", at = new[] { x - 1, 50 }, pickaxe = 35, axe = 10,
                ores = new[] { new { name = "Copper", at = new[] { action == "mine" ? x : x + 10, 50 } } },
                trees = new[] { new[] { action == "chop" ? x : x + 10, 50 } },
                rules = store.PromptRules(), memory = store.PromptMemory(includeRules: false) }, json);
            string raw = await client.SendAsync(options with { MaxTokens = 128 }, CompanionProtocol.SystemPrompt, state, default);
            var decision = CompanionProtocol.Parse(raw);
            bool modePassed = decision.Mode == expected;
            bool actionPassed = decision.Type == action;
            bool targetPassed = decision.Parameters.TryGetProperty("tileX", out var tileX) && tileX.GetInt32() == x
                && decision.Parameters.GetProperty("tileY").GetInt32() == 50;
            bool passed = modePassed && actionPassed && targetPassed;
            results.Add(new { kind = "recall", label, day, expected, state, raw, passed, modePassed, actionPassed, targetPassed, seconds = timer.Elapsed.TotalSeconds,
                measurement = DecisionMeasurement.Measure(decision, CompanionPolicy.Check(store.Memory.Preferences, decision, day: day)) });
            Console.WriteLine($"PAHF {label} {timer.Elapsed.TotalSeconds:F1}s: {raw} => {passed}");
        }
        try
        {
            var id = Guid.NewGuid(); var store = new CompanionStore(root, id);
            await Probe("A0_no_memory_night", store, false, "act", 101);
            var learned = await Teach(Teaching);
            check(learned.Updates.Count == 2 && learned.Updates.Any(u => u.When == "night" && u.Value == "required")
                && learned.Updates.Any(u => u.When == "day" && u.Value == "not_required"), "live extracts both time conditions");
            store.ApplyPreferences(learned.Updates, Teaching, "live-teach");
            store = new CompanionStore(root, id);
            await Probe("A2_reload_night_new_target", store, false, "ask", 117);
            await Probe("A2_reload_day_new_target", store, true, "act", 126);
            await Probe("A2_transfer_night_chop", store, false, "ask", 138, "chop");
            await Probe("A2_transfer_day_chop", store, true, "act", 145, "chop");
            await Probe("A2_other_world", new CompanionStore(root, Guid.NewGuid()), false, "act", 156);
            var ambiguous = await Teach("以后别这样了。");
            check(ambiguous.Intent == "clarify" && ambiguous.Updates.Count == 0, "live ambiguous feedback asks instead of guessing");
            var clarified = await Teach("以后别这样了。\n玩家补充：夜里采集要先问，白天直接采集。", new { message = "夜里采集要先问，白天直接采集。", originalMessage = "以后别这样了。", clarificationQuestion = "你希望以后怎样采集？", preferences = "", dialogue = Array.Empty<string>() });
            check(clarified.Updates.Count == 2 && clarified.Updates.Any(u => u.When == "night" && u.Value == "required"), "live clarification answer resolves original feedback");
            const string driftText = "我改主意了，以后白天采集也先问我。";
            var drift = await Teach(driftText);
            store.ApplyPreferences(drift.Updates, driftText, "live-drift");
            check(drift.Updates.Any(u => u.When == "day" && u.Value == "required"), "live correction updates day condition");
            await Probe("A2_drift_day", new CompanionStore(root, id), true, "ask", 167);
            var temporary = await Teach("这次不用问了。");
            check(temporary.Updates.Count == 0, "live temporary permission does not overwrite learned rules");
            await Probe("A2_after_temporary_night", store, false, "ask", 179);
            const string reverseText = "以后改成白天采集先问，夜里不用。";
            var reverse = await Teach(reverseText);
            store.ApplyPreferences(reverse.Updates, reverseText, "live-reverse");
            check(reverse.Updates.Any(u => u.When == "night" && u.Value == "not_required")
                && reverse.Updates.Any(u => u.When == "day" && u.Value == "required"), "live reversed preference is explicit, not a night heuristic");
            await Probe("A2_reverse_night", store, false, "act", 188);
            await Probe("A2_reverse_day", store, true, "ask", 198);
        }
        finally
        {
            Directory.CreateDirectory(".runtime");
            File.WriteAllText(".runtime/pahf-live-results.json", JsonSerializer.Serialize(new { suite = "development", heldOut = false,
                model = options.Model, temperature = options.Temperature, system = CompanionProtocol.SystemPrompt,
                preferenceSystem = PreferenceProtocol.SystemPrompt, results }, json));
            Directory.Delete(root, true);
        }
        foreach (var row in results)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row));
            if (doc.RootElement.TryGetProperty("passed", out var passed)) check(passed.GetBoolean(), "live PAHF: " + doc.RootElement.GetProperty("label").GetString());
        }
    }
}
