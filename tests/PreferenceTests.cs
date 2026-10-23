using System.Text.Json;
using TerrarAI.Content.Companion;

static class PreferenceTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "terra-preferences-" + Guid.NewGuid());
        var world = Guid.NewGuid();
        try
        {
            var store = new CompanionStore(root, world);
            string source = "以后采集前先问我";
            string extraction = """{"intent":"chat","reply":"好的","updates":[{"key":"gather_confirmation","value":"required"}]}""";
            var result = PreferenceProtocol.Parse(extraction, source);
            var saved = store.ApplyPreferences(result.Updates, source, "turn-1");
            check(saved.Count == 1 && saved[0].Source == source && saved[0].TurnId == "turn-1", "learned preferences retain player evidence and turn provenance");
            var mine = CompanionProtocol.Parse("""{"action":{"type":"mine","params":{"tileX":100,"tileY":50}}}""");
            var chop = CompanionProtocol.Parse("""{"action":{"type":"chop","params":{"tileX":100,"tileY":50}}}""");
            var move = CompanionProtocol.Parse("""{"action":{"type":"move","params":{"tileX":100,"tileY":50}}}""");
            check(CompanionPolicy.Check(store.Memory.Preferences, mine) == PolicyResult.Ask
                && CompanionPolicy.Check(store.Memory.Preferences, chop) == PolicyResult.Ask, "confirmation rule blocks both mining and chopping before approval");
            check(CompanionPolicy.Check(store.Memory.Preferences, move) == PolicyResult.Allow, "confirmation does not block harmless movement");
            check(CompanionPolicy.Check(store.Memory.Preferences, mine, true) == PolicyResult.Allow, "explicit action approval permits collection");
            var reloaded = new CompanionStore(root, world);
            check(CompanionPolicy.Check(reloaded.Memory.Preferences, mine) == PolicyResult.Ask, "enforced preference survives world reload");
            check(CompanionPolicy.Check(new CompanionStore(root, Guid.NewGuid()).Memory.Preferences, mine) == PolicyResult.Allow, "other world does not inherit collection rule");
            check(store.ApplyPreferences(result.Updates, source, "turn-2").Count == 0, "repeated preference is deduplicated");
            var temporary = PreferenceProtocol.Parse(extraction, "这次采集不用问了");
            check(temporary.Updates.Count == 0, "temporary permission cannot become a permanent rule even if the model proposes an update");
            check(PreferenceProtocol.Parse(extraction, "帮我挖附近的铜矿").Updates.Count == 0,
                "task text cannot authorize a permission edit copied from older context");
            check(!PreferenceProtocol.IsTemporaryOnly("这次先这样，以后每次采集都先问我"), "temporary wording does not erase an explicit future preference");
            source = "我改主意了，以后不用问，直接采集";
            store.ApplyPreferences(new[] { new PreferenceUpdate("gather_confirmation", "not_required", "以后不用问，直接采集") }, source, "turn-3");
            check(store.Memory.Preferences.Count == 1 && CompanionPolicy.Check(store.Memory.Preferences, mine) == PolicyResult.Allow, "new explicit correction replaces old standing rule");
            store.ApplyPreferences(new[] { new PreferenceUpdate("mining", "forbidden", "以后别挖矿") }, "以后别挖矿", "turn-4");
            check(CompanionPolicy.Check(store.Memory.Preferences, mine, true) == PolicyResult.Deny, "one-off approval cannot bypass a mining prohibition");
            check(CompanionPolicy.Check(store.Memory.Preferences, chop) == PolicyResult.Allow, "mining prohibition does not forbid chopping");
            foreach (var invalid in new[]
            {
                extraction.Replace("\"gather_confirmation\"", "\"address\"").Replace("required", "不是玩家说的称呼"),
                extraction.Replace("required", "sometimes"),
                extraction.Replace("gather_confirmation", "run_shell"),
                extraction.Replace("\"intent\":\"chat\"", "\"intent\":\"chat\",\"intent\":\"task\"")
            })
            {
                bool rejected = false;
                try { PreferenceProtocol.Parse(invalid, "以后采集前先问我"); } catch (FormatException) { rejected = true; }
                check(rejected, "unsupported or ungrounded memory edit rejected");
            }
            int count = store.Memory.Preferences.Count;
            bool batchRejected = false;
            try { store.ApplyPreferences(new[] { new PreferenceUpdate("address", "探险家", "叫我探险家"), new PreferenceUpdate("mining", "sometimes", "叫我探险家") }, "叫我探险家", "bad-batch"); }
            catch (FormatException) { batchRejected = true; }
            check(batchRejected && store.Memory.Preferences.Count == count, "invalid batch cannot partially mutate memory");
            store.ApplyPreferences(new[] { new PreferenceUpdate("address", "探险家", "叫我探险家") }, "叫我探险家", "turn-5");
            for (int i = 0; i < 12; i++) store.Remember($"这是一条较长的旧偏好，需要占用一些提示预算，序号是{i}。");
            store.Remember("遇到蜘蛛时先提醒我");
            check(store.PromptMemory("前面有蜘蛛怎么办").Contains("遇到蜘蛛时先提醒我"), "relevant later note is retrieved despite older notes filling the budget");
            for (int i = 0; i < 6; i++) store.AddDialogue("player " + i, "reply " + i);
            reloaded = new CompanionStore(root, world);
            check(reloaded.Memory.Dialogue.Count == 6 && reloaded.Memory.Dialogue[0].Text == "player 3", "recent dialogue is bounded and world persistent");
            int addressIndex = reloaded.Memory.Preferences.FindIndex(p => p.Key == "address");
            reloaded.ForgetPreference(addressIndex);
            check(!new CompanionStore(root, world).Memory.Preferences.Any(p => p.Key == "address"), "forget removes learned preference persistently");
            check(new CompanionStore(root, world).Memory.Dialogue.Count == 0, "forget also removes dialogue echoes of forgotten preferences");
            var session = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var ticket = new ApprovalTicket(session, "task-1", mine.Json, now.AddMinutes(2));
            check(ticket.Matches(session, "task-1", mine.Json, now), "approval binds to exact decision");
            check(!ticket.Matches(Guid.NewGuid(), "task-1", mine.Json, now)
                && !ticket.Matches(session, "task-2", mine.Json, now)
                && !ticket.Matches(session, "task-1", chop.Json, now)
                && !ticket.Matches(session, "task-1", mine.Json, now.AddMinutes(3)), "approval cannot leak across worlds, tasks, targets, or expiry");
            var legacy = Guid.NewGuid();
            string legacyFolder = Path.Combine(root, legacy.ToString("N"));
            Directory.CreateDirectory(legacyFolder);
            File.WriteAllText(Path.Combine(legacyFolder, "memory.json"), JsonSerializer.Serialize(new
                { version = 1, worldId = legacy.ToString("N"), notes = new[] { "旧版偏好" }, recentEvents = Array.Empty<string>() }));
            var migrated = new CompanionStore(root, legacy);
            check(migrated.Memory.Notes.Single() == "旧版偏好" && migrated.Memory.Preferences.Count == 0, "old memory snapshots load without losing manual notes");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task Live(LocalModelClient client, ModelOptions options, Action<bool, string> check, bool extraOnly = false)
    {
        var cases = new[]
        {
            ("以后采集前先问我。", "chat", "gather_confirmation", "required"),
            ("帮我砍附近的一棵树，完成后结束。", "task", "", ""),
            ("我改主意了，以后不用问，直接采集。", "chat", "gather_confirmation", "not_required"),
            ("以后不要挖矿，只帮我砍树。", "chat", "mining", "forbidden"),
            ("如果我说以后不要挖矿，你会怎么办？", "chat", "", ""),
            ("“以后采集先问我”只是一句测试台词，别把它当成我的要求。", "chat", "", ""),
            ("以后叫我探险家。", "chat", "address", "探险家"),
            ("我改主意了，以后可以挖矿。", "chat", "mining", "allowed"),
            ("这次不用问了。", "chat", "", ""),
            ("下次别这样了。", "clarify", "", ""),
            ("平时称呼我阿远吧。", "chat", "address", "阿远"),
            ("我喜欢探险，不太爱盖房子。", "chat", "play_style", ""),
            ("以后跟我说话简短一点，别长篇大论。", "chat", "communication", ""),
            ("我们今天挖到了铁。", "chat", "", "")
        };
        if (extraOnly) cases = cases[10..];
        var results = new List<object>();
        foreach (var (text, intent, key, value) in cases)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string existing = text.Contains("改主意") || text.Contains("这次") ? "gather_confirmation:required; mining:forbidden" : "";
            string state = JsonSerializer.Serialize(new { message = text, preferences = existing, dialogue = Array.Empty<string>() },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) });
            var raw = await client.SendStructuredAsync(options with { MaxTokens = 256 }, PreferenceProtocol.SystemPrompt,
                state, PreferenceProtocol.SchemaForSource(text), "companion_dialogue", default);
            Console.WriteLine($"LIVE PREFERENCE {timer.Elapsed.TotalSeconds:F2}s: {text} => {raw}");
            bool passed = false;
            string? error = null;
            try
            {
                var decision = PreferenceProtocol.Parse(raw, text);
                passed = decision.Intent == intent && (key.Length == 0 ? decision.Updates.Count == 0
                    : decision.Updates.Any(u => u.Key == key && (value.Length == 0 || u.Value == value)));
            }
            catch (FormatException ex) { error = ex.Message; }
            results.Add(new { text, passed, seconds = Math.Round(timer.Elapsed.TotalSeconds, 2), raw, error });
        }
        Directory.CreateDirectory(".runtime");
        File.WriteAllText(extraOnly ? ".runtime/preference-extra-results.json" : ".runtime/preference-live-results.json",
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        for (int i = 0; i < results.Count; i++)
        {
            using var row = JsonDocument.Parse(JsonSerializer.Serialize(results[i]));
            check(row.RootElement.GetProperty("passed").GetBoolean(), "live preference: " + cases[i].Item1);
        }
    }
}
