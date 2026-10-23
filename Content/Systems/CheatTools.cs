using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

// Cheat mode: off unless the player turns it on with the switch in the chat window (per visit; leaving
// the world turns it off). Every mutating cheat uses the same revocable permission and native scope.
internal static partial class CheatTools
{
    internal static readonly CheatAuthority Authority = new();
    internal static bool Enabled => Authority.Enabled;

    internal static void Toggle()
    {
        Authority.Toggle();
        CompanionUISystem.Publish(Enabled
            ? "· 作弊模式已打开：可生成物品／建筑、修改时间／晴雨、召唤Boss、设置世界出生点。请重新发送要执行的指令。"
            : "· 作弊模式已关闭。", Microsoft.Xna.Framework.Color.LightGray);
        CompanionMemorySystem.Record("cheat_mode", null, null, new { enabled = Enabled });
    }

    internal static void Reset() => Authority.Reset();

    internal const string Off = CheatWorldExecutor.Off;

    private static string? Guard(Player player, CheatPermit? permit) => !Authority.Allows(permit ?? Authority.Capture())
        ? Off : CheatWorldRules.ScopeRefusal(new NativeCheatWorldSite(player).Read());

    internal static string SpawnItem(Player player, int type, int count, CheatPermit? permit = null)
    {
        if (Guard(player,permit) is string error) return error;
        if (type <= ItemID.None || !ContentSamples.ItemsByType.TryGetValue(type,out var sample)) return "没有这个物品。";
        count = Math.Clamp(count, 1, Math.Max(1, sample.maxStack) * 10);
        int left = count;
        while (left > 0)
        {
            int stack = Math.Min(left, Math.Max(1, sample.maxStack));
            ModTools.Give(player, new Item(type, stack));
            left -= stack;
        }
        CompanionMemorySystem.Record("cheat_item", null, null, new { type, name = Lang.GetItemNameValue(type), count });
        return $"生成了 {Lang.GetItemNameValue(type)} ×{count}，放进你背包了（作弊）。";
    }

    // ---- StructureHelper: saved .shstruct / .shmstruct files ------------------------------------------
    private static string Folder => ModLoader.ModPath.Replace("Mods", "SavedStructures");

    private static Mod? Helper => ModLoader.TryGetMod("StructureHelper", out var mod) ? mod : null;

    private static string[] Files()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".shstruct") || f.EndsWith(".shmstruct")).ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    internal static string Structures()
    {
        if (Helper == null) return "没装 StructureHelper。";
        var names = Files().Select(Path.GetFileNameWithoutExtension).ToList();
        return names.Count == 0
            ? "SavedStructures 文件夹里还没有建筑。用 StructureHelper 的结构魔杖框选一块地方保存，就会出现在这里。"
            : "存好的建筑：" + string.Join("、", names.Take(30));
    }

    // Stamp a saved structure beside the player (to the side they face, standing on their ground line).
    internal static string Generate(Player player, string name, CheatPermit? permit = null)
    {
        if (Guard(player,permit) is string error) return error;
        var helper = Helper;
        if (helper == null) return "没装 StructureHelper。";
        if (name.Trim().Length == 0) return Structures();
        var files = Files();
        string? path = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == name.Trim())
            ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(name.Trim()));
        if (path == null) return $"没找到叫「{name}」的建筑。" + Structures();
        bool multi = path.EndsWith(".shmstruct");
        try
        {
            var api = helper.Code.GetType(multi ? "StructureHelper.API.MultiStructureGenerator" : "StructureHelper.API.Generator")
                ?? throw new InvalidOperationException("StructureHelper 的接口变了");
            var dimsMethod = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "GetStructureDimensions" && m.GetParameters().Length == (multi ? 4 : 3));
            var dims = (Point16)dimsMethod.Invoke(null, multi
                ? new object[] { path, helper, 0, true } : new object[] { path, helper, true })!;
            int feet = (int)(player.Bottom.Y / 16f);
            int px = (int)(player.Center.X / 16f);
            int x = player.direction >= 0 ? px + 3 : px - 3 - dims.X;
            var pos = new Point16(x, feet - dims.Y);
            if (!WorldGen.InWorld(pos.X, pos.Y, 10) || !WorldGen.InWorld(pos.X + dims.X, pos.Y + dims.Y, 10))
                return "这里离世界边缘太近，放不下。";
            var gen = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == (multi ? "GenerateMultistructureRandom" : "GenerateStructure"));
            var flags = Enum.ToObject(gen.GetParameters().Last().ParameterType, 0);
            if (Guard(player,permit) is string revoked) return revoked;
            gen.Invoke(null, new object[] { path, pos, helper, true, false, flags });
            WorldGen.RangeFrame(pos.X, pos.Y, pos.X + dims.X, pos.Y + dims.Y);
            CompanionMemorySystem.Record("cheat_structure", null, null, new { path, x = pos.X, y = pos.Y, w = dims.X, h = dims.Y });
            return $"生成了「{Path.GetFileNameWithoutExtension(path)}」（{dims.X}×{dims.Y}），在你{(player.direction >= 0 ? "右" : "左")}边，" +
                "原来那块地方的方块被覆盖了（作弊）。";
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            return "生成失败：" + inner.Message;
        }
    }
}
