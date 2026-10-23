"""Import Wiki Offline WIKIDATA packages / SQLite into a local read-only RAG index.
No network calls. Wiki data is separate from the mod and retains its own license.
"""
import argparse
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sqlite3
import struct
import tempfile
from urllib.parse import quote
from datetime import datetime, timezone
try:
    from .wiki_html import article_passages
except ImportError:
    from wiki_html import article_passages

LICENSE = 'https://creativecommons.org/licenses/by-nc-sa/4.0/'

def terms(text):
    text = text.lower()
    words = re.findall(r'[a-z0-9_]+', text)
    for run in re.findall(r'[\u3400-\u9fff]+', text):
        words.extend(run[i:i+2] for i in range(len(run)-1))
        if len(run) == 1: words.append(run)
    return ' '.join(words)

def package_object(pairs):
    # Releases use both PascalCase and camelCase (including mD5). Reject ambiguous keys.
    names={name.casefold():name for name in ('Id','Title','Files','RelativePath','Size','MD5','UpdateTime','AppVersion','IsPageDownloaded','IsResourceDownloaded')}
    seen=set(); result={}
    for key,value in pairs:
        folded=key.casefold()
        if folded in seen: raise ValueError('Ambiguous duplicate package metadata key')
        seen.add(folded); result[names.get(folded,key)]=value
    return result

def extract_package(source, destination):
    """Read exact WIKIDATA framing; never extract arbitrary paths or media."""
    with open(source, 'rb') as stream:
        if stream.read(8) != b'WIKIDATA': raise ValueError('Unsupported package header (expected WIKIDATA)')
        raw = stream.read(4)
        if len(raw) != 4: raise ValueError('Truncated header')
        size, = struct.unpack('<i', raw)
        if not 2 <= size <= 8*1024*1024: raise ValueError('Invalid metadata size')
        meta = json.loads(stream.read(size),object_pairs_hook=package_object)
        files = meta.get('Files', [])
        if not files or len(files) > 100000: raise ValueError('Invalid file list')
        seen, selected, total = set(), [], 0
        for file in files:
            name = file['RelativePath'].replace('\\', '/')
            path = PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts or ':' in name or name.casefold() in seen:
                raise ValueError('Unsafe/duplicate package path')
            seen.add(name.casefold())
            length = file['Size']
            if type(length) != int or not 0 <= length <= 8*1024**3: raise ValueError('Invalid file size')
            if not re.fullmatch('[0-9a-fA-F]{32}', file['MD5']): raise ValueError('Missing file hash')
            total += length
            if path.name.lower().endswith('.db'): selected.append(name)
        if len(selected) != 1: raise ValueError('Expected exactly one content database')
        if stream.tell()+total != os.fstat(stream.fileno()).st_size: raise ValueError('Truncated/trailing package data')
        for file in files:
            name = file['RelativePath'].replace('\\', '/')
            digest, left = hashlib.md5(), file['Size']
            target = open(destination, 'wb') if name == selected[0] else None
            try:
                while left:
                    block = stream.read(min(left, 1024*1024))
                    if not block: raise ValueError('Truncated package member')
                    digest.update(block); left -= len(block)
                    if target: target.write(block)
            finally:
                if target: target.close()
            if digest.hexdigest().lower() != file['MD5'].lower(): raise ValueError('Package member checksum mismatch')
        return meta

def import_wiki(source, output, provenance=None):
    source, output = Path(source).resolve(), Path(output).resolve()
    if source == output: raise ValueError('Output must differ from source')
    provenance = dict(provenance or {})
    # A package AppVersion/UpdateTime is not a Terraria version. Unknown data stays quarantined.
    version = provenance.get('game_version', '')
    evidence = provenance.get('version_evidence', '').strip()
    if version and (not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', version) or not evidence):
        raise ValueError('A game version requires explicit version_evidence')
    base = provenance.get('base_url', 'https://terraria.wiki.gg/zh/wiki/')
    if base not in ('https://terraria.wiki.gg/zh/wiki/', 'https://terraria.wiki.gg/wiki/'):
        raise ValueError('Only vanilla official Wiki sources are supported')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='terra-wiki-', dir=output.parent) as work, ExitStack() as resources:
        db = source
        package = {}
        if source.is_dir(): magic = b''
        else:
            with source.open('rb') as f: magic = f.read(8)
        if magic == b'WIKIDATA':
            db = Path(work)/'input.db'; package = extract_package(source, db)
            if package.get('Id') not in (None, 1, 3): raise ValueError('This package is not a vanilla Terraria Wiki')
        if package.get('Id') == 3 and 'base_url' not in provenance:
            base = 'https://terraria.wiki.gg/wiki/'
        if package.get('Id') == 3 and '/zh/' in base: raise ValueError('English package cannot use Chinese source URLs')
        src = None
        if source.is_dir():
            pages_dir = source/'pages' if (source/'pages').is_dir() else source
            if not any(pages_dir.glob('*.html')): raise ValueError('Expected a legacy Wiki pages directory or wiki-assets directory')
            rows = []
            digest = hashlib.sha256()
            for page in sorted(pages_dir.rglob('*.html')):
                if page.is_symlink() or not page.resolve().is_relative_to(pages_dir.resolve()): raise ValueError('Unsafe HTML path')
                title = page.relative_to(pages_dir).with_suffix('').as_posix()
                if title.startswith(('即将推出的新功能','Template:','模板:','MediaWiki:')): continue
                data = page.read_bytes()
                if len(data) > 16*1024*1024: raise ValueError('HTML page too large')
                digest.update(title.encode()); digest.update(data)
                rows.append((title,page,datetime.fromtimestamp(page.stat().st_mtime,timezone.utc).isoformat()+' (archive file time, not Wiki revision)'))
            redirect_path = pages_dir.parent/'redirect.json'
            if redirect_path.is_file():
                redirects = json.loads(redirect_path.read_text(encoding='utf-8-sig')); digest.update(redirect_path.read_bytes())
                if not isinstance(redirects,dict): raise ValueError('Unsupported legacy redirects')
            else: redirects = {}
            source_hash = digest.hexdigest()
            page_rows = ((title,page.read_text(encoding='utf-8-sig'),modified) for title,page,modified in rows)
        else:
            src = sqlite3.connect(db.as_uri()+'?mode=ro', uri=True)
            resources.callback(src.close)
            src.execute('PRAGMA trusted_schema=OFF')
            tables = {r[0] for r in src.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            table = next((t for t in ('WikiPage', 'Page') if t in tables), None)
            if table is None: raise ValueError('No WikiPage/Page table found')
            cols = {r[1] for r in src.execute('PRAGMA table_info('+table+')')}
            if not {'Title', 'Content'} <= cols: raise ValueError('Unsupported Wiki page schema')
            page_rows = src.execute('SELECT Title,Content,'+('LastModified' if 'LastModified' in cols else "''")+' FROM '+table)
            with source.open('rb') as raw_source: source_hash = hashlib.file_digest(raw_source,'sha256').hexdigest()
            redirects = {}
            for redirect_table in ('WikiRedirect','Redirect'):
                if redirect_table not in tables: continue
                c = {r[1] for r in src.execute('PRAGMA table_info('+redirect_table+')')}
                if {'FromName','ToTarget'} <= c:
                    redirects.update(src.execute('SELECT FromName,ToTarget FROM '+redirect_table))
        tmp = Path(work)/'wiki.db'
        out = sqlite3.connect(tmp)
        resources.callback(out.close)
        out.executescript('''CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE chunks(id INTEGER PRIMARY KEY,title TEXT NOT NULL,url TEXT NOT NULL,text TEXT NOT NULL,
          game_version TEXT NOT NULL,scope TEXT NOT NULL,modified TEXT NOT NULL);
        CREATE VIRTUAL TABLE search USING fts5(title_terms,body_terms);
        CREATE INDEX version_scope ON chunks(game_version,scope);
        CREATE INDEX chunk_title ON chunks(title,game_version,scope);
        CREATE TABLE redirects(alias TEXT PRIMARY KEY,target TEXT NOT NULL);''')
        metadata = dict(schema='1', game_version=version, version_evidence=evidence,
            scope='vanilla', base_url=base, license=LICENSE, attribution='Terraria Wiki contributors',
            snapshot=provenance.get('snapshot','unknown'), source_sha256=source_hash,
            package=json.dumps(package,ensure_ascii=False), modifications='HTML converted to plain text; recipe spans resolved and implicit count 1 made explicit; historical sections and future-content pages excluded; local chunks/index')
        count, pages = 0, 0
        for title, content, modified in page_rows:
            if not title or not content or len(title)>300: continue
            passages = list(article_passages(content,title))
            if not passages: continue
            pages += 1
            for chunk in passages:
                count += 1
                out.execute('INSERT INTO chunks VALUES(?,?,?,?,?,?,?)', (count,title,base+quote(title.replace(' ','_')),chunk,version,'vanilla',str(modified or '')))
                out.execute('INSERT INTO search(rowid,title_terms,body_terms) VALUES(?,?,?)',(count,terms(title),terms(chunk)))
        for alias,target in redirects.items():
            if not isinstance(alias,str) or not isinstance(target,str): continue
            # Anchors label sections rather than separate pages. Normalize them for retrieval.
            out.execute('INSERT OR IGNORE INTO redirects VALUES(?,?)',(alias,target.split('#',1)[0]))
        metadata.update(pages=str(pages), chunks=str(count))
        out.executemany('INSERT INTO metadata VALUES(?,?)',metadata.items())
        out.commit(); out.close();
        if src: src.close()
        if not count: raise ValueError('No readable Wiki content')
        os.replace(tmp,output)
        return dict(pages=pages,chunks=count,game_version=version or 'unknown (quarantined)',output=str(output))

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('source',type=Path); p.add_argument('--output',type=Path,required=True)
    p.add_argument('--provenance',type=Path,help='JSON: base_url, game_version, version_evidence, snapshot; do not guess version')
    a=p.parse_args()
    print(json.dumps(import_wiki(a.source,a.output,json.loads(a.provenance.read_text(encoding='utf-8-sig')) if a.provenance else None),ensure_ascii=False,indent=2))
if __name__=='__main__': main()
