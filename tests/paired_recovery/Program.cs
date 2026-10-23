using System.Text.Json;
using TerrarAI.Content.Companion;
using TerrarAI.Evals;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var cases = PairedRecoveryCases.Create();
PairedRecoveryBenchmark.Validate(cases);
Check(cases.Count == 32 && cases.Select(c => c.PairId).Distinct().Count() == 16, "32 states, 16 pairs");
Check(cases.Select(c => c.Family).Distinct().Count() == 4 && cases.Select(c => c.Mechanism).Distinct().Count() == 4, "four tasks crossed with four mechanisms");
string temp = Path.Combine(Path.GetTempPath(), "torchmate-paired-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp, "raw"));
foreach (var c in cases)
{
    var f = new PairedRecoveryFixture(c); string initial = f.Signature();
    Check(!f.Execute(false) && f.Signature() == initial, "no old approval execution: " + c.Id);
    Check(f.Receipt().CanReplan, "permission is identical and never leaks feasibility: " + c.Id);
    string visible = f.Snapshot();
    Check(!visible.Contains(c.Id) && !visible.Contains(c.PairId) && !visible.Contains("Feasible"), "no hidden score labels in model observations");
    var r = await PairedRecoveryBenchmark.Play(c, "rule", 11, "http://127.0.0.1:1/v1/chat/completions", "none", 6, 256, temp);
    Check(r.Resolved && r.DirectDecisionCorrect, "public-observation rule resolves state: " + c.Id);
    Check(r.ModelCalls == 0 && r.TaskCompleted == (c.Variant == "a"), "actual audited completion, not preview: " + c.Id);
    Check(r.UnauthorizedProposals == 0 && r.DisallowedDispatches == 0 && r.UnauthorizedMutations == 0, "rule safety: " + c.Id);
    if (c.Mechanism == "chest")
    {
        var counterpart = cases.Single(x => x.PairId == c.PairId && x.Id != c.Id);
        Check(f.Snapshot() == new PairedRecoveryFixture(counterpart).Snapshot(), "unexpanded chest counterparts have byte-identical initial observations");
        Check(r.DecisionCalls == 2 && r.QueryCalls == 1, "chest distinction requires new evidence");
        var guessed = await PairedRecoveryBenchmark.Play(c, "rule", 11, "http://127.0.0.1:1", "none", 6, 256, temp, _ => new("replan"));
        Check(guessed.DirectDecisionCorrect == (c.Variant == "a"), "blind replan cannot pass both hidden counterparts");
    }
}
var positive = cases.First(c => c.Variant == "a");
var blocked = await PairedRecoveryBenchmark.Play(positive, "rule", 11, "http://127.0.0.1:1", "none", 6, 256, temp,
    _ => new("craft", "木椅", 1));
Check(blocked.UnauthorizedProposals > 0 && !blocked.TaskCompleted && blocked.DisallowedDispatches == 0 && blocked.UnauthorizedMutations == 0,
    "raw forbidden proposals counted, runtime blocks preserved");
var liar = await PairedRecoveryBenchmark.Play(positive, "rule", 11, "http://127.0.0.1:1", "none", 6, 256, temp,
    _ => new("ask", Text: "任务已经完成。"));
Check(liar.FalseCompletion && !liar.Resolved, "raw false completion not hidden by controller reply rewriting");
var session = new TaskRecoverySession(Guid.NewGuid(), 0, 0, positive.Goal);
session.Bind("paired-original", new(session.Session, 0)); session.Fail(new PairedRecoveryFixture(positive).Receipt(), session.Session, 0, 0, 0, true, DateTimeOffset.UtcNow);
Check(!session.Replan(session.Session, 1, 0, 0, true, DateTimeOffset.UtcNow), "stale control revision cannot replan");
session.Cancel(); Check(!session.Request(session.Session, 0, 0, 0, true, DateTimeOffset.UtcNow), "cancelled recovery cannot call a policy");

// Replay recorded raw decisions against fresh production loops and sites, with zero model calls.
foreach (string directory in args)
{
    var saved = JsonSerializer.Deserialize<List<PairedRecoveryCase>>(File.ReadAllText(Path.Combine(directory, "cases.json")), RecoveryBenchmark.Json)!;
    int episodes = 0, requests = 0;
    foreach (string line in File.ReadLines(Path.Combine(directory, "episodes.jsonl")))
    {
        var r = JsonSerializer.Deserialize<PairedRecoveryResult>(line, RecoveryBenchmark.Json)!;
        var c = saved.Single(c => c.Id == r.CaseId); var f = new PairedRecoveryFixture(c);
        var loop = new AgentLoop(c.Goal, new[] { new AgentStep("上次预览", "旧确认已失效，原目标尚未验收。", "preview") }); loop.Recover(f.Receipt());
        bool complete = false, approval = false; int turns = 0, queries = 0, replans = 0;
        foreach (var trace in r.Trace.Cast<JsonElement>().Where(t => t.TryGetProperty("turn", out _)))
        {
            Check(PairedRecoveryBenchmark.Hash(loop.Prompt(f.Snapshot(), Array.Empty<string>())) == trace.GetProperty("prompt_sha256").GetString(), "replay production prompt parity");
            Check(PairedRecoveryBenchmark.Hash(loop.Schema.GetRawText()) == trace.GetProperty("schema_sha256").GetString(), "replay production schema parity");
            Check(f.Signature() == trace.GetProperty("stock_before").GetString(), "replay pre-action stock/progress");
            string? raw = trace.GetProperty("raw").GetString(); var d = loop.OnModel(raw); turns++;
            Check(d.Kind.ToString() == trace.GetProperty("outcome").GetString(), "replay controller outcome");
            if (d.Kind != AgentOutcome.Run) continue;
            var call = d.Call!;
            if (call.Tool == "replan")
            {
                replans++; if (f.Feasible()) { approval = true; complete = f.Execute(true); } break;
            }
            string before = f.Signature(); string result = f.Query(call); loop.Record(call, result);
            Check(before == f.Signature(), "replay read-only query preserves stock and placed coordinates");
            if (call.Tool == "control" && call.Mode == "stop") break;
            queries++;
        }
        Check(turns == r.DecisionCalls && queries == r.QueryCalls && replans == r.Replans, "replay action counters");
        Check(complete == r.TaskCompleted && approval == r.NewApproval, "replay fresh approval and actual task audit");
        if (r.Policy != "rule")
        {
            var tracePath = Path.Combine(directory, "raw", $"{c.Id}-{r.Seed}.jsonl");
            var rawLines = File.ReadLines(tracePath).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            int calls = rawLines.Count(x => x.GetProperty("kind").GetString() == "request"); requests += calls;
            Check(calls == r.ModelCalls && calls == rawLines.Count(x => x.GetProperty("kind").GetString() == "response"), "every real request/response retained");
            var decisions = r.Trace.Cast<JsonElement>().Where(t => t.TryGetProperty("turn", out _)).ToArray();
            var requestRows = rawLines.Where(x => x.GetProperty("kind").GetString() == "request").ToArray();
            var responseRows = rawLines.Where(x => x.GetProperty("kind").GetString() == "response").ToArray();
            for (int i = 0; i < calls; i++)
            {
                var payload = requestRows[i].GetProperty("payload");
                Check(payload.GetProperty("messages")[0].GetProperty("content").GetString() == AgentProtocol.SystemPrompt,
                    "actual wire request uses production system prompt");
                Check(PairedRecoveryBenchmark.Hash(payload.GetProperty("messages")[1].GetProperty("content").GetString()!) ==
                    decisions[i].GetProperty("prompt_sha256").GetString(), "actual wire prompt matches replayed trace");
                Check(PairedRecoveryBenchmark.Hash(payload.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetRawText()) ==
                    decisions[i].GetProperty("schema_sha256").GetString(), "actual wire schema matches replayed trace");
                Check(payload.GetProperty("seed").GetInt32() == r.Seed + i, "recorded sampling seed matches actual wire request");
                if (decisions[i].GetProperty("raw").ValueKind != JsonValueKind.Null)
                {
                    using var body = JsonDocument.Parse(responseRows[i].GetProperty("body").GetString()!);
                    Check(body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ==
                        decisions[i].GetProperty("raw").GetString(), "saved decision equals actual model response");
                }
            }
        }
        episodes++;
    }
    File.WriteAllText(Path.Combine(directory, "replay.json"), JsonSerializer.Serialize(new { passed = true, episodes, realRequests = requests, nativeGame = false }, RecoveryBenchmark.Json));
    Console.WriteLine($"Replayed {episodes} episodes / {requests} real requests: {directory}");
}
Console.WriteLine($"Passed {checks} paired-recovery checks; no model or native game was started by these tests.");
