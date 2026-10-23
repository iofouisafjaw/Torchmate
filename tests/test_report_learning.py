import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("report", Path(__file__).resolve().parents[1] / "scripts/report_learning.py")
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class MeasurementTests(unittest.TestCase):
    def test_no_evidence_is_unknown_not_perfect_score(self):
        data = report.summarize([])
        self.assertIsNone(data["decisions"]["forced_ask"]["rate"])
        self.assertIsNone(data["effects"]["task_success_rate"])

    def test_denominators_and_guard_are_separate(self):
        def row(policy, asked, action):
            return {"kind": "decision_measurement", "data": {
                "proposal": '{"action":{"type":"' + action + '"}}',
                "measurement": {"policy": policy, "modelAsked": asked, "forcedAsk": policy == "Ask" and not asked,
                    "unnecessaryAsk": policy == "Allow" and asked}}}
        data = report.summarize([row("Ask", False, "mine"), row("Ask", True, "chop"),
            row("Allow", True, "mine"), row("Allow", False, "say"),
            {"kind": "action_result", "data": {"candidate": False, "targetVerified": False}},
            {"kind": "action_interrupted", "data": {"mutations": [{"removed": True}]}},
            {"kind": "action_rejected", "data": {"error": "missing tool"}}])
        self.assertEqual(data["decisions"]["forced_ask"]["rate"], .5)
        self.assertEqual(data["decisions"]["unnecessary_ask"]["rate"], 1)
        self.assertEqual(data["effects"]["verified_target_removals"], 0)
        self.assertEqual(data["effects"]["interrupted_direct_removals"], 1)
        self.assertEqual(data["effects"]["positive_candidates"], 0)
        self.assertEqual(data["decisions"]["rejected_before_measurement"], 1)


if __name__ == "__main__": unittest.main()
