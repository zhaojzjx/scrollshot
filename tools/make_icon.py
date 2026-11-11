"""生成 ScrollShot 应用图标：蓝底代码窗口 + 右下角小相机徽标。

输出：
  src/ScrollShot/Assets/app.ico         9 档尺寸 (16..256) 的 BMP 帧 ico
  docs/images/app-icon-512.png          512px 透明底纯图标（可直接引用）
  docs/images/app-icon.png              256 / 48 / 16 三档对照预览

用法：python tools/make_icon.py
"""
from PIL import Image, ImageDraw, ImageFilter
import numpy as np
import io
import os
import struct

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO = os.path.join(ROOT, "src", "ScrollShot", "Assets", "app.ico")
PURE = os.path.join(ROOT, "docs", "images", "app-icon-512.png")
PREVIEW = os.path.join(ROOT, "docs", "images", "app-icon.png")

S = 1024          # 统一超采样画布，绘制后按目标尺寸缩小
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

BLUE_TOP = (96, 165, 255)
BLUE_BOT = (37, 99, 235)
ORANGE = (249, 115, 22)
WHITE = (250, 252, 255, 255)


def plate():
    """蓝色竖向渐变 + 圆角外形。"""
    g = np.zeros((S, 3), dtype=np.uint8)
    for y in range(S):
        t = y / (S - 1)
        g[y] = [int(BLUE_TOP[i] + (BLUE_BOT[i] - BLUE_TOP[i]) * t) for i in range(3)]
    img = Image.fromarray(np.repeat(g[None, :, :], S, axis=0), "RGB").convert("RGBA")
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=int(S * 0.215), fill=255)
    img.putalpha(mask)
    return img


def camera(d, cx, cy, r, tiny):
    """右下角徽标里的白色小相机。tiny=True 时只留机身 + 镜头，供 16/20px 使用。"""
    # 橙色圆盘，白色细描边把它从蓝底和白窗口里分离出来
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(*ORANGE, 255),
              outline=(255, 255, 255, 255), width=max(3, int(r * 0.10)))

    bw, bh = r * 0.98, r * 0.62           # 机身
    by = cy - bh * 0.32
    d.rounded_rectangle((cx - bw / 2, by, cx + bw / 2, by + bh),
                        radius=max(2, int(r * 0.14)), fill=WHITE)
    if not tiny:
        # 顶部取景器凸起
        d.rounded_rectangle((cx - r * 0.30, by - r * 0.17, cx - r * 0.02, by + r * 0.04),
                            radius=max(2, int(r * 0.05)), fill=WHITE)
    # 镜头：深色圆 + 白环 + 中心点
    lr = r * (0.30 if tiny else 0.27)
    ly = by + bh * 0.52
    d.ellipse((cx - lr, ly - lr, cx + lr, ly + lr), fill=(28, 78, 168, 255))
    ir = lr * 0.66
    d.ellipse((cx - ir, ly - ir, cx + ir, ly + ir), outline=(255, 255, 255, 255),
              width=1 if tiny else max(2, int(lr * 0.22)))
    cr = lr * (0.30 if tiny else 0.20)
    d.ellipse((cx - cr, ly - cr, cx + cr, ly + cr), fill=(255, 255, 255, 255))
    if not tiny:
        # 快门小点
        sr = r * 0.065
        sx, sy = cx + bw * 0.34, by + bh * 0.20
        d.ellipse((sx - sr, sy - sr, sx + sr, sy + sr), fill=(*ORANGE, 255))


def draw_pixel(px):
    """16/20px 直接按整像素网格绘制：不缩放、无半透明边，小图标才不糊。"""
    im = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, px - 1, px - 1), radius=max(2, round(px * 0.20)),
                        fill=(45, 110, 235, 255))
    d.rounded_rectangle((2, 2, round(px * 0.78), round(px * 0.84)), radius=1, fill=WHITE)
    for y, xe, col in [(0.28, 0.52, (59, 130, 246)), (0.44, 0.44, (150, 182, 226)),
                       (0.60, 0.46, (175, 202, 238))]:
        d.rectangle((round(px * 0.16), round(px * y), round(px * xe),
                     round(px * y) + max(1, round(px * 0.075))), fill=(*col, 255))

    cx, cy, r = round(px * 0.70), round(px * 0.70), round(px * 0.25)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(*ORANGE, 255))
    bw, bh = round(r * 1.15), round(r * 0.75)
    by = cy - round(bh / 3)
    # 顶部取景器凸起：16px 下靠这一笔把「白方块」认成相机
    d.rectangle((cx - round(bw * 0.32), by - 1, cx - round(bw * 0.02), by),
                fill=(255, 255, 255, 255))
    d.rectangle((cx - bw // 2, by, cx + bw // 2, by + bh), fill=(255, 255, 255, 255))
    # 镜头用实心方块而非 3px 圆形（后者缩到 16px 会变成十字，不像镜头）
    lr = max(1, round(r * 0.28))
    ly = by + round(bh * 0.55)
    d.rectangle((cx - lr, ly - lr, cx + lr, ly + lr), fill=(28, 78, 168, 255))
    d.rectangle((cx - lr, ly - lr, cx - lr, ly - lr), fill=(255, 255, 255, 255))
    return im


def draw(px):
    tiny = px <= 20
    img = plate()
    d = ImageDraw.Draw(img)

    # 白色代码窗口
    x0, y0, x1, y1 = int(S * 0.135), int(S * 0.115), int(S * 0.80), int(S * 0.845)
    d.rounded_rectangle((x0, y0, x1, y1), radius=int(S * 0.055), fill=WHITE,
                        outline=(112, 165, 235, 255), width=max(3, int(S * 0.013)))

    if not tiny:
        # 窗口顶栏：三个小圆点 + 分隔线
        for i, cx in enumerate((0.185, 0.235, 0.285)):
            r = S * 0.015
            x, y = S * cx, S * 0.170
            d.ellipse((x - r, y - r, x + r, y + r), fill=(203, 215, 230, 255))
        d.rounded_rectangle((int(S * 0.165), int(S * 0.225), int(S * 0.775), int(S * 0.231)),
                            radius=3, fill=(228, 236, 246, 255))

    # 代码行：右端止于 0.56，给右下角徽标让路
    rows = [(0.31, 0.56, (59, 130, 246)), (0.43, 0.50, (150, 182, 226)),
            (0.55, 0.55, (175, 202, 238))]
    if not tiny:
        rows.append((0.67, 0.46, (198, 216, 240)))
    h = S * (0.085 if tiny else 0.055)
    for y, xe, col in rows:
        d.rounded_rectangle((int(S * 0.185), int(S * y), int(S * xe), int(S * y) + int(h)),
                            radius=int(h / 2), fill=(*col, 255))

    # 右下角小相机徽标
    camera(d, S * 0.755, S * 0.755, S * (0.235 if tiny else 0.205), tiny)

    out = img.resize((px, px), Image.Resampling.LANCZOS)
    if tiny:
        # 16/20px 下 LANCZOS 留下的半透明羽化边会显脏，直接二值化收成硬边
        a = np.asarray(out.getchannel("A"))
        out.putalpha(Image.fromarray(np.where(a >= 110, 255, 0).astype(np.uint8), "L"))
    return out


def build_ico(frames):
    """把每帧的 BMP DIB 拼成标准 ico，目录项严格 16 字节。"""
    entries = []
    for s in SIZES:
        buf = io.BytesIO()
        frames[s].save(buf, format="ICO", sizes=[(s, s)], bitmap_format="bmp")
        raw = buf.getvalue()
        head = bytearray(raw[6:14])                   # bWidth..wBitCount
        head[4:6] = struct.pack("<H", 1)              # Pillow 写 planes=0，ICO 规范要求 1
        nb, off = struct.unpack_from("<II", raw, 14)  # dwBytesInRes, dwImageOffset
        entries.append((bytes(head), raw[off:off + nb]))

    ico = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    dirs, blobs = b"", b""
    for head, data in entries:
        dirs += head + struct.pack("<II", len(data), offset)
        blobs += data
        offset += len(data)
    out = ico + dirs + blobs

    for i, s in enumerate(SIZES):
        w, _, _, _, planes, bpp, nb, off = struct.unpack_from("<BBBBHHII", out, 6 + i * 16)
        assert (w or 256) == s and planes == 1 and bpp == 32 and off + nb <= len(out)
        assert struct.unpack_from("<I", out, off)[0] == 40
    return out


def build_preview(frames):
    w, h = 920, 340
    sheet = Image.new("RGBA", (w, h), (247, 249, 252, 255))
    d = ImageDraw.Draw(sheet)
    d.text((20, 14), "ScrollShot  -  camera badge", fill=(40, 55, 75, 255))
    sheet.alpha_composite(frames[256], (20, 48))
    d.text((20, 312), "256 px", fill=(70, 85, 105, 255))
    sheet.alpha_composite(frames[48].resize((180, 180), Image.Resampling.NEAREST), (350, 90))
    d.text((350, 286), "48 px (x3.75)", fill=(70, 85, 105, 255))
    sheet.alpha_composite(frames[16].resize((180, 180), Image.Resampling.NEAREST), (640, 90))
    d.text((640, 286), "16 px (x11)", fill=(70, 85, 105, 255))
    return sheet.convert("RGB")


if __name__ == "__main__":
    frames = {s: (draw_pixel(s) if s <= 20 else draw(s)) for s in SIZES}
    os.makedirs(os.path.dirname(ICO), exist_ok=True)
    os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
    with open(ICO, "wb") as f:
        f.write(build_ico(frames))
    draw(512).save(PURE)
    build_preview(frames).save(PREVIEW)
    print("ico :", ICO, os.path.getsize(ICO), "bytes,", len(SIZES), "frames")
    print("pure:", PURE, os.path.getsize(PURE), "bytes")
    print("prev:", PREVIEW, os.path.getsize(PREVIEW), "bytes")
