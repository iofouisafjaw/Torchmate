"""Rule-based reading of the player's situation.

The program decides *what matters now* from the snapshot the mod sends (life, defense, tools,
weapons, armor, inventory, NPCs, defeated bosses, time). The language model only phrases it.

Snapshot (from the mod, CompanionWorldInfo.ForBrain):
  world:   name, difficulty, hardmode, evil ('crimson'|'corruption'), day, clock, bloodMoon, raining, ...
  progress: bosses [keys], townNpcs [names], altarsSmashed
  player:  life, lifeMax, mana, manaMax, defense, biome, layer, pick, axe, hammer,
           hook, wings, boots, weapons [{name,damage,class,useTime}], armor [names],
           accessories [names], inventory [{key,name,count}], healing {name,count}, coins (copper)
"""
from knowledge import BOSSES, BY_KEY, BY_PROG, CLASS_NAMES, VANILLA_OPTIONAL, boss_for_world, rec_for


class Finding:
    __slots__ = ("priority", "topic", "text", "pages", "items")

    def __init__(self, priority, topic, text, pages=(), items=()):
        self.priority, self.topic, self.text = priority, topic, text
        self.pages, self.items = list(pages), list(items)


def _inv(state):
    """Inventory indexed by internal key and by display name."""
    out = {}
    for i in state["player"].get("inventory", []):
        for k in (i.get("key"), i.get("name")):
            if k:
                out[k] = i
    return out


def _has(state, *keys):
    inv = _inv(state)
    return any(k in inv for k in keys)


def _count(state, *keys):
    inv = _inv(state)
    return sum(inv[k].get("count", 0) for k in keys if k in inv)


def main_class(state):
    """The damage class of the strongest weapon carried (None if only starter tools)."""
    weapons = state["player"].get("weapons") or []
    if not weapons:
        return None
    best = max(weapons, key=lambda w: w.get("dps") or w.get("damage", 0))
    return best.get("class")


def checklist(state):
    """Boss Checklist entries (only when that mod is enabled), in progression order."""
    return sorted(state["progress"].get("checklist") or [], key=lambda e: e.get("progression", 0))


def from_checklist(e):
    """A Boss Checklist entry in the same shape as knowledge.BOSSES (vanilla ones map to the table)."""
    if e.get("mod") == "Terraria" and e.get("progression") in BY_PROG:
        return BY_PROG[e["progression"]]
    prog = float(e.get("progression", 0))
    return dict(key=e.get("key"), prog=prog, order=prog, hard=prog > 7, optional=False, name=e.get("name", ""),
                summon="/".join(e.get("spawnItems") or []), where="", when="", rec=rec_for(prog),
                spawnInfo=e.get("spawnInfo", ""), loot=e.get("loot", []), mod=e.get("mod", ""))


def next_boss(state):
    entries = checklist(state)
    if entries:   # includes modded bosses (Calamity etc.) in their intended order
        for e in entries:
            if e.get("type") != "boss" or e.get("downed"):
                continue
            if e.get("mod") == "Terraria" and e.get("progression") in VANILLA_OPTIONAL:
                continue
            return from_checklist(e)
        return None
    defeated = set(state["progress"].get("bosses", []))
    hard = state["world"].get("hardmode")
    for b in BOSSES:
        if b["key"] in defeated or b["optional"]:
            continue
        if b["hard"] and not hard:
            continue
        if b["key"] == "wall_of_flesh" and hard:
            continue
        return b
    return None


def optional_bosses(state):
    defeated = set(state["progress"].get("bosses", []))
    hard = state["world"].get("hardmode")
    nxt = next_boss(state)
    limit = nxt["order"] if nxt else 99
    return [b for b in BOSSES if b["optional"] and b["key"] not in defeated and b["hard"] == bool(hard)
            and b["order"] < limit + 2]


def readiness(state, boss):
    p = state["player"]
    life_need, def_need = boss["rec"]
    gaps = []
    if p.get("lifeMax", 100) < life_need:
        gaps.append(f"生命上限 {p.get('lifeMax')}，建议至少 {life_need}")
    if p.get("defense", 0) < def_need:
        gaps.append(f"防御 {p.get('defense', 0)}，建议至少 {def_need}")
    heal = p.get("healing") or {}
    if heal.get("count", 0) < 5:
        gaps.append("治疗药水不足 5 瓶")
    if not p.get("hook"):
        gaps.append("没有抓钩")
    weapons = p.get("weapons") or []
    best = max((w.get("damage", 0) for w in weapons), default=0)
    floor = 12 if boss["order"] <= 3 else 20 if boss["order"] <= 7 else 40
    if best < floor:
        gaps.append(f"最强武器伤害只有 {best}，偏弱")
    return gaps


def analyse(state):
    """Return (facts, findings). facts: short lines describing the player; findings: ranked advice."""
    w, p, prog = state["world"], state["player"], state["progress"]
    facts, out = [], []
    hard = bool(w.get("hardmode"))
    defeated = [BY_KEY[k]["name"] if k in BY_KEY else k for k in prog.get("bosses", [])]
    if checklist(state):
        defeated = [e["name"] for e in checklist(state) if e.get("downed") and e.get("type") == "boss"]
    evil_name = "猩红" if w.get("evil") == "crimson" else "腐化"

    facts.append(f"世界：{w.get('name', '')}（{w.get('difficulty', '经典')}，{evil_name}世界，"
                 f"{'困难模式' if hard else '困难模式之前'}），{w.get('clock', '')}"
                 + ("，血月" if w.get("bloodMoon") else "") + ("，下雨" if w.get("raining") else ""))
    facts.append("已击败：" + ("、".join(defeated) if defeated else "还没有击败任何 Boss"))
    mods = [m for m in prog.get("mods") or [] if m not in ("BossChecklist",)]
    if mods:
        facts.append("装了模组：" + "、".join(mods[:10]))
    facts.append(f"生命 {p.get('life')}/{p.get('lifeMax')}，魔力 {p.get('mana')}/{p.get('manaMax')}，防御 {p.get('defense', 0)}")
    tools = []
    if p.get("pick"):
        tools.append(f"{p.get('pickName', '镐')}（镐力 {p['pick']}%）")
    if p.get("axe"):
        tools.append(f"{p.get('axeName', '斧')}（斧力 {p['axe']}%）")
    facts.append("工具：" + ("、".join(tools) if tools else "没有镐和斧"))
    weapons = p.get("weapons") or []
    if weapons:
        facts.append("武器：" + "、".join(f"{x['name']}（{x.get('damage', 0)} {CLASS_NAMES.get(x.get('class'), '')}伤害）"
                                       for x in weapons[:4]))
    armor = p.get("armor") or []
    facts.append("盔甲：" + ("、".join(armor) if armor else "没穿盔甲"))
    acc = p.get("accessories") or []
    facts.append("饰品：" + ("、".join(acc) if acc else "没有饰品"))
    mobility = [n for n, f in (("抓钩", "hook"), ("翅膀", "wings"), ("加速靴", "boots")) if p.get(f)]
    facts.append("机动：" + ("、".join(mobility) if mobility else "没有抓钩、翅膀或加速靴"))
    heal = p.get("healing") or {}
    facts.append(f"治疗药水：{heal.get('name')} ×{heal.get('count')}" if heal.get("count") else "治疗药水：没有")
    npcs = prog.get("townNpcs") or []
    facts.append(f"城镇 NPC（{len(npcs)}）：" + ("、".join(npcs) if npcs else "无"))
    materials = [f"{i['name']}×{i['count']}" for i in p.get("inventory", []) if i.get("material")]
    if materials:
        facts.append("背包材料：" + "、".join(materials[:16]))
    if p.get("coins"):
        c = p["coins"]
        facts.append(f"钱：{c // 10000} 金 {c // 100 % 100} 银")

    # ---- what matters now -------------------------------------------------------------------
    life_max = p.get("lifeMax", 100)
    nxt = next_boss(state)

    if not hard and life_max < 400:
        need = (min(400, max(200, nxt["rec"][0] if nxt else 200)) - life_max + 19) // 20
        if need > 0:
            out.append(Finding(10 if life_max < 200 else 6, "生命水晶",
                               f"生命上限只有 {life_max}：去地下找生命水晶，每个 +20，再找 {need} 个就够打下一个 Boss。"
                               + ("用过生命水晶后护士才会入住。" if life_max <= 100 else ""),
                               pages=[("生命水晶", ["小贴士"])], items=[]))
    if hard and life_max >= 400 and life_max < 500 and "plantera" in prog.get("bosses", []):
        out.append(Finding(7, "生命果", f"生命上限 {life_max}：去地下丛林找生命果，每个 +5，最多到 500。",
                           pages=[("生命果", ["小贴士"])]))

    armor_n = len(armor)
    if armor_n < 3 and not hard:
        ore_bars = [i for i in p.get("inventory", []) if i.get("key", "").endswith("Bar")]
        bars = "手上有 " + "、".join(f"{i['name']}×{i['count']}" for i in ore_bars) if ore_bars else "手上还没有锭"
        worn = "还没穿盔甲" if armor_n == 0 else f"只穿了 {armor_n} 件盔甲"
        out.append(Finding(9 if p.get("defense", 0) < 6 else 5, "盔甲",
                           f"{worn}（防御 {p.get('defense', 0)}），{bars}。"
                           "凑齐一整套金属盔甲（金/铂金最好，银/钨也够打前期 Boss）。",
                           items=[i["name"].replace("锭", "盔甲") for i in ore_bars if i["name"].endswith("锭")][:1]))

    pick = p.get("pick", 0)
    if not hard and not prog.get("bosses") and pick <= 35 and not _has(state, "IronAnvil", "LeadAnvil"):
        out.append(Finding(8, "起步", "还在开荒：用木材做工作台，先盖一间能住人的房子；再下矿挖石块和铜/锡、铁/铅矿，"
                                    "做熔炉炼锭、做铁砧/铅砧，换更好的镐和盔甲。",
                           items=["工作台", "熔炉", "铁砧"]))
    if not hard:
        if pick < 55:
            want = "金/铂金镐或噩梦镐/死亡使者镐（55%）才能挖魔矿/猩红矿和黑曜石" if pick >= 40 else \
                   "先做铁镐/铅镐，再升到金/铂金镐（55%）"
            out.append(Finding(7, "镐", f"镐力 {pick}%：{want}。", items=[]))
        elif pick < 65 and "eye_of_cthulhu" in prog.get("bosses", []):
            out.append(Finding(5, "镐", f"镐力 {pick}%：用魔矿锭/猩红矿锭做噩梦镐或死亡使者镐，才能挖地狱的狱石。",
                               items=["噩梦镐", "死亡使者镐"]))

    if not p.get("hook"):
        out.append(Finding(6, "抓钩", "没有抓钩：躲 Boss 和探洞都靠它。",
                           items=["抓钩"]))
    if not p.get("boots") and not hard:
        out.append(Finding(4, "移动", "没有加速靴：地下金箱能开出赫尔墨斯靴，打克眼时满场跑能躲大部分冲撞。",
                           pages=[("赫尔墨斯靴", ["小贴士"])]))
    if heal.get("count", 0) < 5:
        out.append(Finding(5, "药水", "治疗药水不够：打 Boss 前至少带一组。",
                           items=["弱效治疗药水"]))
    if len(npcs) < 4 and not hard:
        out.append(Finding(5, "NPC", f"现在只有 {len(npcs)} 个城镇 NPC。多盖几间合格的房子（墙、门、桌、椅、光源）；"
                                      "身上带 50 银币商人会来，用过生命水晶后护士会来。",
                           pages=[("Guide-基地", [])]))
    if not _has(state, "Torch") and not hard:
        out.append(Finding(4, "火把", "背包里没有火把：下矿必带。", items=["火把"]))

    if nxt:
        name, summon, where = boss_for_world(nxt, w.get("evil"))
        gaps = readiness(state, nxt)
        status = "已经基本准备好" if not gaps else "还差：" + "；".join(gaps)
        has_summon = bool(summon) and _has(state, *(s for s in summon.split("/")))
        timing = ""
        if nxt["when"] == "夜晚":
            timing = "现在是夜里，可以打。" if not w.get("day") else "要等到晚上 19:30 以后召唤。"
        if nxt.get("mod"):   # modded boss, placed by Boss Checklist's progression order
            intro = f"按 Boss Checklist 的进度顺序，下一个 Boss 是{name}（{nxt['mod']} 模组）。召唤：{(nxt.get('spawnInfo') or '见说明')[:90]}"
        else:
            intro = f"主线下一个 Boss 是{name}（在{where}用{summon}召唤）"
        out.append(Finding(8 if not gaps else 6, "下一个Boss",
                           f"{intro}。{status}。"
                           + ("背包里已有召唤物。" if has_summon else "") + timing,
                           pages=[(name, ["召唤和生成", "生成", "召唤"]), (f"Guide-{name}攻略", ["作战", "第一阶段", "地形准备"])],
                           items=[s for s in summon.split("/")]))
    for b in optional_bosses(state):
        name, summon, where = boss_for_world(b, w.get("evil"))
        gaps = readiness(state, b)
        if len(gaps) <= 2:
            out.append(Finding(3, "可选Boss", f"可选：{name}（{where}，用{summon}），掉落对现在有帮助。",
                               pages=[(name, ["掉落", "小贴士"])], items=[summon]))

    if w.get("bloodMoon"):
        out.append(Finding(9, "事件", "现在是血月：怪物更多更凶，别在外面乱跑；也可以趁机钓鱼刷血月掉落。",
                           pages=[("血月", ["小贴士"])]))
    out.sort(key=lambda f: -f.priority)
    return facts, out


def boss_plan(state, boss_name):
    """Facts for a question about a specific boss: readiness gaps relative to this player."""
    entry = next((b for b in BOSSES if b["name"] == boss_name or boss_name in b["name"].split("/")), None)
    modded = next((e for e in checklist(state) if e.get("name") == boss_name and e.get("mod") != "Terraria"), None)
    if modded:
        b = from_checklist(modded)
        gaps = readiness(state, b)
        lines = [f"{boss_name}（{b['mod']} 模组）：{'这个世界已经击败过' if modded.get('downed') else '还没击败'}。"
                 f"召唤：{modded.get('spawnInfo') or '没有说明'}",
                 "按你现在的状态（参照同进度原版 Boss 的标准）：" + ("已经基本准备好。" if not gaps else "还差 " + "；".join(gaps) + "。")]
        return b, lines
    if not entry:
        return None, []
    name, summon, where = boss_for_world(entry, state["world"].get("evil"))
    if "/" in entry["name"]:
        name = boss_name
    done = entry["key"] in state["progress"].get("bosses", [])
    gaps = readiness(state, entry)
    lines = [f"{name}：{'这个世界已经击败过' if done else '还没击败'}；在{where}用{summon}召唤（{entry['when']}）。",
             "按你现在的状态：" + ("已经基本准备好。" if not gaps else "还差 " + "；".join(gaps) + "。")]
    return entry, lines
