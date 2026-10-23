"""Recipes and modded-item info exported by the mod (ModData/TerrarAI/knowledge/gamedata.json).

This comes from the running game, so it covers every loaded mod (Calamity, Thorium, ...) and the
exact game version; the recipe lines use the same text format as the Wiki index.
"""
import json
import os
import re

_PRODUCT = re.compile(r"^配方：(.+?) ×\d+；")


class GameData:
    def __init__(self, path):
        self.path, self.mtime = path, os.path.getmtime(path)
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        self.mods = data.get("mods", [])
        self.by_product, self.lines = {}, data.get("recipes", [])
        for line in self.lines:
            m = _PRODUCT.match(line)
            if m:
                self.by_product.setdefault(m.group(1), []).append(line)
        self.items = {i["name"]: i for i in data.get("modItems", []) if i.get("name")}

    def recipes_for(self, name, limit=3):
        return list(dict.fromkeys(self.by_product.get(name, [])))[:limit]

    def describe(self, name):
        i = self.items.get(name)
        if not i:
            return ""
        parts = [f"{name}（来自模组 {i.get('mod')}）"]
        for key, label in (("damage", "伤害"), ("defense", "防御"), ("pick", "镐力")):
            if i.get(key):
                parts.append(f"{label} {i[key]}")
        if i.get("accessory"):
            parts.append("饰品")
        if i.get("tooltip"):
            parts.append("说明：" + i["tooltip"])
        return "；".join(parts)


_cache = {}


def load(path):
    """GameData for `path`, re-read when the file changes; None if missing or unreadable."""
    if not path or not os.path.exists(path):
        return None
    hit = _cache.get(path)
    try:
        if hit and hit.mtime == os.path.getmtime(path):
            return hit
        _cache[path] = GameData(path)
        return _cache[path]
    except (OSError, ValueError):
        return hit
