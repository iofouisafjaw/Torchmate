"""Start the project-local Qwen server plus the TerrarAI brain. Ctrl-C shuts both down.

    mod -> :PORT brain (advice: rules + Wiki + model; everything else forwarded) -> :PORT+1 llama-server

Mac (Intel): CPU build in .runtime/b11609, small context, few threads (unchanged from before).
Windows:     CUDA build in .runtime/llama-win (scripts/setup_windows.py), all layers on the GPU.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import threading
from release_support import ALIAS, model_path, settings

ROOT = Path(__file__).resolve().parents[1]
WINDOWS = sys.platform == "win32"


def default_model():
    """Prefer the biggest downloaded Qwen3 (14B > 8B > 4B); the mod talks to it under the same alias."""
    return model_path(ROOT)


def inference_profile(root=None):
    """Local, reversible memory settings shared by both startup entry points."""
    path = (root or ROOT) / ".runtime/model-inference.json"
    if not path.exists():
        return {}
    profile = json.loads(path.read_text(encoding="utf-8-sig"))
    limits = {"ctx": (512, 131072), "cache_mb": (0, 32768),
              "batch_size": (1, 4096), "ubatch_size": (1, 4096)}
    choices = {"kv_cache": ("f16", "q8_0"), "flash_attention": ("auto", "on", "off")}
    if not isinstance(profile, dict) or set(profile) - (set(limits) | set(choices)):
        raise ValueError("Unknown model-inference.json fields")
    for name, value in profile.items():
        if name in limits and (type(value) is not int or not limits[name][0] <= value <= limits[name][1]):
            raise ValueError("Invalid inference setting: " + name)
        if name in choices and value not in choices[name]:
            raise ValueError("Unsupported inference setting: " + name)
    return profile


def memory_settings(args, profile, cpu):
    defaults = {"ctx": 12288 if WINDOWS else 2048, "cache_mb": 1024 if WINDOWS else 256,
                "batch_size": 2048 if WINDOWS and not cpu else 256,
                "ubatch_size": 512 if WINDOWS and not cpu else 128,
                "kv_cache": "f16", "flash_attention": "auto"}
    result = {name: getattr(args, name, None) if getattr(args, name, None) is not None else profile.get(name, value)
              for name, value in defaults.items()}
    if result["ctx"] < 512 or result["cache_mb"] < 0 or not 1 <= result["ubatch_size"] <= result["batch_size"] <= 4096:
        raise ValueError("Invalid context/cache/batch settings; require 1 <= ubatch <= batch <= 4096")
    if WINDOWS and not cpu and result["kv_cache"] != "f16" and result["flash_attention"] == "off":
        raise ValueError("Quantized GPU KV cache requires flash attention")
    if cpu or not WINDOWS:
        result["kv_cache"], result["flash_attention"] = "f16", "auto"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--threads", type=int, default=4 if WINDOWS else 2)
    parser.add_argument("--batch-threads", type=int, default=8 if WINDOWS else 4)
    parser.add_argument("--cache-mb", type=int, help="Host RAM prompt cache; local profile or original default")
    parser.add_argument("--ctx", type=int, help="Context tokens; overrides the local memory profile")
    parser.add_argument("--batch-size", type=int)
    parser.add_argument("--ubatch-size", type=int)
    parser.add_argument("--kv-cache", choices=("f16", "q8_0"))
    parser.add_argument("--flash-attention", choices=("auto", "on", "off"))
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--cpu", action="store_true", help="Windows: do not use the GPU")
    parser.add_argument("--model", help="GGUF file (default: the largest Qwen3 in .models)")
    parser.add_argument("--no-brain", action="store_true", help="serve llama-server directly on --port")
    args = parser.parse_args()
    if args.port < 1 or args.port > 65534:
        parser.error("--port must leave room for the model upstream port (1–65534)")
    cpu = args.cpu or WINDOWS and settings(ROOT).get("backend") == "cpu"
    memory = memory_settings(args, inference_profile(), cpu)
    if WINDOWS:
        binary = ROOT / ".runtime/llama-win/llama-server.exe"
        hint = "Run  python scripts/setup_windows.py  first."
    else:
        binary = ROOT / ".runtime/b11609/llama-b11609/llama-server"
        hint = "Run python3 benchmarks/prepare.py first."
    model = Path(args.model) if args.model else default_model()
    if not binary.exists() or not model.exists():
        sys.exit(hint)
    llama_port = args.port if args.no_brain else args.port + 1
    command = [str(binary), "-m", str(model), "--alias", ALIAS, "--host", "127.0.0.1",
               "--port", str(llama_port), "-t", str(args.threads), "-tb", str(args.batch_threads),
               "-np", "1", "-c", str(memory["ctx"]), "--cache-ram", str(memory["cache_mb"]),
               "--reasoning", "off", "--no-webui", "--offline"]
    if WINDOWS and not cpu:
        command += ["-ngl", "99", "-b", str(memory["batch_size"]), "-ub", str(memory["ubatch_size"]),
                    "--flash-attn", memory["flash_attention"], "--cache-type-k", memory["kv_cache"],
                    "--cache-type-v", memory["kv_cache"]]  # weights stay fully on the GPU
    else:
        command += ["-ngl", "0", "--no-op-offload", "-b", str(memory["batch_size"]), "-ub", str(memory["ubatch_size"])]
    print("Model:", model.name, flush=True)
    print("Memory profile:", json.dumps(memory), flush=True)
    print("Local endpoint: http://127.0.0.1:%s/v1/chat/completions" % args.port, flush=True)
    process = subprocess.Popen(command)
    if not args.no_brain:
        sys.path.insert(0, str(ROOT / "scripts/brain"))
        from server import serve
        httpd = serve(args.port, "http://127.0.0.1:%d" % llama_port)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
    try:
        sys.exit(process.wait())
    except KeyboardInterrupt:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit("Model startup stopped: " + str(error))
