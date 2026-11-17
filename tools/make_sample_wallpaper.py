"""生成 AudioSwitch 文档用的示例壁纸（暖色渐变 + 柔和光斑）。

只用于 README / 外观预览，不参与程序运行。
运行：python tools/make_sample_wallpaper.py
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

WIDTH, HEIGHT = 1600, 900
OUTPUT = Path(__file__).resolve().parent.parent / "docs" / "wallpaper.png"

TOP = np.array([38, 30, 84], dtype=np.float32)      # 深靛蓝
MIDDLE = np.array([132, 58, 104], dtype=np.float32)  # 紫红
BOTTOM = np.array([240, 150, 80], dtype=np.float32)  # 暖橙

GLOWS = (
    ((1180, 210), 430, np.array([255, 200, 130]), 0.55),
    ((240, 700), 400, np.array([110, 170, 255]), 0.40),
    ((830, 500), 520, np.array([255, 120, 170]), 0.28),
)


def gradient() -> np.ndarray:
    ys, xs = np.mgrid[0:HEIGHT, 0:WIDTH].astype(np.float32)
    t = xs / WIDTH * 0.35 + ys / HEIGHT * 0.65
    t = np.clip(t, 0.0, 1.0)[..., None]

    first = TOP + (MIDDLE - TOP) * np.clip(t / 0.5, 0, 1)
    second = MIDDLE + (BOTTOM - MIDDLE) * np.clip((t - 0.5) / 0.5, 0, 1)
    return np.where(t < 0.5, first, second).astype(np.float32)


def add_glows(image: np.ndarray) -> np.ndarray:
    ys, xs = np.mgrid[0:HEIGHT, 0:WIDTH].astype(np.float32)
    result = image
    for (cx, cy), radius, color, strength in GLOWS:
        distance2 = (xs - cx) ** 2 + (ys - cy) ** 2
        falloff = np.exp(-distance2 / (2 * radius ** 2)) * strength
        result = result + falloff[..., None] * color
    return np.clip(result, 0, 255)


def add_grain(image: np.ndarray) -> np.ndarray:
    rng = np.random.default_rng(7)
    grain = rng.normal(0, 2.6, (HEIGHT, WIDTH, 1)).astype(np.float32)
    return np.clip(image + grain, 0, 255)


def main() -> None:
    pixels = add_grain(add_glows(gradient()))
    image = Image.fromarray(pixels.astype(np.uint8), mode="RGB")
    image = image.convert("RGB").resize((WIDTH, HEIGHT), Image.LANCZOS)
    image.save(OUTPUT, format="PNG", optimize=True)
    print(f"Generated sample wallpaper: {OUTPUT} ({image.width}x{image.height})")


if __name__ == "__main__":
    main()
