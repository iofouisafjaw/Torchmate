"""Read-only access to the offline Chinese Terraria Wiki index (built by scripts/import_wiki.py).

Python's sqlite3 ships with FTS5, which the Windows system SQLite used by the mod lacks, so the
full-text search lives here. Pages are stored as overlapping 640-character windows; `page()`
stitches them back together and `sections()` splits a page at its headings.
"""
from functools import lru_cache
import re
import sqlite3
from urllib.parse import quote

BASE_URL = "https://terraria.wiki.gg/zh/wiki/"
# Where each kind of index may point. A row outside its scope's sites is never read.
SITES = {
    "vanilla": ("https://terraria.wiki.gg/wiki/", "https://terraria.wiki.gg/zh/wiki/"),
    "calamity": ("https://calamity.huijiwiki.com/wiki/", "https://calamitymod.wiki.gg/wiki/",
                 "https://calamitymod.wiki.gg/zh/wiki/"),
}
_HEADING_BAD = re.compile(r"[，。：:|（）()、“”‘’,.!！?？;；0-9%×/]")


def terms(text):
    """Same tokenisation as the index: latin words plus overlapping CJK bigrams."""
    text = text.lower()
    words = re.findall(r"[a-z0-9_]+", text)
    for run in re.findall(r"[㐀-鿿]+", text):
        words.extend(run[i:i + 2] for i in range(len(run) - 1))
        if len(run) == 1:
            words.append(run)
    return words


class Wiki:
    def __init__(self, path, kind="vanilla"):
        self.path, self.kind = str(path), kind
        sites = SITES[kind]
        self.db = sqlite3.connect(f"file:{self.path}?mode=ro", uri=True, check_same_thread=False)
        meta = dict(self.db.execute("SELECT key,value FROM metadata"))
        self.version = meta.get("game_version", "")
        self.snapshot = meta.get("snapshot", "")
        self.license = meta.get("license", "")
        self.attribution = meta.get("attribution", "Terraria Wiki contributors")
        self.evidence = meta.get("version_evidence", "")
        self.schema = meta.get("schema", "")
        self.scope = meta.get("scope", "")
        base = meta.get("base_url", "")
        self.base_url = base if base in sites else sites[-1] if kind == "vanilla" else sites[0]
        # A compatible header cannot authorize a foreign-version or other-scope row.
        globs = " OR ".join("url GLOB '%s*'" % s for s in sites)
        self.db.execute(f"""CREATE TEMP VIEW eligible_chunks AS SELECT * FROM chunks
            WHERE game_version=(SELECT value FROM metadata WHERE key='game_version')
            AND scope='{kind}' AND ({globs})""")
        titles = {t for (t,) in self.db.execute("SELECT DISTINCT title FROM eligible_chunks")}
        self.titles = titles
        # Redirect aliases are mostly English names; keep only those that point at a real page.
        self.redirects = {a: t for a, t in self.db.execute("SELECT alias,target FROM redirects") if t in titles}
        self.language = meta.get("language", "")
        # "DesertScourge" (a mod's internal name) -> "Desert Scourge" (an English page title).
        self.by_key = {}
        for name, title in list(self.redirects.items()) + [(t, t) for t in titles]:
            self.by_key.setdefault(squash(name), title)

    def url(self, title):
        return self.base_url + quote(title.replace(" ", "_"))

    def resolve(self, name):
        if name in self.titles:
            return name
        return self.redirects.get(name)

    def resolve_key(self, key):
        """Page for a mod's internal name (Boss Checklist key or item class name)."""
        k = squash(key)
        for cut in ("", "head", "body", "boss"):
            if cut and not k.endswith(cut):
                continue
            hit = self.by_key.get(k[:len(k) - len(cut)] if cut else k)
            if hit:
                return hit
        return None

    @lru_cache(maxsize=512)
    def page(self, title):
        rows = [r for (r,) in self.db.execute(
            "SELECT text FROM eligible_chunks WHERE title=? AND text NOT LIKE '配方：%' ORDER BY id", (title,))]
        out = ""
        for r in rows:
            if out and out.endswith(r[:160]):
                out += r[160:]
            elif out and len(r) < 160 and out.endswith(r):
                continue
            else:
                out += ("\n" if out else "") + r
        return out

    @lru_cache(maxsize=512)
    def sections(self, title):
        """[(heading, body)] in page order; the text before the first heading is under ''."""
        lines = self.page(title).split("\n")
        out, head, body = [], "", []
        def at(i):
            return lines[i] if i < len(lines) else ""

        def prose(line):
            return len(line) > 20 or len(line) >= 8 and line.endswith(("。", "：", "！"))

        def candidate(line):
            return 2 <= len(line) <= 14 and not _HEADING_BAD.search(line)
        for i, line in enumerate(lines):
            # A heading is a short bare line followed by prose, or by a sub-heading and then prose.
            is_head = candidate(line) and (prose(at(i + 1)) or candidate(at(i + 1)) and prose(at(i + 2)))
            if is_head:
                if body:
                    out.append((head, "\n".join(body).strip()))
                head, body = line.strip(), []
            else:
                body.append(line)
        if body:
            out.append((head, "\n".join(body).strip()))
        return out

    def section(self, title, *names, limit=900, follow=True):
        """Text of the first section whose heading contains any of `names` (plus following
        sub-sections until the text budget is used)."""
        secs = self.sections(title)
        for i, (head, body) in enumerate(secs):
            if any(n in head for n in names):
                text = body
                j = i + 1
                while follow and len(text) < limit and j < len(secs) and len(secs[j][1]) < limit:
                    text += f"\n【{secs[j][0]}】{secs[j][1]}"
                    j += 1
                return clip(text, limit)
        return ""

    def intro(self, title, limit=600):
        """Lead paragraphs of a page, with the few infobox fields worth reading (tooltip, damage...)."""
        lines = self.page(title).split("\n")
        keep = [l for l in lines[:80] if re.match(r"(工具提示|伤害|防御|使用时间|击退|暴击率|环境|最大生命值|类型|"
                                                  r"Tooltip|Damage|Defense|Use time|Knockback|Critical chance|"
                                                  r"Environment|Max Life|Type|Rarity|Health|Summoning) \|", l)
                and not l.rstrip().endswith("|")]
        # First prose line: Chinese full stop, or an English sentence (the Calamity wiki may be English).
        first = next((i for i, l in enumerate(lines) if len(l) > 16 and "|" not in l
                      and ("。" in l or re.search(r"[a-z]\. |[a-z]\.$", l))), len(lines))
        body = [l for l in lines[first:] if l.count("|") < 3]
        return clip("\n".join(keep[:6] + body), limit)

    def recipes_for(self, name, limit=3):
        """Structured recipe lines '配方：X ×n；材料：…；制作站：…' that produce `name`."""
        rows = self.db.execute("SELECT DISTINCT text FROM eligible_chunks WHERE text LIKE ? LIMIT ?",
                               (f"配方：{name} ×%", limit))
        return [r for (r,) in rows]

    def recipes_using(self, name, limit=6):
        rows = self.db.execute("SELECT DISTINCT text FROM eligible_chunks WHERE text LIKE '配方：%' AND text LIKE ? LIMIT ?",
                               (f"%材料：%{name} ×%", limit))
        return [r for (r,) in rows]

    def search(self, text, limit=4, exclude=()):
        """BM25 over title/body bigrams; returns [(title, excerpt)] one hit per page."""
        words = [w for w in dict.fromkeys(terms(text)) if w not in _STOP]
        if not words:
            return []
        query = " OR ".join('"' + w.replace('"', '') + '"' for w in words[:24])
        try:
            rows = self.db.execute(
                "SELECT c.title, c.text FROM search JOIN eligible_chunks c ON c.id = search.rowid "
                "WHERE search MATCH ? ORDER BY bm25(search, 4.0, 1.0) LIMIT 40", (query,)).fetchall()
        except sqlite3.OperationalError:
            return []
        seen, out = set(exclude), []
        for title, body in rows:
            if title in seen or "/" in title or title in _NOISE_PAGES or body.count("|") > 8:
                continue
            seen.add(title)
            out.append((title, best_window(body, words)))
            if len(out) >= limit:
                break
        return out


_NOISE_PAGES = {"可能修饰语列表", "Guide-制作速查手册", "物品 ID", "NPC ID", "图格 ID", "射弹 ID", "增益 ID"}
_STOP = set(terms("怎么 如何 什么 可以 请问 告诉 我想 一下 哪个 哪些 这个 那个 当前 现在 是否 我们 应该 需要 建议 下一 一步 "
                  "干啥 做什 么用 有什 一个 还是 就是 的话 吗 呢 吧 啊"))


def squash(name):
    return re.sub(r"[^a-z0-9]", "", name.lower())


def clip(text, limit):
    text = re.sub(r"\n{2,}", "\n", text).strip()
    return text if len(text) <= limit else text[:limit].rstrip() + "…"


def best_window(text, words, size=360):
    if len(text) <= size:
        return text
    best, start = -1, 0
    for i in range(0, len(text) - size + 1, 60):
        window = text[i:i + size]
        score = sum(1 for w in words if w in window)
        if score > best:
            best, start = score, i
    return text[start:start + size]
