"""Verify the extracted Alpha kit's manifest. This does not grant distribution permission."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]


def verify(root=None):
    root = (root or ROOT).resolve()
    manifest = json.loads((root / "alpha-manifest.json").read_text(encoding="utf-8"))
    for name, expected in manifest["files"].items():
        relative = PurePosixPath(name)
        path = root / name
        if relative.is_absolute() or ".." in relative.parts or "\\" in name or ":" in name or not path.resolve().is_relative_to(root):
            raise ValueError("Unsafe Alpha manifest path")
        if not path.is_file() or path.stat().st_size != expected["bytes"]:
            raise ValueError("Missing or modified Alpha input: " + name)
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != expected["sha256"]:
                raise ValueError("SHA-256 mismatch: " + name)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    args = parser.parse_args()
    manifest = verify(args.root)
    print(json.dumps({"verified": True, "version": manifest["version"], "files": len(manifest["files"]),
        "publicDistributionApproved": manifest["publicDistributionApproved"]}, indent=2))


if __name__ == "__main__":
    main()
