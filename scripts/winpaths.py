"""Windows locations for tModLoader (Steam) and this project's runtime files."""
import ctypes
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LLAMA_DIR = ROOT / ".runtime" / "llama-win"
MODEL = ROOT / ".models" / "Qwen3-4B-Q4_K_M.gguf"
TMODLOADER_APPID = "1281930"


def documents():
    """The real Documents folder (it may be redirected, e.g. to another drive)."""
    try:
        from ctypes import wintypes
        buf = ctypes.c_wchar_p()
        guid = (ctypes.c_byte * 16).from_buffer_copy(bytes.fromhex("D0 9A D3 FD 8F 23 AF 46 AD B4 6C 85 48 03 69 C7".replace(" ", "")))
        if ctypes.windll.shell32.SHGetKnownFolderPath(ctypes.byref(guid), 0, None, ctypes.byref(buf)) == 0:
            path = buf.value
            ctypes.windll.ole32.CoTaskMemFree(buf)
            return Path(path)
    except Exception:
        pass
    return Path(os.path.expanduser("~")) / "Documents"


def tml_profile():
    return documents() / "My Games" / "Terraria" / "tModLoader"


def llama_server():
    return LLAMA_DIR / "llama-server.exe"
