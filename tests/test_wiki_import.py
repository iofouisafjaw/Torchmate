from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts'))
spec = importlib.util.spec_from_file_location('wiki_import', ROOT/'scripts/import_wiki.py')
wiki = importlib.util.module_from_spec(spec); spec.loader.exec_module(wiki)
PROVENANCE = dict(game_version='1.4.4.9',version_evidence='Synthetic fixture; NOT real Wiki content',snapshot='test-only')

def fixture(folder):
    path=Path(folder)/'source.db'
    with closing(sqlite3.connect(path)) as db, db:
        db.executescript('CREATE TABLE WikiPage(Title TEXT PRIMARY KEY,Content TEXT,LastModified TEXT); CREATE TABLE WikiRedirect(FromName TEXT,ToTarget TEXT);')
        db.executemany('INSERT INTO WikiPage VALUES(?,?,?)',[
            ('木平台','<h2>测试配方</h2><p>测试词条：1 木材可以徒手制作 2 木平台。</p><script>remove me</script>','fixture'),
            ('测试药水','<p>这是合成用的测试词条，与木平台无关。</p>','fixture'),
            ('工作台','<p>测试工作台使用 10 木材制作。</p>','fixture')])
        db.execute('INSERT INTO WikiRedirect VALUES(?,?)',('木制平台','木平台'))
    return path

def package(path, data, **overrides):
    f=dict(RelativePath='data.db',Size=len(data),MD5=hashlib.md5(data).hexdigest()); f.update(overrides)
    meta=json.dumps(dict(Id=1,Title='Terraria Wiki',AppVersion='1.4.4.9',Files=[f])).encode()
    path.write_bytes(b'WIKIDATA'+struct.pack('<i',len(meta))+meta+data)

class WikiImportTests(unittest.TestCase):
    def setUp(self): self.tmp=tempfile.TemporaryDirectory(); self.root=Path(self.tmp.name)
    def tearDown(self): self.tmp.cleanup()
    def test_package_integrity_and_provenance(self):
        source=fixture(self.root); p=self.root/'wiki.pkg'; package(p,source.read_bytes())
        output=self.root/'index.db'; result=wiki.import_wiki(p,output,PROVENANCE)
        self.assertEqual(result['pages'],3)
        with closing(sqlite3.connect(output)) as db, db:
            text=db.execute("SELECT text FROM chunks WHERE title='木平台'").fetchone()[0]
            self.assertIn('1 木材',text); self.assertNotIn('remove me',text)
            self.assertEqual(db.execute("SELECT value FROM metadata WHERE key='game_version'").fetchone()[0],'1.4.4.9')
            self.assertIn('%E6%9C%A8',db.execute("SELECT url FROM chunks WHERE title='木平台'").fetchone()[0])
            self.assertEqual(db.execute("SELECT target FROM redirects").fetchone()[0],'木平台')
            self.assertEqual(db.execute("SELECT count(*) FROM search WHERE search MATCH ?",('"木平"',)).fetchone()[0],2)
    def test_app_version_never_becomes_game_version(self):
        source=fixture(self.root); p=self.root/'wiki.pak'; package(p,source.read_bytes())
        wiki.import_wiki(p,self.root/'index.db')
        with closing(sqlite3.connect(self.root/'index.db')) as db, db:
            self.assertEqual(db.execute("SELECT value FROM metadata WHERE key='game_version'").fetchone()[0],'')
    def test_unsafe_packages_preserve_existing_index(self):
        source=fixture(self.root); data=source.read_bytes(); output=self.root/'index.db'; output.write_bytes(b'keep existing')
        for patch in [dict(RelativePath='../data.db'),dict(RelativePath='/data.db'),dict(RelativePath='C:\\data.db'),dict(Size=-1),dict(Size=len(data)+1),dict(MD5='0'*32)]:
            with self.subTest(patch=patch):
                p=self.root/'bad.pkg'; package(p,data,**patch)
                with self.assertRaises(ValueError): wiki.import_wiki(p,output,PROVENANCE)
                self.assertEqual(output.read_bytes(),b'keep existing')
        p=self.root/'truncated.pkg'; p.write_bytes(b'WIKIDATA\x10')
        with self.assertRaises(ValueError): wiki.import_wiki(p,output)
    def test_mod_wiki_and_false_provenance_rejected(self):
        source=fixture(self.root)
        for provenance in [dict(game_version='1.4.4.9'),dict(game_version='latest',version_evidence='guess'),dict(base_url='https://calamitymod.wiki.gg/wiki/')]:
            with self.assertRaises(ValueError): wiki.import_wiki(source,self.root/'index.db',provenance)
    def test_legacy_page_table_and_scripts(self):
        source=self.root/'legacy.db'
        with closing(sqlite3.connect(source)) as db, db:
            db.execute('CREATE TABLE Page(Title TEXT,Content TEXT)'); db.execute("INSERT INTO Page VALUES('Legacy','<style>bad</style><p>legacy article<img alt=前代主机版></p>')")
        result=wiki.import_wiki(source,self.root/'index.db',PROVENANCE)
        self.assertEqual(result['pages'],1)
        with closing(sqlite3.connect(self.root/'index.db')) as db, db:
            self.assertIn('前代主机版', db.execute('SELECT text FROM chunks').fetchone()[0])
    def test_actual_camel_case_package_metadata(self):
        source=fixture(self.root); data=source.read_bytes(); path=self.root/'v030.pkg'
        meta=json.dumps(dict(id=1,title='Terraria Wiki',files=[dict(relativePath='Terraria_Wiki.db',size=len(data),mD5=hashlib.md5(data).hexdigest())])).encode()
        path.write_bytes(b'WIKIDATA'+struct.pack('<i',len(meta))+meta+data)
        self.assertEqual(wiki.import_wiki(path,self.root/'index.db',PROVENANCE)['pages'],3)
        with self.assertRaises(ValueError):
            wiki.package_object([('Files',[]),('files',[])])
    def test_self_overwrite_rejected(self):
        source=fixture(self.root)
        with self.assertRaises(ValueError): wiki.import_wiki(source,source,PROVENANCE)

if __name__=='__main__': unittest.main()
