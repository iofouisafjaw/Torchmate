"""Generate a synthetic Wiki index for C#/engine tests; never install it for play."""
from contextlib import closing
import json
from pathlib import Path
import sys
from test_wiki_import import fixture, wiki, PROVENANCE

root=Path(sys.argv[1]); root.mkdir(parents=True,exist_ok=True)
(root/'source.db').unlink(missing_ok=True)
source=fixture(root)
wiki.import_wiki(source,root/'wiki.db',PROVENANCE)
wiki.import_wiki(source,root/'unknown.db')
wiki.import_wiki(source,root/'newer.db',dict(PROVENANCE,game_version='1.4.5.8'))

# A matching corpus header must not authorize mismatched/modded rows or external links.
import sqlite3
wiki.import_wiki(source,root/'mixed.db',PROVENANCE)
with closing(sqlite3.connect(root/'mixed.db')) as db, db:
    db.execute("UPDATE chunks SET game_version='1.4.5.8' WHERE title='木平台'")
    db.execute("UPDATE chunks SET scope='calamity' WHERE title='测试药水'")
    db.execute("UPDATE chunks SET url='https://untrusted.invalid/article' WHERE title='工作台'")
