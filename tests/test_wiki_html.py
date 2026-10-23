from contextlib import closing
from pathlib import Path
import json
import sqlite3
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from wiki_html import article_passages
from import_wiki import import_wiki

HTML='''<title>not article</title><div class="mw-parser-output">not article</div><div id="mw-content-text"><div class="mw-parser-output">
<div id="toc">not article</div><p>平台可以向下穿过。</p><h2>制作<span class="mw-editsection">编辑</span></h2>
<table class="terraria recipes"><tr><th>产物</th><th>材料</th><th>制作站</th></tr>
<tr><td class="result"><span>灰烬木平台</span><span class="id">内部物品ID：5204</span><span class="am">2</span></td><td class="ingredients"><ul><li>灰烬木</li></ul></td><td class="station" rowspan="3">徒手</td></tr>
<tr><td class="result"><div class="version-note">仅限 <img alt="任天堂3DS版">：</div>遗留平台</td><td class="ingredients"><ul><li>骨头</li></ul></td></tr>
<tr><td class="result"><img alt="木平台"><span>木平台</span><span class="id">内部物品ID：94</span><span class="am">2</span></td><td class="ingredients"><ul><li><img alt="木材"><span>木材</span></li></ul></td></tr></table>
<h2>历史</h2><p>旧版现在使用666木材。</p></div></div>'''

class WikiHtmlTests(unittest.TestCase):
    def test_preserves_quantities_and_inherited_station_after_legacy_row(self):
        parts=list(article_passages(HTML,'平台'))
        self.assertIn('配方：木平台 ×2；材料：木材 ×1；制作站：徒手。',parts)
        text='\n'.join(parts)
        for noise in ['not article','666','5204','内部物品ID','遗留平台','编辑']:
            self.assertNotIn(noise,text)
        self.assertIn('平台可以向下穿过',text)
    def test_recipe_material_quantities_and_result_rowspan(self):
        s='''<table class="recipes"><tr><td class="result" rowspan="2">篝火</td><td class="ingredients"><ul><li>木材<span class="am">10</span></li><li>火把<span class="am">5</span></li></ul></td><td class="station" rowspan="2">徒手</td></tr><tr><td class="ingredients"><ul><li>灰烬木<span class="am">10</span></li><li>火把<span class="am">5</span></li></ul></td></tr></table>'''
        parts=list(article_passages(s,'篝火'))
        self.assertEqual(len(parts),2)
        self.assertIn('配方：篝火 ×1；材料：木材 ×10、火把 ×5；制作站：徒手。',parts)
    def test_recipe_catalog_caption_is_the_shared_station(self):
        html='<table class="recipes"><caption>工作台 <b>和</b> 椅子</caption><tr><td class="result">护目镜</td><td class="ingredients"><ul><li>晶状体<span class="am">2</span></li></ul></td></tr></table>'
        self.assertEqual(list(article_passages(html,'配方/工作台')),[
            '配方：护目镜 ×1；材料：晶状体 ×2；制作站：工作台 和 椅子。'])
    def test_legacy_directory_import_and_redirect_anchor(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);pages=root/'assets/pages';pages.mkdir(parents=True)
            (pages/'平台.html').write_text(HTML, encoding='utf-8')
            (pages/'即将推出的新功能.html').write_text('<p>未来假配方</p>', encoding='utf-8')
            (pages.parent/'redirect.json').write_text(json.dumps({'木平台':'平台#制作'}), encoding='utf-8')
            path=root/'out.db'
            result=import_wiki(pages.parent,path,{'game_version':'1.4.4.9','version_evidence':'test fixture'})
            self.assertEqual(result['pages'],1)
            with closing(sqlite3.connect(path)) as db, db:
                self.assertEqual(db.execute('SELECT target FROM redirects').fetchone()[0],'平台')
                self.assertEqual(db.execute("SELECT count(*) FROM chunks WHERE text LIKE '配方：木平台 ×2%'").fetchone()[0],1)
    def test_symlink_outside_directory_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);(root/'pages').mkdir();(root/'outside.html').write_text('<p>outside</p>', encoding='utf-8')
            (root/'pages/link.html').symlink_to(root/'outside.html')
            with self.assertRaises(ValueError): import_wiki(root/'pages',root/'out.db')
if __name__=='__main__': unittest.main()
