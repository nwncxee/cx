#!/usr/bin/env python3
"""CX-374: read a `/alloy snap` scene colour (cx_snap/N_vkscene.pfm), print its channel stats linear and in
sRGB, and write a percentile-stretched PNG beside it, so a sub-code-value sky fault is a number and a picture
rather than an impression. A second set compares against the first, which is how an A/B is read.

    snap_stats.py <dir> <N> [<dir2> <N2>]
"""
import os
import sys

import numpy as np
from PIL import Image


def read_pfm(p):
    with open(p, 'rb') as f:
        assert f.readline().strip() == b'PF'
        w, h = map(int, f.readline().split())
        scale = float(f.readline())
        a = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4').reshape(h, w, 3)
    return a[::-1].astype(np.float64)   # bottom-up -> top-down


def stats(name, a):
    m = a.reshape(-1, 3).mean(0)
    s = a.reshape(-1, 3).std(0)
    mn = a.reshape(-1, 3).min(0)
    mx = a.reshape(-1, 3).max(0)
    print(f"{name:16s} mean {m[0]:.5f} {m[1]:.5f} {m[2]:.5f}  std {s[0]:.5f} {s[1]:.5f} {s[2]:.5f}"
          f"  min {mn[0]:.4f} {mn[1]:.4f} {mn[2]:.4f}  max {mx[0]:.4f} {mx[1]:.4f} {mx[2]:.4f}")


def save(name, a):
    lo, hi = np.percentile(a, 0.5), np.percentile(a, 99.5)   # percentile stretch
    x = (a - lo) / max(hi - lo, 1e-9)
    Image.fromarray((np.clip(x, 0, 1) * 255).astype(np.uint8)).save(name)


def encode(a):   # linear -> sRGB
    a = np.clip(a, 0, 1)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * a ** (1 / 2.4) - 0.055)


def one(d, n):
    p = os.path.join(d, f'{n}_vkscene.pfm')
    if not os.path.exists(p):
        sys.exit(f"no {p} -- /alloy snap writes one scene colour per armed frame")
    a = read_pfm(p)
    stats(n, a)
    stats(n + '>srgb', encode(a))
    save(os.path.join(d, f'{n}_vkscene_stretch.png'), a)
    return a


a = one(sys.argv[1], sys.argv[2])
if len(sys.argv) > 4:
    b = one(sys.argv[3], sys.argv[4])
    if a.shape != b.shape:
        sys.exit(f"the two sets are {a.shape} and {b.shape} -- an A/B needs one camera at one size")
    dlt = b - a
    stats('b-a', dlt)
    save(os.path.join(sys.argv[3], f'{sys.argv[4]}_diff.png'), np.abs(dlt))
