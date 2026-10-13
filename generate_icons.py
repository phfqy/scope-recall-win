"""生成 ScopeRecallWin 的 MSIX 应用图标（Assets/）。

品牌标识：圆角方牌 + 垂直渐变（Fluent Accent 系）+ 白色"光圈/镜头"图形，
隐喻 Scope（范围）与 Recall（检索）。透明背景，任意尺寸清晰。

用法（任一 Python + Pillow）：
    python generate_icons.py

产物（尺寸与 Package.appxmanifest 引用一一对应）：
    Assets/StoreLogo.png            50x50   （包属性 Logo）
    Assets/Square44x44Logo.png      44x44    （应用图标）
    Assets/Square150x150Logo.png    150x150  （中等磁贴）
    Assets/Wide310x150Logo.png      310x150  （宽磁贴）
    Assets/SmallTile.png            71x71    （小磁贴）
    Assets/LargeTile.png            310x310  （大磁贴）
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent / "Assets"

# Fluent 渐变（暗色控制台 Accent 系）：顶 #6ED2F7 → 底 #0288D1
TOP = (110, 210, 247, 255)
BOTTOM = (2, 136, 209, 255)
WHITE = (255, 255, 255, 255)


def _lerp(a: tuple, b: tuple, t: float) -> tuple:
    return tuple(int(round(x + (y - x) * t)) for x, y in zip(a, b))  # type: ignore[return-value]


def _rounded_plate(size: int, radius_ratio: float = 0.22) -> Image.Image:
    """圆角方牌 + 垂直渐变。"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mask = Image.new("L", (size, size), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * radius_ratio), fill=255)

    grad = Image.new("RGBA", (size, size))
    gd = ImageDraw.Draw(grad)
    for y in range(size):
        gd.line([(0, y), (size, y)], fill=_lerp(TOP, BOTTOM, y / max(size - 1, 1)))
    img.paste(grad, (0, 0), mask)
    return img


def _aperture(draw: ImageDraw.ImageDraw, cx: float, cy: float, r: float) -> None:
    """白色光圈：外环 + 内圆 + 高光点。"""
    w = max(int(r * 0.16), 1)
    draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=WHITE, width=w)
    inner = r * 0.42
    draw.ellipse([cx - inner, cy - inner, cx + inner, cy + inner], fill=WHITE)
    hl = r * 0.13
    hx, hy = cx - inner * 0.38, cy - inner * 0.38
    draw.ellipse([hx - hl, hy - hl, hx + hl, hy + hl], fill=_lerp(TOP, BOTTOM, 0.35))


def _square_icon(size: int, radius_ratio: float = 0.22) -> Image.Image:
    img = _rounded_plate(size, radius_ratio)
    _aperture(ImageDraw.Draw(img), size / 2, size / 2, size * 0.27)
    return img


def _wide_icon(w: int, h: int) -> Image.Image:
    """宽磁贴：左侧圆角方牌 + 右侧品牌字标（无外部字体依赖的几何字）。"""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    plate_h = int(h * 0.82)
    plate = _square_icon(plate_h, radius_ratio=0.20)
    img.paste(plate, (int(h * 0.09), int(h * 0.09)), plate)

    # 右侧三根递增立柱，隐喻"记忆增长/趋势"
    d = ImageDraw.Draw(img)
    x0 = int(h * 0.09) + plate_h + int(h * 0.16)
    bar_w = max(int(h * 0.075), 2)
    gap = bar_w
    base_y = h - int(h * 0.22)
    for i, ratio in enumerate((0.34, 0.55, 0.78)):
        bh = int(h * 0.5 * ratio)
        x = x0 + i * (bar_w + gap)
        color = _lerp(TOP, BOTTOM, 0.25 + 0.25 * i)
        d.rounded_rectangle([x, base_y - bh, x + bar_w, base_y],
                            radius=bar_w // 2, fill=color + (255,) if len(color) == 3 else color)
    return img


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    jobs = {
        "StoreLogo.png": _square_icon(50, radius_ratio=0.18),
        "Square44x44Logo.png": _square_icon(44, radius_ratio=0.18),
        "Square150x150Logo.png": _square_icon(150),
        "SmallTile.png": _square_icon(71),
        "LargeTile.png": _square_icon(310),
        "Wide310x150Logo.png": _wide_icon(310, 150),
    }
    for name, img in jobs.items():
        img.save(ASSETS / name, format="PNG")
        print(f"{name:26s} {img.size[0]}x{img.size[1]}")


if __name__ == "__main__":
    main()
