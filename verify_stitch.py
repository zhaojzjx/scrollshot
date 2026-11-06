import sys
from PIL import Image
import numpy as np

D = r"C:\Users\赵荣晖\Pictures\滚动截图"
names = ["ScrollShot_20261031_173509_699_01.png",
         "ScrollShot_20261031_173509_699_02.png",
         "ScrollShot_20261031_173509_699_03.png",
         "ScrollShot_20261031_173509_699_04.png"]

imgs = [np.asarray(Image.open(D + "\\" + n).convert("L"), dtype=np.int16) for n in names]
for n, a in zip(names, imgs):
    print(f"{n[-10:]}  {a.shape[1]}x{a.shape[0]}")

def tail_head_sad(a, b, k=100):
    """a 的最后 k 行 vs b 的最前 k 行"""
    ta = a[-k:, :]
    hb = b[:k, :]
    w = min(ta.shape[1], hb.shape[1])
    return float(np.abs(ta[:, :w] - hb[:, :w]).mean())

print("\n== 卷间衔接：上卷末 100 行 vs 下卷首 100 行 ==")
for i in range(len(imgs) - 1):
    print(f"vol{i+1} -> vol{i+2}   平均灰度差 = {tail_head_sad(imgs[i], imgs[i+1]):.3f}")

print("\n== 对照：上卷末 100 行 vs 下卷第 1000~1100 行（不相邻区域）==")
for i in range(len(imgs) - 1):
    a, b = imgs[i], imgs[i + 1]
    if b.shape[0] < 1200:
        continue
    ta = a[-100:, :]
    hb = b[1000:1100, :]
    w = min(ta.shape[1], hb.shape[1])
    print(f"vol{i+1} vs vol{i+2}[1000:]   平均灰度差 = {float(np.abs(ta[:, :w] - hb[:, :w]).mean()):.3f}")

print("\n== 卷内是否存在整段纯色带（疑似未重绘白块）==")
for n, a in zip(names, imgs):
    rows_std = a.std(axis=1)
    flat = np.where(rows_std < 1.0)[0]
    runs, start = [], None
    prev = None
    for y in flat:
        if start is None:
            start = y
        elif prev is not None and y != prev + 1:
            runs.append((start, prev))
            start = y
        prev = y
    if start is not None:
        runs.append((start, prev))
    long_runs = [(s, e) for s, e in runs if e - s >= 20]
    print(f"{n[-6:]}  纯色行 {len(flat)}  其中连续>=20行: {long_runs[:5]}")
