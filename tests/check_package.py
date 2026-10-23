"""Inspect the actual .tmod header, integrity, contents and compiled assembly."""
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import zlib

ROOT = Path(__file__).resolve().parents[1]


def inspect(path):
    data = path.read_bytes()
    stream = io.BytesIO(data)

    def string():
        size = 0
        for shift in range(0, 35, 7):
            byte = stream.read(1)[0]
            size |= (byte & 127) << shift
            if byte < 128:
                return stream.read(size).decode('utf-8')
        raise ValueError('Invalid .NET string length')

    assert stream.read(4) == b'TMOD', 'Missing TMOD header'
    loader = string()
    digest = stream.read(20)
    stream.read(256)
    size, = struct.unpack('<i', stream.read(4))
    start = stream.tell()
    assert len(data) - start == size, 'Truncated package'
    assert hashlib.sha1(data[start:]).digest() == digest, 'Package hash mismatch'
    name, version = string(), string()
    assert name == 'TerrarAI', 'Package identity must not depend on the checkout folder name'
    count, = struct.unpack('<i', stream.read(4))
    entries = []
    for _ in range(count):
        entry = string()
        length, compressed = struct.unpack('<ii', stream.read(8))
        assert entry in {'TerrarAI.dll', 'TerrarAI.pdb', 'Info', 'description.txt', 'icon.png', 'icon_small.png',
                         'LICENSE', 'THIRD_PARTY_NOTICES.md', 'LICENSE_STATUS.md', 'licenses/ImproveGame-MIT.txt'} or (
            entry.startswith(('Content/', 'Localization/')) and not entry.endswith(('.cs', '.py'))), entry
        entries.append((entry, length, compressed))
    assert len({entry for entry, _, _ in entries}) == count, 'Duplicate package entry'
    assert {'LICENSE', 'THIRD_PARTY_NOTICES.md', 'LICENSE_STATUS.md', 'licenses/ImproveGame-MIT.txt'}.issubset(
        {entry for entry, _, _ in entries}), 'Required license notices are missing from the mod package'
    payload = stream.tell()
    offset = 0
    assembly = None
    checked_resources = []
    for entry, length, compressed in entries:
        block = data[payload + offset:payload + offset + compressed]
        offset += compressed
        if entry == 'TerrarAI.dll':
            assembly = block if length == compressed else zlib.decompress(block, -15)
        elif entry in {'icon.png', 'icon_small.png', 'description.txt', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'LICENSE_STATUS.md', 'licenses/ImproveGame-MIT.txt'}:
            resource = block if length == compressed else zlib.decompress(block, -15)
            assert resource == (ROOT / entry).read_bytes(), 'Stale replacement asset or notice: ' + entry
            checked_resources.append(entry)
    assert payload + offset == len(data), 'File table does not cover package contents'
    assert assembly == (ROOT / 'bin/Debug/net8.0/TerrarAI.dll').read_bytes(), 'Stale or missing assembly'
    return dict(passed=True, package=str(path.resolve()), name=name, version=version,
                loader=loader, bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                files=[entry for entry, _, _ in entries], assembly_matches=True, current_resources=checked_resources)


if __name__ == '__main__':
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'dist/TerrarAI.tmod'
    print(json.dumps(inspect(path), ensure_ascii=False, indent=2))
