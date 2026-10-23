import argparse
import contextlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import start_qwen

PROFILE = {"ctx": 8192, "cache_mb": 256, "batch_size": 512, "ubatch_size": 128,
           "kv_cache": "q8_0", "flash_attention": "on"}


class MemoryProfileTests(unittest.TestCase):
    def test_original_defaults_without_profile(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(start_qwen, "WINDOWS", True):
            self.assertEqual(start_qwen.inference_profile(Path(directory)), {})
            selected = start_qwen.memory_settings(argparse.Namespace(), {}, False)
            self.assertEqual((selected["ctx"], selected["kv_cache"], selected["batch_size"], selected["ubatch_size"]),
                             (12288, "f16", 2048, 512))

    def test_saved_profile_and_cli_override(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(start_qwen, "WINDOWS", True):
            root = Path(directory)
            (root / ".runtime").mkdir()
            (root / ".runtime/model-inference.json").write_text(json.dumps(PROFILE), encoding="utf-8")
            loaded = start_qwen.inference_profile(root)
            self.assertEqual(start_qwen.memory_settings(argparse.Namespace(), loaded, False), PROFILE)
            result = start_qwen.memory_settings(argparse.Namespace(ctx=4096), loaded, False)
            self.assertEqual(result["ctx"], 4096)
            self.assertEqual(loaded["ctx"], 8192)

    def test_invalid_profile_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".runtime").mkdir()
            for invalid in ({"ctx": True}, {"ctx": -1}, {"kv_cache": "unsafe"}, {"extra_arguments": "anything"}, []):
                with self.subTest(value=invalid):
                    (root / ".runtime/model-inference.json").write_text(json.dumps(invalid), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        start_qwen.inference_profile(root)

    def test_invalid_batch_and_flash_combination(self):
        with mock.patch.object(start_qwen, "WINDOWS", True):
            for change in ({"batch_size": 64, "ubatch_size": 128}, {"flash_attention": "off"}):
                with self.subTest(change=change), self.assertRaises(ValueError):
                    start_qwen.memory_settings(argparse.Namespace(), dict(PROFILE, **change), False)
            cpu = start_qwen.memory_settings(argparse.Namespace(), PROFILE, True)
            self.assertEqual((cpu["kv_cache"], cpu["flash_attention"]), ("f16", "auto"))

    def test_actual_startup_uses_saved_profile_and_keeps_14b(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            root = Path(directory)
            (root / ".runtime/llama-win").mkdir(parents=True)
            (root / ".runtime/llama-win/llama-server.exe").write_bytes(b"mock executable")
            (root / ".runtime/model-inference.json").write_text(json.dumps(PROFILE), encoding="utf-8")
            model = root / "Qwen3-14B-Q4_K_M.gguf"
            model.write_bytes(b"mock model")
            stack.enter_context(mock.patch.object(start_qwen, "ROOT", root))
            stack.enter_context(mock.patch.object(start_qwen, "WINDOWS", True))
            stack.enter_context(mock.patch.object(start_qwen, "settings", return_value={}))
            stack.enter_context(mock.patch.object(start_qwen, "default_model", return_value=model))
            stack.enter_context(mock.patch.object(sys, "argv", ["start_qwen.py", "--no-brain"]))
            process = stack.enter_context(mock.patch.object(start_qwen.subprocess, "Popen"))
            process.return_value.wait.return_value = 0
            with self.assertRaises(SystemExit) as result:
                start_qwen.main()
            self.assertEqual(result.exception.code, 0)
            command = process.call_args.args[0]
            for flag, value in (("-m", str(model)), ("-c", "8192"), ("-b", "512"), ("-ub", "128"),
                                ("--cache-type-k", "q8_0"), ("--cache-type-v", "q8_0"), ("--flash-attn", "on"), ("-ngl", "99")):
                self.assertEqual(command[command.index(flag) + 1], value)


if __name__ == "__main__":
    unittest.main()
