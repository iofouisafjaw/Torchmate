using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerrarAI.Content.Companion;
using TerrarAI.Evals;

// Offline agent evaluation: each case is a player message + a small world; the real local model plays it
// through the mod's own AgentLoop, and the run is scored against what the case expects.
//
//   dotnet run --project evals/AgentEval.csproj -- [--runs 3] [--only 关键词] [--endpoint URL] [--no-kb]
if(args.Contains("--recovery-bench"))return await RecoveryBenchmark.Run(args);
if(args.Contains("--paired-recovery-bench"))return await PairedRecoveryBenchmark.Run(args);
var opts = Options.Parse(args);
Console.OutputEncoding = Encoding.UTF8;
string root = AppContext.BaseDirectory;
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
var cases = JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(Path.Combine(root, "cases.json")), json)!
    .Where(c => opts.Only == null || c.Id.Contains(opts.Only) || c.Say.Contains(opts.Only)).ToList();
if(opts.Validate)
{
    if(cases.Any(c=>string.IsNullOrWhiteSpace(c.Id)||string.IsNullOrWhiteSpace(c.Say))||cases.Select(c=>c.Id).Distinct().Count()!=cases.Count)
        throw new FormatException("评测题缺少目标或ID重复。");
    foreach(var c in cases.Where(c=>c.Recovery!=null))
        if(AgentProtocol.IsSuccess(c.Recovery!.Describe()))throw new FormatException("失败收据不能标为成功："+c.Id);
    Console.WriteLine($"Validated {cases.Count} cases ({cases.Count(c=>c.Recovery!=null)} failure receipts); model runs: 0.");
    return 0;
}

using var client = new LocalModelClient();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var model = new ModelOptions(opts.Endpoint, "qwen3-4b", "", 0.2f, 900, 120);   // same settings as the mod's agent step
if (!await Reachable(http, opts.Endpoint))
{
    Console.WriteLine($"连不上模型：{opts.Endpoint}。先启动模型（开始游玩.bat 或 python scripts/start_qwen.py）。");
    return 2;
}

var results = new List<CaseResult>();
var clock = Stopwatch.StartNew();
foreach (var c in cases)
{
    var runs = new List<RunResult>();
    for (int i = 0; i < opts.Runs; i++)
    {
        var run = await Play(c);
        run.Problems = c.Expect.Check(run);
        if (run.Problems.Count == 0 && c.Expect.KbHas.Count > 0 && run.End == "forward" && opts.Kb)
            run.Problems.AddRange(await CheckKb(c, run));
        runs.Add(run);
        Console.Write(run.Problems.Count == 0 ? "." : "x");
    }
    var cr = new CaseResult(c, runs);
    results.Add(cr);
    Console.WriteLine($" {cr.Passed}/{runs.Count}  {c.Id}  「{c.Say}」");
}

int passed = results.Sum(r => r.Passed), total = results.Sum(r => r.Runs.Count);
Console.WriteLine($"\n通过 {passed}/{total}（{100.0 * passed / Math.Max(1, total):F0}%），用时 {clock.Elapsed.TotalMinutes:F1} 分钟");
string outDir = Path.GetFullPath(Path.Combine(root, "../../../"));
if (!File.Exists(Path.Combine(outDir, "cases.json"))) outDir = Directory.GetCurrentDirectory();
string report = Report.Write(outDir, results, opts, clock.Elapsed);
Console.WriteLine("报告：" + report);
return passed == total ? 0 : 1;

async Task<RunResult> Play(EvalCase c)
{
    var world = new FakeWorld(c.World);
    var loop = new AgentLoop(c.Say, c.Steps.Select(x => new AgentStep(x.Call, x.Result, x.Tool)));
    TaskRecoverySession? recovery=null;
    if(c.Recovery!=null)
    {
        recovery=new(Guid.NewGuid(),0,0,c.Say);recovery.Bind(c.Recovery.TaskId,new(recovery.Session,0));
        recovery.Fail(c.Recovery,recovery.Session,0,0,0,true,DateTimeOffset.UtcNow);loop.Recover(c.Recovery);
    }
    var run = new RunResult();
    var sw = Stopwatch.StartNew();
    for (int turn = 0; turn < 25; turn++)
    {
        if(recovery!=null&&!recovery.Request(recovery.Session,0,0,0,true,DateTimeOffset.UtcNow))
        {Finish(run,"stop","恢复期限或请求预算已用尽，未执行后续操作。");break;}
        if (loop.BeforeRequest() is AgentDecision stop) { Finish(run, "stop", stop.Text); break; }
        string prompt = loop.Prompt(world.Snapshot(), c.Dialogue);
        string? raw = null, failure = null;
        try { raw = await client.SendStructuredAsync(model, AgentProtocol.SystemPrompt, prompt, loop.Schema, "companion_agent_step", CancellationToken.None); }
        catch (FormatException ex) { run.Trace.Add("  (模型输出不完整：" + ex.Message + ")"); }
        catch (Exception ex) { failure = ex.Message; }
        run.ModelCalls++;
        if (raw != null)
        {
            try { AgentProtocol.Parse(raw); }
            catch (Exception ex) { run.Trace.Add($"  (输出没法用：{ex.Message} 原文：{raw[..Math.Min(160, raw.Length)]})"); }
        }
        var d = loop.OnModel(raw, failure);
        if (d.Call != null) run.Calls.Add(d.Call);
        switch (d.Kind)
        {
            case AgentOutcome.Ask:
                if (d.Call != null) run.Trace.Add($"· {d.Call.Label}（重复，没执行）");
                continue;
            case AgentOutcome.Finish:
                Finish(run, d.Call?.Tool == "reply" ? "reply" : "stop", d.Text);
                break;
            case AgentOutcome.Question: Finish(run, "ask", d.Text); break;
            case AgentOutcome.Forward: Finish(run, "forward", d.Text); break;
            default:
                var call = d.Call!;
                if(call.Tool=="replan")
                {
                    bool allowed=recovery?.Replan(recovery.Session,0,0,0,true,DateTimeOffset.UtcNow)==true;
                    Finish(run,allowed?"replan":"stop",allowed?world.Run(call,loop):"原失败任务不能重规划，未执行；请重新明确下令。");break;
                }
                if (call.Tool == "garden") { Finish(run, "garden", "已提交种植／采收预览，等待玩家确认；离线环境不模拟草药生长或真实掉落。"); break; }
                if (call.Tool == "prepare_eye") { Finish(run, "prepare_eye", "已提交克眼准备预览，等待玩家确认；离线环境不模拟地形、家具增益或药水合成。"); break; }
                if (call.Tool == "cheat_world") { Finish(run, "cheat_world", world.Run(call,loop)); break; }
                if (call.Tool == "expedition") { Finish(run, "expedition", "离线环境没有真实地形，未执行挖掘、移动或地图发现。"); break; }
                if (call.Tool == "locate") { Finish(run, "locate", "离线环境没有真实地图，未创建标记。"); break; }
                if (call.Tool is "build" or "platform" or "craft_chain" or "combat") { Finish(run, call.Tool, call.Tool == "craft_chain" ? "已提交合成链预览，等待玩家确认。" : call.Tool == "combat" ? "已提交战斗指令，离线环境不模拟击杀。" : ""); break; }
                string result = world.Run(call, loop);
                // The same follow-ups the mod adds: does the waiting craft go now?
                if (call.Tool is not "craft" && !AgentProtocol.IsLookup(call.Tool) && (AgentProtocol.IsSuccess(result) || call.Tool is "chop" or "mine")
                    && AgentProtocol.PendingCraft(loop.Steps) is var (want, n))
                    result += "\n" + world.Progress(want, n);
                loop.Record(call, result);
                run.Trace.Add($"· {call.Label} → {result.Replace("\n", " ")}");
                if (loop.AfterRun(call, result) is AgentDecision done) { Finish(run, "reply", done.Text); break; }
                continue;
        }
        break;
    }
    run.Seconds = sw.Elapsed.TotalSeconds;
    run.Bag = new(world.Bag);
    run.Near = world.Near.ToList();
    run.Placed = world.Placed.ToList();
    run.Spawned = world.Spawned.ToList();
    return run;
}

static void Finish(RunResult run, string end, string text) { run.End = end; run.Text = text; run.Trace.Add($"⇒ [{end}] {text}"); }

async Task<List<string>> CheckKb(EvalCase c, RunResult run)
{
    // Ask the knowledge base (the brain in front of the model) the forwarded question with a plain early-game state.
    try
    {
        var body = JsonSerializer.Serialize(new { question = run.Text, state = KbState.For(c), version = "1.4.4.9" });
        string url = opts.Endpoint.Replace("/v1/chat/completions", "/terrarai/advise");
        using var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new() { $"资料库出错 {(int)resp.StatusCode}：{text[..Math.Min(120, text.Length)]}" };
        string reply = JsonDocument.Parse(text).RootElement.GetProperty("reply").GetString() ?? "";
        run.KbReply = reply;
        run.Trace.Add("  资料库：" + reply.Replace("\n", " "));
        return c.Expect.KbHas.Any(k => reply.Contains(k)) ? new() : new() { "资料库的回答没提到：" + string.Join(" / ", c.Expect.KbHas) };
    }
    catch (Exception ex) { return new() { "资料库请求失败：" + ex.Message }; }
}

static async Task<bool> Reachable(HttpClient http, string endpoint)
{
    try
    {
        using var r = await http.GetAsync(endpoint.Replace("/v1/chat/completions", "/health"));
        return r.IsSuccessStatusCode;
    }
    catch { return false; }
}

// ---------------------------------------------------------------------------------------------------
sealed record Options(int Runs, string? Only, string Endpoint, bool Kb, bool Validate)
{
    public static Options Parse(string[] a)
    {
        int runs = 3; string? only = null; string ep = "http://127.0.0.1:8080/v1/chat/completions"; bool kb = true,validate=false;
        for (int i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--runs": runs = int.Parse(a[++i]); break;
                case "--only": only = a[++i]; break;
                case "--endpoint": ep = a[++i]; break;
                case "--no-kb": kb = false; break;
                case "--validate": validate = true; break;
            }
        return new(Math.Max(1, runs), only, ep, kb,validate);
    }
}

public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Say { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Dialogue { get; set; } = new();
    public WorldSpec World { get; set; } = new();
    public Expectation Expect { get; set; } = new();
    public List<Dictionary<string, object>>? Checklist { get; set; }   // Boss Checklist rows for the knowledge-base check
    // Steps already taken in this goal (e.g. the agent asked and the player answered): the goal continues from here.
    public List<SeedStep> Steps { get; set; } = new();
    public TaskFailureReceipt? Recovery { get; set; }
}

public sealed class SeedStep
{
    public string Call { get; set; } = "";
    public string Result { get; set; } = "";
    public string Tool { get; set; } = "";
}

public sealed class RunResult
{
    public List<AgentCall> Calls { get; } = new();
    public List<string> Trace { get; } = new();
    public string End { get; set; } = "none";
    public string Text { get; set; } = "";
    public string KbReply { get; set; } = "";
    public int ModelCalls { get; set; }
    public double Seconds { get; set; }
    public Dictionary<string, int> Bag { get; set; } = new();
    public List<string> Near { get; set; } = new();
    public List<string> Placed { get; set; } = new();
    public List<string> Spawned { get; set; } = new();
    public List<string> Problems { get; set; } = new();
}

public sealed record CaseResult(EvalCase Case, List<RunResult> Runs)
{
    public int Passed => Runs.Count(r => r.Problems.Count == 0);
}

// Everything optional; a case passes when every listed expectation holds.
public sealed class Expectation
{
    public List<string> End { get; set; } = new();          // reply / forward / ask / build / platform / craft_chain (preview) / stop
    public List<string> Calls { get; set; } = new();        // "tool" or "tool:物品" that must happen
    public List<string> NoCalls { get; set; } = new();      // ... that must not
    public string? First { get; set; }                      // the first tool the model picks
    public List<string> TextHas { get; set; } = new();      // final text mentions at least one of these
    public List<string> TextLacks { get; set; } = new();    // ... and none of these
    public int MaxModelCalls { get; set; }
    public Dictionary<string, int> BagHas { get; set; } = new();
    public List<string> Near { get; set; } = new();
    public bool SpawnsReal { get; set; }
    public List<string> KbHas { get; set; } = new();        // knowledge-base answer mentions one of these

    private static bool Match(AgentCall c, string pattern)
    {
        var parts = pattern.Split(':', 2);
        if (c.Tool != parts[0]) return false;
        if (parts.Length == 1) return true;
        string arg = c.Item.Length > 0 ? c.Item : c.Ore.Length > 0 ? c.Ore : c.Mode.Length > 0 ? c.Mode : c.Text;
        return arg.Contains(parts[1]);
    }

    public List<string> Check(RunResult r)
    {
        var p = new List<string>();
        if (End.Count > 0 && !End.Contains(r.End)) p.Add($"结束方式是 {r.End}，期望 {string.Join("/", End)}");
        foreach (var c in Calls) if (!r.Calls.Any(x => Match(x, c))) p.Add("没有调用 " + c);
        foreach (var c in NoCalls) if (r.Calls.Any(x => Match(x, c))) p.Add("不该调用 " + c);
        if (First != null && (r.Calls.Count == 0 || r.Calls[0].Tool != First)) p.Add($"第一步是 {(r.Calls.Count > 0 ? r.Calls[0].Tool : "无")}，期望 {First}");
        if (TextHas.Count > 0 && !TextHas.Any(t => r.Text.Contains(t))) p.Add("回复里没有：" + string.Join(" / ", TextHas));
        foreach (var t in TextLacks) if (r.Text.Contains(t)) p.Add("回复里不该有：" + t);
        if (MaxModelCalls > 0 && r.ModelCalls > MaxModelCalls) p.Add($"用了 {r.ModelCalls} 次模型，上限 {MaxModelCalls}");
        foreach (var (item, n) in BagHas) if (r.Bag.GetValueOrDefault(item) + r.Placed.Count(x => x == item) < n) p.Add($"背包里没有 {item} ×{n}");
        foreach (var s in Near) if (!r.Near.Contains(s)) p.Add("身边没放 " + s);
        if (SpawnsReal && r.Spawned.Count == 0) p.Add("没生成出任何真实物品");
        return p;
    }
}

static class KbState
{
    // A plain early-game snapshot in the shape the mod sends the brain.
    public static object For(EvalCase c) => new
    {
        world = new { id = "eval", name = "测试世界", difficulty = "经典", hardmode = false, evil = "crimson", day = true, clock = "10:00" },
        progress = new { bosses = Array.Empty<string>(), townNpcs = new[] { "向导" }, checklist = c.Checklist ?? new() },
        player = new
        {
            life = 100, lifeMax = 100, mana = 20, manaMax = 20, defense = 0, pick = 35, axe = 35, pickName = "铜镐", axeName = "铜斧",
            hook = false, wings = false, boots = false,
            weapons = new[] { new { name = "铜短剑", damage = 5, @class = "melee", useTime = 8 } },
            armor = Array.Empty<string>(), accessories = Array.Empty<string>(), healing = new { }, coins = 7100,
            inventory = c.World.Bag.Select(p => new { key = "", name = p.Key, count = p.Value }).ToArray()
        }
    };
}

static class Report
{
    public static string Write(string dir, List<CaseResult> results, Options opts, TimeSpan took)
    {
        int passed = results.Sum(r => r.Passed), total = results.Sum(r => r.Runs.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"# 同伴测试报告 {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"\n通过 **{passed}/{total}**（{100.0 * passed / Math.Max(1, total):F0}%），每题跑 {opts.Runs} 次，用时 {took.TotalMinutes:F1} 分钟。\n");
        sb.AppendLine("| 题目 | 通过 | 平均模型调用 | 平均秒数 |\n|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Case.Id}「{r.Case.Say}」 | {r.Passed}/{r.Runs.Count} | {r.Runs.Average(x => x.ModelCalls):F1} | {r.Runs.Average(x => x.Seconds):F1} |");
        sb.AppendLine("\n## 没通过的（每题列一次失败过程）\n");
        foreach (var r in results.Where(r => r.Passed < r.Runs.Count))
        {
            var bad = r.Runs.First(x => x.Problems.Count > 0);
            sb.AppendLine($"### {r.Case.Id}「{r.Case.Say}」 {r.Passed}/{r.Runs.Count}");
            if (r.Case.Note.Length > 0) sb.AppendLine("> " + r.Case.Note);
            sb.AppendLine("问题：" + string.Join("；", bad.Problems));
            sb.AppendLine("```");
            foreach (var t in bad.Trace) sb.AppendLine(t);
            sb.AppendLine("```");
        }
        string path = Path.Combine(dir, "report.md");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        string history = Path.Combine(dir, "history.csv");
        if (!File.Exists(history)) File.WriteAllText(history, "time,passed,total,runs,minutes,only\n", new UTF8Encoding(false));
        File.AppendAllText(history, $"{DateTime.Now:yyyy-MM-dd HH:mm},{passed},{total},{opts.Runs},{took.TotalMinutes:F1},{opts.Only}\n");
        return path;
    }
}
