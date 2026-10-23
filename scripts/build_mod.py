"""Compile and package dist/TerrarAI.tmod through the shared MSBuild staging target."""
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    sdk = ROOT / ".runtime/dotnet"
    local_dotnet = sdk / "dotnet"
    use_local_sdk = sys.platform != "win32" and local_dotnet.exists()
    dotnet = str(local_dotnet) if use_local_sdk else shutil.which("dotnet")
    if not dotnet:
        sys.exit("Install the .NET 8 SDK, or build through tModLoader's Develop Mods menu. See README.md.")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", PYTHONUTF8="1", TERRARAI_PYTHON=sys.executable)
    if use_local_sdk:
        env.update(DOTNET_ROOT=str(sdk), DOTNET_CLI_HOME=str(ROOT / ".runtime/dotnet-home"),
                   PATH=str(sdk) + os.pathsep + env.get("PATH", ""))
    subprocess.run([dotnet, "build", str(ROOT / "TerrarAI.csproj"), "-p:BuildMod=true", "--nologo"],
                   cwd=ROOT, env=env, check=True)
    print("Ready:", ROOT / "dist/TerrarAI.tmod")


if __name__ == "__main__":
    main()
