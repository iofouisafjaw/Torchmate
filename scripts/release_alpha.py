"""Prepare local Windows/source Alpha archives; never publish or push anything.

Without --prepare-only, unresolved release gates stop archive creation.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import io
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DOCS = ("README.md", "LICENSE", "LICENSE_STATUS.md", "THIRD_PARTY_NOTICES.md", "CHANGELOG.md", "build.txt",
        "release/dependencies.lock.json", "release/license-audit.json", "release/acceptance.json",
        "docs/windows-alpha.md", "docs/internal/release-readiness.md", "docs/internal/README.md", "docs/README.md", "docs/demo.md",
        "assets/demo-preview.gif",
        "docs/collaboration.md", "docs/recovery-eval.md", "docs/paired-recovery-eval.md", "docs/source-replacement.md",
        "release/source-provenance.json", "release/config-compatibility.json",
        "docs/playtest.md", "docs/internal/playtest-0.10.30.md", "docs/internal/playtest-0.10.32.md",
        "docs/ore-tunnel.md", "docs/internal/demo-fixes-0.10.34.md", "docs/model-memory.md",
        "docs/learning-loop.md", "docs/architecture.md", "docs/internal/github-preparation.md",
        "docs/wiki-v010-provenance.json", "docs/offline-wiki-sources.json")
RUN_SCRIPTS = ("winpaths.py", "release_support.py", "get_model.py", "setup_windows.py", "start_qwen.py", "play_local.py",
               "verify_alpha.py", "wiki_html.py", "import_wiki.py", "import_calamity_wiki.py")
BOOTSTRAPS = ("安装到Windows.bat", "开始游玩.bat", "启动模型.bat", "下载14B模型.bat", "检查安装.bat")


def check_mod(data, expected_version):
    stream = io.BytesIO(data)
    def string():
        size = 0
        for shift in range(0, 35, 7):
            raw = stream.read(1)
            if not raw:
                raise ValueError("Truncated mod header")
            size |= (raw[0] & 127) << shift
            if raw[0] < 128:
                return stream.read(size).decode("utf-8")
        raise ValueError("Invalid mod string")
    if stream.read(4) != b"TMOD":
        raise ValueError("Invalid mod header")
    string()
    digest = stream.read(20)
    stream.read(256)
    raw = stream.read(4)
    if len(raw) != 4:
        raise ValueError("Truncated mod header")
    size = struct.unpack("<i", raw)[0]
    payload = data[stream.tell():]
    if size != len(payload) or hashlib.sha1(payload).digest() != digest:
        raise ValueError("Corrupt mod package")
    if string() != "TerrarAI" or string() != expected_version:
        raise ValueError("Stale or different mod package; build the current version first")


def release_gates(root, kind="windows"):
    audit = json.loads((root / "release/license-audit.json").read_text(encoding="utf-8"))
    acceptance = json.loads((root / "release/acceptance.json").read_text(encoding="utf-8"))
    source = kind == "source"
    approval = audit.get("sourcePublication", {})
    source_approved = (approval.get("approved") is True and
                       approval.get("scope") == "allowlisted-history-free-source" and
                       approval.get("historyExcluded") is True)
    # The old checkout remains private. Only an allowlisted export can use this
    # source approval; it never approves a direct push of the inherited history.
    blockers = [x["id"] for x in audit["blockers"] if x["status"] != "resolved"
                and not (source and source_approved and x["id"] == "tracked-dotenv-review")]
    version = re.search(r"^version\s*=\s*(\S+)", (root / "build.txt").read_text(encoding="utf-8"), re.M).group(1)
    if acceptance.get("version") != version:
        blockers.append("acceptance-version-mismatch")
    if not (source_approved if source else audit.get("publicDistributionApproved") is True):
        blockers.append("distribution-not-approved")
    if not source and not acceptance.get("nativeAcceptancePassed"):
        blockers.append("player-native-acceptance-pending")
    if not source and not acceptance.get("cleanWindowsInstallPassed"):
        blockers.append("clean-windows-install-pending")
    for name, digest in audit.get("licenseTextSha256", {}).items():
        if not (root / name).is_file() or hashlib.sha256((root / name).read_bytes()).hexdigest() != digest:
            blockers.append("license-text-missing-or-modified:" + name)
    if audit.get("replacementManifest"):
        try:
            from check_provenance import verify
            verify(root)
        except (ValueError, OSError, KeyError):
            blockers.append("source-provenance-invalid")
    return blockers


def inputs(root, kind):
    names = set(DOCS)
    names.update(p.relative_to(root).as_posix() for p in (root / "licenses").glob("*.txt"))
    names.update("scripts/" + p for p in RUN_SCRIPTS)
    names.add("scripts/run_python.bat")
    names.update(p.relative_to(root).as_posix() for p in (root / "scripts/brain").glob("*.py") if not p.name.startswith("test_"))
    names.update(BOOTSTRAPS)
    if kind == "windows":
        names.add("dist/TerrarAI.tmod")
    else:
        names.update(p.relative_to(root).as_posix() for p in (root / "scripts").glob("*.py"))
        names.update(p.relative_to(root).as_posix() for p in (root / "scripts/brain").glob("test_*.py"))
        names.update(("TerrarAI.cs", "TerrarAI_Config.cs", "TerrarAI.csproj", "description.txt", "description_workshop.txt", "icon.png", "icon_small.png", ".gitignore"))
        names.add("assets/companion-icon.svg")
        names.update(("DESIGN.md", "CONTRIBUTING.md", "SECURITY.md", ".editorconfig", ".gitattributes",
                      "docs/internal/reference-code-audit.json"))
        for folder in ("Common", "Content", "Localization", "tests", "evals", ".github"):
            for p in (root / folder).rglob("*"):
                if folder == "evals" and p.name == "report.md":
                    continue
                if folder == "tests" and (p.name.startswith("verification") or any(x in p.relative_to(root).parts for x in ("game_smoke", "interaction_smoke", "supply_smoke", "supply_client")) or p.name.startswith("run_")):
                    continue
                if p.is_file() and not any(x in p.relative_to(root).parts for x in ("bin", "obj", "__pycache__")) and p.suffix.lower() in (".cs", ".csproj", ".py", ".hjson", ".json", ".yml", ".md"):
                    names.add(p.relative_to(root).as_posix())
        names.update("scripts/" + p for p in ("build_windows.py", "build_mod.py", "release_alpha.py", "run_evals.py", "analyze_recovery_eval.py"))
    for name in names:
        path = root / name
        if not path.is_file() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("Missing or external release input: " + name)
        if path.resolve() != root.resolve() / name:
            raise ValueError("Linked release input rejected: " + name)
        if any(p in name.split("/") for p in (".env", ".git", ".models", ".runtime", "benchmarks", "refine-logs")):
            raise ValueError("Private release input rejected: " + name)
    return sorted(names)


def prepare(root=None, kind="windows", prepare_only=False):
    root = root or ROOT
    blockers = release_gates(root, kind)
    if blockers and not prepare_only:
        raise ValueError("Release blocked: " + ", ".join(blockers) + ". Use --prepare-only only for a local, non-distributable preparation archive.")
    version = re.search(r"^version\s*=\s*(\S+)", (root / "build.txt").read_text(encoding="utf-8"), re.M).group(1)
    if not re.fullmatch(r"\d+(?:\.\d+){1,3}", version):
        raise ValueError("Invalid mod version")
    names = inputs(root, kind)
    contents = {name: (root / name).read_bytes() for name in names}
    if kind == "windows":
        check_mod(contents["dist/TerrarAI.tmod"], version)
    private = prepare_only or bool(blockers)
    if private:
        contents["NOT_FOR_DISTRIBUTION.txt"] = ("LOCAL PREPARATION ONLY. Do not upload or redistribute this archive.\nOpen release gates: " + ", ".join(blockers) + "\n").encode()
    manifest = {"schemaVersion": 1, "version": version, "kind": kind, "publicDistributionApproved": not private,
        "blockers": blockers, "containsWeights": False, "containsRuntime": False, "containsWiki": False,
        "files": {name: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()} for name, data in sorted(contents.items())}}
    contents["alpha-manifest.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode()
    out = root / "dist/alpha-preparation" if private else root / "dist/alpha"
    out.mkdir(parents=True, exist_ok=True)
    archive = out / f"TerrarAI-{version}-{kind}-{'PRIVATE-PREP' if private else 'alpha'}.zip"
    temporary = archive.with_suffix(".zip.tmp")
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name, data in sorted(contents.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 20, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            bundle.writestr(info, data)
    temporary.replace(archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    return {"archive": archive.name, "sha256": digest, "bytes": archive.stat().st_size, "files": len(contents), "publicDistributionApproved": not private, "blockers": blockers}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--kind", choices=("windows", "source"), default="windows")
    parser.add_argument("--check", action="store_true", help="Report gates only; write no archives")
    args = parser.parse_args()
    if args.check:
        blockers = release_gates(ROOT, args.kind)
        print(json.dumps({"ready": not blockers, "blockers": blockers}, indent=2))
        raise SystemExit(2 if blockers else 0)
    print(json.dumps(prepare(kind=args.kind, prepare_only=args.prepare_only), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError) as error:
        raise SystemExit(str(error))
