"""Offline checks for the brain's context building (no model needed).

    python scripts/brain/test_brain.py [path/to/wiki.db]
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import advisor  # noqa: E402
from brain import Brain, drops  # noqa: E402
from wiki import Wiki  # noqa: E402

FRESH = {
    "world": {"id": "w1", "name": "狂怒的溪谷", "difficulty": "经典", "hardmode": False, "evil": "crimson",
              "day": True, "clock": "09:54", "bloodMoon": False, "raining": False},
    "progress": {"bosses": [], "townNpcs": ["向导"]},
    "player": {"life": 100, "lifeMax": 100, "mana": 20, "manaMax": 20, "defense": 0, "pick": 35, "axe": 35,
               "pickName": "铜镐", "axeName": "铜斧", "hook": False, "wings": False, "boots": False,
               "weapons": [{"name": "铜短剑", "damage": 5, "class": "melee", "useTime": 8}],
               "armor": [], "accessories": [], "healing": {}, "coins": 0,
               "inventory": [{"key": "CopperShortsword", "name": "铜短剑", "count": 1},
                             {"key": "CopperPickaxe", "name": "铜镐", "count": 1},
                             {"key": "CopperAxe", "name": "铜斧", "count": 1},
                             {"key": "Wood", "name": "木材", "count": 43, "material": True},
                             {"key": "Acorn", "name": "橡实", "count": 2, "material": True}]},
}
MID = {
    "world": {"id": "w2", "name": "测试", "difficulty": "专家", "hardmode": False, "evil": "corruption",
              "day": False, "clock": "21:10"},
    "progress": {"bosses": ["king_slime", "eye_of_cthulhu"], "townNpcs": ["向导", "商人", "护士", "树妖", "军火商"]},
    "player": {"life": 260, "lifeMax": 260, "mana": 60, "manaMax": 60, "defense": 14, "pick": 59, "axe": 70,
               "pickName": "铂金镐", "hook": True, "boots": True,
               "weapons": [{"name": "铂金弓", "damage": 11, "class": "ranged", "useTime": 19},
                           {"name": "铂金宽剑", "damage": 19, "class": "melee", "useTime": 21}],
               "armor": ["铂金头盔", "铂金胸甲", "铂金护胫"], "accessories": ["赫尔墨斯靴", "云朵瓶"],
               "healing": {"name": "弱效治疗药水", "count": 12}, "coins": 54000,
               "inventory": [{"key": "DemoniteOre", "name": "魔矿", "count": 64, "material": True},
                             {"key": "RottenChunk", "name": "腐肉", "count": 9, "material": True}]},
}


def main():
    db = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parents[2] / ".runtime/wiki-v010-index.db")
    wiki = Wiki(db)
    b = Brain(wiki, "http://127.0.0.1:0")
    ok = True

    def check(cond, msg):
        nonlocal ok
        print(("PASS " if cond else "FAIL ") + msg)
        ok &= bool(cond)

    facts, findings = advisor.analyse(FRESH)
    topics = [f.topic for f in findings]
    check(topics[0] in ("生命水晶", "起步", "盔甲"), "fresh world starts with basics: " + ",".join(topics))
    check(advisor.next_boss(FRESH)["key"] == "eye_of_cthulhu", "first mainline boss is the Eye")
    check(advisor.next_boss(MID)["key"] == "evil_boss", "after the Eye comes the evil boss")
    check(b.entities("那我们第一个要打的首领是啥，克眼掉什么") == ["克苏鲁之眼"], "nickname 克眼 links to the Eye")
    check("世界吞噬怪" in b.entities("世吞怎么打"), "世吞 links to the Eater of Worlds")
    check("克苏鲁护盾" in drops(wiki.page("克苏鲁之眼")), "drop table is parsed")

    for q, st, want in [("按我当前世界的进度和背包，建议下一步做什么？", FRESH, "plan"),
                        ("那我们第一个要打的首领是啥，奖励又是啥", FRESH, "plan"),
                        ("世吞怎么打", MID, "boss"),
                        ("可疑眼球怎么合成", FRESH, "recipe"),
                        ("生命水晶在哪找", FRESH, "info")]:
        intent, ents, blocks, prompt = b.build(q, st, wiki.version)
        check(intent == want, f"{q} -> {intent} {ents} blocks={[b[0] for b in blocks]} chars={len(prompt)}")
        check(len(prompt) < 9000, "prompt stays inside the context budget")
    intent, ents, blocks, prompt = b.build("那我们第一个要打的首领是啥，奖励又是啥", FRESH, wiki.version)
    check("克苏鲁之眼" in prompt and "掉落" in prompt, "first-boss question carries the Eye's drops")
    b.history[("w1", wiki.version)] = [("第一个打什么", "克苏鲁之眼", ["克苏鲁之眼"])]
    intent, ents, blocks, prompt = b.build("这些东西的效果都是啥", FRESH, wiki.version)
    check("克苏鲁之眼" in ents, "follow-up question reuses the previous topic")
    # Boss Checklist + game data (as with Calamity installed); names here are test fixtures.
    import copy, json, tempfile
    modded = copy.deepcopy(MID)
    modded["progress"]["checklist"] = [
        {"key": "Terraria KingSlime", "mod": "Terraria", "name": "史莱姆王", "progression": 1.0, "type": "boss", "downed": True},
        {"key": "Terraria EyeofCthulhu", "mod": "Terraria", "name": "克苏鲁之眼", "progression": 2.0, "type": "boss", "downed": True},
        {"key": "TestMod Worm", "mod": "TestMod", "name": "测试沙虫", "progression": 2.5, "type": "boss", "downed": False,
         "spawnInfo": "在沙漠使用测试诱饵", "spawnItems": ["测试诱饵"], "loot": ["测试宝藏袋", "测试沙刃"]},
        {"key": "Terraria EaterofWorlds", "mod": "Terraria", "name": "世界吞噬怪", "progression": 3.0, "type": "boss", "downed": False}]
    tmp = Path(tempfile.mkdtemp()) / "gamedata.json"
    tmp.write_text(json.dumps({"mods": ["TestMod"], "recipes": ["配方：测试诱饵 ×1；材料：沙块 ×40、腐肉 ×12；制作站：工作台。"],
                               "modItems": [{"name": "测试沙刃", "mod": "TestMod", "tooltip": "发射沙弹", "damage": 18}]},
                              ensure_ascii=False), encoding="utf-8")
    modded["gameData"] = str(tmp)
    check(advisor.next_boss(modded)["name"] == "测试沙虫", "Boss Checklist order puts a modded boss next")
    intent, ents, blocks, prompt = b.build("测试沙虫怎么打", modded, wiki.version)
    check(intent == "boss" and any(t.startswith("Boss Checklist") for t, _, _ in blocks)
          and "腐肉 ×3" in prompt and "沙块 ×40" in prompt, "modded boss: spawn info, game recipe and missing materials")
    intent, ents, blocks, prompt = b.build("测试沙刃有什么用", modded, wiki.version)
    check("发射沙弹" in prompt, "modded item tooltip comes from game data")
    # Replies: program-written points verbatim; model numbers must come from its context.
    from brain import numbers_ok, notable_drops, recipe_reply
    check(numbers_ok("找 5 个生命水晶", "再找 5 个") and not numbers_ok("凑够 120 生命", "再找 5 个"),
          "a number the model invents is caught")
    check(notable_drops("魔矿 (30–90) 100%；猩红矿 (30–90) 100%；克苏鲁之眼面具 14.29%；克苏鲁护盾 100%", "corruption")
          == ["魔矿", "克苏鲁护盾"], "drops skip the other world-evil and cosmetics")
    check("你还差：晶状体 ×6" in recipe_reply([("可疑眼球（配方）", "配方：可疑眼球 ×1；材料：晶状体 ×6；制作站：恶魔祭坛 或 猩红祭坛。")], FRESH),
          "recipe answer lists exact materials and what is missing")
    b.ask_model = lambda user, ids, **_: json.dumps({"intro": "先把生命补上，凑够 120 再说。", "tips": ["带 99 个火把"],
                                                    "basis": "world", "citations": []})
    reply = b.advise("按我当前世界的进度和背包，建议下一步做什么？", FRESH, "1.4.4.9")["reply"]
    check("120" not in reply and "99" not in reply and "生命水晶" in reply, "advice keeps program facts and drops invented numbers")
    if "-v" in sys.argv:
        print(prompt)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
