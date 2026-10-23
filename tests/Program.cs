using System.Net;
using System.Text;
using System.Text.Json;
using TerrarAI.Content.Companion;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
const string Move = """{"action":{"type":"move","params":{"tileX":100,"tileY":50}}}""";
Check(CompanionProtocol.Parse(Move).Type == "move", "compact action accepted");
foreach (var invalid in new[] {
    "null", "[]", "{}", "{\"action\":\"move\",\"params\":{}}",
    """{"action":{"type":"move","params":{"tileX":-1,"tileY":5}}}""",
    """{"action":{"type":"move","params":{"tileX":1.5,"tileY":5}}}""",
    """{"action":{"type":"move","params":{"tileX":true,"tileY":5}}}""",
    """{"action":{"type":"build","params":{"direction":"left"}}}""",
    """{"action":{"type":"say","params":{"text":""}}}""",
    """{"action":{"type":"say","type":"complete","params":{"text":"hi"}}}""",
    """{"action":{"type":"move","params":{"tileX":1,"tileY":5,"extra":1}}}"""
})
{
    bool rejected = false;
    try { CompanionProtocol.Parse(invalid); } catch (Exception) { rejected = true; }
    Check(rejected, "malformed/unsupported action rejected: " + invalid);
}

string temp = Path.Combine(Path.GetTempPath(), "terra-companion-tests-" + Guid.NewGuid());
try
{
    var worldA = Guid.NewGuid();
    var worldB = Guid.NewGuid();
    var a = new CompanionStore(temp, worldA);
    a.Remember("不要砍花园里的树");
    a.Observe("Moved to tile(100,50).");
    a.Record("decision_request", "task-a", "decision-a", new { state = "sample", model = "qwen3-4b" });
    a.Record("action_result", "task-a", "decision-a", new { status = "Failure", candidate = false });
    var b = new CompanionStore(temp, worldB);
    Check(b.Memory.Notes.Count == 0 && b.Memory.RecentEvents.Count == 0, "new world has no other-world memories");
    var reloaded = new CompanionStore(temp, worldA);
    Check(reloaded.Memory.Notes.Single() == "不要砍花园里的树", "preferences survive restart");
    Check(reloaded.Memory.RecentEvents.Single().Contains("100,50"), "observations survive restart");
    var records = File.ReadAllLines(Path.Combine(a.DirectoryPath, "learning.jsonl"));
    Check(records.Length == 2, "learning history survives restart");
    using (var log = JsonDocument.Parse(records[1]))
        Check(log.RootElement.GetProperty("worldId").GetString() == worldA.ToString("N") &&
              log.RootElement.GetProperty("decisionId").GetString() == "decision-a" &&
              !log.RootElement.GetProperty("data").GetProperty("candidate").GetBoolean(), "outcomes preserve provenance and failures are not positive examples");
    reloaded.Forget(0);
    Check(new CompanionStore(temp, worldA).Memory.Notes.Count == 0, "forget persists");
    for (int i = 0; i < 40; i++) reloaded.Observe("event " + i);
    Check(reloaded.Memory.RecentEvents.Count == 12 && reloaded.PromptMemory().Length <= 320, "history and prompt remain bounded");
    b.Remember("backup survives");
    b.Remember("newer note");
    File.WriteAllText(Path.Combine(b.DirectoryPath, "memory.json"), "{broken");
    var recovered = new CompanionStore(temp, worldB);
    Check(recovered.RecoveredFromBackup && recovered.Memory.Notes.Contains("backup survives"), "corrupt snapshot recovers from backup");
    Check(Directory.GetFiles(b.DirectoryPath, "*.corrupt-*").Length == 1, "corrupt snapshot retained for recovery");
}
finally { Directory.Delete(temp, true); }

var options = new ModelOptions("http://127.0.0.1:8080/v1/chat/completions", "qwen3-4b", "", 0, 128, 10);
var fake = new FakeModelHandler(Move);
using (var client = new LocalModelClient(new HttpClient(fake)))
{
    await Task.WhenAll(client.SendAsync(options, CompanionProtocol.SystemPrompt, "first", default),
                      client.SendAsync(options, CompanionProtocol.SystemPrompt, "second", default));
    Check(fake.PeakConcurrent == 1, "local model requests are serialized");
    using var payload = JsonDocument.Parse(fake.Payloads[0]);
    Check(!fake.HadAuthorization && !payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), "local requests need no key and disable thinking");
    Check(payload.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_schema", "request carries action schema");
    Check(payload.RootElement.GetProperty("cache_prompt").GetBoolean(), "transport explicitly enables reusable prompt caching");
    using var lifetime = new CancellationTokenSource();
    var oldTask = client.SendAsync(options, CompanionProtocol.SystemPrompt, "old world", lifetime.Token);
    lifetime.CancelAfter(25);
    bool cancelled = false;
    try { await oldTask; } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "world/command cancellation aborts in-flight request");
    Check(await client.SendAsync(options, CompanionProtocol.SystemPrompt, "new world", default) == Move, "cancelled request releases queue for new world");
}
using (var client = new LocalModelClient(new HttpClient(new FakeModelHandler(Move, "length"))))
{
    bool rejected = false;
    try { await client.SendAsync(options, CompanionProtocol.SystemPrompt, "truncate", default); }
    catch (FormatException) { rejected = true; }
    Check(rejected, "truncated model output rejected");
}

PreferenceTests.Run(Check);
LearningLoopTests.Run(Check);
await InquiryTests.Run(Check);
await WikiTests.Run(Check, args);
BrainInquiryTests.Run(Check);
ControlTests.Run(Check);
PlatformTests.Run(Check);
PlatformSupplyTests.Run(Check);
StructureTests.Run(Check);
HousingRepairTests.Run(Check);
ChestTests.Run(Check);
CraftChainTests.Run(Check);
CombatTests.Run(Check);
LocationTests.Run(Check);
ExpeditionTests.Run(Check);
OreTunnelTests.Run(Check);
ReminderTests.Run(Check);
ArenaTests.Run(Check);
GardeningTests.Run(Check);
HealingTests.Run(Check);
CheatWorldTests.Run(Check);
TaskRecoveryTests.Run(Check);
CollaborationTests.Run(Check);
AgentTests.Run(Check);

if (args.Length == 2 && args[0] == "--inquiry-live")
{
    using var client = new LocalModelClient();
    var world = new { Name = "苔藓小屋", Difficulty = "大师", Stage = "困难模式之前", Clock = "夜里 21:00",
        Weather = "晴朗", Biome = "森林", Layer = "地表", life = new { current = 43, maximum = 100 }, mana = new { current = 20, maximum = 20 },
        Defeated = new[] { "克苏鲁之眼" }, inventory = new[] { new { Name = "木材", Count = 17 } },
        inventoryOmitted = 0, Equipment = Array.Empty<string>(), Nearby = new[] { "向导" }, NearbyHostiles = 3 };
    var cases = new[] {
        ("live_snapshot", "我的当前生命和生命上限分别是多少，附近有几个敌怪？"),
        ("progress", "按记录，我打败过克苏鲁之眼吗？"),
        ("unknown", "告诉我箱子里有多少铂金币，还要给我地下金矿的准确坐标。")
    };
    var rows = new List<object>();
    foreach (var (name, question) in cases)
    {
        string state = JsonSerializer.Serialize(new { question, world, preferences = "" }, new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string raw = await client.SendStructuredAsync(options with { Endpoint = args[1], MaxTokens = 384, TimeoutSeconds = 180 },
            InquiryProtocol.SystemPrompt, state, InquiryProtocol.Schema, "companion_inquiry", default);
        string answer = InquiryProtocol.Parse(raw);
        bool ok = name switch {
            "live_snapshot" => answer.Contains("43") && answer.Contains("100") && answer.Contains("3"),
            "progress" => answer.Contains("克苏鲁之眼") && (answer.Contains("已") || answer.Contains("击败")) &&
                !answer.Contains("43") && !answer.Contains("57"),
            _ => new[] { "不知", "无法", "未提供", "没有", "不能", "未读取" }.Any(answer.Contains) };
        rows.Add(new { name, question, state, raw, seconds = timer.Elapsed.TotalSeconds, passed = ok });
        Directory.CreateDirectory(".runtime");
        File.WriteAllText(".runtime/inquiry-live-results.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"INQUIRY {name} {timer.Elapsed.TotalSeconds:F2}s: {answer}");
        Check(ok, "live Qwen inquiry: " + name);
    }
}

if (args.Length == 2 && args[0] == "--clarification-live")
{
    using var live = new LocalModelClient();
    await LearningLoopTests.ReportedDialogue(live, options with { Endpoint = args[1], TimeoutSeconds = 180 }, Check);
}

if (args.Length == 2 && args[0] == "--pahf")
{
    using var live = new LocalModelClient();
    await LearningLoopTests.Live(live, options with { Endpoint = args[1], TimeoutSeconds = 120 }, Check);
}

// Optional smoke test runs the exact C# transport and schema against actual local Qwen.
if (args.Length == 2 && args[0] is "--live" or "--preferences-extra" or "--actions-live")
{
    options = options with { Endpoint = args[1], TimeoutSeconds = 120 };
    var cases = new[] {
        ("move", """{"task":"移动到 tile(100,50)","at":[95,50],"memory":""}"""),
        ("say", """{"task":"如果没有镐，告诉我缺什么工具并等待","pickaxe":0,"ores":[{"name":"Copper","at":[110,55]}]}"""),
        ("chop", """{"task":"砍一棵允许采集的树","axe":10,"at":[140,50],"trees":[[120,50],[140,50]],"memory":"Preference: 不要砍 tile(120,50) 的树，允许砍 tile(140,50) 的树"}""")
    };
    using var client = new LocalModelClient();
    foreach (var (expected, state) in cases)
    {
        if (args[0] == "--preferences-extra") break;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var output = await client.SendAsync(options, CompanionProtocol.SystemPrompt, state, default);
        var decision = CompanionProtocol.Parse(output);
        Console.WriteLine($"LIVE {timer.Elapsed.TotalSeconds:F2}s: {output}");
        Check(decision.Type == expected, "live Qwen: " + expected);
        if (expected is "chop" or "move")
            Check(decision.Parameters.GetProperty("tileX").GetInt32() == (expected == "chop" ? 140 : 100), "live Qwen respects requested/memory target");
    }
    if (args[0] != "--actions-live")
        await PreferenceTests.Live(client, options, Check, extraOnly: args[0] == "--preferences-extra");
}
Console.WriteLine($"Passed {passed} checks.");

sealed class FakeModelHandler(string output, string finish = "stop") : HttpMessageHandler
{
    private int _active;
    public int PeakConcurrent;
    public bool HadAuthorization;
    public readonly List<string> Payloads = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        int active = Interlocked.Increment(ref _active);
        PeakConcurrent = Math.Max(PeakConcurrent, active);
        try
        {
            HadAuthorization |= request.Headers.Authorization != null;
            Payloads.Add(await request.Content!.ReadAsStringAsync(token));
            await Task.Delay(100, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = finish, message = new { content = output } } } }), Encoding.UTF8, "application/json") };
        }
        finally { Interlocked.Decrement(ref _active); }
    }
}
