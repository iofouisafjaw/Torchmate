"""Turn a player question plus a game snapshot into a grounded, detailed answer.

    program: works out what matters (advisor), finds the right Wiki pages/sections/recipes
    model:   only writes the reply from those facts, and cites the Wiki blocks it used
"""
import json
import re
import urllib.request

import advisor
import gamedata
from knowledge import ALIASES, BOSSES, CLASS_NAMES, guide_title

PLAN = re.compile(r"下一步|该干|干啥|干嘛|做什么|干什么|建议|接下来|怎么办|发展|规划|目标|进度|先做|准备什么|缺什么|还差")
BOSS_WORDS = re.compile(r"boss|首领|老板|头目", re.I)
RECIPE = re.compile(r"合成|配方|怎么做|制作|材料|做出|怎么造|用什么做")
# Asking what something is / does / drops: answer that, instead of a "what to do next" plan.
WHAT = re.compile(r"是啥|是什么|是谁|是哪|哪个|哪些|掉落|掉什么|掉啥|爆什么|怎么用|如何使用|怎么使用|有什么用|有啥用|干嘛用|作用|效果|在哪|什么样")
FINAL = re.compile(r"最终|最后|终极|最强|最难")
FOLLOW_UP = re.compile(r"这些|那些|这个|那个|它们|它的|上面|刚才|你说的|前面")
SUMMON_QUESTION = re.compile(r"召唤条件|生成条件|出现条件|自然生成|自然刷新|怎么召唤|如何召唤|怎样召唤|(?:召唤|生成).*(?:需要|要求|条件)|(?:需要|要求).*(?:召唤|生成)")
GENERIC = {"盔甲", "武器", "物品", "工具", "配饰", "敌怪", "玩家", "世界", "生命", "魔力", "防御", "伤害", "背包",
           "建议", "进度", "Boss", "boss", "首领", "制作", "合成", "配方", "材料", "地图", "时间", "白天", "夜晚",
           "下一步", "准备", "效果", "奖励", "掉落", "攻略", "什么", "东西", "钱币", "城镇", "房屋", "第一"}
BOSS_NAMES = {n for b in BOSSES for n in b["name"].split("/")}
SUMMONS = {"世界吞噬怪": "蠕虫诱饵", "克苏鲁之脑": "血腥脊椎"}
CLASS_HEADINGS = {"melee": ("对于战士", "战士", "近战"), "ranged": ("对于射手", "射手", "远程"),
                  "magic": ("对于法师", "法师", "魔法"), "summon": ("对于召唤师", "召唤师")}

SYSTEM = """你是玩家在泰拉瑞亚（电脑版）里的同伴 Qwen，一个很熟这游戏的老玩家朋友。用中文、口语、直接、具体地回答。
写法：先一句话给结论，再分 2–4 点（用 1. 2. 3.），每点写清楚做什么、去哪、用什么、为什么；能写物品名和数量就写。
事实只能来自下面给的【玩家状态】【程序分析】【资料】。模组 Boss 没有 Wiki 攻略时，按召唤说明、掉落和同进度原版 Boss 的准备标准来讲，并说明是按经验推断。数量、配方、掉落率、条件只照资料写，资料里没有的就不要编，可以说“资料里没写”。
【程序分析】是程序根据玩家背包和进度算出来的重点，按它排的优先级来讲，不要漏掉排在最前的两条。
资料里的“前代主机版、3DS版”说法不适用，忽略。不要把玩家状态原样列一遍，不要说“作为AI”，不要输出链接。
150–450 字。只输出 JSON：{"reply":"回答","basis":"wiki 或 world 或 insufficient","citations":[用到的资料编号]}。
用了【资料】就选 wiki 并填编号；只靠玩家状态和程序分析就选 world、citations 为空；什么依据都没有才选 insufficient。"""


class Brain:
    def __init__(self, wiki, upstream, model="qwen3-4b", modwiki=None):
        self.wiki, self.upstream, self.model = wiki, upstream.rstrip("/"), model
        self.modwiki = modwiki   # Calamity wiki index; used only in worlds that load Calamity
        self.history = {}  # (world id, game version) -> [(question, reply, entities)]
        names = {}
        if wiki:
            for t in wiki.titles:
                names[t] = t
            for a, t in wiki.redirects.items():
                if re.search(r"[一-鿿]", a):
                    names.setdefault(a, t)
        for a, t in ALIASES.items():
            names[a] = t
        for boss in BOSS_NAMES:
            names.setdefault(boss, boss)
        self.names = {k: v for k, v in names.items() if len(k) >= 2 and k not in GENERIC and v not in GENERIC}
        self.max_name = max((len(k) for k in self.names), default=0)
        mod_names = {}
        if modwiki:
            mod_names = {t: t for t in modwiki.titles}
            for a, t in modwiki.redirects.items():
                if re.search(r"[一-鿿]", a):
                    mod_names.setdefault(a, t)
        # Vanilla names win: a Calamity page never hijacks a vanilla item's name.
        self.mod_names = {k: v for k, v in mod_names.items()
                          if len(k) >= 2 and k not in GENERIC and v not in GENERIC and k not in self.names}

    # ---------------------------------------------------------------- entity linking
    def entities(self, text, extra=None):
        """Longest-match Wiki titles / aliases (plus this world's modded names) in order of appearance."""
        extra = extra or {}
        longest = max([self.max_name] + [len(k) for k in extra])
        found, i = [], 0
        while i < len(text):
            for L in range(min(longest, len(text) - i), 1, -1):
                t = extra.get(text[i:i + L]) or self.names.get(text[i:i + L])
                if t:
                    if t not in found:
                        found.append(t)
                    i += L
                    break
            else:
                i += 1
        return found

    # ---------------------------------------------------------------- context
    def compatible_wiki(self, version):
        w = self.wiki
        return w if (w and version and w.version == version and w.evidence.strip()
                     and w.schema == "1" and w.scope == "vanilla") else None

    def compatible_modwiki(self, version, state):
        w = self.modwiki
        mods = state.get("progress", {}).get("mods") or []
        return w if (w and version and w.version == version and w.evidence.strip() and w.schema == "1"
                     and w.scope == "calamity" and "CalamityMod" in mods) else None

    def build(self, question, state, version, out=None):
        # Per-request selection: never mutate shared self.wiki in the threaded server.
        wiki = self.compatible_wiki(version)
        mw = self.compatible_modwiki(version, state)
        wid = state.get("world", {}).get("id", "")
        past = self.history.get((wid, version), [])
        gd = gamedata.load(state.get("gameData"))
        board = advisor.checklist(state)
        modded_bosses = {e["name"]: e for e in board if e.get("mod") != "Terraria" and e.get("type") == "boss"}
        extra = {n: n for n in (gd.items.keys() | gd.by_product.keys() if gd else ()) if len(n) >= 2}
        extra.update({n: n for n in modded_bosses if len(n) >= 2})
        if mw:
            for k, v in self.mod_names.items():
                extra.setdefault(k, v)
        ents = self.entities(question, extra)
        if (not ents or FOLLOW_UP.search(question)) and past:
            ents += [e for e in past[-1][2] if e not in ents]
        bosses = [e for e in ents if e in BOSS_NAMES or e in modded_bosses]
        asks_what = bool(WHAT.search(question)) and not PLAN.search(question)
        final_boss = bool(FINAL.search(question) and BOSS_WORDS.search(question))
        intent = ("summon" if bosses and SUMMON_QUESTION.search(question) else
                  "recipe" if RECIPE.search(question) and ents else
                  "info" if final_boss or (asks_what and ents) else
                  "boss" if bosses else
                  "plan" if PLAN.search(question) or BOSS_WORDS.search(question) else
                  "info" if ents else "search")

        facts, findings = advisor.analyse(state)
        analysis, blocks, boss_points = [], [], []
        budget = [5200]

        def add(title, text, limit=700, url=None):
            """url None = a Wiki page (link derived from the title); "" = not from the Wiki."""
            if not text or budget[0] <= 0 or any(b[0] == title and b[1] == text for b in blocks):
                return
            text = text if len(text) <= limit else text[:limit] + "…"
            budget[0] -= len(text)
            if url is None:
                url = wiki.url(title.split("·")[0].split("（")[0]) if wiki else ""
            blocks.append((title, text, url))

        def recipe_lines(name, n=1):
            # The running game's own recipes first: exact version, includes every loaded mod.
            return (gd.recipes_for(name, n) if gd else []) or (wiki.recipes_for(name, n) if wiki else [])

        def add_recipes(name, n=2):
            if gd and gd.recipes_for(name, n):
                add(f"{name}（配方·游戏数据）", "\n".join(gd.recipes_for(name, n)), 400, url="")
                return
            lines = wiki.recipes_for(name, n) if wiki else []
            if not lines and name.endswith("盔甲") and wiki:
                m = re.search(r"由(.+?)组成", wiki.page(name))
                for piece in re.split(r"、|和", m.group(1)) if m else []:
                    lines += wiki.recipes_for(piece.strip(), 1)
            if lines:
                add(f"{name}（配方）", "\n".join(dict.fromkeys(lines)), 400)

        def mod_page(name):
            """Calamity wiki page for a name as the game shows it (Chinese) - by title, or by the
            mod's internal name when the wiki is English (Boss Checklist key, item class name)."""
            if not mw:
                return None
            page = mw.resolve(name)
            if page:
                return page
            keys = [e["key"].split(" ", 1)[-1] for e in board if e.get("name") == name and e.get("key")]
            item = gd.items.get(name) if gd else None
            if item and item.get("key"):
                keys.append(item["key"])
            return next((p for p in map(mw.resolve_key, keys) if p), None)

        def add_mod(name, limit=600, heads=()):
            """A Calamity wiki page (intro, or the first matching section)."""
            page = mod_page(name)
            if not page:
                return False
            text = mw.section(page, *heads, limit=limit) if heads else mw.intro(page, limit)
            label = "·".join([name] + list(heads[:1])) + ("（灾厄 Wiki）" if page == name else f"（灾厄 Wiki：{page}）")
            add(label, text, limit, url=mw.url(page))
            return bool(text)

        cls = advisor.main_class(state) or "melee"
        cls_heads = CLASS_HEADINGS[cls]

        if intent in ("plan", "boss") or (BOSS_WORDS.search(question) and intent != "info"):
            for f in findings[:6]:
                analysis.append(f"[{f.topic}] {f.text}")
        if intent == "plan":
            for f in findings[:4]:
                for item in f.items[:3]:
                    add_recipes(item, 1)
                for page, heads in f.pages[:2]:
                    if wiki and wiki.resolve(page):
                        add(page, wiki.section(wiki.resolve(page), *heads, limit=450, follow=False) if heads
                            else wiki.page(wiki.resolve(page))[:450], 450)
        if intent == "plan" and not bosses and BOSS_WORDS.search(question):
            nxt = advisor.next_boss(state)
            if nxt:
                bosses = [advisor.boss_for_world(nxt, state["world"].get("evil"))[0]]
        if intent == "summon":
            # Preserve the section's conditions and their grouping. Number-presence
            # checks cannot tell manual summoning from natural-spawn conditions.
            for boss in bosses[:2]:
                if boss in modded_bosses:
                    info = modded_bosses[boss].get("spawnInfo")
                    if info:
                        add("Boss Checklist·" + boss + "·召唤说明", info, 850, url="")
                if mw and (page := mod_page(boss)):
                    for head, body in mw.sections(page):
                        if any(n in head.lower() for n in ("召唤", "生成", "spawn", "summon")):
                            add(boss + "·" + head + "（灾厄 Wiki）", body, 850, url=mw.url(page))
                if wiki and boss not in modded_bosses:
                    for head, body in wiki.sections(boss):
                        if any(n in head for n in ("召唤", "生成")) and not re.search(r"3DS|前代主机", head, re.I):
                            add(boss + "·" + head, body, 850)
        for boss in (bosses[:2] if intent not in ("info", "summon") else []):   # battle plans only when not just asking about it
            entry, lines = advisor.boss_plan(state, boss)
            analysis[:0] = lines
            boss_points += lines
            if boss in modded_bosses:   # no offline Wiki for mods: use Boss Checklist + game data
                e = modded_bosses[boss]
                add(f"Boss Checklist·{boss}", f"召唤：{e.get('spawnInfo') or '没有说明'}\n召唤物：{'、'.join(e.get('spawnItems') or []) or '无'}"
                    f"\n掉落：{'、'.join(e.get('loot') or []) or '没有记录'}", 700, url="")
                if mw:
                    add_mod(boss, 500)
                    add_mod(boss, 700, ("攻略", "打法", "战斗", "策略", "技巧", "提示", "Tips", "Strategy", "Strategies"))
                for item in (e.get("spawnItems") or [])[:2]:
                    add_recipes(item, 1)
                    lack = missing(recipe_lines(item), state)
                    if lack is not None and not any(i.get("name") == item for i in state["player"].get("inventory", [])):
                        analysis.append(f"做{item}：" + ("材料都够了。" if not lack else "背包里还差 " + "、".join(lack) + "。"))
                        boss_points.append(analysis[-1])
                continue
            if mw:   # Calamity changes vanilla bosses too
                add_mod(boss, 400, ("灾厄", "改动", "变化", "死亡模式", "复仇模式", "Changes", "Revengeance", "Death Mode"))
            if not wiki:
                continue
            summon = wiki.section(boss, "召唤和生成", "生成", "召唤", limit=600)
            if not summon:
                summon = wiki.intro(boss, 600)
            add(boss, summon, 600)
            table = drops(wiki.page(boss))
            add(boss + "（掉落）", table, 500)
            if re.search(r"奖励|掉落|掉什么|掉啥|爆什么|给什么|能拿到", question):
                loot = notable_drops(table, state["world"].get("evil", ""))
                if loot:
                    boss_points.append(f"打赢{boss}会掉：" + "、".join(loot) + "。")
            g = guide_title(boss)
            if wiki.resolve(g):
                add(g + "·作战", wiki.section(g, "作战", "第一阶段", limit=700), 700)
                add(g + "·场地", wiki.section(g, "地形准备", limit=350), 350)
                add(g + "·" + CLASS_NAMES[cls], wiki.section(g, *cls_heads, limit=600), 600)
                add(g + "·配饰", wiki.section(g, "配饰", limit=350), 350)
            if entry:
                item = SUMMONS.get(boss, entry["summon"])
                add_recipes(item, 1)
                lack = missing(recipe_lines(item), state)
                if lack is not None and not any(i.get("name") == item for i in state["player"].get("inventory", [])):
                    analysis.append(f"做{item}：" + ("材料都够了。" if not lack else "背包里还差 " + "、".join(lack) + "。"))
                    boss_points.append(analysis[-1])
        if intent == "recipe":
            for e in ents[:3]:
                add_recipes(e, 3)
        if intent in ("info", "recipe") and gd:
            for e in ents[:3]:
                if e in gd.items:
                    add(f"{e}（游戏数据）", gd.describe(e), 500, url="")
        if intent in ("info", "recipe") and mw:
            for e in ents[:3]:
                if not (wiki and wiki.resolve(e)) and e not in modded_bosses:
                    add_mod(e, 600)
                    add_mod(e, 350, ("提示", "小贴士", "技巧", "Tips", "Notes"))
        if final_boss:
            order = [e for e in board if e.get("type") == "boss"]
            if order:
                tail = order[-5:]
                add("Boss 进度（Boss Checklist，按进度排）",
                    "最后几个 Boss：" + " → ".join(f"{e.get('name')}（{e.get('mod')}）" for e in tail) +
                    f"\n按进度最后的是：{tail[-1].get('name')}（来自 {tail[-1].get('mod')}）。原版的最终 Boss 是月亮领主。", 500, url="")
            else:
                add("Boss 进度", "原版泰拉瑞亚的最终 Boss 是月亮领主（月球事件之后召唤）。", 200, url="")
        if intent == "info":   # a question about a boss: what it is and what it drops, not a battle plan
            for boss in bosses[:2]:
                if boss in modded_bosses:
                    e = modded_bosses[boss]
                    add(f"Boss Checklist·{boss}", f"召唤：{e.get('spawnInfo') or '没有说明'}\n召唤物：{'、'.join(e.get('spawnItems') or []) or '无'}"
                        f"\n掉落：{'、'.join(e.get('loot') or []) or '没有记录'}", 700, url="")
                    add_mod(boss, 500)
                elif wiki and wiki.resolve(boss):
                    add(boss, wiki.intro(boss, 500), 500)
                    add(boss + "（掉落）", drops(wiki.page(boss)), 500)
        if intent in ("info", "recipe") and wiki:
            for e in ents[:3]:
                if e in BOSS_NAMES or not wiki.resolve(e):
                    continue
                add(e, wiki.intro(e, 600), 600)
                add(e + "·小贴士", wiki.section(e, "小贴士", limit=350, follow=False), 350)
        if intent != "summon" and (intent == "search" or len(blocks) < 2) and wiki:
            for title, text in wiki.search(question, 3, exclude=[b[0].split("·")[0].split("（")[0] for b in blocks]):
                add(title, text, 450)
        if intent != "summon" and (intent == "search" or len(blocks) < 2) and mw:
            for title, text in mw.search(question, 2, exclude=[b[0].split("·")[0].split("（")[0] for b in blocks]):
                add(title + "（灾厄 Wiki）", text, 450, url=mw.url(title))

        # Program-written points shown verbatim to the player (the model only adds an opener and tips).
        if out is not None:
            if boss_points:   # about a boss: summon, readiness, what to prepare for it
                prep = [f.text for f in findings if f.topic in ("生命水晶", "盔甲", "药水", "抓钩", "移动")][:2]
                out["points"] = list(dict.fromkeys(boss_points + prep))[:6]
            else:
                out["points"] = [f.text for f in findings[:5]]
            out["recipes"] = [(t, x) for t, x, _ in blocks if t.endswith(("（配方）", "（配方·游戏数据）"))]
        prompt = ["【问题】" + question]
        if past:
            prompt.append("【之前的对话】\n" + "\n".join(f"玩家：{q}\n你：{a[:160]}" for q, a, _ in past[-2:]))
        prompt.append("【玩家状态】\n" + "\n".join(facts))
        if analysis:
            prompt.append("【程序分析】\n" + "\n".join(analysis))
        if blocks:
            english = mw and not mw.language.startswith("zh") and any("灾厄 Wiki" in t for t, _, _ in blocks)
            if english:
                prompt.append("【注意】灾厄 Wiki 资料是英文：回答时翻成中文，物品和 Boss 用玩家游戏里的中文名"
                              "（资料标题里括号前的名字），英文名不要写出来。")
            prompt.append("【资料】（中文 Wiki" + ("、灾厄 Wiki" if mw else "") + " 摘录、Boss Checklist、游戏内配方数据，编号可引用）\n" +
                          "\n".join(f"[{i + 1}] {t}\n{x}" for i, (t, x, _) in enumerate(blocks)))
        return intent, ents, blocks, "\n\n".join(prompt)

    # ---------------------------------------------------------------- model
    def ask_model(self, user, ids, max_tokens=900, points=False):
        body_props = ({"intro": {"type": "string", "minLength": 1, "maxLength": 80},
                       "tips": {"type": "array", "maxItems": 3, "items": {"type": "string", "minLength": 4, "maxLength": 90}}}
                      if points else {"reply": {"type": "string", "minLength": 1, "maxLength": 1000}})
        schema = {"type": "object", "additionalProperties": False, "required": list(body_props) + ["basis", "citations"],
                  "properties": dict(body_props, **{
                      "basis": {"type": "string", "enum": ["wiki", "world", "insufficient"] if ids else ["world", "insufficient"]},
                      "citations": {"type": "array", "maxItems": min(2, len(ids)), "uniqueItems": True,
                                    "items": {"type": "integer", "enum": ids} if ids else {"type": "integer"}}})}
        body = {"model": self.model, "temperature": 0.3, "max_tokens": max_tokens, "stream": False,
                "cache_prompt": True, "chat_template_kwargs": {"enable_thinking": False},
                "messages": [{"role": "system", "content": SYSTEM_POINTS if points else SYSTEM},
                             {"role": "user", "content": user + "\n/no_think"}],
                "response_format": {"type": "json_schema", "json_schema": {"name": "terrarai_advice", "schema": schema}}}
        req = urllib.request.Request(self.upstream + "/v1/chat/completions", json.dumps(body).encode(),
                                     {"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=180) as r:
            data = json.load(r)
        return data["choices"][0]["message"]["content"]

    def advise(self, question, state, version):
        match = re.fullmatch(r"(?:Terraria )?v?(\d+\.\d+\.\d+(?:\.\d+)?)", version.strip())
        version = match.group(1) if match else ""
        extra = {}
        intent, ents, blocks, user = self.build(question, state, version, out=extra)
        ids = list(range(1, len(blocks) + 1))
        points, recipes = extra.get("points", []), extra.get("recipes", [])
        if intent == "summon":
            # No model rewriting: retain the source's manual/natural distinction.
            cites = ids[:2]
            reply = "\n\n".join(f"【{t}，资料摘录】\n{x}" for t, x, _ in blocks[:2]) if cites else \
                "没有找到与当前游戏版本匹配的召唤／生成条件章节，请打开对应 Wiki 页面核对。"
            answer = {"basis": "wiki" if cites else "insufficient"}
        elif intent == "recipe" and recipes:
            # Recipes are data: shown exactly as recorded; the model only adds a one-line opener.
            lines = recipe_reply(recipes, state)
            user += "\n\n【要点】（程序已写好，会原样显示在你的开场之后）\n" + lines
            answer = json.loads(self.ask_model(user, ids, max_tokens=300, points=True))
            intro = safe_model_note(answer.get("intro", ""), user, blocks)
            reply = (intro + "\n" if intro else "") + lines
            cites = [i for i, b in enumerate(blocks, 1) if b[0] in {t for t, _ in recipes}][:2]
            answer["basis"] = "wiki" if cites else "world"
        elif intent in ("plan", "boss") and points:
            user += "\n\n【要点】（程序已写好，会原样显示在你的开场之后）\n" + "\n".join(f"{i}. {p}" for i, p in enumerate(points, 1))
            answer = json.loads(self.ask_model(user, ids, max_tokens=500, points=True))
            tips = [note for t in answer.get("tips", []) if (note := safe_model_note(t, user, blocks))][:3]
            intro = safe_model_note(answer.get("intro", ""), user, blocks)
            reply = (intro + "\n" if intro else "") + "\n".join(f"{i}. {p}" for i, p in enumerate(points, 1))
            if tips:
                reply += "\n小技巧：" + "；".join(t.rstrip("。；") for t in tips) + "。"
            cites = [c for c in answer.get("citations", []) if c in ids][:2] if tips else []
            if not cites:
                answer["basis"] = "world"
        else:
            answer = json.loads(self.ask_model(user, ids))
            reply = drop_unsupported_numbers(answer.get("reply", ""), user) or "资料不足，暂时无法确认这些数值。"
            cites = [c for c in answer.get("citations", []) if c in ids][:2]
        basis = answer.get("basis", "world")
        if basis == "wiki" and not cites:
            raise ValueError("Wiki answer has no compatible source citation")
        if basis != "wiki":
            cites = []
        reply = re.sub(r"https?://\S+", "", reply).strip()[:1000]
        wid = state.get("world", {}).get("id", "")
        self.history.setdefault((wid, version), []).append((question, reply, ents))
        self.history[(wid, version)] = self.history[(wid, version)][-4:]
        w = self.compatible_wiki(version)
        mw = self.compatible_modwiki(version, state)
        status = "已检索本地 Wiki（Python 全文检索）" if w else "未找到离线 Wiki，只用了游戏状态"
        if self.wiki and not w:
            status = f"Wiki 版本 {self.wiki.version or '未知'} 与游戏 {version or '未知'} 未确认匹配，已隔离。"
        if mw:
            status += "；灾厄 Wiki 已加入检索"
        sources = []
        for i in cites:
            title, text, url = blocks[i - 1]
            src = next((x for x in (w, mw) if x and url.startswith(x.base_url)), None)
            sources.append({"id": i, "title": title, "url": url, "excerpt": text[:220],
                            "game_version": src.version if src else version})
        # Provenance of whichever indexes the cited passages came from (vanilla first).
        cited = [x for x in (w, mw) if x and any(s["url"].startswith(x.base_url) for s in sources)]
        main = cited[0] if cited else w
        return {"reply": reply, "basis": basis, "citations": cites, "sources": sources, "status": status,
                "intent": intent, "game_version": main.version if main else "", "snapshot": main.snapshot if main else "",
                "evidence": "；".join(x.evidence for x in cited) or (w.evidence if w else ""),
                "license": "；".join(dict.fromkeys(x.license for x in cited)) or (w.license if w else ""),
                "attribution": "、".join(x.attribution for x in cited) or (w.attribution if w else "")}


SYSTEM_POINTS = """你是玩家在泰拉瑞亚（电脑版）里的同伴 Qwen，一个熟这游戏的老玩家朋友，说话口语、直接。
程序已经根据玩家的背包和进度写好了【要点】，会原样显示给玩家。你只负责两样：
intro：一句开场（不超过 40 字），点出眼下最要紧的一件事，像朋友提醒一样，不要写数字。
tips：从【资料】里挑 0–3 条【要点】没说到、但对眼前这件事真有用的技巧（打法、场地、躲招、该带什么），每条不超过 60 字。
只能来自【资料】；资料里没有就给空数组。不要重复要点，不要编数字和物品，不要说“作为AI”。
用了资料就 basis=wiki 并在 citations 填资料编号；否则 basis=world、citations 为空。只输出 JSON。"""


def numbers_ok(text, context):
    """Every number the model wrote must appear in what it was given (blocks invented counts/percentages)."""
    return set(re.findall(r"\d+(?:\.\d+)?", text or "")) <= set(re.findall(r"\d+(?:\.\d+)?", context or ""))


def safe_model_note(text, context, blocks):
    """Condition claims must be an intact excerpt, never assembled from nearby stats."""
    text = text.strip()
    if not numbers_ok(text, context):
        return ""
    if re.search(r"召唤|生成|刷新|出现条件", text) and not any(text in body for _, body, _ in blocks):
        return ""
    return text


def drop_unsupported_numbers(text, context):
    parts = re.split(r"(?<=[。；！？\n])", text or "")
    kept = [p for p in parts if numbers_ok(p, context)]
    return "".join(kept).strip()


_RECIPE = re.compile(r"配方：(.+?) ×(\d+)；材料：(.+?)；制作站：(.+?)(?:；条件：(.+?))?。")


def recipe_reply(recipes, state):
    lines, first = [], None
    for _, text in recipes:
        for line in text.split("\n"):
            m = _RECIPE.match(line.strip())
            if not m:
                continue
            first = first or line.strip()
            out = m.group(1), m.group(2), m.group(3).replace("、", " + "), re.sub(r"\s*或\s*", " 或 ", m.group(4))
            item = f"{out[0]}：{out[2]}，在{out[3]}做" + (f"（一次出 {out[1]} 个）" if out[1] != "1" else "")
            if m.group(5):
                item += f"，条件：{m.group(5)}"
            if item not in lines:
                lines.append(item)
    if not lines:
        return "\n".join(t for _, t in recipes)
    lack = missing([first], state)
    tail = "" if lack is None else ("\n你背包里的材料够了。" if not lack else "\n你还差：" + "、".join(lack) + "。")
    return "\n".join(lines[:4]) + tail


def missing(recipe_lines, state):
    """Ingredients of the first recipe line that the main inventory lacks, e.g. ['腐肉 ×6']."""
    if not recipe_lines:
        return None
    m = re.search(r"材料：(.+?)；", recipe_lines[0])
    if not m:
        return None
    have = {}
    for i in state["player"].get("inventory", []):
        have[i.get("name")] = have.get(i.get("name"), 0) + i.get("count", 0)
    out = []
    for part in m.group(1).split("、"):
        mm = re.match(r"(.+?) ×(\d+)", part.strip())
        if not mm or mm.group(1).startswith("任何"):
            continue
        need = int(mm.group(2)) - have.get(mm.group(1), 0)
        if need > 0:
            out.append(f"{mm.group(1)} ×{need}")
    return out


def notable_drops(table, evil="", limit=6):
    """Item names from a drop table worth mentioning (no masks, trophies, relics, bags, coins or
    the other world-evil's items)."""
    other = r"猩红|组织样本|血腥" if evil == "corruption" else r"魔矿|邪箭|腐化|暗影鳞片" if evil == "crimson" else None
    out = []
    for part in table.split("；"):
        name = re.sub(r"[（(].*$", "", part).strip()
        name = re.sub(r"\s+[\d.]+%$", "", name).strip()
        if not name or name.startswith("仅") or (other and re.search(other, name)):
            continue
        if re.search(r"面具|纪念章|圣物|宝藏袋|金币|银币|铜币|治疗药水|种子|Badger|\[", name) or name in out:
            continue
        out.append(name)
    return out[:limit]


def drops(page):
    """The drop table from a boss page infobox: lines between '物品（数量）掉落率' and '声音'."""
    m = re.search(r"物品（数量）掉落率\n(.+?)\n声音", page, re.S)
    if not m:
        return ""
    lines = [l for l in m.group(1).split("\n") if l.strip()]
    pairs, i = [], 0
    while i < len(lines):
        if i + 1 < len(lines) and re.match(r"^[\d.]+%", lines[i + 1]):
            pairs.append(f"{lines[i]} {lines[i + 1]}")
            i += 2
        else:
            pairs.append(lines[i])
            i += 1
    return "；".join(pairs)
