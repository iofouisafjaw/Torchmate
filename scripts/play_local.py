"""Start the local Qwen service (reuse it if running) and open Steam tModLoader.
Mac: port 18080, CPU.  Windows: port from the mod config (default 8080), GPU."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

import os
from release_support import ALIAS, model_path, settings
ROOT = Path(__file__).resolve().parents[1]
WINDOWS = sys.platform == "win32"
if WINDOWS:
    sys.path.insert(0, str(ROOT / "scripts"))
    from winpaths import tml_profile
    PROFILE = tml_profile()
    PORT = 8080
    _cfg = PROFILE / "ModConfigs/TerrarAI_TerrarAI_Config.json"
    if _cfg.exists():   # follow whatever endpoint the mod is configured to use
        try:
            from urllib.parse import urlparse
            PORT = urlparse(json.loads(_cfg.read_text(encoding="utf-8"))["BaseEndpoint"]).port or 8080
        except Exception:
            pass
else:
    PROFILE = Path.home() / "Library/Application Support/Terraria/tModLoader"
    PORT = 18080
ENDPOINT = "http://127.0.0.1:%d" % PORT


def ready():
    try:
        with urllib.request.urlopen(ENDPOINT + "/health", timeout=3) as response:
            return response.status == 200
    except urllib.error.HTTPError as error:
        if error.code == 503:
            return False
        raise
    except urllib.error.URLError as error:
        if isinstance(error.reason, ConnectionRefusedError):
            return None
        raise


def has_brain():
    """True when the server on PORT runs the current brain code (not an older launch or older brain)."""
    sys.path.insert(0, str(ROOT / "scripts/brain"))
    try:
        from server import CODE_VERSION
    except Exception:
        CODE_VERSION = None
    try:
        with urllib.request.urlopen(ENDPOINT + "/health", timeout=3) as response:
            info = json.load(response)
    except urllib.error.HTTPError as error:
        try:
            info = json.loads(error.read() or b"{}")
        except ValueError:
            return False
    except Exception:
        return False
    return bool(info.get("brain")) and (CODE_VERSION is None or info.get("code") == CODE_VERSION)


def stop_old_server():
    """Compatibility stub: a PID file alone is insufficient proof of process ownership."""
    return False


def main():
    global PROFILE, PORT, ENDPOINT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check configuration and model without launching anything")
    parser.add_argument("--cpu", action="store_true", help="Start a new service with zero GPU layers")
    parser.add_argument("--service-only", action="store_true", help="Start/reuse service without opening Steam")
    parser.add_argument("--profile", type=Path, help="Explicit tModLoader user-data directory")
    args = parser.parse_args()
    if sys.platform not in ("darwin", "win32"):
        sys.exit("This launcher supports macOS and Windows.")
    if args.profile:
        PROFILE = args.profile.resolve()
        PORT = 8080 if WINDOWS else 18080
        ENDPOINT = "http://127.0.0.1:%d" % PORT
        if not args.check and not args.service_only:
            sys.exit("For a custom profile use --service-only, then open the matching tModLoader instance yourself. Steam is not opened with an implicit profile.")
    cfg_path = PROFILE / "ModConfigs/TerrarAI_TerrarAI_Config.json"
    if cfg_path.exists():
        config = json.loads(cfg_path.read_text(encoding="utf-8-sig"))
        from urllib.parse import urlparse
        parsed = urlparse(config["BaseEndpoint"])
        if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.username or parsed.password or parsed.query or parsed.fragment:
            sys.exit("This launcher requires an explicit loopback HTTP endpoint; no configuration was changed.")
        PORT = parsed.port or 80
        if PORT < 1 or PORT > 65534:
            sys.exit("The configured port must leave room for the model upstream port (1–65534).")
        ENDPOINT = "http://127.0.0.1:%d" % PORT
        if config["BaseEndpoint"] != ENDPOINT + "/v1/chat/completions":
            sys.exit("The installed mod uses a different endpoint. Check its configuration first.")
        if config.get("Model", ALIAS) != ALIAS:
            sys.exit(f"The installed mod uses a different API alias. This kit serves {ALIAS}; review the mod configuration first.")
    elif not WINDOWS:
        sys.exit("Mod config not found; open the mod once in tModLoader first.")
    if not (PROFILE / "Mods/TerrarAI.tmod").exists():
        sys.exit("TerrarAI.tmod has not been installed in the Steam profile.")
    if not (PROFILE / "Mods/enabled.json").exists() or "TerrarAI" not in json.loads((PROFILE / "Mods/enabled.json").read_text(encoding="utf-8-sig")):
        sys.exit("Enable TerrarAI in the game's Mods menu first.")
    state = ready()
    if state is not None and not has_brain():
        sys.exit("Port %d is occupied by a different or outdated service. It was left running. Stop your known launcher manually, then retry." % PORT)
    if args.check and state is not True:
        sys.exit("Model is not ready. Run this launcher without --check to start it.")
    process = None
    if state is None:
        logs = ROOT / ".runtime/playtest"
        logs.mkdir(exist_ok=True, parents=True)
        with (logs / "qwen.log").open("ab") as output:
            extra = [] if WINDOWS else ["--threads", "2", "--batch-threads", "4", "--cache-mb", "256"]
            if args.cpu:
                extra += ["--cpu"]
            kw = dict(creationflags=0x00000200 | 0x08000000) if WINDOWS else dict(start_new_session=True)  # own group, no console
            process = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/start_qwen.py"), *extra,
                "--port", str(PORT)], cwd=ROOT, stdin=subprocess.DEVNULL,
                stdout=output, stderr=subprocess.STDOUT, **kw)
        (logs / "model-process.json").write_text(json.dumps({"pid": process.pid,
            "endpoint": ENDPOINT + "/v1/chat/completions", "gpu": WINDOWS and not args.cpu and settings(ROOT).get("backend") != "cpu",
            "log": str(logs / "qwen.log")}, indent=2))
    deadline = time.monotonic() + 120
    while ready() is not True:
        if process is not None and process.poll() is not None:
            sys.exit("Model startup failed. See .runtime/playtest/qwen.log.")
        if time.monotonic() > deadline:
            sys.exit("Model is still starting. See .runtime/playtest/qwen.log.")
        time.sleep(1)
    with urllib.request.urlopen(ENDPOINT + "/v1/models", timeout=5) as response:
        models = json.load(response)
    if not any(row.get("id") == ALIAS for row in models.get("data", [])):
        sys.exit("Port %d is serving a different model; it has been left running." % PORT)
    expected = model_path(ROOT)
    try:
        with urllib.request.urlopen(ENDPOINT + "/props", timeout=5) as response:
            actual = json.load(response).get("model_path")
        if not actual or Path(actual).resolve() != expected.resolve():
            sys.exit("Service model differs from the selected GGUF (or model identity is unavailable); it was left running.")
    except (OSError, ValueError) as error:
        sys.exit("Cannot verify service model identity: " + str(error))
    print(f"Ready: {expected.name} at {ENDPOINT} (compatibility alias: {ALIAS})", flush=True)
    print("TerrarAI is installed and enabled. Inference backend: see .runtime/playtest/qwen.log.", flush=True)
    if not args.check and not args.service_only:
        if WINDOWS:
            os.startfile("steam://rungameid/1281930")
        else:
            subprocess.run(["open", "steam://rungameid/1281930"], check=True)
        print("Opened Steam tModLoader. In a single-player world, click Qwen companion, then Check connection.", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit("Launcher stopped: " + str(error))
