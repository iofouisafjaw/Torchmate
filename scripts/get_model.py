"""Download a pinned official Qwen3 Q4_K_M model (14b default; 8b experimental)."""
import argparse
from pathlib import Path
from release_support import download_checked, load_lock, verified

ROOT = Path(__file__).resolve().parents[1]


def fetch(size="14b", root=None):
    root = root or ROOT
    artifact = load_lock(root)["models"][size]
    dest = root / ".models" / artifact["file"]
    print(f"Official {artifact['repo']} @ {artifact['revision']} ({artifact['bytes'] / 1e9:.2f} GB); SHA-256 checked.")
    return download_checked(artifact, dest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", choices=("14b", "8b"), default="14b")
    parser.add_argument("--check", action="store_true", help="Only verify an existing model; do not download")
    args = parser.parse_args()
    if args.check:
        artifact = load_lock(ROOT)["models"][args.size]
        path = ROOT / ".models" / artifact["file"]
        if not verified(path, artifact):
            raise SystemExit(f"Model missing or failed size/SHA-256 validation: {path}")
        print("Verified:", path.name)
        return
    print("Ready:", fetch(args.size))


if __name__ == "__main__":
    main()
