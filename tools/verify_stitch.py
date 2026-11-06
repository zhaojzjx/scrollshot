#!/usr/bin/env python3
"""
卷间衔接自检工具 —— 验证 ScrollShot 输出的多卷图片是否真的连续。

检查三件事：
  1. 卷间衔接：上卷末 N 行是否等于下卷首 N 行（VolumeOverlap 是否生效）；
  2. 对照：与不相邻区域的平均差，排除"整张图都一样"造成的假阳性；
  3. 白块：卷内是否存在连续纯色行（未完成重绘的痕迹）。

用法：
  python verify_stitch.py [目录] [--overlap 100]

  目录缺省为 ~/Pictures/滚动截图；--overlap 缺省 100，与 Config.VolumeOverlap 默认值一致。

依赖：pip install pillow numpy
"""
import argparse
import os
import sys
from glob import glob

try:
    import numpy as np
    from PIL import Image
except ImportError:
    sys.exit("缺少依赖，请先执行：pip install pillow numpy")


def load(paths):
    return [np.asarray(Image.open(p).convert("L"), dtype=np.int16) for p in paths]


def mean_abs_diff(a, b):
    w = min(a.shape[1], b.shape[1])
    return float(np.abs(a[:, :w] - b[:, :w]).mean())


def flat_runs(img, threshold=1.0, min_run=20):
    """返回连续纯色行区间（行标准差 < threshold 视为纯色）"""
    flat = np.where(img.std(axis=1) < threshold)[0]
    runs, start, prev = [], None, None
    for y in flat:
        if start is None:
            start = y
        elif y != prev + 1:
            runs.append((start, prev))
            start = y
        prev = y
    if start is not None:
        runs.append((start, prev))
    return [(s, e) for s, e in runs if e - s >= min_run]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", nargs="?", default=os.path.expanduser(r"~\Pictures\滚动截图"))
    ap.add_argument("--overlap", type=int, default=100)
    args = ap.parse_args()

    if not os.path.isdir(args.dir):
        sys.exit(f"目录不存在：{args.dir}")

    paths = sorted(glob(os.path.join(args.dir, "*.png")))
    if len(paths) < 2:
        sys.exit(f"至少需要 2 张图才能比对，当前找到 {len(paths)} 张")

    imgs = load(paths)
    k = args.overlap

    print(f"目录：{args.dir}")
    print(f"卷间重叠校验行数：{k}\n")
    for p, a in zip(paths, imgs):
        print(f"  {os.path.basename(p):<45} {a.shape[1]}x{a.shape[0]}")

    print(f"\n== 卷间衔接：上卷末 {k} 行 vs 下卷首 {k} 行 ==")
    ok = True
    for i in range(len(imgs) - 1):
        d = mean_abs_diff(imgs[i][-k:, :], imgs[i + 1][:k, :])
        good = d < 1.0
        ok = ok and good
        print(f"  {'OK ' if good else '!! '}vol{i + 1} -> vol{i + 2}   平均灰度差 = {d:.3f}")

    print("\n== 对照：与不相邻区域比（应明显更大，否则比对无意义）==")
    for i in range(len(imgs) - 1):
        if imgs[i + 1].shape[0] < 1200:
            continue
        d = mean_abs_diff(imgs[i][-k:, :], imgs[i + 1][1000:1000 + k, :])
        print(f"     vol{i + 1} vs vol{i + 2}[1000:]   平均灰度差 = {d:.3f}")

    print("\n== 白块检查：卷内连续纯色行（>=20 行视为可疑）==")
    for p, a in zip(paths, imgs):
        runs = flat_runs(a)
        print(f"  {os.path.basename(p)[-14:]}  {'可疑 ' + str(runs[:5]) if runs else '无'}")

    print("\n结论：" + ("卷间衔接正常" if ok else "存在衔接断裂，请检查 VolumeOverlap 与切点逻辑"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
