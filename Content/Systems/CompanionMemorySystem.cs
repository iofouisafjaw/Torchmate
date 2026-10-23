using System;
using System.IO;
using System.Threading;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed class CompanionMemorySystem : ModSystem
{
    internal static CompanionStore? Store { get; private set; }
    internal static EpisodeRecorder? Episodes { get; private set; }
    internal static Guid Session { get; private set; }
    private static CancellationTokenSource? _lifetime;
    internal static CancellationToken WorldToken => _lifetime?.Token ?? new CancellationToken(true);
    internal static string? LastTaskId { get; private set; }
    internal static string? LastDecisionId { get; private set; }

    public override void PostWorldLoad()
    {
        if (!ServerAuthority.IsServer) return;
        Session = Guid.NewGuid();
        _lifetime = new CancellationTokenSource();
        LastTaskId = LastDecisionId = null;
        try
        {
            Store = new CompanionStore(Path.Combine(Main.SavePath, "ModData", "TerrarAI", "worlds"),
                Main.ActiveWorldFileData.UniqueId, Session);
            Episodes = new EpisodeRecorder(Store);
            Record("world_opened", null, null, new { name = Main.worldName, recovered = Store.RecoveredFromBackup });
        }
        catch (Exception ex)
        {
            Store = null;
            Episodes = null;
            Mod.Logger.Error("Companion memory could not be opened: " + ex.Message);
        }
    }

    public override void OnWorldUnload() => CloseWorld();
    public override void ClearWorld() => CloseWorld();
    public override void Unload() => CloseWorld();

    private static void CloseWorld()
    {
        if (_lifetime == null) return;
        _lifetime.Cancel();
        CompanionDialogueSystem.Cancel();
        CompanionInquirySystem.Cancel();
        if (Main.npc != null)
            foreach (var npc in Main.npc)
                if (npc?.active == true && npc.ModNPC is AIAgentNPC companion)
                    companion.StopCommand("world_unloaded");
        Record("world_closed", null, null, new { });
        Store = null;
        Episodes = null;
        Session = Guid.Empty;
        _lifetime.Dispose();
        _lifetime = null;
        LastTaskId = LastDecisionId = null;
    }

    internal static void Record(string kind, string? taskId, string? decisionId, object data,
        string? episodeId = null, string? turnId = null)
    {
        try
        {
            Episodes?.Record(kind, taskId, decisionId, data, episodeId, turnId);
            if (taskId != null) LastTaskId = taskId;
            if (decisionId != null) LastDecisionId = decisionId;
        }
        catch (Exception ex) { TerrarAI.LogWarn("Learning record could not be saved: " + ex.Message); }
    }

    internal static void Observe(string text)
    {
        try { Store?.Observe(text); }
        catch (Exception ex) { TerrarAI.LogWarn("Companion memory could not be saved: " + ex.Message); }
    }
}
