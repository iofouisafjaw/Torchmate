"""Hand-checked progression data. Facts that the Wiki states (summon details, drops, strategies)
are read from the Wiki at answer time; this table only fixes the order, the stable keys the mod
reports, and conservative readiness thresholds used by the advisor."""

# key: as reported by the mod (CompanionWorldInfo.ForBrain)
# rec: (max life, defense) a typical player has when the fight is comfortable, not a game rule.
BOSSES = [
    dict(key="king_slime", prog=1, name="史莱姆王", order=1, hard=False, optional=True, summon="史莱姆王冠",
         where="任意地点", when="任意时间", rec=(120, 6)),
    dict(key="eye_of_cthulhu", prog=2, name="克苏鲁之眼", order=2, hard=False, optional=False, summon="可疑眼球",
         where="地表", when="夜晚", rec=(200, 10)),
    dict(key="evil_boss", prog=3, name="世界吞噬怪/克苏鲁之脑", order=3, hard=False, optional=False, summon="蠕虫诱饵/血腥脊椎",
         where="腐化之地/猩红之地", when="任意时间", rec=(200, 12)),
    dict(key="queen_bee", prog=4, name="蜂王", order=4, hard=False, optional=True, summon="憎恶之蜂",
         where="丛林（地下蜂巢里砸幼虫也行）", when="任意时间", rec=(240, 15)),
    dict(key="skeletron", prog=5, name="骷髅王", order=5, hard=False, optional=False, summon="老人",
         where="地牢入口", when="夜晚", rec=(300, 15)),
    dict(key="deerclops", prog=6, name="独眼巨鹿", order=6, hard=False, optional=True, summon="鹿华",
         where="雪原", when="任意时间", rec=(300, 15)),
    dict(key="wall_of_flesh", prog=7, name="血肉墙", order=7, hard=False, optional=False, summon="向导巫毒娃娃",
         where="地狱（扔进岩浆）", when="任意时间", rec=(340, 20)),
    dict(key="queen_slime", prog=8, name="史莱姆皇后", order=8, hard=True, optional=True, summon="明胶水晶",
         where="神圣之地", when="任意时间", rec=(400, 30)),
    dict(key="the_destroyer", prog=10, name="毁灭者", order=9, hard=True, optional=False, summon="机械蠕虫",
         where="地表", when="夜晚", rec=(400, 35)),
    dict(key="the_twins", prog=9, name="双子魔眼", order=10, hard=True, optional=False, summon="机械魔眼",
         where="地表", when="夜晚", rec=(400, 40)),
    dict(key="skeletron_prime", prog=11, name="机械骷髅王", order=11, hard=True, optional=False, summon="机械骷髅头",
         where="地表", when="夜晚", rec=(420, 40)),
    dict(key="plantera", prog=12, name="世纪之花", order=12, hard=True, optional=False, summon="世纪之花球茎",
         where="地下丛林", when="任意时间", rec=(460, 50)),
    dict(key="golem", prog=13, name="石巨人", order=13, hard=True, optional=False, summon="丛林蜥蜴电池",
         where="丛林神庙", when="任意时间", rec=(500, 55)),
    dict(key="duke_fishron", prog=14, name="猪龙鱼公爵", order=14, hard=True, optional=True, summon="松露虫",
         where="海洋（钓鱼）", when="任意时间", rec=(500, 60)),
    dict(key="empress_of_light", prog=15, name="光之女皇", order=15, hard=True, optional=True, summon="七彩草蛉",
         where="神圣之地", when="夜晚", rec=(500, 60)),
    dict(key="lunatic_cultist", prog=17, name="拜月教邪教徒", order=16, hard=True, optional=False, summon="拜月教邪教徒",
         where="地牢入口", when="任意时间", rec=(500, 60)),
    dict(key="moon_lord", prog=18, name="月亮领主", order=17, hard=True, optional=False, summon="天界柱",
         where="四根天界柱", when="任意时间", rec=(500, 70)),
]
BY_KEY = {b["key"]: b for b in BOSSES}
# prog: Boss Checklist's progression value for the same boss, so modded bosses can be placed between them.
BY_PROG = {b["prog"]: b for b in BOSSES}
VANILLA_OPTIONAL = {b["prog"] for b in BOSSES if b["optional"]}


def rec_for(progression):
    """Readiness thresholds for any progression value: those of the last vanilla boss at or before it."""
    best = BOSSES[0]
    for b in BOSSES:
        if b["prog"] <= progression and b["prog"] >= best["prog"]:
            best = b
    return best["rec"]
EVIL_BOSS = {"corruption": ("世界吞噬怪", "蠕虫诱饵", "腐化之地"), "crimson": ("克苏鲁之脑", "血腥脊椎", "猩红之地")}

# Player-facing nicknames the Wiki redirects do not cover.
ALIASES = {
    "史王": "史莱姆王", "史莱姆国王": "史莱姆王", "克眼": "克苏鲁之眼", "眼球boss": "克苏鲁之眼",
    "世吞": "世界吞噬怪", "克脑": "克苏鲁之脑", "蜂后": "蜂王", "骷髅": "骷髅王", "巨鹿": "独眼巨鹿",
    "肉山": "血肉墙", "肉墙": "血肉墙", "史后": "史莱姆皇后", "机械眼": "双子魔眼", "双子眼": "双子魔眼",
    "机械虫": "毁灭者", "机械骷髅": "机械骷髅王", "花后": "世纪之花", "世花": "世纪之花", "石头人": "石巨人",
    "猪鲨": "猪龙鱼公爵", "猪龙鱼": "猪龙鱼公爵", "光女": "光之女皇", "教徒": "拜月教邪教徒", "月总": "月亮领主",
    "月球领主": "月亮领主",
}


def guide_title(boss_name):
    return f"Guide-{boss_name}攻略"


def boss_for_world(entry, evil):
    """The evil-biome boss depends on the world; return (name, summon, where)."""
    if entry["key"] != "evil_boss":
        return entry["name"], entry["summon"], entry["where"]
    return EVIL_BOSS.get(evil, EVIL_BOSS["corruption"])


# Pages that hold general progression advice, per stage.
STAGE_GUIDES = {
    "start": ("Guide-入门", ["第一天", "庇护所", "武器", "盔甲", "治疗", "挖矿和探索"]),
    "pre": ("Guide-游戏进程", ["开始游戏", "落稳脚跟", "突破", "进入地牢", "制霸地狱"]),
    "hard": ("Guide-困难模式入门", []),
}

# Pages where gear for each class and stage is discussed.
CLASS_GUIDE = "Guide-职业设置"
ARMOR_GUIDE = "Guide-盔甲进程"
CLASS_NAMES = {"melee": "战士", "ranged": "射手", "magic": "法师", "summon": "召唤师"}
