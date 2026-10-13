"""生成应用图标 Assets/app.ico（多尺寸）。

用途：csproj 的 ApplicationIcon（exe 图标）与窗口图标（AppWindow.SetIcon）。
配色与软件一致：深色底（#1E2530 → #12171E）+ 青色强调（#4FC3F7），主体为"记忆条目层叠"意象。

用法：python generate_icons.py
"""
import os
import sys

from PIL import Image, ImageDraw

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Assets")
OUT_PATH = os.path.join(OUT_DIR, "app.ico")

# 各尺寸（16/24/32/48/64/128/256 —— 覆盖任务栏、标题栏、资源管理器与高 DPI）
SIZES = [16, 24, 32, 48, 64, 128, 256]

BG_TOP = (30, 37, 48, 255)      # #1E2530
BG_BOTTOM = (18, 23, 30, 255)   # #12171E
ACCENT = (79, 195, 247, 255)    # #4FC3F7
ACCENT_DIM = (79, 195, 247, 165)
ACCENT_FAINT = (79, 195, 247, 95)


def draw_master(size: int = 256) -> Image.Image:
    """绘制母版（256x256，4 倍超采样后缩放，边缘更平滑）。"""
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 圆角底：竖向渐变（逐行绘制，再按圆角遮罩裁剪）
    grad = Image.new("RGBA", (s, s), BG_TOP)
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / max(1, s - 1)
        color = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(4))
        gd.line([(0, y), (s, y)], fill=color)

    radius = int(s * 0.22)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    draw = ImageDraw.Draw(img)

    # 主体：三条"记忆条目"横条，水平居中、宽度递减、自上而下由亮到淡（层叠的记忆）
    bar_h = int(s * 0.11)
    radius_bar = int(bar_h / 2)
    bars = [
        (0.28, 0.72, 0.28, ACCENT),        # top, width ratio, y ratio, color
        (0.28, 0.58, 0.445, ACCENT_DIM),
        (0.28, 0.44, 0.61, ACCENT_FAINT),
    ]
    for left_ratio, right_ratio, y_ratio, color in bars:
        x0 = int(s * left_ratio)
        x1 = int(s * right_ratio)
        y0 = int(s * y_ratio)
        draw.rounded_rectangle([x0, y0, x1, y0 + bar_h], radius=radius_bar, fill=color)

    return img.resize((size, size), Image.LANCZOS)


def main() -> int:
    os.makedirs(OUT_DIR, exist_ok=True)
    master = draw_master(256)
    # ICO 保存：PIL 会按 sizes 生成各尺寸（从母版缩放）
    master.save(OUT_PATH, format="ICO", sizes=[(x, x) for x in SIZES])
    print(f"已生成 {OUT_PATH}")
    with Image.open(OUT_PATH) as check:
        print("包含尺寸:", sorted(check.info.get("sizes", [])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
