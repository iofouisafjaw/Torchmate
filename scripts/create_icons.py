"""Render our code-native pixel lantern mark. No upstream image is an input.

The editable SVG is the only design source; the PNG encoder uses Python stdlib.
"""
from pathlib import Path
import struct
import xml.etree.ElementTree as ET
import zlib

ROOT = Path(__file__).resolve().parents[1]


def render(svg, size):
    root = ET.fromstring(svg)
    extent = float(root.attrib["viewBox"].split()[2])
    shapes = []
    for shape in root:
        if shape.tag.rsplit("}", 1)[-1] != "rect":
            raise ValueError("Icon source supports rectangles only")
        shapes.append((tuple(float(shape.attrib.get(k, 0)) for k in ("x", "y", "width", "height")),
                       bytes.fromhex(shape.attrib["fill"].lstrip("#")) + b"\xff"))
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            sx, sy = (x + .5) * extent / size, (y + .5) * extent / size
            color = bytes(4)
            for (left, top, width, height), fill in shapes:
                if left <= sx < left + width and top <= sy < top + height:
                    color = fill
            rows.extend(color)

    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def main():
    source = (ROOT / "assets/companion-icon.svg").read_text(encoding="utf-8")
    for name, size in (("icon.png", 80), ("icon_small.png", 30)):
        (ROOT / name).write_bytes(render(source, size))
        print(f"Rendered {name}: {size} x {size}")


if __name__ == "__main__":
    main()
