using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

public sealed class WorldMemory
{
    public int Version { get; set; } = 1;
    public string WorldId { get; set; } = "";
    public List<string> Notes { get; set; } = new();
    public List<string> RecentEvents { get; set; } = new();
    public List<LearnedPreference> Preferences { get; set; } = new();
    public List<DialogueLine> Dialogue { get; set; } = new();
    public long RuleRevision { get; set; }
    public TaskReference? LastTask { get; set; }
}

public sealed record LearnedPreference(string Key, string Value, string Evidence, string Source,
    string TurnId, DateTimeOffset UpdatedAt, string When = "any");
public sealed record DialogueLine(string Role, string Text);

public sealed class CompanionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string DirectoryPath { get; }
    public string SessionId { get; }
    public WorldMemory Memory { get; }
    public bool RecoveredFromBackup { get; }
    private string MemoryPath => Path.Combine(DirectoryPath, "memory.json");
    private readonly object _gate = new();

    public CompanionStore(string root, Guid worldId, Guid? session = null)
    {
        if (worldId == Guid.Empty) throw new ArgumentException("World must have a stable UUID.");
        SessionId = (session ?? Guid.NewGuid()).ToString("N");
        DirectoryPath = Path.Combine(root, worldId.ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        WorldMemory? memory = null;
        if (File.Exists(MemoryPath))
        {
            try { memory = ReadMemory(MemoryPath, worldId); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                memory = ReadMemory(MemoryPath + ".bak", worldId);
                File.Copy(MemoryPath, MemoryPath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                File.Copy(MemoryPath + ".bak", MemoryPath, true);
                RecoveredFromBackup = true;
            }
        }
        Memory = memory ?? new WorldMemory { WorldId = worldId.ToString("N") };
        if (memory == null) Save();
    }

    private static WorldMemory ReadMemory(string path, Guid worldId)
    {
        var memory = JsonSerializer.Deserialize<WorldMemory>(File.ReadAllText(path), JsonOptions);
        if (memory == null || memory.WorldId != worldId.ToString("N") || memory.Version != 1
            || memory.Notes == null || memory.RecentEvents == null || memory.Preferences == null || memory.Dialogue == null)
            throw new InvalidDataException("Invalid world memory: " + path);
        return memory;
    }

    public IReadOnlyList<LearnedPreference> ApplyPreferences(IReadOnlyList<PreferenceUpdate> updates, string source, string turnId)
    {
        foreach (var update in updates) PreferenceProtocol.Validate(update, source);
        lock (_gate)
        {
            var previous = Memory.Preferences.ToList();
            long revision = Memory.RuleRevision;
            var changed = new List<LearnedPreference>();
            try
            {
                foreach (var update in updates)
                {
                    var existing = Memory.Preferences.FirstOrDefault(p => p.Key == update.Key && p.When == update.When
                        && (update.Key != "note" || p.Value == update.Value));
                    if (existing?.Value == update.Value) continue;
                    if (existing != null) Memory.Preferences.Remove(existing);
                    if (Memory.Preferences.Count >= 24) throw new InvalidOperationException("Preference memory is full; forget a preference first.");
                    var preference = new LearnedPreference(update.Key, update.Value, update.Evidence,
                        CompanionProtocol.Clip(source, 512), turnId, DateTimeOffset.UtcNow, update.When);
                    Memory.Preferences.Add(preference);
                    if (PreferenceProtocol.IsOperational(update.Key)) Memory.RuleRevision++;
                    changed.Add(preference);
                }
                if (changed.Count > 0) Save();
                return changed;
            }
            catch { Memory.Preferences = previous; Memory.RuleRevision = revision; throw; }
        }
    }

    public void ForgetPreference(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= Memory.Preferences.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var previous = Memory.Preferences.ToList();
            var previousDialogue = Memory.Dialogue.ToList();
            long revision = Memory.RuleRevision;
            if (PreferenceProtocol.IsOperational(Memory.Preferences[index].Key)) Memory.RuleRevision++;
            Memory.Preferences.RemoveAt(index);
            // Old replies can restate the removed preference. Do not feed them back as conversational context.
            Memory.Dialogue.Clear();
            try { Save(); } catch { Memory.Preferences = previous; Memory.Dialogue = previousDialogue; Memory.RuleRevision = revision; throw; }
        }
    }

    public void SetLastTask(TaskReference task)
    {
        lock (_gate)
        {
            var old = Memory.LastTask;
            Memory.LastTask = task;
            try { Save(); } catch { Memory.LastTask = old; throw; }
        }
    }

    public void AddDialogue(string player, string reply)
    {
        lock (_gate)
        {
            var previous = Memory.Dialogue.ToList();
            Memory.Dialogue.Add(new DialogueLine("player", CompanionProtocol.Clip(player, 240)));
            Memory.Dialogue.Add(new DialogueLine("companion", CompanionProtocol.Clip(reply, 120)));
            Memory.Dialogue = Memory.Dialogue.TakeLast(6).ToList();
            try { Save(); } catch { Memory.Dialogue = previous; throw; }
        }
    }

    public void Remember(string text)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 120) throw new ArgumentException("Memory must contain 1–120 characters.");
        lock (_gate)
        {
            if (Memory.Notes.Contains(text)) return;
            if (Memory.Notes.Count >= 32) throw new InvalidOperationException("Memory is full; forget a note first.");
            Memory.Notes.Add(text);
            try { Save(); } catch { Memory.Notes.RemoveAt(Memory.Notes.Count - 1); throw; }
        }
    }

    public void Forget(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= Memory.Notes.Count) throw new ArgumentOutOfRangeException(nameof(index));
            string note = Memory.Notes[index];
            Memory.Notes.RemoveAt(index);
            try { Save(); } catch { Memory.Notes.Insert(index, note); throw; }
        }
    }

    public void Observe(string text)
    {
        lock (_gate)
        {
            var previous = Memory.RecentEvents.ToList();
            Memory.RecentEvents.Add(CompanionProtocol.Clip(text, 120));
            if (Memory.RecentEvents.Count > 12) Memory.RecentEvents.RemoveAt(0);
            try { Save(); } catch { Memory.RecentEvents = previous; throw; }
        }
    }

    public JsonElement PromptRules()
    {
        lock (_gate)
            return JsonSerializer.SerializeToElement(Memory.Preferences.Where(p => PreferenceProtocol.IsOperational(p.Key))
                .Select(p => new { key = p.Key, when = p.When, value = p.Value }));
    }

    public string PromptMemory(string query = "", bool includeRules = true)
    {
        lock (_gate)
        {
            // Rules are always enforced separately. Rank soft notes by relevance, then recency.
            var selected = new List<string>();
            int budget = 300;
            var rules = Memory.Preferences.Where(p => p.Key == "address" || (includeRules && PreferenceProtocol.IsOperational(p.Key)))
                .Select(p => p.Key + (p.When == "any" ? "" : "@" + p.When) + ":" + p.Value);
            var notes = Memory.Preferences.Where(p => p.Key is not ("gather_confirmation" or "mining" or "chopping" or "address"))
                .Select(p => p.Value).Concat(Memory.Notes).Reverse()
                .OrderByDescending(note => Relevance(query, note));
            foreach (string note in rules.Concat(notes))
            {
                var line = "Preference: " + note;
                if (line.Length > budget) continue;
                selected.Add(line);
                budget -= line.Length;
            }
            foreach (string recent in Memory.RecentEvents.TakeLast(2))
            {
                if (budget <= 15) break;
                var line = CompanionProtocol.Clip("Past: " + recent, budget);
                selected.Add(line);
                budget -= line.Length;
            }
            return string.Join("\n", selected);
        }
    }

    private static int Relevance(string query, string note)
    {
        int score = 0;
        for (int i = 0; i + 1 < query.Length; i++)
            if (!char.IsWhiteSpace(query[i]) && note.Contains(query.Substring(i, 2), StringComparison.OrdinalIgnoreCase)) score++;
        return score;
    }

    public void Record(string kind, string? taskId, string? decisionId, object data,
        string? episodeId = null, string? turnId = null)
    {
        lock (_gate)
        {
            var record = new { version = 2, utc = DateTimeOffset.UtcNow, worldId = Memory.WorldId, sessionId = SessionId,
                kind, episodeId, turnId, taskId, decisionId, data };
            File.AppendAllText(Path.Combine(DirectoryPath, "learning.jsonl"),
                JsonSerializer.Serialize(record, JsonOptions) + "\n", new UTF8Encoding(false));
        }
    }

    private void Save()
    {
        var temp = MemoryPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, Memory, JsonOptions);
            stream.Flush(true);
        }
        if (File.Exists(MemoryPath)) File.Copy(MemoryPath, MemoryPath + ".bak", true);
        File.Move(temp, MemoryPath, true);
    }
}
