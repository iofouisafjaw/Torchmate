"""Windows: run the core tests, compile the mod, package TerrarAI.tmod and install it.

Needs the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8) and Steam tModLoader.
Close tModLoader first (Windows keeps the loaded .tmod file locked).

    python scripts/build_windows.py [--tml "E:\\SteamLibrary\\steamapps\\common\\tModLoader"] [--skip-tests]
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from winpaths import tml_profile  # noqa: E402


def find_tml():
    candidates = [Path(r"E:\SteamLibrary\steamapps\common\tModLoader")]
    try:  # Steam's other library folders
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
        candidates.append(steam / "steamapps/common/tModLoader")
        vdf = (steam / "steamapps/libraryfolders.vdf").read_text(encoding="utf-8", errors="ignore")
        for lib in re.findall(r'"path"\s+"([^"]+)"', vdf):
            candidates.append(Path(lib.replace("\\\\", "\\")) / "steamapps/common/tModLoader")
    except Exception:
        pass
    for c in candidates:
        if (c / "tModLoader.dll").exists():
            return c
    return None


LOG = ROOT / ".runtime/build-last.log"


def run(cmd, **kw):
    """Run and echo a command; everything is also kept in .runtime/build-last.log for diagnosis."""
    line = "> " + " ".join(str(c) for c in cmd)
    print(line, flush=True)
    LOG.parent.mkdir(exist_ok=True)
    with LOG.open("a", encoding="utf-8") as log:
        log.write(line + "\n")
        proc = subprocess.Popen([str(c) for c in cmd], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **kw)
        for raw in proc.stdout:
            text = raw.decode("utf-8", errors="replace")
            print(text, end="", flush=True)
            log.write(text)
        code = proc.wait()
        log.write(f"[exit {code}]\n")
    if code:
        sys.exit(f"Failed (exit {code}). Full output: {LOG}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tml", help="tModLoader install folder")
    ap.add_argument("--skip-tests", action="store_true")
    ap.add_argument("--no-install", action="store_true", help="only build dist/TerrarAI.tmod; do not change the game profile")
    a = ap.parse_args()
    dotnet = shutil.which("dotnet")
    sdks = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True).stdout if dotnet else ""
    if not re.search(r"^8\.", sdks, re.M):
        sys.exit(".NET 8 SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8   (then open a new window)")
    tml = Path(a.tml).resolve() if a.tml else find_tml()
    if not tml or not (tml / "tModLoader.dll").exists():
        sys.exit("tModLoader not found; pass --tml <install folder>.")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               PYTHONUTF8="1", TERRARAI_PYTHON=sys.executable)
    LOG.parent.mkdir(exist_ok=True)
    LOG.write_text("", encoding="utf-8")

    if not a.skip_tests:
        run([sys.executable, ROOT / "scripts/check_provenance.py"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/CompanionTests.csproj"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/native_replacement/NativeReplacementTests.csproj", "--", ROOT], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/terrain_route/TerrainRouteTests.csproj"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/tunnel_workshop/TunnelWorkshopTests.csproj"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/recovery_adapter/RecoveryAdapterTests.csproj"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/collaboration_adapter/CollaborationAdapterTests.csproj"], env=env, cwd=ROOT)
        run([dotnet, "run", "--project", ROOT / "tests/recovery_eval/RecoveryEvalTests.csproj"], env=env, cwd=ROOT)
        run([sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py"], env=env, cwd=ROOT)
        if (ROOT / ".runtime/wiki-v010-index.db").exists():
            run([sys.executable, ROOT / "scripts/brain/test_brain.py"], env=env, cwd=ROOT)

    # The project owns the whitelist, clean staging, isolated profile and dist export.
    run([dotnet, "build", ROOT / "TerrarAI.csproj", f"-p:TModLoaderPath={tml}", "-p:BuildMod=true", "--nologo"],
        env=env, cwd=ROOT)
    built = ROOT / "dist/TerrarAI.tmod"
    if not a.skip_tests:
        run([dotnet, "run", "--project", ROOT / "tests/collaboration_wiring/CollaborationWiringTests.csproj",
             f"-p:TModLoaderPath={tml}", "--", ROOT / "bin/Debug/net8.0/TerrarAI.dll"], env=env, cwd=ROOT)
    if a.no_install:
        print("Ready:", built)
        return
    mods = tml_profile() / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(built, mods / "TerrarAI.tmod")
        print("Installed:", mods / "TerrarAI.tmod")
    except PermissionError:
        sys.exit("Built dist/TerrarAI.tmod, but the installed copy is locked: close tModLoader and run this again.")
    print("Done. Start the game with 开始游玩.bat")


if __name__ == "__main__":
    main()
