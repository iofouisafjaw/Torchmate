"""Calamity wiki index: built from cached pages, read only in worlds that load Calamity (synthetic data)."""
from contextlib import closing
import copy
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
sys.path.insert(0, str(ROOT / "scripts/brain"))
import import_calamity_wiki as cal  # noqa: E402
from brain import Brain  # noqa: E402
from wiki import Wiki  # noqa: E402
from test_brain import MID  # noqa: E402

VERSION = "1.4.4.9"
PAGES = {
    "测试灾虫": '<div class="mw-parser-output"><p>测试灾虫是困难模式前在沙漠出现的蠕虫首领，会钻地冲撞玩家。</p>'
            '<h2>攻略</h2><p>在沙漠上空搭一条长平台，绕圈躲开冲撞，用穿透武器打它的体节。</p>'
            '<h2>历史</h2><p>旧版本里它的血量更高，这段不应被检索到。</p></div>',
    "测试沙刃": '<div class="mw-parser-output"><p>测试沙刃是一把近战武器，挥动时会射出旋转的沙刃。</p></div>',
}


class CalamityWikiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        cache = cal.Cache(root / "cache")
        for i, (title, html) in enumerate(PAGES.items(), 1):
            cache.put(title, i, html)
        self.db = root / "calamity.db"
        meta = {"schema": "1", "scope": "calamity", "game_version": VERSION, "version_evidence": "测试证据",
                "base_url": cal.SOURCES["huiji"]["base"], "snapshot": "2026-10-17", "license": "CC BY-NC-SA 3.0",
                "attribution": "测试维基 贡献者"}
        cal.build(self.db, cache, {t: i for i, t in enumerate(PAGES, 1)}, {"灾虫": "测试灾虫"}, meta,
                  cal.SOURCES["huiji"]["base"])
        self.mw = Wiki(self.db, kind="calamity")
        self.addCleanup(self.mw.db.close)

    def state(self, calamity=True):
        s = copy.deepcopy(MID)
        s["progress"]["mods"] = ["CalamityMod"] if calamity else []
        s["progress"]["checklist"] = [
            {"key": "CalamityMod TestWorm", "mod": "CalamityMod", "name": "测试灾虫", "progression": 2.5,
             "type": "boss", "downed": False, "spawnInfo": "在沙漠使用测试诱饵", "spawnItems": [], "loot": []}]
        return s

    def test_index_keeps_article_and_drops_history(self):
        self.assertEqual(self.mw.titles, set(PAGES))
        self.assertEqual(self.mw.resolve("灾虫"), "测试灾虫")
        page = self.mw.page("测试灾虫")
        self.assertIn("长平台", page)
        self.assertNotIn("旧版本", page)
        self.assertTrue(self.mw.url("测试灾虫").startswith("https://calamity.huijiwiki.com/wiki/"))

    def test_foreign_host_rows_are_never_read(self):
        # closing(): on Windows an open handle would block deleting the temp folder.
        with closing(sqlite3.connect(self.db)) as db, db:
            db.execute("UPDATE chunks SET url='https://untrusted.invalid/x' WHERE title='测试沙刃'")
        mixed = Wiki(self.db, kind="calamity")
        self.addCleanup(mixed.db.close)
        self.assertNotIn("测试沙刃", mixed.titles)
        # A Calamity index is not a vanilla index.
        vanilla = Wiki(self.db)
        self.addCleanup(vanilla.db.close)
        self.assertEqual(vanilla.titles, set())

    def ask(self, brain, question, state):
        captured = []

        def model(prompt, ids, **_):
            captured.append(prompt)
            return json.dumps({"reply": "好", "basis": "wiki" if ids else "world", "citations": ids[-1:], "intro": "好", "tips": []})

        with patch.object(brain, "ask_model", side_effect=model):
            result = brain.advise(question, state, VERSION)
        return result, captured[0]

    def test_calamity_pages_reach_the_model_with_provenance(self):
        brain = Brain(None, "http://127.0.0.1:0", modwiki=self.mw)
        result, prompt = self.ask(brain, "测试沙刃有什么用", self.state())
        self.assertIn("旋转的沙刃", prompt)
        self.assertEqual(result["sources"][0]["game_version"], VERSION)
        self.assertTrue(result["sources"][0]["url"].startswith("https://calamity.huijiwiki.com/wiki/"))
        self.assertEqual(result["game_version"], VERSION)
        self.assertEqual(result["evidence"], "测试证据")
        self.assertIn("测试维基", result["attribution"])

    def test_boss_strategy_comes_from_calamity_page(self):
        brain = Brain(None, "http://127.0.0.1:0", modwiki=self.mw)
        _, prompt = self.ask(brain, "测试灾虫怎么打", self.state())
        self.assertIn("长平台", prompt)

    def test_worlds_without_calamity_never_see_it(self):
        brain = Brain(None, "http://127.0.0.1:0", modwiki=self.mw)
        result, prompt = self.ask(brain, "测试沙刃有什么用", self.state(calamity=False))
        self.assertNotIn("旋转的沙刃", prompt)
        self.assertEqual(result["sources"], [])

    def test_version_mismatch_is_quarantined(self):
        brain = Brain(None, "http://127.0.0.1:0", modwiki=self.mw)
        self.assertIsNone(brain.compatible_modwiki("1.4.5.0", self.state()))


class EnglishCalamityWikiTests(unittest.TestCase):
    """The official wiki is English; the game shows Chinese names. Internal names bridge the two."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        cache = cal.Cache(root / "cache")
        cache.put("Test Scourge", 1, '<div class="mw-parser-output"><p>The Test Scourge is a worm boss fought in the desert.</p>'
                  '<h2>Tips</h2><p>Build a long skybridge above the desert and circle around it to dodge the charges.</p></div>')
        cache.put("Sand Blade", 2, '<div class="mw-parser-output"><p>The Sand Blade is a melee weapon that fires spinning blades.</p></div>')
        base = cal.SOURCES["wikigg"]["base"]
        meta = {"schema": "1", "scope": "calamity", "game_version": VERSION, "version_evidence": "test",
                "base_url": base, "snapshot": "2026-10-17", "license": "CC BY-NC-SA 3.0", "attribution": "wiki",
                "language": "en"}
        db = root / "calamity.db"
        cal.build(db, cache, {"Test Scourge": 1, "Sand Blade": 2}, {}, meta, base)
        self.mw = Wiki(db, kind="calamity")
        self.addCleanup(self.mw.db.close)
        game = root / "gamedata.json"
        game.write_text(json.dumps({"mods": ["CalamityMod"], "recipes": [], "modItems": [
            {"name": "测试沙刃", "key": "SandBlade", "mod": "CalamityMod", "tooltip": "", "damage": 20}]},
            ensure_ascii=False), encoding="utf-8")
        self.state = copy.deepcopy(MID)
        self.state["gameData"] = str(game)
        self.state["progress"]["mods"] = ["CalamityMod"]
        self.state["progress"]["checklist"] = [
            {"key": "CalamityMod TestScourgeHead", "mod": "CalamityMod", "name": "测试灾虫", "progression": 2.5,
             "type": "boss", "downed": False, "spawnInfo": "", "spawnItems": [], "loot": []}]

    def prompt(self, question):
        brain = Brain(None, "http://127.0.0.1:0", modwiki=self.mw)
        return brain.build(question, self.state, VERSION)[3]

    def test_internal_names_find_english_pages(self):
        self.assertEqual(self.mw.resolve_key("TestScourgeHead"), "Test Scourge")
        boss = self.prompt("测试灾虫怎么打")
        self.assertIn("skybridge", boss)
        self.assertIn("翻成中文", boss)
        self.assertIn("spinning blades", self.prompt("测试沙刃有什么用"))


if __name__ == "__main__":
    unittest.main()
