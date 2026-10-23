"""Regression tests for condition association and numeric rejection; synthetic sources only."""
from contextlib import closing
import copy
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/brain'))
from brain import Brain, drop_unsupported_numbers, numbers_ok, safe_model_note
from wiki import Wiki
from test_brain import FRESH
from test_wiki_import import fixture, wiki as importer, PROVENANCE


class GroundingTests(unittest.TestCase):
    def corpus(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        source = fixture(root)
        with closing(sqlite3.connect(source)) as db, db:
            db.execute('INSERT INTO WikiPage VALUES(?,?,?)', ('克苏鲁之眼',
                '<h2>召唤和生成</h2><p>测试手动规则：在夜晚使用测试眼球，角色无需达到自然生成门槛。</p>'
                '<p>测试自然生成规则：只有最大生命达到 222、防御大于 11、并有 5 个城镇 NPC 才会自然生成。</p>'
                '<h2>战斗建议</h2><p>测试建议：准备平台和药水；这些建议不属于生成条件。</p>', 'fixture'))
        path = root / 'wiki.db'
        importer.import_wiki(source, path, PROVENANCE)
        wiki = Wiki(path)
        self.addCleanup(wiki.db.close)
        return Brain(wiki, 'http://127.0.0.1:0')

    def test_summon_conditions_preserve_source_groups_without_model_rewrite(self):
        brain = self.corpus()
        with patch.object(brain, 'ask_model', side_effect=AssertionError('must not rewrite condition groups')):
            for question in ('克眼的召唤条件是什么', '如何召唤克眼', '克眼需要多少血防才能召唤', '克眼怎么自然生成'):
                result = brain.advise(question, FRESH, '1.4.4.9')
                self.assertEqual(result['intent'], 'summon')
                self.assertIn('测试手动规则：在夜晚使用测试眼球', result['reply'])
                self.assertIn('测试自然生成规则：只有最大生命达到 222', result['reply'])
                self.assertNotIn('战斗建议', result['reply'])
                self.assertEqual(len(result['sources']), 1)
                self.assertIn('terraria.wiki.gg/', result['sources'][0]['url'])
                self.assertEqual(result['game_version'], '1.4.4.9')

    def test_summon_without_compatible_source_is_insufficient(self):
        brain = self.corpus()
        with patch.object(brain, 'ask_model', side_effect=AssertionError('must fail closed without source')):
            for version in ('1.4.5.8', ''):
                result = brain.advise('克眼的召唤条件是什么', FRESH, version)
                self.assertEqual(result['basis'], 'insufficient')
                self.assertEqual(result['sources'], [])
                self.assertNotIn('222', result['reply'])

    def test_unknown_boss_section_never_falls_back_to_unrelated_search(self):
        brain = self.corpus()
        with patch.object(brain, 'ask_model', side_effect=AssertionError('no invented conditions')):
            result = brain.advise('史莱姆王的召唤条件是什么', FRESH, '1.4.4.9')
        self.assertEqual(result['basis'], 'insufficient')
        self.assertEqual(result['sources'], [])

    def test_modded_conditions_can_use_live_checklist_without_vanilla_substitution(self):
        brain = self.corpus()
        state = copy.deepcopy(FRESH)
        state['progress']['checklist'] = [dict(name='测试沙虫', mod='FixtureMod', type='boss', progression=0.5,
            spawnInfo='测试规则：站在测试沙块上使用测试号角。', spawnItems=['测试号角'])]
        with patch.object(brain, 'ask_model', side_effect=AssertionError('must preserve live conditions')):
            result = brain.advise('测试沙虫的召唤条件是什么', state, '1.4.4.9')
        self.assertIn('站在测试沙块上使用测试号角', result['reply'])
        self.assertNotIn('222', result['reply'])
        self.assertIn('Boss Checklist', result['sources'][0]['title'])

    def test_model_notes_cannot_combine_numbers_into_new_conditions(self):
        blocks = [('fixture', '手动规则：夜晚用测试眼球。自然规则：222 最大生命、11 防御。', '')]
        mixed = '手动召唤需要222最大生命和11防御'
        self.assertTrue(numbers_ok(mixed, blocks[0][1]))
        self.assertEqual(safe_model_note(mixed, blocks[0][1], blocks), '')
        self.assertEqual(safe_model_note('手动规则：夜晚用测试眼球。', blocks[0][1], blocks), '手动规则：夜晚用测试眼球。')

    def test_numeric_guard_never_resurrects_rejected_sentences(self):
        self.assertEqual(drop_unsupported_numbers('需要999生命。', '已有100生命。'), '')
        self.assertEqual(drop_unsupported_numbers('需要999生命。先准备平台。', '已有100生命。'), '先准备平台。')
        self.assertFalse(numbers_ok('需要10防御', '100生命，210防御'))
        self.assertFalse(numbers_ok('概率33', '概率33.33'))
        self.assertTrue(numbers_ok('有100生命', '当前生命100，其他数据。'))


if __name__ == '__main__':
    unittest.main()
