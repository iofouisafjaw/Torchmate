"""Run the companion's offline evaluation (evals/) against the local Qwen.

    python scripts/run_evals.py [--runs 3] [--only 关键词] [--no-kb]

Starts the model service first if it is not already running (and stops it again afterwards),
then plays every case in evals/cases.json and writes evals/report.md plus a line in evals/history.csv.
The game does not need to be open.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
HEALTH = "http://127.0.0.1:8080/health"


def healthy():
    try:
        with urllib.request.urlopen(HEALTH, timeout=3) as r:
            return r.status == 200 and json.load(r).get("ok", True)
    except Exception:
        return False


def stop(server):
    """start_qwen.py runs llama-server as a child: take the whole tree down, not just the launcher."""
    if sys.platform == "win32":
        subprocess.call(["taskkill", "/PID", str(server.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        server.terminate()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--only")
    parser.add_argument("--endpoint")
    parser.add_argument("--no-kb", action="store_true")
    parser.add_argument("--validate", action="store_true", help="Validate fixtures only; never contact or start a model")
    parser.add_argument("--recovery-bench", action="store_true")
    for key in ("cases", "split", "seeds", "arms", "out"):
        parser.add_argument("--" + key)
    options = parser.parse_args()  # Help and invalid options exit before any process/network work.
    if options.validate and options.recovery_bench:
        parser.error("--validate is the ordinary fixture validator; do not combine it with --recovery-bench")
    dotnet = shutil.which("dotnet")
    sdks = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True).stdout if dotnet else ""
    if not re.search(r"^8\.", sdks, re.M):
        sys.exit(".NET 8 SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8")
    command = [dotnet, "run", "--project", str(ROOT / "evals/AgentEval.csproj"), "-c", "Release", "--", *sys.argv[1:]]
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    if options.validate:
        return subprocess.call(command, cwd=ROOT, env=env)
    server = None
    if not healthy():
        print("模型没在运行，先启动它（第一次加载要一会儿）……", flush=True)
        flags = subprocess.CREATE_NEW_CONSOLE if sys.platform == "win32" else 0
        server = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/start_qwen.py")], cwd=ROOT, creationflags=flags)
        for _ in range(240):
            if healthy():
                break
            if server.poll() is not None:
                sys.exit("模型服务启动失败，看看弹出的窗口里的报错。")
            time.sleep(1)
        else:
            stop(server)
            sys.exit("等了 4 分钟模型还没准备好。")
    try:
        code = subprocess.call(command, cwd=ROOT, env=env)
    finally:
        if server is not None:
            stop(server)
    print("\n详细报告在：", ROOT / "evals/report.md")
    return code


if __name__ == "__main__":
    sys.exit(main())
