// Compatibility schema re-declared from the frozen field/default inventory.
using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
namespace TerrarAI;
public class TerrarAI_Config : ModConfig
{
    internal static TerrarAI_Config Get() => ModContent.GetInstance<TerrarAI_Config>();
    public override ConfigScope Mode => ConfigScope.ServerSide;
    internal string GetEffectiveApiKey() => ApiKey;
    [DefaultValue(true)]
    public bool EnableInventoryReminders { get; set; } = true;
    [DefaultValue(true)]
    public bool EnableLowHealthReminders { get; set; } = true;
    [DefaultValue(true)]
    public bool EnableBloodMoonReminders { get; set; } = true;
    [DefaultValue(true)]
    public bool EnableNightReminders { get; set; } = true;
    [DefaultValue(true)]
    public bool EnableReminders { get; set; } = true;
    [Range(10,50),DefaultValue(30)]
    public int AutoHealingThresholdPercent { get; set; } = 30;
    [DefaultValue(true)]
    public bool EnableAutomaticHealing { get; set; } = true;
    [DefaultValue(true)]
    public bool EnableAutomaticDefense { get; set; } = true;
    [DefaultValue(false)]
    public bool EnableVerboseLogging { get; set; } = false;
    [DefaultValue(false)]
    public bool EnableCreativeMode { get; set; } = false;
    [Range(30, 300), DefaultValue(90)]
    public int MaxPlanningSeconds { get; set; } = 90;
    [Range(10, 300), DefaultValue(60)]
    public int RequestTimeoutSeconds { get; set; } = 60;
    [Range(1, 30), DefaultValue(8)]
    public int MaxDecisionSteps { get; set; } = 8;
    [Range(48, 256), DefaultValue(128)]
    public int MaxOutputTokens { get; set; } = 128;
    [Range(0f, 2f), DefaultValue(0.2f)]
    public float Temperature { get; set; } = 0.2f;
    [DefaultValue("")]
    public string ApiKey { get; set; } = "";
    [DefaultValue("qwen3-4b")]
    public string Model { get; set; } = "qwen3-4b";
    [DefaultValue("http://127.0.0.1:8080/v1/chat/completions")]
    public string BaseEndpoint { get; set; } = "http://127.0.0.1:8080/v1/chat/completions";
}
