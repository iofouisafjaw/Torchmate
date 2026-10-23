"""Pinned artifacts and integrity-checked downloads shared by the Alpha scripts (stdlib only)."""
import hashlib
import json
import os
from pathlib import Path
import re
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
ALIAS = "qwen3-4b"  # Wire compatibility, not the actual parameter count.


def load_lock(root=None):
    return json.loads(((root or ROOT) / "release/dependencies.lock.json").read_text(encoding="utf-8"))


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verified(path, artifact):
    path = Path(path)
    return path.is_file() and path.stat().st_size == artifact["bytes"] and sha256(path) == artifact["sha256"]


def download_checked(artifact, dest, opener=None):
    """Promote downloads only after pinned size and SHA-256 match; resume interrupted requests."""
    opener = opener or urllib.request.urlopen
    dest = Path(dest)
    if not re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]) or artifact["bytes"] <= 0:
        raise ValueError("Missing pinned artifact integrity metadata")
    if dest.exists():
        if verified(dest, artifact):
            return dest
        raise ValueError(f"Existing file failed size/SHA-256 validation: {dest}. Move it aside before retrying.")
    dest.parent.mkdir(parents=True, exist_ok=True)
    part = dest.with_name(dest.name + ".part")
    done = part.stat().st_size if part.exists() else 0
    if done == artifact["bytes"]:
        if not verified(part, artifact):
            raise ValueError("Completed .part failed SHA-256; remove that partial file and retry")
        part.replace(dest)
        return dest
    if done > artifact["bytes"]:
        raise ValueError("Partial download exceeds pinned size; remove the .part file and retry")
    headers = {"User-Agent": "TerrarAI-Windows-Alpha"}
    if done:
        headers["Range"] = f"bytes={done}-"
    request = urllib.request.Request(artifact["url"], headers=headers)
    with opener(request, timeout=120) as response:
        if response.status == 206:
            match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", response.headers.get("Content-Range", ""))
            if not match or int(match[1]) != done or int(match[3]) != artifact["bytes"] or int(match[2]) != artifact["bytes"] - 1:
                raise ValueError("Download resume range does not match the pinned artifact")
        elif response.status == 200:
            done = 0
        else:
            raise ValueError(f"Unexpected download status: {response.status}")
        length = response.headers.get("Content-Length")
        if length is not None and int(length) != artifact["bytes"] - done:
            raise ValueError("Download Content-Length does not match pinned size")
        last = time.monotonic()
        with part.open("ab" if done else "wb") as stream:
            while chunk := response.read(4 << 20):
                stream.write(chunk)
                done += len(chunk)
                if done > artifact["bytes"]:
                    raise ValueError("Download exceeded pinned size")
                if time.monotonic() - last > 1:
                    print(f"  {dest.name}: {done / 1e9:.2f}/{artifact['bytes'] / 1e9:.2f} GB", flush=True)
                    last = time.monotonic()
    if not verified(part, artifact):
        raise ValueError("Incomplete or corrupt download; final file was not installed. Retry to resume, or remove a corrupt .part file.")
    part.replace(dest)
    return dest


def atomic_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_name(path.name + ".tmp")
    part.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(part, path)


def settings(root=None):
    path = (root or ROOT) / ".runtime/alpha-settings.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def model_path(root=None, explicit=None):
    root = root or ROOT
    if explicit:
        return Path(explicit).expanduser().resolve()
    selected = settings(root).get("model")
    if selected:
        result = (root / selected).resolve()
        if not result.is_relative_to((root / ".models").resolve()):
            raise ValueError("Configured model must stay inside this kit's .models folder")
        return result
    for size in ("14b", "8b", "4b"):
        for path in sorted((root / ".models").glob("*.gguf")):
            if size in path.name.lower():
                return path
    return root / ".models/Qwen3-14B-Q4_K_M.gguf"
