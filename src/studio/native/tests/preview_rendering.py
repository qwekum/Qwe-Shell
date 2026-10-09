"""Render published themes through the actual native export, without applying them."""
import base64
import ctypes
import hashlib
import json
from pathlib import Path
import sys
import struct
import zlib
from collections import Counter


def write_frame(path, frame):
    pixels = base64.b64decode(frame["pixels"], validate=True)
    rows = bytearray()
    width, height = frame["width"], frame["height"]
    for y in range(height):
        rows.append(0)
        for x in range(width):
            b, g, r, a = pixels[(y * width + x) * 4:(y * width + x + 1) * 4]
            rows.extend((min(255, r * 255 // a) if a else 0,
                         min(255, g * 255 // a) if a else 0,
                         min(255, b * 255 // a) if a else 0, a))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) +
                     chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def main():
    dll = ctypes.CDLL(str(Path(sys.argv[1]).resolve()))
    dll.shell_studio_preview.argtypes = (ctypes.c_wchar_p, ctypes.c_size_t)
    dll.shell_studio_preview.restype = ctypes.c_void_p
    dll.shell_studio_free.argtypes = (ctypes.c_void_p,)
    count = 0

    def render(source, dpi=96, **extra):
        request = dict(operation="render", source=source, context=dict(dpi=dpi, themeMode=1),
                       mode="standalone", sampleMenu=[dict(title="&Open"), dict(title="Copy", checked=True),
                       dict(separator=True), dict(title="Disabled", disabled=True), dict(title="Unicode λ 日本語")])
        request.update(extra)
        data = json.dumps(request, ensure_ascii=True)
        pointer = dll.shell_studio_preview(data, len(data))
        assert pointer
        try:
            result = json.loads(ctypes.string_at(pointer).decode("utf-8"))
        finally:
            dll.shell_studio_free(pointer)
        assert result.get("available"), result
        frame = result["frame"]
        pixels = base64.b64decode(frame["pixels"], validate=True)
        assert len(pixels) == frame["width"] * frame["height"] * 4
        assert frame["dpi"] == dpi and len(frame["rows"]) == len(result["items"])
        return result, hashlib.sha256(pixels).hexdigest()

    for name in ("catppuccin-mocha-mauve.nss", "catppuccin-latte-mauve.nss"):
        source = (Path(__file__).parent / "fixtures" / "catppuccin" / name).read_text(encoding="utf-8-sig")
        frames = [render(source, dpi)[0]["frame"] for dpi in (96, 144, 192)]
        expected_background = bytes.fromhex("2e1e1eff" if "mocha" in name else "f5f1efff")
        for frame in frames:
            raw = base64.b64decode(frame["pixels"], validate=True)
            colors = Counter(raw[index:index + 4] for index in range(0, len(raw), 4))
            assert colors[expected_background] > frame["width"] * frame["height"] // 4, {
                "theme": name, "dpi": frame["dpi"], "expectedPbgra": expected_background.hex(),
                "commonColors": [(color.hex(), total) for color, total in colors.most_common(3)]}
        if len(sys.argv) > 2:
            output = Path(sys.argv[2]); output.mkdir(parents=True, exist_ok=True)
            for frame in frames:
                write_frame(output / f"{Path(name).stem}-{frame['dpi']}.png", frame)
        assert frames[0]["height"] < frames[1]["height"] < frames[2]["height"], frames
        count += 3
    first, first_hash = render("theme { background.color=#112233 }")
    changed, changed_hash = render("theme { background.color=#334455 }")
    assert first_hash != changed_hash
    assert first["dependencies"] != changed["dependencies"]
    count += 1
    original = [dict(id="original", title="Original captured row", childrenAvailable=False)]
    captured, _ = render("", mode="captured", capture=dict(original=original, entries=[dict(title="Wrong final row")]))
    assert captured["items"][0]["title"] == "Original captured row"
    count += 1
    print(f"PASS {count} native theme/render checks; offscreen only")


if __name__ == "__main__":
    main()
