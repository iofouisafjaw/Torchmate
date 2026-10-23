"""Offline release/install/startup checks with synthetic artifacts and temporary profiles.

No actual model download, native executable, Steam client or personal profile is touched.
"""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch, Mock
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import get_model
import play_local
import release_alpha
import release_support as support
import setup_windows as setup
import verify_alpha


class Response(io.BytesIO):
    def __init__(self, body, status=200, headers=None):
        super().__init__(body)
        self.status = status
        self.headers = headers if headers is not None else {"Content-Length": str(len(body))}


def artifact(data, name="model.gguf"):
    return {"file": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "url": "https://example.invalid/" + name}


def tmod(version="1.2.3"):
    def s(text):
        data = text.encode()
        return bytes([len(data)]) + data
    payload = s("TerrarAI") + s(version) + struct.pack("<i", 0)
    return b"TMOD" + s("test-loader") + hashlib.sha1(payload).digest() + bytes(256) + struct.pack("<i", len(payload)) + payload


class AlphaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="TerrarAI Alpha 空间 ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.profile = self.root / "Test Profile with spaces"
        self.profile.mkdir()

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def lock(self, model=b"model", archives=None):
        data = {"models": {"14b": artifact(model, "Qwen3-14B-Q4_K_M.gguf"), "8b": artifact(b"small", "Qwen3-8B-Q4_K_M.gguf")},
                "llama": {"tag": "test-pin", "runtimes": {"cpu": archives or [], "cuda": archives or []}}}
        self.write("release/dependencies.lock.json", json.dumps(data).encode())
        return data

    def test_download_hash_and_cached_reuse(self):
        data = b"complete model"
        path = self.root / "model.gguf"
        support.download_checked(artifact(data), path, lambda *a, **k: Response(data))
        self.assertEqual(path.read_bytes(), data)
        support.download_checked(artifact(data), path, Mock(side_effect=AssertionError("no network for verified cache")))
        self.assertFalse(path.with_suffix(".gguf.part").exists())

    def test_resume_range_and_ignored_range(self):
        data = b"0123456789"
        for code in (200, 206):
            path = self.root / f"m{code}.gguf"
            path.with_suffix(".gguf.part").write_bytes(data[:4])
            def open_request(request, **kw):
                self.assertEqual(request.get_header("Range"), "bytes=4-")
                return Response(data if code == 200 else data[4:], code,
                    {"Content-Length": str(len(data) if code == 200 else 6), "Content-Range": "bytes 4-9/10"})
            support.download_checked(artifact(data), path, open_request)
            self.assertEqual(path.read_bytes(), data)

    def test_corruption_and_wrong_ranges_never_promote(self):
        expected = artifact(b"abcde")
        for code, body, headers in ((200, b"wrong", {}), (200, b"abc", {}), (206, b"cde", {"Content-Range": "bytes 2-4/5"})):
            path = self.root / f"bad{code}-{len(body)}.gguf"
            with self.assertRaises(ValueError):
                support.download_checked(expected, path, lambda *a, **k: Response(body, code, headers))
            self.assertFalse(path.exists())

    def test_bad_existing_file_preserved(self):
        path = self.write("model.gguf", b"wrong")
        with self.assertRaises(ValueError):
            support.download_checked(artifact(b"right"), path, Mock(side_effect=AssertionError("no network")))
        self.assertEqual(path.read_bytes(), b"wrong")

    def test_complete_partial_verified_before_rename(self):
        path = self.root / "m.gguf"
        self.write("m.gguf.part", b"ready")
        support.download_checked(artifact(b"ready"), path, Mock(side_effect=AssertionError("no network")))
        self.assertEqual(path.read_bytes(), b"ready")

    def archive(self, name, members):
        path = self.root / name
        with zipfile.ZipFile(path, "w") as z:
            for filename, data in members.items():
                z.writestr(filename, data)
        return path

    def test_safe_runtime_extract_and_conflicting_case(self):
        target = self.root / "stage"
        target.mkdir()
        setup.extract_runtime(self.archive("runtime.zip", {"folder/llama-server.exe": b"exe", "folder/native.dll": b"dll"}), target)
        self.assertEqual((target / "llama-server.exe").read_bytes(), b"exe")
        with self.assertRaises(ValueError):
            setup.extract_runtime(self.archive("conflict.zip", {"NATIVE.DLL": b"different"}), target)

    def test_zip_traversal_and_links_rejected(self):
        for i, name in enumerate(("../escape", "/escape", "C:/escape", "..\\escape")):
            target = self.root / str(i)
            target.mkdir()
            with self.assertRaises(ValueError):
                setup.extract_runtime(self.archive(f"bad{i}.zip", {name: b"x"}), target)
        path = self.root / "link.zip"
        with zipfile.ZipFile(path, "w") as z:
            info = zipfile.ZipInfo("link")
            info.external_attr = 0o120777 << 16
            z.writestr(info, "outside")
        with self.assertRaises(ValueError):
            setup.extract_runtime(path, self.root)

    def test_runtime_pin_reuse_and_upgrade_backup(self):
        archive = self.archive("runtime.zip", {"llama-server.exe": b"exe", "native.dll": b"dll"})
        a = artifact(archive.read_bytes(), "runtime.zip")
        self.lock(archives=[a])
        self.write(".runtime/download/runtime.zip", archive.read_bytes())
        setup.setup_llama(backend="cpu", root=self.root)
        self.assertTrue(setup.runtime_valid(self.root, "cpu"))
        (self.root / ".runtime/llama-win/unexpected.dll").write_bytes(b"unlisted")
        self.assertFalse(setup.runtime_valid(self.root, "cpu"))
        (self.root / ".runtime/llama-win/unexpected.dll").unlink()
        self.assertFalse(setup.runtime_valid(self.root, "cuda"))
        with patch.object(setup, "download_checked", side_effect=AssertionError("cached runtime")):
            setup.setup_llama(backend="cpu", root=self.root)
        setup.setup_llama(force=True, backend="cpu", root=self.root)
        self.assertEqual(len(list((self.root / ".runtime").glob("llama-win.previous-*"))), 1)
        self.assertTrue(setup.runtime_valid(self.root, "cpu"))

    def test_broken_runtime_upgrade_keeps_old_installation(self):
        self.write(".runtime/llama-win/llama-server.exe", b"old")
        archive = self.archive("runtime.zip", {"wrong.exe": b"not server"})
        a = artifact(archive.read_bytes(), "runtime.zip")
        self.lock(archives=[a])
        self.write(".runtime/download/runtime.zip", archive.read_bytes())
        with self.assertRaises(ValueError):
            setup.setup_llama(backend="cpu", root=self.root)
        self.assertEqual((self.root / ".runtime/llama-win/llama-server.exe").read_bytes(), b"old")

    def test_install_upgrade_idempotence_and_preservation(self):
        self.write("dist/TerrarAI.tmod", b"new mod")
        mods = self.profile / "Mods"
        mods.mkdir()
        (mods / "enabled.json").write_text('["OtherMod"]')
        (mods / "TerrarAI.tmod").write_bytes(b"old mod")
        cfg = self.profile / "ModConfigs/TerrarAI_TerrarAI_Config.json"
        cfg.parent.mkdir()
        cfg.write_text('{"BaseEndpoint":"http://127.0.0.1:19000/v1/chat/completions"}')
        before = cfg.read_bytes()
        setup.install_mod(self.profile, self.root)
        setup.install_mod(self.profile, self.root)
        self.assertEqual(json.loads((mods / "enabled.json").read_text()), ["OtherMod", "TerrarAI"])
        self.assertEqual((mods / "TerrarAI.tmod.bak").read_bytes(), b"old mod")
        self.assertEqual(cfg.read_bytes(), before)
        self.assertFalse((self.profile / "Worlds").exists())

    def test_invalid_profile_list_stops_before_mod_write(self):
        self.write("dist/TerrarAI.tmod", b"new")
        mods = self.profile / "Mods"
        mods.mkdir()
        (mods / "enabled.json").write_text('{"not":"a list"}')
        with self.assertRaises(ValueError):
            setup.install_mod(self.profile, self.root)
        self.assertFalse((mods / "TerrarAI.tmod").exists())

    def test_readonly_setup_missing_state_has_no_side_effects(self):
        self.lock()
        before = sorted(str(p.relative_to(self.root)) for p in self.root.rglob("*"))
        result = setup.diagnose(self.root, self.profile)
        self.assertFalse(result["modelVerified"])
        self.assertFalse(result["runtimeVerified"])
        self.assertEqual(before, sorted(str(p.relative_to(self.root)) for p in self.root.rglob("*")))

    def test_model_selection_respects_explicit_8b(self):
        self.write(".models/Qwen3-14B-Q4_K_M.gguf", b"big")
        small = self.write(".models/Qwen3-8B-Q4_K_M.gguf", b"small")
        support.atomic_json(self.root / ".runtime/alpha-settings.json", {"model": ".models/" + small.name})
        self.assertEqual(support.model_path(self.root), small.resolve())
        support.atomic_json(self.root / ".runtime/alpha-settings.json", {"model": "../outside.gguf"})
        with self.assertRaises(ValueError):
            support.model_path(self.root)

    @contextlib.contextmanager
    def launcher(self, argv, ready=True, brain=True, wrong_model=False):
        mods = self.profile / "Mods"
        mods.mkdir(exist_ok=True)
        (mods / "TerrarAI.tmod").write_bytes(b"mod")
        (mods / "enabled.json").write_text('["TerrarAI"]')
        expected = self.root / ".models/actual14b.gguf"
        def response(url, **kw):
            data = {"data": [{"id": support.ALIAS}]} if url.endswith("/v1/models") else {"model_path": str(expected if not wrong_model else expected.with_name("other8b.gguf"))}
            return Response(json.dumps(data).encode())
        with patch.object(play_local, "ROOT", self.root), patch.object(play_local, "PROFILE", self.profile), \
             patch.object(play_local, "ENDPOINT", "http://127.0.0.1:8080"), patch.object(play_local, "PORT", 8080), patch.object(play_local, "WINDOWS", True), \
             patch.object(play_local.sys, "platform", "win32"), \
             patch.object(sys, "argv", ["play_local.py", *argv]), patch.object(play_local, "ready", return_value=ready), \
             patch.object(play_local, "has_brain", return_value=brain), patch.object(play_local, "model_path", return_value=expected), \
             patch.object(play_local.urllib.request, "urlopen", side_effect=response), \
             patch.object(play_local.subprocess, "Popen") as spawn, patch.object(play_local.os, "startfile", create=True) as steam, \
             patch.object(play_local, "stop_old_server", side_effect=AssertionError("never kill by PID file")):
            yield spawn, steam

    def test_check_missing_service_never_starts_or_stops(self):
        with self.launcher(["--check"], ready=None) as (spawn, steam):
            with self.assertRaises(SystemExit):
                play_local.main()
            spawn.assert_not_called()
            steam.assert_not_called()

    def test_check_busy_service_never_restarts(self):
        self.write(".runtime/playtest/model-process.json", b'{"pid":1234}')
        with self.launcher(["--check"], brain=False) as (spawn, steam):
            with self.assertRaises(SystemExit):
                play_local.main()
            spawn.assert_not_called()
            steam.assert_not_called()
        self.assertEqual((self.root / ".runtime/playtest/model-process.json").read_bytes(), b'{"pid":1234}')

    def test_healthy_service_only_checks_actual_model_and_does_not_open_steam(self):
        with self.launcher(["--service-only"]) as (spawn, steam):
            play_local.main()
            spawn.assert_not_called()
            steam.assert_not_called()

    def test_wrong_model_same_alias_is_rejected(self):
        with self.launcher(["--check"], wrong_model=True) as (spawn, steam):
            with self.assertRaises(SystemExit):
                play_local.main()
            spawn.assert_not_called()
            steam.assert_not_called()

    def test_custom_profile_and_remote_endpoint_guard(self):
        config = self.profile / "ModConfigs/TerrarAI_TerrarAI_Config.json"
        config.parent.mkdir()
        config.write_text('{"BaseEndpoint":"http://127.0.0.1:19091/v1/chat/completions","Model":"qwen3-4b"}')
        with self.launcher(["--check", "--profile", str(self.profile)]) as (spawn, steam):
            play_local.main()
            self.assertEqual(play_local.PORT, 19091)
            spawn.assert_not_called()
            steam.assert_not_called()
        config.write_text('{"BaseEndpoint":"https://example.invalid/v1/chat/completions"}')
        with self.launcher(["--check", "--profile", str(self.profile)]) as (spawn, steam):
            with self.assertRaises(SystemExit):
                play_local.main()
            spawn.assert_not_called()
            steam.assert_not_called()

    def kit_fixture(self):
        names = set(release_alpha.DOCS) | set(release_alpha.BOOTSTRAPS) | {"scripts/run_python.bat"}
        names.update("scripts/" + name for name in release_alpha.RUN_SCRIPTS)
        names.update(p.relative_to(ROOT).as_posix() for p in (ROOT / "licenses").glob("*.txt"))
        names.update(p.relative_to(ROOT).as_posix() for p in (ROOT / "scripts/brain").glob("*.py") if not p.name.startswith("test_"))
        for name in names:
            self.write(name, (ROOT / name).read_bytes())
        self.write("build.txt", b"version = 1.2.3\n")
        self.write("dist/TerrarAI.tmod", tmod())

    def test_release_gate_blocks_without_prepare_flag(self):
        self.kit_fixture()
        with self.assertRaisesRegex(ValueError, "distribution-not-approved"):
            release_alpha.prepare(self.root)
        self.assertFalse((self.root / "dist/alpha").exists())

    def test_private_allowlist_archive_and_extracted_scripts(self):
        self.kit_fixture()
        for name in (".env", ".models/private.gguf", ".runtime/private.log", "personal.wld", "benchmarks/private.json"):
            self.write(name, b"private sentinel must never export")
        result = release_alpha.prepare(self.root, prepare_only=True)
        self.assertFalse(result["publicDistributionApproved"])
        archive = self.root / "dist/alpha-preparation" / result["archive"]
        with zipfile.ZipFile(archive) as z:
            self.assertIn("NOT_FOR_DISTRIBUTION.txt", z.namelist())
            for name in ("LICENSE", "licenses/ImproveGame-MIT.txt", "licenses/llama.cpp-MIT.txt", "licenses/Qwen3-Apache-2.0.txt"):
                self.assertEqual(z.read(name), (ROOT / name).read_bytes())
            self.assertIn("scripts/run_python.bat", z.namelist())
            self.assertNotIn(".env", z.namelist())
            for name in z.namelist():
                self.assertNotIn(b"private sentinel", z.read(name))
            target = self.root / "Extracted Kit 空间"
            z.extractall(target)
        self.assertFalse(verify_alpha.verify(target)["publicDistributionApproved"])
        (target / "scripts/setup_windows.py").write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            verify_alpha.verify(target)

    def test_stale_mod_and_tampered_mod_rejected(self):
        with self.assertRaises(ValueError):
            release_alpha.check_mod(tmod("0.0.1"), "1.2.3")
        data = bytearray(tmod())
        data[-1] ^= 1
        with self.assertRaises(ValueError):
            release_alpha.check_mod(bytes(data), "1.2.3")

    def test_official_dependency_pins_and_license_text_evidence(self):
        lock = support.load_lock(ROOT)
        for entry in lock["models"].values():
            self.assertTrue(entry["repo"].startswith("Qwen/"))
            self.assertIn(entry["revision"], entry["url"])
            self.assertRegex(entry["sha256"], r"^[0-9a-f]{64}$")
        audit = json.loads((ROOT / "release/license-audit.json").read_text())
        self.assertFalse(audit["publicDistributionApproved"])
        for path, digest in audit["licenseTextSha256"].items():
            self.assertEqual(support.sha256(ROOT / path), digest)
        for entry in audit["upstream"]["affectedFiles"]:
            if entry.get("disposition") == "removed":
                self.assertFalse((ROOT / entry["path"]).exists())
                self.assertIsNone(entry["currentSha256"])
            else:
                self.assertEqual(support.sha256(ROOT / entry["path"]), entry["currentSha256"])


if __name__ == "__main__":
    unittest.main()
