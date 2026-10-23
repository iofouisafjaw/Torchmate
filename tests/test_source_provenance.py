import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import check_provenance as provenance
import create_icons
import release_alpha
import run_evals


class SourceProvenanceTests(unittest.TestCase):
    def test_current_reviewed_surface(self):
        self.assertTrue(provenance.verify(ROOT)["passed"])

    def test_all_inherited_inputs_accounted(self):
        data = json.loads((ROOT / provenance.MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(len(data["upstreamFiles"]), 31)
        self.assertEqual(sum(x["disposition"] == "removed" for x in data["upstreamFiles"]), 8)
        self.assertFalse(data["cleanRoomClaim"])
        self.assertEqual(data["screening"]["unreviewedMatches"], 0)

    def test_icons_are_reproducible_and_replaced(self):
        source = (ROOT / "assets/companion-icon.svg").read_text(encoding="utf-8")
        audit = json.loads((ROOT / "release/license-audit.json").read_text(encoding="utf-8"))
        for name, size in (("icon.png", 80), ("icon_small.png", 30)):
            with self.subTest(name=name):
                data = (ROOT / name).read_bytes()
                self.assertEqual(data, create_icons.render(source, size))
                self.assertEqual(struct.unpack(">II", data[16:24]), (size, size))
                old = next(x for x in audit["upstream"]["affectedFiles"] if x["path"] == name)
                self.assertNotEqual(hashlib.sha256(data).hexdigest(), old["upstreamSha256"])

    def test_release_still_closed(self):
        gates = release_alpha.release_gates(ROOT)
        self.assertIn("distribution-not-approved", gates)
        self.assertIn("player-native-acceptance-pending", gates)
        self.assertNotIn("source-provenance-invalid", gates)

    def test_source_export_excludes_history_and_old_material(self):
        names = release_alpha.inputs(ROOT, "source")
        self.assertIn("assets/companion-icon.svg", names)
        self.assertIn("release/source-provenance.json", names)
        self.assertIn("tests/native_replacement/Program.cs", names)
        for name in names:
            self.assertFalse(any(p in (".env", ".git", ".runtime", "v0.1.0", "v0.2.0", "v0.3.0") for p in Path(name).parts))
            self.assertNotEqual(name, "plan.md")

    def test_tokenizer_does_not_erase_urls_in_strings(self):
        self.assertIn('"http://localhost/v1"', provenance.tokens('var x="http://localhost/v1"; // comment'))
        self.assertNotIn("comment", provenance.tokens('/* comment */ var x=1;'))

    def test_eval_help_does_not_start_or_contact_model(self):
        with mock.patch.object(sys, "argv", ["run_evals.py", "--help"]), mock.patch.object(run_evals, "healthy") as health, mock.patch.object(run_evals.subprocess, "Popen") as spawn:
            with self.assertRaises(SystemExit) as result:
                run_evals.main()
            self.assertEqual(result.exception.code, 0)
            health.assert_not_called()
            spawn.assert_not_called()

    def test_eval_validation_never_starts_or_contacts_model(self):
        with mock.patch.object(sys, "argv", ["run_evals.py", "--validate"]), mock.patch.object(run_evals.shutil, "which", return_value="dotnet"), mock.patch.object(run_evals.subprocess, "run", return_value=mock.Mock(stdout="8.0.426\n")), mock.patch.object(run_evals.subprocess, "call", return_value=0) as call, mock.patch.object(run_evals.subprocess, "Popen") as spawn, mock.patch.object(run_evals, "healthy") as health:
            self.assertEqual(run_evals.main(), 0)
            self.assertIn("--validate", call.call_args.args[0])
            health.assert_not_called()
            spawn.assert_not_called()

    def test_unknown_eval_option_fails_before_model(self):
        with mock.patch.object(sys, "argv", ["run_evals.py", "--unknown-option"]), mock.patch.object(run_evals, "healthy") as health, mock.patch.object(run_evals.subprocess, "Popen") as spawn:
            with self.assertRaises(SystemExit) as result:
                run_evals.main()
            self.assertEqual(result.exception.code, 2)
            health.assert_not_called()
            spawn.assert_not_called()

    def fixture(self, directory):
        root = Path(directory)
        (root / "release").mkdir()
        (root / "code.cs").write_text("reviewed", encoding="utf-8")
        manifest = {"files": {"code.cs": {"sha256": provenance.digest(root / "code.cs")}},
                    "removed": ["removed.cs"], "upstreamFiles": [], "screening": {"unreviewedMatches": 0}}
        (root / provenance.MANIFEST).write_text(json.dumps(manifest), encoding="utf-8")
        return root

    def test_changed_file_rejected(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(provenance, "surface", return_value=["code.cs"]):
            root = self.fixture(directory)
            (root / "code.cs").write_text("unreviewed", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                provenance.verify(root)

    def test_returned_removed_file_rejected(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(provenance, "surface", return_value=["code.cs"]):
            root = self.fixture(directory)
            (root / "removed.cs").write_text("old", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "has returned"):
                provenance.verify(root)

    def test_new_unreviewed_input_rejected(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(provenance, "surface", return_value=["code.cs", "extra.cs"]):
            root = self.fixture(directory)
            with self.assertRaisesRegex(ValueError, "surface differs"):
                provenance.verify(root)


if __name__ == "__main__":
    unittest.main()
