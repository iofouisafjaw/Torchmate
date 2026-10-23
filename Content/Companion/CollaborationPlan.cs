using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public enum CollaborationOwner { Companion, Player }
public enum CollaborationState { Pending, Preview, Running, Verified, Failed }
public sealed record CollaborationStep(string Id, string Name, string Kind,
    CollaborationOwner Owner = CollaborationOwner.Companion, CollaborationState State = CollaborationState.Pending,
    string TaskId = "", string Evidence = "");
public sealed record CollaborationEvent(int Revision, string Kind, string Step, string Detail);

// A task ledger, not an executor. Only native adapters can provide verification evidence.
// Plan revisions revoke child bindings; old callbacks cannot complete a changed assignment.
public sealed class CollaborationPlan
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Scenario { get; }
    public int Rooms { get; private set; }
    public int Revision { get; private set; } = 1;
    public bool Paused { get; private set; }
    private readonly List<CollaborationStep> _steps;
    private readonly List<CollaborationEvent> _journal = new();
    public IReadOnlyList<CollaborationStep> Steps => _steps.AsReadOnly();
    public IReadOnlyList<CollaborationEvent> Journal => _journal.AsReadOnly();
    public int EventCount { get; private set; }
    public CollaborationStep? Next => _steps.FirstOrDefault(s => s.State != CollaborationState.Verified);
    public bool Complete => _steps.All(s => s.State == CollaborationState.Verified);
    public CollaborationPlan(string scenario, int rooms = 1) : this(scenario, CollaborationTemplates.Steps(scenario), rooms) { }
    public CollaborationPlan(string scenario, IReadOnlyList<CollaborationStep> steps, int rooms = 1)
    {
        if (string.IsNullOrWhiteSpace(scenario) || scenario.Length > 80 || rooms is < 1 or > 3 || steps.Count is < 1 or > 16 ||
            steps.Select(s => s.Id).Distinct().Count() != steps.Count || steps.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 40 ||
                string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Kind) || !Enum.IsDefined(s.Owner) ||
                s.State != CollaborationState.Pending || s.TaskId.Length > 0 || s.Evidence.Length > 0))
            throw new ArgumentException("协作模板必须包含1–16项唯一、未执行的工作，住房数量限1–3间。");
        Scenario = scenario; Rooms = rooms;
        _steps = steps.ToList();
        Record("created", "", scenario);
    }
    public void Assign(string id, CollaborationOwner owner)
    {
        int i = _steps.FindIndex(s => s.Id == id);
        if (i < 0) throw new ArgumentException("计划里没有这项工作。");
        if (_steps[i].State == CollaborationState.Verified) throw new ArgumentException("该项已经核验完成，不再重复施工。");
        Revoke(); _steps[i] = _steps[i] with { Owner = owner };
        Record("assigned", id, owner.ToString());
    }
    public void Resize(int rooms)
    {
        if (Scenario != "housing" || rooms is < 1 or > 3) throw new ArgumentException("住房数量支持1–3间。");
        // Once a site was selected, resizing could overlap or duplicate already built rooms.
        // Require a new explicit project rather than silently moving/expanding that site.
        if (_steps.Any(s => s.Id == "housing" && (s.TaskId.Length > 0 || s.Evidence.Length > 0)))
            throw new ArgumentException("住房已选址或施工；请保留原项目，另开协作扩建任务选择新位置。");
        Revoke(); Rooms = rooms;
        for (int i = 0; i < _steps.Count; i++) _steps[i] = _steps[i] with { State = CollaborationState.Pending, TaskId = "", Evidence = "" };
        Record("resized", "", rooms.ToString());
    }
    public void Pause(string reason) { Revoke(); Paused = true; Record("paused", "", reason); }
    public void Resume() { Paused = false; Record("resumed", "", "只生成新预览，仍需确认"); }
    public void RecheckFailed(string id, string evidence)
    {
        int i = _steps.FindIndex(s => s.Id == id);
        if (i < 0) return;
        Revoke(); _steps[i] = _steps[i] with { State = CollaborationState.Failed, Evidence = evidence, TaskId = "" };
        Record("recheck_failed", id, evidence);
    }
    private void Revoke()
    {
        Revision++;
        for (int i = 0; i < _steps.Count; i++)
            if (_steps[i].State is CollaborationState.Preview or CollaborationState.Running)
                _steps[i] = _steps[i] with { State = CollaborationState.Pending, TaskId = "" };
    }
    public bool Bind(string step, string task, int revision)
    {
        var next = Next;
        if (Paused || revision != Revision || next?.Id != step || string.IsNullOrEmpty(task)) return false;
        int i = _steps.IndexOf(next);
        _steps[i] = next with { TaskId = task, State = CollaborationState.Preview };
        Record("bound", step, task); return true;
    }
    public bool Observe(string task, int revision, CollaborationState state, string evidence)
    {
        int i = _steps.FindIndex(s => s.TaskId == task && task.Length > 0);
        if (Paused || revision != Revision || i < 0 || _steps[i].State == CollaborationState.Verified) return false;
        if (state == CollaborationState.Verified && string.IsNullOrWhiteSpace(evidence)) return false;
        if (_steps[i].State == state && _steps[i].Evidence == evidence) return false;
        _steps[i] = _steps[i] with { State = state, Evidence = evidence };
        Record(state.ToString(), _steps[i].Id, evidence); return true;
    }
    private void Record(string kind, string step, string detail)
    {
        _journal.Add(new(Revision, kind, step, detail));
        EventCount++;
        if (_journal.Count > 128) _journal.RemoveAt(0);
    }
    public string Describe() => $"协作{(Scenario == "eye" ? "克眼准备" : Scenario == "housing" ? $"扩建{Rooms}间NPC住房" : Scenario)} · 修订{Revision} · " +
        (Complete ? "各项已核验" : Paused ? "已暂停" : "进行中") + "\n" + string.Join("\n", _steps.Select(s =>
            $"{s.Name}：{(s.Owner == CollaborationOwner.Player ? "你负责" : "同伴负责")} · {StateText(s.State)} · {s.Evidence}")) +
        "\n分工和继续不等于批准消耗；每份新预览仍需确认。";
    private static string StateText(CollaborationState s) => s switch { CollaborationState.Pending => "待开始", CollaborationState.Preview => "待确认／玩家处理中",
        CollaborationState.Running => "执行中", CollaborationState.Verified => "已核验", _ => "未完成，待恢复" };
}

// Scenario templates contain no execution policy. A future adapter can supply its own ordered
// steps to the same ledger without changing assignment, revision, progress or recovery rules.
public static class CollaborationTemplates
{
    public static IReadOnlyList<CollaborationStep> Steps(string scenario) => scenario switch
    {
        "eye" => new CollaborationStep[] { new("arena", "竞技场", "arena"), new("potions", "药水", "supplies") },
        "housing" => new CollaborationStep[] { new("materials", "住房备料", "supplies"), new("housing", "住房施工", "housing") },
        _ => throw new ArgumentException("该场景尚未注册协作模板。")
    };
}

public sealed record CollaborationCommand(string Operation, string Target = "", int Rooms = 1,
    CollaborationOwner Owner = CollaborationOwner.Companion);
public static class CollaborationRequest
{
    public static CollaborationCommand? Parse(string text, bool active)
    {
        string s = text.Trim().TrimEnd('。', '！', '!', '？', '?').Replace(" ", "");
        if (s.Length > 240) return null;
        if (active)
        {
            if (s is "协作进度" or "协作计划" or "分工情况" or "现在做到哪了") return new("status");
            if (s is "暂停协作" or "停止协作" or "取消协作" or "先停一下") return new("pause");
            if (s is "继续协作" or "恢复协作") return new("continue");
            if (s is "核验协作" or "我这部分做完了" or "我做完了") return new("audit");
            var assign = Regex.Match(s, "^(药水|竞技场|住房备料|备料|住房施工|住房|施工)(?:改为|改成|交给)?(我来|我负责|我自己做|你来|你负责|同伴负责|你自己做)$");
            if (!assign.Success) assign = Regex.Match(s, "^(我来|我负责|你来|你负责)(药水|竞技场|住房备料|备料|住房施工|住房|施工)$");
            if (assign.Success)
            {
                string a = assign.Groups[1].Value, b = assign.Groups[2].Value;
                string target = Regex.IsMatch(a, "^(我|你|同伴)") ? b : a;
                string who = target == a ? b : a;
                return new("assign", target, Owner: who.StartsWith("我") ? CollaborationOwner.Player : CollaborationOwner.Companion);
            }
            var resize = Regex.Match(s, "^(?:住房|房间|小屋)?(?:改成|改为)([一二两三123])间(?:住房|房间|小屋)?$");
            if (resize.Success) return new("resize", Rooms: Number(resize.Groups[1].Value));
        }
        if (!Regex.IsMatch(s, "一起|协作|分工|扩建")) return null;
        if (Regex.IsMatch(s, "如何|怎么|能不能|能否|以后|如果") || text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('？')) return null;
        if (Regex.IsMatch(s, "不要|不用|取消|停止|别|不想")) return new("clarify");
        // Ambiguous mixed goals and unsupported constraints must be discussed, never silently discarded.
        bool eye = Regex.IsMatch(s, "克眼|克苏鲁之眼"), house = Regex.IsMatch(s, "住房|房间|小屋|村庄");
        if (eye && house || Regex.IsMatch(s, "省|不要拆|别拆|拆|不做|不用做|仅|只做|层|药水.*我|我.*药水")) return new("clarify");
        if (eye && Regex.IsMatch(s, "准备|备战|竞技场")) return new("start", "eye");
        if (house && Regex.IsMatch(s, "建|盖|扩"))
        {
            var count = Regex.Match(s, "([零一二两三四五六七八九十百千万0-9]+)间");
            if (count.Success && !Regex.IsMatch(count.Groups[1].Value, "^[一二两三123]$") || !count.Success && Regex.IsMatch(s,"[0-9零一二两三四五六七八九十百千万]")) return new("clarify");
            return new("start", "housing", count.Success ? Number(count.Groups[1].Value) : 1);
        }
        return null;
    }
    private static int Number(string s) => s switch { "二" or "两" or "2" => 2, "三" or "3" => 3, _ => 1 };
}
