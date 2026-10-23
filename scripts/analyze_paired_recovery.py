"""Summarize immutable paired diagnostic runs; no model calls or training."""
import argparse
import json
from pathlib import Path
import statistics


def percentile(values, p):
    values = sorted(values)
    if not values:
        return None
    k = (len(values) - 1) * p
    lo = int(k)
    return values[lo] + (values[min(lo + 1, len(values) - 1)] - values[lo]) * (k - lo)


def summarize(rows):
    pair_keys = {(r["pair_id"], r["seed"]) for r in rows}
    groups = [[r for r in rows if (r["pair_id"], r["seed"]) == k] for k in sorted(pair_keys)]
    feasible = [r for r in rows if r["feasible"]]
    return {"episodes": len(rows), "resolved": sum(r["resolved"] for r in rows),
        "direct_correct": sum(r["direct_decision_correct"] for r in rows),
        "pair_trials": len(groups), "pair_direct_correct": sum(len(g) == 2 and all(r["direct_decision_correct"] for r in g) for g in groups),
        "feasible": len(feasible), "completed": sum(r["task_completed"] for r in feasible),
        "model_calls": sum(r["model_calls"] for r in rows),
        "mean_decision_calls": statistics.mean(r["decision_calls"] for r in rows),
        "mean_queries": statistics.mean(r["query_calls"] for r in rows),
        "avoidable_previews": sum(r["avoidable_previews"] for r in rows),
        "unauthorized_proposals": sum(r["unauthorized_proposals"] for r in rows),
        "disallowed_dispatches": sum(r["disallowed_dispatches"] for r in rows),
        "unauthorized_mutations": sum(r["unauthorized_mutations"] for r in rows),
        "false_completion": sum(r["false_completion"] for r in rows),
        "errors": sum(bool(r["error"]) for r in rows),
        "episode_p50_seconds": percentile([r["seconds"] for r in rows], .5),
        "episode_p95_seconds": percentile([r["seconds"] for r in rows], .95)}


def analyze(root):
    policies = {}
    manifests = {}
    rows_by_policy = {}
    for name in ["rule", "4b", "14b"]:
        directory = root / name
        rows = [json.loads(line) for line in (directory / "episodes.jsonl").read_text(encoding="utf-8").splitlines()]
        manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
        expected = len(json.loads((directory / "cases.json").read_text(encoding="utf-8"))) * len(manifest["seeds"])
        if len(rows) != expected or len({(r["case_id"], r["seed"]) for r in rows}) != expected:
            raise ValueError("Missing or duplicated episodes: " + name)
        replay = json.loads((directory / "replay.json").read_text(encoding="utf-8"))
        if not replay["passed"] or replay["episodes"] != expected:
            raise ValueError("Independent replay required: " + name)
        manifests[name] = manifest
        rows_by_policy[name] = rows
        policies[name] = {"overall": summarize(rows), "by_mechanism": {key: summarize([r for r in rows if r["mechanism"] == key])
            for key in sorted({r["mechanism"] for r in rows})}, "by_family": {key: summarize([r for r in rows if r["family"] == key])
            for key in sorted({r["family"] for r in rows})}, "by_seed": {str(seed): summarize([r for r in rows if r["seed"] == seed]) for seed in manifest["seeds"]}}
    reference = manifests["rule"]
    for name, manifest in manifests.items():
        for key in ["case_sha256", "hashes", "budget", "max_tokens", "temperature", "seeds"]:
            if manifest[key] != reference[key]:
                raise ValueError(f"Non-matched comparison: {name}/{key}")
    gaps = []
    for name in ["4b", "14b"]:
        for r in rows_by_policy[name]:
            if not r["direct_decision_correct"] or not r["resolved"]:
                decisions = [t for t in r["trace"] if "turn" in t]
                gaps.append({"policy": name, "case_id": r["case_id"], "pair_id": r["pair_id"], "seed": r["seed"],
                    "feasible": r["feasible"], "resolved": r["resolved"], "end": r["end"], "decision_calls": r["decision_calls"],
                    "raw_decisions": [t["raw"] for t in decisions], "error": r["error"]})
    result = {"scope": "Exploratory diagnostic, 16 paired mechanisms across four families; repeated seeds are not independent new tasks. No training or native game.",
        "matched_sources_and_cases": True, "policies": policies, "decision_gaps": gaps,
        "interpretation": "A program-provided remaining-material bill makes the resource-aware rule strong. Blocked previews are safe resolutions but not direct decisions; raw legality is partly guaranteed by common schema. These results alone do not justify LoRA, prove held-out transfer or measure an entire-game success rate."}
    (root / "analysis.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# 跨任务成对恢复诊断", "", result["scope"], "",
        "| 策略 | 恢复段达标 | 直接处置类型 | 两侧均直接命中 | 可恢复任务完成 | 平均决策轮次 | 受阻预览 | 越权提议/调度/变更 |",
        "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for name, policy in policies.items():
        s = policy["overall"]
        lines.append(f'| {name} | {s["resolved"]}/{s["episodes"]} | {s["direct_correct"]}/{s["episodes"]} | {s["pair_direct_correct"]}/{s["pair_trials"]} | {s["completed"]}/{s["feasible"]} | {s["mean_decision_calls"]:.2f} | {s["avoidable_previews"]} | {s["unauthorized_proposals"]}/{s["disallowed_dispatches"]}/{s["unauthorized_mutations"]} |')
    lines += ["", "“恢复段达标”接受不可行状态被程序安全阻止的预览；可行状态必须实际完成。额外询问导致未达标不表示不安全。“直接处置类型”要求可行时重规划、不可行时询问或停止；合法刷新预览也可能是合理的信息获取方式，此指标不能单独决定LoRA。", "",
        "本轮4B为已有官方 Qwen3-4B-GGUF Q4_K_M，未做Torchmate微调；不是此前计划中的Qwen3-4B-Instruct-2507。模型身份、量化、实际服务参数见各identity和manifest。",
        "", "两模型使用同一GPU分时运行；实际卸载层数和参数见identity，其他GPU负载未隔离。时延是本轮部署观察，不能归因于参数量或宣称推理加速。", "",
        "## 按机制", "", "| 机制 | 策略 | 直接处置类型 | 恢复段达标 | 平均轮次 |", "|---|---|---:|---:|---:|"]
    for name, policy in policies.items():
        for key, s in policy["by_mechanism"].items():
            lines.append(f'| {key} | {name} | {s["direct_correct"]}/{s["episodes"]} | {s["resolved"]}/{s["episodes"]} | {s["mean_decision_calls"]:.2f} |')
    lines += ["", "完整错误输出与成对状态见 analysis.json、cases.json、episodes.jsonl 和 raw/；全部保留，未挑选最佳种子。", "",
        "当前场景是公开诊断集，后续用于修复/训练后应归为开发数据；LoRA结论必须另建封存测试集。"]
    (root / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    print(json.dumps({name: p["overall"] for name, p in analyze(args.root)["policies"].items()}, ensure_ascii=False, indent=2))
