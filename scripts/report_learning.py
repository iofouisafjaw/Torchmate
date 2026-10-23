"""Read-only episode/PAHF measurements. No model calls or training writes."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import statistics


def summarize(records):
    kinds = Counter(row.get("kind") for row in records)
    opened = {r["episodeId"] for r in records if r.get("kind") == "episode_opened" and r.get("episodeId")}
    closed = {r["episodeId"] for r in records if r.get("kind") == "episode_closed" and r.get("episodeId")}
    measurements = [r["data"] for r in records if r.get("kind") == "decision_measurement"]
    # NPC logs a nested measurement plus the unmodified proposal.
    measured = [m.get("measurement", m) for m in measurements]
    actions = [r["data"] for r in records if r.get("kind") == "action_result"]
    interrupted = [r["data"] for r in records if r.get("kind") == "action_interrupted"]
    latencies = [r["data"]["latencyMs"] for r in records if r.get("kind") in {"model_response", "dialogue_response"}
                 and isinstance(r.get("data", {}).get("latencyMs"), (int, float))]
    asks = [m for m in measured if m.get("policy") == "Ask"]
    def is_collection(row):
        try: return json.loads(row.get("proposal", "{}"))["action"]["type"] in {"mine", "chop"}
        except (ValueError, KeyError, TypeError): return False
    allows = [m.get("measurement", m) for m in measurements
              if is_collection(m) and m.get("measurement", m).get("policy") == "Allow"]
    proposal_types = Counter()
    for proposal in measurements:
        try: proposal_types[json.loads(proposal.get("proposal", "{}"))["action"]["type"]] += 1
        except (ValueError, KeyError, TypeError): proposal_types["unparsed"] += 1
    def ratio(n, d):
        return {"numerator": n, "denominator": d, "rate": round(n / d, 4) if d else None}
    def percentile(values, p):
        if not values: return None
        values = sorted(values)
        return round(values[max(0, math.ceil(len(values) * p) - 1)], 1)
    return {
        "records": len(records), "event_counts": dict(kinds),
        "episodes": {"opened": len(opened), "closed": len(opened & closed), "unfinished": len(opened - closed)},
        "unlinked_task_events": sum(bool(r.get("taskId")) and not r.get("episodeId") for r in records),
        "decisions": {"measured": len(measured),
            "rejected_before_measurement": kinds["action_rejected"],
            "proposal_action_types": dict(proposal_types),
            "model_asked_when_required": ratio(sum(bool(m.get("modelAsked")) for m in asks), len(asks)),
            "forced_ask": ratio(sum(bool(m.get("forcedAsk")) for m in asks), len(asks)),
            "unnecessary_ask": ratio(sum(bool(m.get("unnecessaryAsk")) for m in allows), len(allows)),
            "denied_proposals": sum(bool(m.get("deniedProposal")) for m in measured),
            "missing_mode": sum(bool(m.get("missingMode")) for m in measured)},
        "effects": {"terminal_actions": len(actions),
            "interrupted_actions": len(interrupted),
            "interrupted_direct_removals": sum(m.get("removed") is True for a in interrupted for m in a.get("mutations", [])),
            "interrupted_receipts_missing": sum("mutations" not in a for a in interrupted),
            "verified_target_removals": sum(a.get("targetVerified") is True for a in actions),
            "positive_candidates": sum(a.get("candidate") is True for a in actions),
            "task_success_rate": None,
            "limitation": "Direct target tile receipts only; neither inventory attribution nor task completion is verified."},
        "requests": kinds["dialogue_request"] + kinds["decision_request"],
        "latency_ms": {"responses": len(latencies), "p50": round(statistics.median(latencies), 1) if latencies else None,
            "p95": percentile(latencies, .95)},
        "token_usage": None,
        "feedback_unresolved": sum(r.get("data", {}).get("relation") == "unresolved" for r in records if r.get("kind") == "player_feedback"),
        "interpretation": "Ask rates cover valid proposals only; inspect rejected proposals and action types too. Guard compliance is not model learning. Free-form logs have no independent task oracle; use paired development/held-out scenarios for learning claims. Token usage is not collected."
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path, help="A world's learning.jsonl")
    parser.add_argument("--since-line", type=int, default=0, help="Skip this many earlier JSONL lines")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    rows = []
    for number, line in enumerate(args.log.read_text().splitlines()[args.since_line:], args.since_line + 1):
        if not line.strip(): continue
        try: rows.append(json.loads(line))
        except json.JSONDecodeError as error: raise SystemExit(f"Invalid JSON at line {number}: {error}")
    report = json.dumps(summarize(rows), ensure_ascii=False, indent=2)
    if args.output: args.output.write_text(report + "\n")
    print(report)


if __name__ == "__main__":
    main()
