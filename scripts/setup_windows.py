"""Windows Alpha setup: pinned runtime/model downloads and optional game-profile install.

Requires Python 3.11+ x64 and Steam tModLoader. --check is read-only. No game is launched here.
"""
import argparse
import json
from pathlib import Path, PurePosixPath
import shutil
import struct
import sys
import tempfile
import uuid
import zipfile
from release_support import atomic_json, download_checked, load_lock, settings, sha256, verified
from winpaths import ROOT, tml_profile


def extract_runtime(archive, target):
    """Flat executable layout, without traversal, symlinks or conflicting DLLs."""
    target = Path(target).resolve()
    with zipfile.ZipFile(archive) as bundle:
        for info in bundle.infolist():
            path = PurePosixPath(info.filename.replace("\\", "/"))
            if path.is_absolute() or ".." in path.parts or any(":" in p for p in path.parts) or (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Unsafe runtime archive member")
            if info.is_dir():
                continue
            dest = target / path.name
            if not dest.resolve().is_relative_to(target):
                raise ValueError("Runtime extraction left its staging directory")
            existing = next((p for p in target.iterdir() if p.name.casefold() == dest.name.casefold()), None)
            data = bundle.read(info)
            if existing and existing.read_bytes() != data:
                raise ValueError("Conflicting runtime archive files: " + path.name)
            if not existing:
                dest.write_bytes(data)


def runtime_valid(root, backend):
    folder = root / ".runtime/llama-win"
    try:
        metadata = json.loads((folder / "build.json").read_text(encoding="utf-8"))
        lock = load_lock(root)["llama"]
        if metadata["tag"] != lock["tag"] or metadata["backend"] != backend or not (folder / "llama-server.exe").is_file():
            return False
        if metadata["artifacts"] != lock["runtimes"][backend] or not metadata.get("files"):
            return False
        if {p.name for p in folder.iterdir() if p.is_file()} != set(metadata["files"]) | {"build.json"}:
            return False
        return all(Path(name).name == name and sha256(folder / name) == digest for name, digest in metadata["files"].items())
    except (OSError, ValueError, KeyError):
        return False


def setup_llama(force=False, backend="cuda", root=None):
    root = root or ROOT
    lock = load_lock(root)["llama"]
    if not force and runtime_valid(root, backend):
        return root / ".runtime/llama-win/llama-server.exe"
    runtime = (root / ".runtime").resolve()
    runtime.mkdir(parents=True, exist_ok=True)
    artifacts = lock["runtimes"][backend]
    archives = [download_checked(a, runtime / "download" / a["file"]) for a in artifacts]
    target = runtime / "llama-win"
    # Validate a complete staged runtime first. Keep a recoverable previous directory.
    with tempfile.TemporaryDirectory(prefix="llama-stage-", dir=runtime) as work:
        stage = Path(work) / "payload"
        stage.mkdir()
        for archive in archives:
            extract_runtime(archive, stage)
        if not (stage / "llama-server.exe").exists():
            raise ValueError("Pinned runtime archive contains no llama-server.exe")
        atomic_json(stage / "build.json", {"tag": lock["tag"], "backend": backend, "artifacts": artifacts,
            "files": {p.name: sha256(p) for p in sorted(stage.iterdir()) if p.is_file()}})
        backup = None
        if target.exists():
            backup = runtime / ("llama-win.previous-" + uuid.uuid4().hex[:12])
            if not target.resolve().is_relative_to(runtime) or not backup.resolve().is_relative_to(runtime):
                raise ValueError("Runtime swap escaped workspace")
            target.rename(backup)
        try:
            stage.rename(target)
        except OSError:
            if backup:
                backup.rename(target)
            raise
    return target / "llama-server.exe"


def install_mod(profile=None, root=None):
    root = root or ROOT
    profile = Path(profile or tml_profile()).resolve()
    if not profile.is_dir():
        raise ValueError("tModLoader profile not found. Start Steam tModLoader once and quit, or pass --profile.")
    source = root / "dist/TerrarAI.tmod"
    if not source.is_file():
        raise ValueError("dist/TerrarAI.tmod missing; use the prepared kit or build from source.")
    mods = profile / "Mods"
    enabled_path = mods / "enabled.json"
    enabled = json.loads(enabled_path.read_text(encoding="utf-8-sig")) if enabled_path.exists() else []
    if not isinstance(enabled, list) or any(not isinstance(x, str) for x in enabled):
        raise ValueError("enabled.json is not a mod-name list; no installation was performed")
    mods.mkdir(parents=True, exist_ok=True)
    dest = mods / "TerrarAI.tmod"
    if dest.exists() and sha256(dest) != sha256(source):
        shutil.copy2(dest, dest.with_suffix(".tmod.bak"))
    staged = dest.with_name("TerrarAI.tmod.tmp")
    shutil.copy2(source, staged)
    staged.replace(dest)
    if "TerrarAI" not in enabled:
        atomic_json(enabled_path, enabled + ["TerrarAI"])
    return dest


def diagnose(root=None, profile=None, size=None, backend=None):
    root = root or ROOT
    cfg = settings(root)
    size, backend = size or cfg.get("size", "14b"), backend or cfg.get("backend", "cuda")
    artifact = load_lock(root)["models"][size]
    profile = Path(profile or tml_profile())
    source, installed = root / "dist/TerrarAI.tmod", profile / "Mods/TerrarAI.tmod"
    return {"pythonSupported": sys.version_info >= (3, 11) and struct.calcsize("P") == 8,
        "backend": backend, "model": artifact["file"], "modelVerified": verified(root / ".models" / artifact["file"], artifact),
        "runtimeVerified": runtime_valid(root, backend), "profileExists": profile.is_dir(),
        "modInstalled": source.is_file() and installed.is_file() and sha256(source) == sha256(installed), "wikiBundled": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", choices=("14b", "8b"))
    parser.add_argument("--backend", choices=("cuda", "cpu"))
    parser.add_argument("--force-llama", action="store_true")
    parser.add_argument("--skip-model", action="store_true", help="Install controls only; no model download")
    parser.add_argument("--no-install", action="store_true", help="Set up kit files only; do not write a game profile")
    parser.add_argument("--profile", type=Path, help="Explicit tModLoader user-data directory")
    parser.add_argument("--check", action="store_true", help="Read-only diagnosis; no downloads, installs or processes")
    args = parser.parse_args()
    if args.check:
        result = diagnose(profile=args.profile, size=args.size, backend=args.backend)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        raise SystemExit(0 if all(result[k] for k in ("pythonSupported", "modelVerified", "runtimeVerified", "modInstalled")) else 1)
    if sys.platform != "win32" or sys.version_info < (3, 11) or struct.calcsize("P") != 8:
        raise SystemExit("Use Windows x64 with Python 3.11+ x64; the prepared kit needs no .NET SDK.")
    if (ROOT / "alpha-manifest.json").exists():
        from verify_alpha import verify
        verify(ROOT)
    cfg = settings(ROOT)
    size, backend = args.size or cfg.get("size", "14b"), args.backend or cfg.get("backend", "cuda")
    setup_llama(args.force_llama, backend)
    artifact = load_lock(ROOT)["models"][size]
    if not args.skip_model:
        from get_model import fetch
        fetch(size, ROOT)
    atomic_json(ROOT / ".runtime/alpha-settings.json", {"size": size, "model": ".models/" + artifact["file"], "backend": backend})
    if not args.no_install:
        print("Installed:", install_mod(args.profile))
    print("Setup finished. Run 开始游玩.bat; --cpu is available for startup diagnostics. Optional Wiki data is not included.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit("Setup stopped: " + str(error))
