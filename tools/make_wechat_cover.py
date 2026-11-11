"""生成公众号封面图（900x383，2.35:1，微信首图推荐比例）。

用法：python tools/make_wechat_cover.py
输出：docs/images/wechat-cover.png
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ICON = ROOT / "docs" / "images" / "app-icon-512.png"
OUT = ROOT / "docs" / "images" / "wechat-cover.png"

W, H = 900, 383
FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"
FONT_REG = "C:/Windows/Fonts/msyh.ttc"

TITLE = ["内网代码想给外网 AI 看？", "三步截成一张长图"]
SUB = "ScrollShot · 绿色单文件 · 不联网 · 开源免费"


def gradient() -> Image.Image:
    img = Image.new("RGB", (W, H), (30, 79, 168))
    top, bot = (37, 99, 235), (18, 46, 110)
    px = img.load()
    for y in range(H):
        t = y / (H - 1)
        row = tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3))
        for x in range(W):
            px[x, y] = row
    return img


def main() -> None:
    base = gradient().convert("RGBA")
    d = ImageDraw.Draw(base, "RGBA")

    # 右侧柔光，给图标留出干净背景
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((520, -90, 1040, 500), fill=(255, 255, 255, 26))
    base.alpha_composite(glow)

    f_title = ImageFont.truetype(FONT_BOLD, 46)
    f_sub = ImageFont.truetype(FONT_REG, 21)
    f_tag = ImageFont.truetype(FONT_BOLD, 20)

    # 左上角小标签：单独图层做半透明，直接画在 RGBA 上会被当成实色
    tag_layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    td = ImageDraw.Draw(tag_layer)
    td.rounded_rectangle((72, 58, 72 + 176, 58 + 40), radius=20, fill=(255, 255, 255, 38))
    td.text((72 + 18, 58 + 9), "SCROLLSHOT", font=f_tag, fill=(255, 255, 255, 240))
    base.alpha_composite(tag_layer)

    y = 122
    for line in TITLE:
        d.text((72, y), line, font=f_title, fill=(255, 255, 255, 255))
        y += 62

    d.text((74, y + 18), SUB, font=f_sub, fill=(214, 230, 255, 235))

    # 右侧应用图标 + 白边
    icon = Image.open(ICON).convert("RGBA").resize((224, 224), Image.Resampling.LANCZOS)
    ring = Image.new("RGBA", (244, 244), (0, 0, 0, 0))
    ImageDraw.Draw(ring).rounded_rectangle((0, 0, 243, 243), radius=54, fill=(255, 255, 255, 34))
    base.alpha_composite(ring, (628, 78))
    base.alpha_composite(icon, (638, 88))

    base.convert("RGB").save(OUT, quality=95)
    print("cover:", OUT, OUT.stat().st_size, "bytes", base.size)


if __name__ == "__main__":
    main()
