"""TerrarAI brain: sits between the mod and llama-server.

    mod --> :8080 (this) --> :8081 llama-server
            /terrarai/advise   detailed advice: rules + Wiki (FTS5) + model
            /health            status
            anything else      forwarded unchanged to llama-server

    python scripts/brain/server.py --port 8080 --upstream http://127.0.0.1:8081
"""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sys
import time
import traceback
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "scripts"))
from brain import Brain  # noqa: E402
from wiki import Wiki  # noqa: E402


def code_version():
    """Fingerprint of the brain's own source, so the launcher can tell a stale server from a current one."""
    import hashlib
    digest = hashlib.sha256()
    for f in sorted(HERE.glob("*.py")):
        digest.update(f.name.encode()); digest.update(f.read_bytes())
    return digest.hexdigest()[:12]


CODE_VERSION = code_version()


def find_wiki(explicit=None):
    candidates = [Path(explicit)] if explicit else []
    candidates.append(ROOT / ".runtime/wiki-v010-index.db")
    try:
        from winpaths import tml_profile
        candidates.append(tml_profile() / "ModData/TerrarAI/knowledge/wiki.db")
    except Exception:
        pass
    for c in candidates:
        if c.exists():
            try:
                return Wiki(c)
            except Exception as e:
                print("Wiki not usable:", c, e, flush=True)
    return None


def find_modwiki(explicit=None):
    """Calamity wiki index from scripts/import_calamity_wiki.py (optional)."""
    path = Path(explicit) if explicit else ROOT / ".runtime/calamity-wiki.db"
    if not path.exists():
        return None
    try:
        return Wiki(path, kind="calamity")
    except Exception as e:
        print("Calamity wiki not usable:", path, e, flush=True)
        return None


class Handler(BaseHTTPRequestHandler):
    brain: Brain = None
    upstream = ""
    log = None
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # quiet default access log
        pass

    def reply(self, code, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        if self.path == "/health":
            # Ready only when the model behind it is (llama-server answers 503 while loading).
            try:
                with urllib.request.urlopen(self.upstream + "/health", timeout=3) as r:
                    model_ok = r.status == 200
            except Exception:
                model_ok = False
            w, mw = self.brain.wiki, self.brain.modwiki
            return self.reply(200 if model_ok else 503, {"ok": model_ok, "brain": True, "code": CODE_VERSION, "wiki": bool(w),
                                                         "wikiVersion": w.version if w else None,
                                                         "calamityWiki": mw.snapshot if mw else None})
        self.forward("GET", b"")

    def do_POST(self):
        raw = self.body()
        if self.path == "/terrarai/advise":
            return self.advise(raw)
        self.forward("POST", raw)

    def advise(self, raw):
        started = time.time()
        try:
            req = json.loads(raw)
            result = self.brain.advise(req["question"], req["state"], req.get("version", ""))
            ms = int((time.time() - started) * 1000)
            result["latencyMs"] = ms
            print(f"[advise] {ms} ms · {result['intent']} · {req['question'][:40]}", flush=True)
            self.write_log({"question": req["question"], "intent": result["intent"], "ms": ms,
                            "reply": result["reply"], "basis": result["basis"]})
            self.reply(200, result)
        except urllib.error.URLError as e:
            self.reply(502, {"error": f"model server not reachable: {e}"})
        except Exception as e:
            traceback.print_exc()
            self.reply(500, {"error": str(e)})

    def write_log(self, row):
        if self.log:
            try:
                with open(self.log, "a", encoding="utf-8") as f:
                    f.write(json.dumps(dict(row, utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())),
                                       ensure_ascii=False) + "\n")
            except OSError:
                pass

    def forward(self, method, raw):
        headers = {k: v for k, v in self.headers.items() if k.lower() in ("content-type", "authorization", "accept")}
        req = urllib.request.Request(self.upstream + self.path, data=raw if method == "POST" else None,
                                     headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=600) as r:
                self.reply(r.status, r.read(), r.headers.get("Content-Type", "application/json"))
        except urllib.error.HTTPError as e:
            self.reply(e.code, e.read(), e.headers.get("Content-Type", "application/json"))
        except Exception as e:
            self.reply(502, {"error": {"message": f"model server not reachable: {e}"}})


def serve(port, upstream, wiki_path=None, model="qwen3-4b", modwiki_path=None):
    wiki = find_wiki(wiki_path)
    print("Wiki:", f"{wiki.path} ({wiki.version})" if wiki else "not found (advice uses game state only)", flush=True)
    modwiki = find_modwiki(modwiki_path)
    print("Calamity wiki:", f"{modwiki.path} ({modwiki.snapshot}, {len(modwiki.titles)} pages)" if modwiki
          else "not installed (python scripts/import_calamity_wiki.py)", flush=True)
    Handler.brain = Brain(wiki, upstream, model, modwiki=modwiki)
    Handler.upstream = upstream.rstrip("/")
    Handler.log = ROOT / ".runtime/brain.jsonl"
    Handler.log.parent.mkdir(exist_ok=True)
    httpd = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    httpd.daemon_threads = True
    print(f"Brain listening on http://127.0.0.1:{port} -> {upstream}", flush=True)
    return httpd


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--upstream", default="http://127.0.0.1:8081")
    ap.add_argument("--wiki")
    ap.add_argument("--calamity-wiki")
    a = ap.parse_args()
    serve(a.port, a.upstream, a.wiki, modwiki_path=a.calamity_wiki).serve_forever()


if __name__ == "__main__":
    main()
