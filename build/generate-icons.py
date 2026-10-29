#!/usr/bin/env python3
"""Generates every platform icon from one drawing. Run: python3 build/generate-icons.py"""
import math, os, struct, io
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024

def draw(size=S, background=True, rounded=True):
    scale = 4  # supersample for smooth edges
    n = size * scale
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if background:
        # Vertical navy gradient in a rounded square (iOS/macOS-style squircle approximation).
        grad = Image.new("RGBA", (n, n))
        gd = ImageDraw.Draw(grad)
        top, bottom = (16, 42, 84), (7, 20, 43)
        for y in range(n):
            t = y / n
            gd.line([(0, y), (n, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
        mask = Image.new("L", (n, n), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=int(n * 0.225) if rounded else 0, fill=255)
        img.paste(grad, (0, 0), mask)

    cx, cy = n / 2, n / 2
    # Heptagon (a nod to the Kubernetes wheel) in teal.
    r = n * 0.36
    pts = [(cx + r * math.sin(2 * math.pi * k / 7), cy - r * math.cos(2 * math.pi * k / 7)) for k in range(7)]
    d.polygon(pts, outline=(46, 196, 182, 255), width=int(n * 0.045))

    # Hull + keel.
    hull = [(cx - n * 0.22, cy - n * 0.02), (cx + n * 0.22, cy - n * 0.02), (cx + n * 0.14, cy + n * 0.10), (cx - n * 0.14, cy + n * 0.10)]
    d.polygon(hull, fill=(255, 255, 255, 255))
    d.polygon([(cx - n * 0.035, cy + n * 0.10), (cx + n * 0.035, cy + n * 0.10), (cx, cy + n * 0.215)], fill=(255, 255, 255, 255))
    # Mast + sail.
    d.rectangle([cx - n * 0.012, cy - n * 0.24, cx + n * 0.012, cy - n * 0.02], fill=(255, 255, 255, 255))
    d.polygon([(cx + n * 0.03, cy - n * 0.22), (cx + n * 0.03, cy - n * 0.05), (cx + n * 0.17, cy - n * 0.05)], fill=(46, 196, 182, 255))
    return img.resize((size, size), Image.LANCZOS)

def save(img, *path):
    p = os.path.join(ROOT, *path)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    img.save(p)
    return p

def main():
    master = draw()
    save(master, "assets", "brand", "telltale-kube-1024.png")
    save(draw(512), "src", "TelltaleKube.App", "Assets", "telltale-kube.png")
    save(draw(256), "packaging", "linux", "telltale-kube.png")
    # Windows .ico (multi-resolution)
    ico = os.path.join(ROOT, "src", "TelltaleKube.App", "Assets", "telltale-kube.ico")
    master.save(ico, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    # macOS .icns (PNG-encoded entries)
    entries = b""
    for code, sz in ((b"ic07", 128), (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024)):
        buf = io.BytesIO(); draw(sz).save(buf, "PNG"); data = buf.getvalue()
        entries += code + struct.pack(">I", len(data) + 8) + data
    icns = os.path.join(ROOT, "packaging", "macos", "telltale-kube.icns")
    os.makedirs(os.path.dirname(icns), exist_ok=True)
    with open(icns, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(entries) + 8) + entries)
    # Android launcher (legacy + round), densities
    for folder, sz in (("mipmap-mdpi", 48), ("mipmap-hdpi", 72), ("mipmap-xhdpi", 96), ("mipmap-xxhdpi", 144), ("mipmap-xxxhdpi", 192)):
        save(draw(sz), "src", "TelltaleKube.Android", "Resources", folder, "appicon.png")
    # Android 12+ splash icon: foreground only, transparent background
    save(draw(288, background=False), "src", "TelltaleKube.Android", "Resources", "drawable", "splash_icon.png")
    # iOS app icon (single 1024 asset; Xcode 14+ generates the rest). App Store icons must be opaque.
    appicon = os.path.join("src", "TelltaleKube.iOS", "Assets.xcassets", "AppIcon.appiconset")
    save(draw(rounded=False).convert("RGB"), appicon, "icon-1024.png")
    with open(os.path.join(ROOT, appicon, "Contents.json"), "w") as f:
        f.write('{\n  "images" : [ { "filename" : "icon-1024.png", "idiom" : "universal", "platform" : "ios", "size" : "1024x1024" } ],\n  "info" : { "author" : "xcode", "version" : 1 }\n}\n')
    with open(os.path.join(ROOT, "src", "TelltaleKube.iOS", "Assets.xcassets", "Contents.json"), "w") as f:
        f.write('{\n  "info" : { "author" : "xcode", "version" : 1 }\n}\n')
    print("icons generated")

if __name__ == "__main__":
    main()
