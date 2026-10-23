"""Collect the latest local evidence; does not run tests or manufacture missing results."""
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read_json(path):
    return json.loads(path.read_text()) if path.exists() else None


def checks(path):
    text = path.read_text() if path.exists() else ""
    counts = re.findall(r"Passed (\d+) checks\.", text)
    return {"completed": bool(counts) and "FAIL:" not in text and "Unhandled exception" not in text,
            "checks": int(counts[-1]) if counts else None, "log": str(path.relative_to(ROOT))}


def main():
    runtime = ROOT / ".runtime"
    live = read_json(runtime / "pahf-live-results.json")
    profile = runtime / "tml-smoke-profile"
    game = read_json(profile / "smoke-result.json")
    measurements = read_json(profile / "smoke-measurements.json")
    package = ROOT / "dist/TerrarAI.tmod"
    paths = [ROOT / p for p in ["TerrarAI.cs", "TerrarAI_Config.cs", "build.txt", "TerrarAI.csproj"]]
    for folder in ["Content", "Common", "Localization"]:
        paths.extend(p for p in (ROOT / folder).rglob("*") if p.is_file())
    source_hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(paths)}
    recalls = [r for r in (live or {}).get("results", []) if r.get("kind") == "recall"]
    protocol = (ROOT / "Content/Companion/CompanionProtocol.cs").read_text()
    current_system = protocol.split('SystemPrompt = """', 1)[1].split('"""', 1)[0]
    # C# raw string literals remove their common indentation.
    import textwrap
    current_system = textwrap.dedent(current_system.strip("\n")).rstrip()
    report_log = (runtime / "report-tests.log").read_text() if (runtime / "report-tests.log").exists() else ""
    evidence = {
        "version": "0.4", "model": "Qwen3-4B Q4_K_M", "training": False,
        "build": {"package": str(package.relative_to(ROOT)),
            "package_sha256": hashlib.sha256(package.read_bytes()).hexdigest() if package.exists() else None,
            "source_sha256": hashlib.sha256(json.dumps(source_hashes, sort_keys=True).encode()).hexdigest()},
        "core": checks(runtime / "core-tests.log"),
        "pahf_development": {**checks(runtime / "pahf-tests.log"), "held_out": False,
            "system_matches_current_source": bool(live) and live.get("system") == current_system,
            "recall_passed": sum(r["passed"] for r in recalls), "recall_total": len(recalls),
            "cases": recalls},
        "action_regression": checks(runtime / "action-regression.log"),
        "report_tests": {"completed": "\nOK\n" in report_log, "log": ".runtime/report-tests.log"},
        "game": game, "game_measurements": measurements,
        "limits": ["No GUI, physics, pathfinding, FPS, multiplayer, or LoRA evaluation.",
            "Development cases informed prompt changes; no held-out learning or SkillEvo improvement claim.",
            "Direct target receipts do not verify item attribution or complete task success.",
            "Full A0/A1 ablations, token telemetry, and skill evolution remain unimplemented."]
    }
    (ROOT / "tests/verification.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({key: evidence[key] for key in ["version", "core", "action_regression"]}, ensure_ascii=False))
    print("PAHF recalls:", evidence["pahf_development"]["recall_passed"], "/", len(recalls),
          "; system matches:", evidence["pahf_development"]["system_matches_current_source"])


if __name__ == "__main__": main()
