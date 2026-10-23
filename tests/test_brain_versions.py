"""Version isolation at the Brain/model boundary, using only synthetic data."""
from contextlib import closing
import copy
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/brain'))
from brain import Brain
from wiki import Wiki
from test_brain import FRESH
from test_wiki_import import fixture, wiki as importer, PROVENANCE

VERSION = '1.4.4.9'


class BrainVersionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def corpus(self, **metadata):
        source = fixture(self.root)
        path = self.root / 'wiki.db'
        importer.import_wiki(source, path, PROVENANCE)
        with closing(sqlite3.connect(path)) as db, db:
            for key, value in metadata.items():
                db.execute('UPDATE metadata SET value=? WHERE key=?', (value, key))
        wiki = Wiki(path)
        self.addCleanup(wiki.db.close)
        return wiki

    def answer(self, brain, version, question='木平台怎么合成', state=None):
        captured = []

        def model(prompt, ids, **_options):  # points/max_tokens: newer answer modes
            captured.append((prompt, ids))
            return json.dumps(dict(reply='fixture answer', basis='wiki' if ids else 'world', citations=ids[:1]))

        with patch.object(brain, 'ask_model', side_effect=model):
            result = brain.advise(question, state or FRESH, version)
        return result, captured[0]

    def test_matching_version_keeps_provenance(self):
        brain = Brain(self.corpus(), 'http://127.0.0.1:0')
        result, (prompt, ids) = self.answer(brain, 'Terraria v' + VERSION)
        self.assertIn('1 木材', prompt)
        self.assertTrue(ids)
        self.assertEqual(result['game_version'], VERSION)
        self.assertEqual(result['sources'][0]['game_version'], VERSION)
        self.assertEqual(result['evidence'], PROVENANCE['version_evidence'])

    def test_mismatched_runtime_never_reaches_model(self):
        brain = Brain(self.corpus(), 'http://127.0.0.1:0')
        # A previous answer must not reintroduce old Wiki facts after a version switch.
        brain.history[('w1', VERSION)] = [('old', 'OLD_WIKI_MARKER', ['木平台'])]
        result, (prompt, ids) = self.answer(brain, '1.4.5.8')
        self.assertNotIn('1 木材', prompt)
        self.assertNotIn('OLD_WIKI_MARKER', prompt)
        self.assertEqual(ids, [])
        self.assertEqual(result['sources'], [])
        self.assertIn('已隔离', result['status'])
        self.assertEqual(brain.wiki.version, VERSION)
        # A rejected request cannot disable Wiki retrieval for a later compatible request.
        result, _ = self.answer(brain, VERSION)
        self.assertTrue(result['sources'])

    def test_unknown_runtime_never_reaches_model(self):
        brain = Brain(self.corpus(), 'http://127.0.0.1:0')
        for version in ('', 'unknown', '1.4.4.9junk'):
            with self.subTest(version=version):
                result, (prompt, ids) = self.answer(brain, version)
                self.assertNotIn('1 木材', prompt)
                self.assertEqual(ids, [])
                self.assertEqual(result['sources'], [])

    def test_unknown_corpus_version_is_quarantined(self):
        brain = Brain(self.corpus(game_version=''), 'http://127.0.0.1:0')
        result, (_, ids) = self.answer(brain, VERSION)
        self.assertEqual(ids, [])
        self.assertEqual(result['sources'], [])

    def test_missing_evidence_is_quarantined(self):
        brain = Brain(self.corpus(version_evidence=' '), 'http://127.0.0.1:0')
        result, (prompt, ids) = self.answer(brain, VERSION)
        self.assertNotIn('1 木材', prompt)
        self.assertEqual(ids, [])
        self.assertEqual(result['sources'], [])

    def test_wrong_scope_or_schema_is_quarantined(self):
        wiki = self.corpus()
        brain = Brain(wiki, 'http://127.0.0.1:0')
        for field, value in (('scope', 'modded'), ('schema', '2')):
            with self.subTest(field=field), patch.object(wiki, field, value):
                self.assertIsNone(brain.compatible_wiki(VERSION))

    def test_mixed_rows_cannot_bypass_matching_header(self):
        wiki = self.corpus()
        with closing(sqlite3.connect(wiki.path)) as db, db:
            db.execute("UPDATE chunks SET game_version='1.4.5.8' WHERE title='木平台'")
            db.execute("UPDATE chunks SET scope='modded' WHERE title='测试药水'")
            db.execute("UPDATE chunks SET url='https://untrusted.invalid/' WHERE title='工作台'")
        mixed = Wiki(wiki.path)
        self.addCleanup(mixed.db.close)
        self.assertEqual(mixed.titles, set())
        self.assertEqual(mixed.page('木平台'), '')
        self.assertEqual(mixed.recipes_for('木平台'), [])
        self.assertEqual(mixed.recipes_using('木材'), [])
        self.assertEqual(mixed.search('木平台 工作台 测试药水'), [])

    def test_game_data_recipe_survives_wiki_quarantine(self):
        brain = Brain(self.corpus(game_version='1.4.5.8'), 'http://127.0.0.1:0')
        path = self.root / 'gamedata.json'
        path.write_text(json.dumps({'mods': [], 'modItems': [], 'recipes': [
            '配方：木平台 ×2；材料：木材 ×1；制作站：徒手。']}, ensure_ascii=False), encoding='utf-8')
        state = copy.deepcopy(FRESH)
        state['gameData'] = str(path)
        result, (prompt, _) = self.answer(brain, VERSION, state=state)
        self.assertIn('配方：木平台 ×2', prompt)
        self.assertEqual(result['sources'][0]['url'], '')
        self.assertEqual(result['sources'][0]['game_version'], VERSION)
        self.assertEqual(result['game_version'], '')

    def test_invalid_wiki_basis_is_not_relabelled_as_world(self):
        brain = Brain(None, 'http://127.0.0.1:0')
        with patch.object(brain, 'ask_model', return_value='{"reply":"invented recipe","basis":"wiki","citations":[]}'):
            with self.assertRaises(ValueError):
                brain.advise('木平台怎么合成', FRESH, VERSION)


if __name__ == '__main__':
    unittest.main()
