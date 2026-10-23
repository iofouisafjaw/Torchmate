"""Check the replacement inventory and hashes; optionally screen against pinned upstream.

This is a technical source-integrity check, not a legal clearance decision.
--record refreshes evidence after an intentional review; it cannot approve release.
"""
import argparse
from datetime import datetime, timezone
import difflib
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "release/source-provenance.json"
CONTRACT = "Content/Actions/AgentAction.cs"
LEX = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\w+|[^\s]')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def surface(root):
    names = {"TerrarAI.cs", "TerrarAI_Config.cs", "TerrarAI.csproj", "build.txt", "icon.png", "icon_small.png",
             "assets/companion-icon.svg", "release/config-compatibility.json", "docs/source-replacement.md",
             "README.md", "LICENSE", "LICENSE_STATUS.md", "THIRD_PARTY_NOTICES.md", "description.txt", "description_workshop.txt"}
    for folder, suffixes in (("Content", (".cs",)), ("Common", (".cs",)), ("Localization", (".hjson",)),
                              ("scripts", (".py",))):
        names.update(p.relative_to(root).as_posix() for p in (root / folder).rglob("*")
                     if p.is_file() and p.suffix in suffixes and "__pycache__" not in p.parts)
    return sorted(names)


def tokens(source):
    return [m.group() for m in LEX.finditer(source) if not m.group().startswith(("//", "/*"))]


def screen(root, audit):
    pin = audit["upstream"]["commit"]
    names = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", pin], cwd=root, text=True).splitlines()
    # Explicit C# suffix: .env and other historical private files are never read.
    inherited = {name: tokens(subprocess.check_output(["git", "show", f"{pin}:{name}"], cwd=root).decode("utf-8-sig"))
                 for name in names if name.endswith(".cs")}
    matches = []
    for name in surface(root):
        if not name.endswith(".cs"):
            continue
        current = tokens((root / name).read_text(encoding="utf-8-sig"))
        for origin, previous in inherited.items():
            match = difflib.SequenceMatcher(None, previous, current, autojunk=False).find_longest_match()
            if match.size < 75:
                continue
            shared = current[match.b:match.b + match.size]
            # Public result/argument declarations kept for existing local callers.
            compatibility = (name == origin == CONTRACT and "AgentActionResult" in shared and
                             "WorldGen" not in shared and "OnTick" not in shared and "_terminal" not in shared and match.size <= 150)
            matches.append({"file": name, "upstreamFile": origin, "tokens": match.size,
                            "review": "public-result-contract" if compatibility else "needs-review"})
    return {"thresholdTokens": 75, "method": "longest contiguous lexical token match across current and pinned C# files",
            "scannedCurrentCSharpFiles": sum(name.endswith(".cs") for name in surface(root)),
            "inheritedCSharpFiles": len(inherited), "matches": matches,
            "unreviewedMatches": sum(m["review"] == "needs-review" for m in matches),
            "limitation": "A screening aid; smaller/reworked fragments and legal status require provenance review. Not a clean-room claim."}


def verify(root=ROOT):
    manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8"))
    if set(manifest["files"]) != set(surface(root)):
        raise ValueError("Source provenance surface differs from reviewed inventory")
    for name, expected in manifest["files"].items():
        path = root / name
        if not path.resolve().is_relative_to(root.resolve()) or not path.is_file() or digest(path) != expected["sha256"]:
            raise ValueError("Provenance hash mismatch: " + name)
    for name in manifest["removed"]:
        if (root / name).exists():
            raise ValueError("Removed inherited input has returned: " + name)
    for item in manifest["upstreamFiles"]:
        if item["disposition"] == "removed":
            continue
        if digest(root / item["path"]) == item["upstreamSha256"]:
            raise ValueError("Unchanged inherited input remains: " + item["path"])
    if manifest["screening"]["unreviewedMatches"]:
        raise ValueError("Source comparison has unreviewed matches")
    return {"passed": True, "files": len(manifest["files"]), "inheritedInputs": len(manifest["upstreamFiles"]),
            "removed": len(manifest["removed"]), "publicDistributionApproved": False}


def record(root=ROOT):
    audit = json.loads((root / "release/license-audit.json").read_text(encoding="utf-8"))
    review = screen(root, audit)
    if review["unreviewedMatches"]:
        raise ValueError(json.dumps(review, indent=2))
    removed = [item["path"] for item in audit["upstream"]["affectedFiles"] if not (root / item["path"]).exists()]
    removed += ["Content/Systems/ModelRouter.cs", "plan.md"]
    manifest = {"schemaVersion": 1, "version": re.search(r"^version\s*=\s*(\S+)", (root / "build.txt").read_text(encoding="utf-8"), re.M).group(1), "reviewedAt": datetime.now(timezone.utc).date().isoformat(), "upstreamCommit": audit["upstream"]["commit"],
                "publicDistributionApproved": False, "cleanRoomClaim": False,
                "review": "docs/source-replacement.md", "screening": review, "removed": removed,
                "upstreamFiles": [{"path": item["path"], "upstreamSha256": item["upstreamSha256"],
                   "preReplacementSha256": item.get("preReplacementSha256", item["currentSha256"]),
                   "disposition": "removed" if item["path"] in removed else "original-code-native-art" if item["path"].endswith(".png") else
                                  "compatibility-declarations-recreated" if item["path"] == "TerrarAI_Config.cs" else "implementation-replaced-local-integration-retained"}
                   for item in audit["upstream"]["affectedFiles"]],
                "files": {name: {"sha256": digest(root / name)} for name in surface(root)}}
    (root / MANIFEST).write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return verify(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record", action="store_true")
    parser.add_argument("--compare-upstream", action="store_true")
    args = parser.parse_args()
    if args.record:
        result = record()
    else:
        result = verify()
        if args.compare_upstream:
            result["comparison"] = screen(ROOT, json.loads((ROOT / "release/license-audit.json").read_text(encoding="utf-8")))
            if result["comparison"]["unreviewedMatches"]:
                raise ValueError("Unreviewed source overlap")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
