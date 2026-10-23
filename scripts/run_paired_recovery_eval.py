"""Run isolated real-model paired diagnostics; no game, training or default service changes."""
import argparse
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL_4B = {"sha256": "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
               "revision": "bc640142c66e1fdd12af0bd68f40445458f3869b"}


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def get_json(url):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(url, timeout=2) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True)
    parser.add_argument("--policies", default="rule,4b,14b")
    parser.add_argument("--seeds", default="11,29,47")
    parser.add_argument("--gpu-layers-4b", type=int, default=99)
    parser.add_argument("--gpu-layers-14b", type=int, default=99)
    parser.add_argument("--port", type=int, default=19110)
    parser.add_argument("--only", default="")
    args = parser.parse_args()
    policies = args.policies.split(",")
    if not policies or len(set(policies)) != len(policies) or set(policies) - {"rule", "4b", "14b"}:
        parser.error("Use distinct rule,4b,14b policies")
    out = Path(args.out).resolve()
    if out.exists():
        parser.error("Use a fresh output directory")
    # Do not terminate or reuse an unrelated server on our chosen port.
    with socket.socket() as probe:
        if probe.connect_ex(("127.0.0.1", args.port)) == 0:
            parser.error("Private evaluation port is already occupied")
    out.mkdir(parents=True)
    runtime = ROOT / ".runtime/llama-win/llama-server.exe"
    dll = runtime.with_name("llama-server-impl.dll")
    args_file = out / "run.json"
    args_file.write_text(json.dumps({**vars(args), "runtime_sha256": digest(runtime),
        "runtime_impl_sha256": digest(dll), "no_training": True, "native_game": False,
        "note": "Local original Qwen3-4B, NOT Qwen3-4B-Instruct-2507; this diagnoses existing checkpoints, not the planned 2507 SFT base."}, indent=2), encoding="utf-8")
    build = subprocess.run(["dotnet", "build", "evals/AgentEval.csproj", "--nologo"], cwd=ROOT)
    if build.returncode:
        raise SystemExit(build.returncode)
    failures = []
    for policy in policies:
        process = None
        server_log = None
        identity_path = out / (policy + "-identity.json")
        try:
            if policy != "rule":
                model = ROOT / ".models" / ("Qwen3-4B-Q4_K_M.gguf" if policy == "4b" else "Qwen3-14B-Q4_K_M.gguf")
                sha = digest(model)
                if policy == "14b":
                    lock = json.loads((ROOT / "release/dependencies.lock.json").read_text(encoding="utf-8"))["models"]["14b"]
                    if sha != lock["sha256"]:
                        raise RuntimeError("14B differs from pinned deployed checkpoint")
                else:
                    # Same pin as the legacy preparation script; no private benchmark dependency.
                    if sha != ORIGINAL_4B["sha256"]:
                        raise RuntimeError("4B differs from existing pinned original checkpoint")
                command = [str(runtime), "-m", str(model), "--alias", "paired-eval", "--host", "127.0.0.1",
                    "--port", str(args.port), "-t", "8", "-tb", "8", "-np", "1", "-c", "8192",
                    "--cache-ram", "256", "--reasoning", "off", "--no-webui", "--offline",
                    "-ngl", str(args.gpu_layers_4b if policy == "4b" else args.gpu_layers_14b), "-b", "512", "-ub", "128", "--flash-attn", "on",
                    "--cache-type-k", "q8_0", "--cache-type-v", "q8_0"]
                identity_path.write_text(json.dumps({"model_path": str(model), "sha256": sha,
                    "model_name": "Qwen3-4B-Q4_K_M" if policy == "4b" else "Qwen3-14B-Q4_K_M",
                    "task_finetuned": False, "model_source": "Qwen/Qwen3-4B-GGUF" if policy == "4b" else lock["repo"],
                    "source_revision": ORIGINAL_4B["revision"] if policy == "4b" else lock["revision"],
                    "command": command}, indent=2), encoding="utf-8")
                server_log = (out / (policy + "-server.log")).open("w", encoding="utf-8")
                process = subprocess.Popen(command, cwd=ROOT, stdout=server_log, stderr=subprocess.STDOUT,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                (out / (policy + "-pid.json")).write_text(json.dumps({"pid": process.pid}), encoding="utf-8")
                deadline = time.monotonic() + 180
                while True:
                    if process.poll() is not None:
                        raise RuntimeError(f"Owned model server exited {process.returncode}; see server log")
                    try:
                        if get_json(f"http://127.0.0.1:{args.port}/health").get("status") == "ok":
                            break
                    except Exception:
                        pass
                    if time.monotonic() > deadline:
                        raise TimeoutError("Owned model server did not become ready")
                    time.sleep(1)
                print(f"READY {policy}, pid={process.pid}, sha256={sha}", flush=True)
            command = ["dotnet", "run", "--no-build", "--project", "evals/AgentEval.csproj", "--",
                "--paired-recovery-bench", "--policy", policy, "--seeds", args.seeds,
                "--out", str(out / policy), "--endpoint", f"http://127.0.0.1:{args.port}/v1/chat/completions"]
            if policy != "rule":
                command += ["--identity", str(identity_path)]
            if args.only:
                command += ["--only", args.only]
            with (out / (policy + "-eval.log")).open("w", encoding="utf-8") as log:
                result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
            print(f"FINISHED {policy}, exit={result.returncode}", flush=True)
            if result.returncode:
                failures.append(policy)
        except Exception as error:
            (out / (policy + "-error.txt")).write_text(str(error), encoding="utf-8")
            failures.append(policy)
            print(f"ERROR {policy}: {error}", flush=True)
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            if server_log:
                server_log.close()
    (out / "finished.json").write_text(json.dumps({"failed_policies": failures}), encoding="utf-8")
    return bool(failures)


if __name__ == "__main__":
    raise SystemExit(main())
