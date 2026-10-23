// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using TerrarAI.Content.Systems;
using Terraria.ModLoader;
namespace TerrarAI;
// Mod identity remains stable for existing worlds and configuration files.
public class TerrarAI : Mod
{
    internal static TerrarAI? Instance { get; private set; }
    private static XAIClient? _model;
    internal static XAIClient? Client => _model;
    internal static XAIClient RequireClient() => _model ??= new();
    public override void Load() { Instance = this; _model = new(); }
    public override void Unload() { var previous = _model; _model = null; Instance = null; previous?.Dispose(); }
    internal static void LogError(string text) => Instance?.Logger.Error(text);
    internal static void LogWarn(string text) => Instance?.Logger.Warn(text);
    internal static void LogInfo(string text) => Instance?.Logger.Info(text);
}
