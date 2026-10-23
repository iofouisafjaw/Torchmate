using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

internal static partial class CheatTools
{
    internal static string World(Player player, CheatWorldRequest request, CheatPermit? permit = null)
    {
        var result = CheatWorldExecutor.Execute(Authority,permit ?? Authority.Capture(),request,new NativeCheatWorldSite(player));
        CompanionMemorySystem.Record("cheat_world",null,null,new { operation=request.Operation.ToString(),request.Value,result.Success,result.Message });
        return result.Message;
    }
}

// Game-thread adapter. Never calls itself from a model thread, changes boss progression flags,
// consumes summon items or bypasses native NPC creation. Readbacks report actual immediate effects.
internal sealed class NativeCheatWorldSite(Player player) : ICheatWorldSite
{
    private int _spawnX, _spawnY;
    private static int[] Types(string boss) => boss switch {
        "king_slime" => new int[]{NPCID.KingSlime}, "eye" => new int[]{NPCID.EyeofCthulhu},
        "eater" => new int[]{NPCID.EaterofWorldsHead}, "brain" => new int[]{NPCID.BrainofCthulhu},
        "queen_bee" => new int[]{NPCID.QueenBee}, "skeletron" => new int[]{NPCID.SkeletronHead},
        "deerclops" => new int[]{NPCID.Deerclops}, "wall" => new int[]{NPCID.WallofFlesh},
        "queen_slime" => new int[]{NPCID.QueenSlimeBoss}, "twins" => new int[]{NPCID.Retinazer,NPCID.Spazmatism},
        "destroyer" => new int[]{NPCID.TheDestroyer}, "prime" => new int[]{NPCID.SkeletronPrime},
        "plantera" => new int[]{NPCID.Plantera}, "golem" => new int[]{NPCID.Golem},
        "duke" => new int[]{NPCID.DukeFishron}, "empress" => new int[]{NPCID.HallowBoss},
        "cultist" => new int[]{NPCID.CultistBoss}, "moon_lord" => new int[]{NPCID.MoonLordCore},
        _ => Array.Empty<int>()
    };
    private static bool BossActive(NPC n) => n.active && (n.boss || n.type is NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail
        or NPCID.TheDestroyerBody or NPCID.TheDestroyerTail or NPCID.WallofFleshEye);
    public CheatWorldContext Read()
    {
        var biomes = new List<string>();
        if (player.ZoneCorrupt) biomes.Add("corruption"); if (player.ZoneCrimson) biomes.Add("crimson");
        if (player.ZoneJungle) { biomes.Add("jungle"); if (player.Center.Y/16f>Main.worldSurface) biomes.Add("deep_jungle"); }
        if (player.ZoneSnow) biomes.Add("snow"); if (player.ZoneHallow) biomes.Add("hallow");
        if (player.Center.Y/16f>=Main.maxTilesY-205) biomes.Add("underworld");
        if (player.ZoneLihzhardTemple) biomes.Add("temple"); if (player.ZoneBeach) biomes.Add("ocean");
        bool owner = player.whoAmI>=0 && player.whoAmI<Main.maxPlayers && player.whoAmI==Main.myPlayer &&
            ReferenceEquals(Main.player[player.whoAmI],player);
        return new(Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu,owner&&player.active&&!player.dead&&!player.ghost,
            Main.gamePaused||ModContent.GetInstance<CompanionUISystem>().WantsPause,Main.dayTime,
            Main.npc.Any(BossActive),Main.npc.Count(n=>!n.active),biomes);
    }
    public string? Preflight(CheatWorldRequest request)
    {
        if (request.Operation==CheatWorldOperation.Boss)
        {
            int x=(int)(player.Center.X/16f),y=(int)(player.Center.Y/16f);
            if (!WorldGen.InWorld(x,y,80)) return "这里太靠近世界边界，请向世界内侧移动后再召唤。";
            return null;
        }
        if (request.Operation!=CheatWorldOperation.Spawn) return null;
        _spawnX=(int)(player.Center.X/16f); _spawnY=(int)Math.Round(player.Bottom.Y/16f);
        if (player.width>32 || player.height>64 || !float.IsFinite(player.Bottom.Y) || !float.IsFinite(player.Center.X) ||
            Math.Abs(player.Bottom.Y-_spawnY*16f)>2f || Math.Abs(player.velocity.Y)>0.1f ||
            !CheatWorldRules.SafeSpawn(_spawnX,_spawnY,Main.maxTilesX,Main.maxTilesY,Cell))
            return "请站在三格宽的平整实心地面上，头顶留四格空间；出生点周围不能有液体、危险方块、电线或落沙。不会拆改地形。";
        return null;
    }
    private static CheatSpawnCell Cell(int x,int y)
    {
        var t=Framing.GetTileSafely(x,y);
        bool active=t.HasTile&&!t.IsActuated;
        bool solid=active&&Main.tileSolid[t.TileType]&&!Main.tileSolidTop[t.TileType];
        bool hazard=t.LiquidAmount>0||t.RedWire||t.BlueWire||t.GreenWire||t.YellowWire||active&&t.TileType is
            TileID.Spikes or TileID.WoodenSpikes or TileID.Traps or TileID.Boulder or TileID.Explosives or
            TileID.Sand or TileID.Ebonsand or TileID.Crimsand or TileID.Pearlsand or TileID.Silt or TileID.Slush;
        hazard |= active && (TileID.Sets.TouchDamageHot[t.TileType] || TileID.Sets.TouchDamageBleeding[t.TileType] ||
            TileID.Sets.TouchDamageImmediate[t.TileType]>0 || TileID.Sets.TouchDamageDestroyTile[t.TileType]);
        return new(!solid,solid&&!t.IsHalfBlock&&t.Slope==SlopeType.Solid,hazard);
    }
    public CheatWorldResult Apply(CheatWorldRequest request)
    {
        switch (request.Operation)
        {
            case CheatWorldOperation.Time:
                CheatWorldRequest.TryMinute(request.Value,out int minute);
                var clock=CheatWorldRequest.Clock(minute);
                Main.fastForwardTimeToDawn=false; Main.fastForwardTimeToDusk=false;
                Main.SkipToTime((int)clock.Ticks,clock.Day);
                bool timeMatched=Main.dayTime==clock.Day&&Main.time==clock.Ticks;
                return new(timeMatched,timeMatched?$"时间已设为{request.Value}（{(clock.Day?"白天":"夜晚")}，作弊）。跨昼夜按原生切换处理，可能触发当天事件。":"时间修改未生效，请检查其他模组的时间控制。" );
            case CheatWorldOperation.Weather:
                bool rain=request.Value=="rain";
                if (rain) Main.StartRain(); else Main.StopRain();
                bool matched=Main.raining==rain;
                return new(matched,matched?$"天气已改为{(rain?"下雨":"晴天／停雨")}（作弊）。{(!rain?"血月、沙尘暴等事件及风速保持原状。":"雨量和持续时间由游戏决定。")}":"天气修改未生效，请检查其他模组的天气控制。" );
            case CheatWorldOperation.Spawn:
                // Recheck current feet/terrain immediately before writing the native world-save fields.
                if (Preflight(request) is string spawnError) return new(false,spawnError);
                Main.spawnTileX=_spawnX; Main.spawnTileY=_spawnY;
                return new(Main.spawnTileX==_spawnX&&Main.spawnTileY==_spawnY,
                    $"世界默认出生点已设为({_spawnX},{_spawnY})（作弊），随正常世界存档保存。已有个人床绑定仍优先，不会被清除。" );
            case CheatWorldOperation.Boss:
                var boss=CheatWorldRequest.FindBoss(request.Value)!;
                var types=Types(boss.Key);
                if(types.Length==0) return new(false,"该Boss尚未绑定原生生成入口，未进行召唤。");
                foreach (int type in types)
                {
                    if (boss.Key=="wall") NPC.SpawnWOF(player.Center);
                    else NPC.SpawnOnPlayer(player.whoAmI,type);
                }
                int found=types.Count(type=>NPC.AnyNPCs(type));
                return new(found==types.Length,found==types.Length
                    ? $"已召唤{boss.Name}（作弊{(boss.Key=="twins"?"，两只魔眼均在场":"")}）。{(boss.Key=="empress"&&Main.dayTime?"白天光之女皇攻击极危险。":"")}只核验当前生成，后续行为由游戏决定。"
                    : $"{boss.Name}召唤未完整成功：当前确认{found}/{types.Length}个主体。不会自动重试；请核对现场和原生生成条件。" );
            default: return new(false,"不支持这个作弊操作。");
        }
    }
}
