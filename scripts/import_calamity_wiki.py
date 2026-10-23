"""Download the Calamity Mod wiki into a local index the brain can search (run on your own PC).

    python scripts/import_calamity_wiki.py              # 中文灾厄 Wiki, falls back to calamitymod.wiki.gg
    python scripts/import_calamity_wiki.py --source wikigg
    python scripts/import_calamity_wiki.py --limit 40   # quick trial run

Pages are fetched one at a time through the wiki's public API, politely (one request at a time,
a short pause between them) and cached in .runtime/calamity-cache/: a second run only fetches
pages that changed, and an interrupted run continues where it stopped.

Wiki text is CC BY-NC-SA (the wiki's own contributors). It stays on this computer, for personal
use; every answer that uses it names and links the page it came from.
"""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
from import_wiki import terms  # noqa: E402
from wiki_html import article_passages  # noqa: E402

SOURCES = {
    "huiji": {"api": "https://calamity.huijiwiki.com/api.php", "base": "https://calamity.huijiwiki.com/wiki/"},
    "wikigg-zh": {"api": "https://calamitymod.wiki.gg/zh/api.php", "base": "https://calamitymod.wiki.gg/zh/wiki/"},
    "wikigg": {"api": "https://calamitymod.wiki.gg/api.php", "base": "https://calamitymod.wiki.gg/wiki/"},
}
# A Chinese wiki with only a handful of translated pages is less useful than the full English one.
MIN_PAGES = {"huiji": 300, "wikigg-zh": 300}
# Headings whose sections describe old versions or are navigation, in both wikis' languages.
SKIP_HEADINGS = ("历史", "更新历史", "版本历史", "参考", "参考资料", "导航", "参见", "图库", "画廊",
                 "History", "References", "Navigation", "See also", "Gallery", "Navboxes")
GUIDE_NAMESPACES = {"guide", "guides", "攻略", "指南"}
USER_AGENT = "TerrarAI-offline-index/1.0 (personal offline game companion; one request at a time)"
# Calamity Mod 2.x runs on tModLoader for Terraria 1.4.4; the user's runtime must match.
DEFAULT_GAME_VERSION = "1.4.4.9"


class Api:
    def __init__(self, url, delay=0.6, log=print):
        self.url, self.delay, self.log, self.last = url, delay, log, 0.0

    def get(self, **params):
        params = dict(params, format="json", formatversion="2", maxlag="5")
        url = self.url + "?" + urllib.parse.urlencode(params)
        for attempt in range(6):
            wait = self.delay - (time.time() - self.last)
            if wait > 0:
                time.sleep(wait)
            self.last = time.time()
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept-Encoding": "gzip"})
            try:
                with urllib.request.urlopen(req, timeout=60) as r:
                    raw = r.read()
                    if r.headers.get("Content-Encoding") == "gzip":
                        raw = gzip.decompress(raw)
                data = json.loads(raw)
            except urllib.error.HTTPError as e:
                if e.code in (429, 500, 502, 503, 504) and attempt < 5:
                    pause = float(e.headers.get("Retry-After") or 0) or 5 * (attempt + 1)
                    self.log(f"  服务器忙（{e.code}），{pause:.0f} 秒后重试…")
                    time.sleep(pause)
                    continue
                raise
            except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
                if attempt < 5:
                    self.log(f"  网络问题（{e}），稍后重试…")
                    time.sleep(5 * (attempt + 1))
                    continue
                raise
            if "error" in data:
                if data["error"].get("code") == "maxlag" and attempt < 5:
                    time.sleep(5)
                    continue
                raise RuntimeError(f"{data['error'].get('code')}: {data['error'].get('info')}")
            return data
        raise RuntimeError("too many retries")

    def each(self, **params):
        """Follow MediaWiki 'continue' to the end."""
        cont = {}
        while True:
            data = self.get(**params, **cont)
            yield data
            if "continue" not in data:
                return
            cont = data["continue"]


def site(api):
    data = api.get(action="query", meta="siteinfo", siprop="general|namespaces|rightsinfo")["query"]
    spaces = [0] + [int(ns["id"]) for ns in data["namespaces"].values() if isinstance(ns, dict)
                    and {str(ns.get("name", "")).lower(), str(ns.get("canonical", "")).lower()} & GUIDE_NAMESPACES]
    return data["general"], data.get("rightsinfo", {}), spaces


def page_list(api, spaces):
    """{title: latest revision} for every article, and {redirect: article} for the names pointing at them."""
    pages, redirects = {}, {}
    for ns in spaces:
        for data in api.each(action="query", generator="allpages", gapnamespace=str(ns),
                             gapfilterredir="nonredirects", gaplimit="max", prop="info|redirects",
                             rdprop="title", rdlimit="max"):
            for p in data.get("query", {}).get("pages", []):
                if "lastrevid" in p:
                    pages[p["title"]] = p["lastrevid"]
                for r in p.get("redirects", []):
                    redirects[r["title"]] = p["title"]
    return pages, redirects


def wanted(title):
    # Sub-pages that only hold history or galleries; everything else is kept.
    return not re.search(r"/(历史|更新历史|图库|History|Gallery|Sounds?|音效)$", title)


class Cache:
    def __init__(self, folder):
        self.folder = Path(folder)
        self.folder.mkdir(parents=True, exist_ok=True)

    def path(self, title):
        return self.folder / (hashlib.sha1(title.encode("utf-8")).hexdigest() + ".json.gz")

    def get(self, title):
        p = self.path(title)
        if not p.exists():
            return None
        try:
            return json.loads(gzip.decompress(p.read_bytes()))
        except (OSError, ValueError):
            return None

    def put(self, title, revid, html):
        tmp = self.path(title).with_suffix(".tmp")
        tmp.write_bytes(gzip.compress(json.dumps({"title": title, "revid": revid, "html": html},
                                                 ensure_ascii=False).encode("utf-8")))
        os.replace(tmp, self.path(title))


def fetch_pages(api, cache, pages, log=print):
    todo = [t for t, rev in pages.items() if (c := cache.get(t)) is None or c.get("revid") != rev]
    log(f"共 {len(pages)} 页，需要下载 {len(todo)} 页（其余已在缓存里）。")
    started = time.time()
    for n, title in enumerate(todo, 1):
        try:
            data = api.get(action="parse", page=title, prop="text|revid", disabletoc="1",
                           disableeditsection="1", disablelimitreport="1")["parse"]
        except RuntimeError as e:
            log(f"  跳过 {title}：{e}")
            continue
        text = data["text"] if isinstance(data["text"], str) else data["text"].get("*", "")
        cache.put(title, pages[title], text)
        if n % 25 == 0 or n == len(todo):
            left = (time.time() - started) / n * (len(todo) - n)
            log(f"  {n}/{len(todo)} · 还要约 {left / 60:.0f} 分钟")


def build(output, cache, pages, redirects, meta, base):
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as work:
        tmp = Path(work) / "calamity.db"
        out = sqlite3.connect(tmp)
        try:
            kept, count = _write(out, cache, pages, redirects, meta, base)
        finally:
            out.close()   # Windows cannot move or delete an open database
        if not count:
            raise RuntimeError("没有读到任何页面内容")
        os.replace(tmp, output)
    return kept, count


def _write(out, cache, pages, redirects, meta, base):
    out.executescript("""CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
    CREATE TABLE chunks(id INTEGER PRIMARY KEY,title TEXT NOT NULL,url TEXT NOT NULL,text TEXT NOT NULL,
      game_version TEXT NOT NULL,scope TEXT NOT NULL,modified TEXT NOT NULL);
    CREATE VIRTUAL TABLE search USING fts5(title_terms,body_terms);
    CREATE INDEX version_scope ON chunks(game_version,scope);
    CREATE INDEX chunk_title ON chunks(title,game_version,scope);
    CREATE TABLE redirects(alias TEXT PRIMARY KEY,target TEXT NOT NULL);""")
    count = kept = 0
    for title in sorted(pages):
        c = cache.get(title)
        if not c or not c.get("html"):
            continue
        passages = [p for p in article_passages(c["html"], title, SKIP_HEADINGS) if p.strip()]
        if not passages:
            continue
        kept += 1
        url = base + urllib.parse.quote(title.replace(" ", "_"))
        for chunk in passages:
            count += 1
            out.execute("INSERT INTO chunks VALUES(?,?,?,?,?,?,?)",
                        (count, title, url, chunk, meta["game_version"], "calamity", f"revision {c.get('revid')}"))
            out.execute("INSERT INTO search(rowid,title_terms,body_terms) VALUES(?,?,?)",
                        (count, terms(title), terms(chunk)))
    for alias, target in redirects.items():
        if target in pages:
            out.execute("INSERT OR IGNORE INTO redirects VALUES(?,?)", (alias, target))
    meta = dict(meta, pages=str(kept), chunks=str(count))
    out.executemany("INSERT INTO metadata VALUES(?,?)", meta.items())
    out.commit()
    return kept, count


def run(source, output, cache_dir, limit=None, delay=0.6, game_version=DEFAULT_GAME_VERSION, log=print):
    src = SOURCES[source]
    api = Api(src["api"], delay, log)
    general, rights, spaces = site(api)
    name = general.get("sitename", source)
    log(f"连接到 {name}（{src['api']}），收录命名空间 {spaces}")
    pages, redirects = page_list(api, spaces)
    pages = {t: r for t, r in pages.items() if wanted(t)}
    log(f"条目 {len(pages)} 个，重定向 {len(redirects)} 个")
    if len(pages) < MIN_PAGES.get(source, 1):
        raise RuntimeError(f"只有 {len(pages)} 个条目，内容太少")
    if limit:
        pages = dict(list(sorted(pages.items()))[:limit])
    cache = Cache(Path(cache_dir) / source)
    fetch_pages(api, cache, pages, log)
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    meta = {
        "schema": "1", "scope": "calamity", "mod": "CalamityMod", "game_version": game_version,
        "version_evidence": f"灾厄 Mod 2.x 运行于 tModLoader（Terraria {game_version}）；{name} 于 {today} "
                            f"通过 API 读取的最新修订，按当时灾厄最新版编写",
        "base_url": src["base"], "source": src["api"], "snapshot": today, "site": name,
        "language": general.get("lang", ""),
        "license": rights.get("url") or "https://creativecommons.org/licenses/by-nc-sa/3.0/",
        "license_text": rights.get("text") or "CC BY-NC-SA 3.0",
        "attribution": f"{name} 贡献者",
        "modifications": "HTML 转为纯文本；去掉历史、导航、图库段落；本地分块与全文索引",
    }
    kept, count = build(output, cache, pages, redirects, meta, src["base"])
    log(f"完成：{kept} 页，{count} 段 → {output}")
    return kept, count


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", choices=["auto", *SOURCES], default="auto")
    ap.add_argument("--output", type=Path, default=ROOT / ".runtime/calamity-wiki.db")
    ap.add_argument("--cache", type=Path, default=ROOT / ".runtime/calamity-cache")
    ap.add_argument("--limit", type=int, help="only the first N pages (trial run)")
    ap.add_argument("--delay", type=float, default=0.6, help="seconds between requests")
    ap.add_argument("--game-version", default=DEFAULT_GAME_VERSION)
    a = ap.parse_args()
    order = list(SOURCES) if a.source == "auto" else [a.source]
    for i, source in enumerate(order):
        try:
            run(source, a.output, a.cache, a.limit, a.delay, a.game_version)
            return 0
        except (urllib.error.URLError, RuntimeError, OSError, KeyError, ValueError) as e:
            why = "站点拒绝程序访问（403），不绕过" if getattr(e, "code", None) == 403 else \
                "没有这个站点" if getattr(e, "code", None) == 404 or isinstance(e, ValueError) else str(e)
            print(f"{source} 不可用：{why}", flush=True)
            if i + 1 < len(order):
                print("改用下一个来源…", flush=True)
    return 1


if __name__ == "__main__":
    sys.exit(main())
