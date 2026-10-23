"""Convert Wiki HTML to text passages; preserve recipe rowspan and quantity semantics."""
from html.parser import HTMLParser
import re

VOID = {'area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'}

class Node:
    def __init__(self, tag='', attrs=()):
        self.tag, self.attrs, self.children = tag, dict(attrs), []
    @property
    def classes(self): return set(self.attrs.get('class','').split())
    def descendants(self, tag=None):
        for child in self.children:
            if isinstance(child, Node):
                if tag is None or child.tag == tag: yield child
                yield from child.descendants(tag)

class Tree(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node(); self.stack = [self.root]
    def handle_starttag(self, tag, attrs):
        node = Node(tag, attrs); self.stack[-1].children.append(node)
        if tag not in VOID: self.stack.append(node)
    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag,attrs)
        if tag not in VOID: self.handle_endtag(tag)
    def handle_endtag(self, tag):
        for i in range(len(self.stack)-1,0,-1):
            if self.stack[i].tag == tag:
                del self.stack[i:]; break
    def handle_data(self, text): self.stack[-1].children.append(text)

def normalized(text): return re.sub(r'\s+',' ',text).strip()

JUNK = {'toc','navbox','mw-editsection','mw-references-wrap','catlinks','id','history-header','history-title'}
PLATFORMS = {'电脑版','主机版','移动版','前代主机版','任天堂3DS版','3DS版'}

def text_of(node, skip_recipes=False, skip_amount=False, skip_version=False):
    if isinstance(node,str): return node
    classes=node.classes
    if node.tag in ('script','style','nav') or classes & JUNK or node.attrs.get('id') in ('toc','catlinks','siteNotice'): return ''
    if skip_recipes and node.tag == 'table' and 'recipes' in classes: return ''
    if skip_amount and 'am' in classes: return ''
    if skip_version and classes & {'eico','version-note'}: return ''
    if node.tag=='img':
        alt=node.attrs.get('alt','')
        # Item names are repeated as real text. Version-only icons still carry essential meaning.
        return ' '+alt+' ' if alt in PLATFORMS else ''
    parts=[text_of(x,skip_recipes,skip_amount,skip_version) for x in node.children]
    sep=' | ' if node.tag in ('tr',) else ''
    text=sep.join(parts)
    if node.tag in ('p','div','li','tr','h1','h2','h3','h4','br'): text='\n'+text+'\n'
    if 'am' in classes: text=' ×'+normalized(text)+' '
    return text

def legacy_only(node):
    for mark in node.descendants():
        if 'version-note' in mark.classes:
            label=normalized(text_of(mark))
            if ('仅限' in label or 'only' in label.lower()) and '电脑版' not in label and 'Desktop' not in label:
                return True
    return False

def item_amount(node):
    amounts=[normalized(text_of(x)) for x in node.descendants() if 'am' in x.classes]
    amount=re.sub(r'^×\s*','',amounts[-1]) if amounts else '1'
    name=normalized(text_of(node,skip_amount=True,skip_version=True))
    return name,amount

def recipes(table):
    """Resolve spans before dropping legacy rows; otherwise inherited stations shift."""
    pending={}
    caption=next((n for n in table.children if isinstance(n,Node) and n.tag=='caption'),None)
    if caption is not None and legacy_only(caption): return
    for row in table.descendants('tr'):
        values={role: value for role,(value,remaining) in pending.items() if remaining>0}
        pending={role:(value,remaining-1) for role,(value,remaining) in pending.items() if remaining>1}
        for cell in (x for x in row.children if isinstance(x,Node) and x.tag=='td'):
            role=next((r for r in ('result','ingredients','station') if r in cell.classes),None)
            if not role: continue
            values[role]=cell
            try: span=min(10000,max(1,int(cell.attrs.get('rowspan','1'))))
            except ValueError: span=1
            pending.pop(role,None)
            if span>1: pending[role]=(cell,span-1)
        if 'station' not in values and caption is not None: values['station']=caption
        if not all(role in values for role in ('result','ingredients','station')): continue
        if any(legacy_only(n) for n in values.values()): continue
        result,amount=item_amount(values['result'])
        ingredients=[]
        items=list(values['ingredients'].descendants('li'))
        if not items: items=[values['ingredients']]
        for item in items:
            name,number=item_amount(item)
            if name: ingredients.append(f'{name} ×{number}')
        station=normalized(text_of(values['station'],skip_version=True))
        if result and ingredients and station:
            yield f'配方：{result} ×{amount}；材料：'+ '、'.join(ingredients) + f'；制作站：{station}。'

def article_passages(html, title, skip_headings=('历史','参考','参考资料','导航','参见','图库')):
    parser=Tree();parser.feed(html)
    root=next((n for n in parser.root.descendants() if n.attrs.get('id')=='mw-content-text'),parser.root)
    body=next((n for n in root.descendants() if 'mw-parser-output' in n.classes),root)
    # Heading boundaries keep old patch notes from being retrieved as current behavior.
    parts=[]; recipe_parts=[]; skip_level=None
    for node in body.children:
        if isinstance(node,Node) and re.fullmatch('h[1-6]',node.tag):
            level=int(node.tag[1]); heading=normalized(text_of(node))
            if skip_level is not None and level<=skip_level: skip_level=None
            if heading in skip_headings: skip_level=level
        if skip_level is not None: continue
        if isinstance(node,Node):
            tables=([node] if node.tag=='table' else [])+list(node.descendants('table'))
            for table in tables:
                if 'recipes' in table.classes: recipe_parts.extend(recipes(table))
        parts.append(text_of(node,skip_recipes=True))
    text='\n'.join(x for line in ''.join(parts).splitlines() if (x:=normalized(line)))
    for offset in range(0,len(text),480): yield text[offset:offset+640]
    # Recipe rows are independent passages: quantities and shared stations stay together.
    yield from dict.fromkeys(recipe_parts)
