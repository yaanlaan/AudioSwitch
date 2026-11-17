"""生成 AudioSwitch 应用图标（多尺寸 ICO）。

用法（需要 Pillow）：
    python tools/make_icon.py
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024
OUT = Path(__file__).resolve().parent.parent / "src" / "AudioSwitch" / "Assets" / "app.ico"

TOP = (94, 160, 255)
BOTTOM = (43, 105, 232)


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def gradient(size: int) -> Image.Image:
    img = Image.new("RGB", (size, size))
    draw = ImageDraw.Draw(img)
    for y in range(size):
        t = y / (size - 1)
        color = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3))
        draw.line((0, y, size, y), fill=color)
    return img


def main() -> None:
    canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    radius = int(SIZE * 0.235)
    canvas.paste(gradient(SIZE), (0, 0), rounded_mask(SIZE, radius))

    # 轻微顶部高光，让图标在浅色/深色任务栏上都有层次
    highlight = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    hd = ImageDraw.Draw(highlight)
    hd.ellipse((-SIZE * 0.35, -SIZE * 0.95, SIZE * 1.35, SIZE * 0.45), fill=(255, 255, 255, 26))
    canvas = Image.alpha_composite(canvas, Image.composite(highlight, Image.new("RGBA", (SIZE, SIZE)), rounded_mask(SIZE, radius)))

    draw = ImageDraw.Draw(canvas)
    white = (255, 255, 255, 255)

    # 扬声器本体
    draw.rounded_rectangle(
        (SIZE * 0.255, SIZE * 0.375, SIZE * 0.40, SIZE * 0.625),
        radius=SIZE * 0.028,
        fill=white,
    )
    # 扬声器锥体
    draw.polygon(
        [
            (SIZE * 0.40, SIZE * 0.375),
            (SIZE * 0.55, SIZE * 0.265),
            (SIZE * 0.55, SIZE * 0.735),
            (SIZE * 0.40, SIZE * 0.625),
        ],
        fill=white,
    )

    # 两道声波弧线
    center = (SIZE * 0.55, SIZE * 0.50)
    for radius_scale, width_scale in ((0.145, 0.042), (0.235, 0.040)):
        r = SIZE * radius_scale
        box = (center[0] - r, center[1] - r, center[0] + r, center[1] + r)
        draw.arc(box, start=-52, end=52, fill=white, width=round(SIZE * width_scale))

    # 右下角“切换”双箭头暗示
    arrow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ad = ImageDraw.Draw(arrow)
    cx, cy = SIZE * 0.715, SIZE * 0.775
    s = SIZE * 0.105
    ad.line((cx - s, cy - s * 0.35, cx + s, cy - s * 0.35), fill=(255, 255, 255, 235), width=round(SIZE * 0.036))
    ad.polygon(
        [
            (cx + s, cy - s * 0.35),
            (cx + s - s * 0.42, cy - s * 0.72),
            (cx + s - s * 0.42, cy + s * 0.02),
        ],
        fill=(255, 255, 255, 235),
    )
    ad.line((cx + s, cy + s * 0.45, cx - s, cy + s * 0.45), fill=(255, 255, 255, 235), width=round(SIZE * 0.036))
    ad.polygon(
        [
            (cx - s, cy + s * 0.45),
            (cx - s + s * 0.42, cy + s * 0.08),
            (cx - s + s * 0.42, cy + s * 0.82),
        ],
        fill=(255, 255, 255, 235),
    )
    canvas = Image.alpha_composite(canvas, arrow)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    canvas.save(OUT, format="ICO", sizes=sizes)
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
