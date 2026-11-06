"""Semi-automatic product setup: find the wires inside a unit template, calibrate the pixel size
(same steps as the C# AutoWires)."""
import math

import cv2
import numpy as np

from .inspect import FImage, ball_score, find_units, inspect_wire
from .product import Wire


def _seg_len(s):
    return max(1e-6, math.hypot(s[2] - s[0], s[3] - s[1]))


def _same(a, b, perp_tol, gap_tol):
    la = _seg_len(a)
    ux, uy = (a[2] - a[0]) / la, (a[3] - a[1]) / la
    lb = _seg_len(b)
    vx, vy = (b[2] - b[0]) / lb, (b[3] - b[1]) / lb
    if abs(ux * vx + uy * vy) < math.cos(math.radians(12)):
        return False

    def perp(x, y):
        return abs((x - a[0]) * -uy + (y - a[1]) * ux)

    if perp(b[0], b[1]) > perp_tol or perp(b[2], b[3]) > perp_tol:
        return False
    t1 = (b[0] - a[0]) * ux + (b[1] - a[1]) * uy
    t2 = (b[2] - a[0]) * ux + (b[3] - a[1]) * uy
    return min(t1, t2) <= la + gap_tol and max(t1, t2) >= -gap_tol


def _merge(segs, perp_tol, gap_tol):
    """Union collinear, overlapping or nearly touching segments into one per wire."""
    n = len(segs)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(n):
        for j in range(i + 1, n):
            if _same(segs[i], segs[j], perp_tol, gap_tol):
                parent[find(i)] = find(j)
    groups = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(segs[i])
    out = []
    for members in groups.values():
        longest = max(members, key=_seg_len)
        l = _seg_len(longest)
        ux, uy = (longest[2] - longest[0]) / l, (longest[3] - longest[1]) / l
        pts = [(s[0], s[1]) for s in members] + [(s[2], s[3]) for s in members]
        mx, my = sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)
        proj = [(p[0] - mx) * ux + (p[1] - my) * uy for p in pts]
        lo, hi = min(proj), max(proj)
        out.append((mx + lo * ux, my + lo * uy, mx + hi * ux, my + hi * uy))
    return out


def _line_kernel(theta, length, off):
    """Averaging kernel along a line of length `length` at angle theta, offset `off` px along its normal."""
    size = length + 2 * abs(off) + 3
    if size % 2 == 0:
        size += 1
    c = size // 2
    dx, dy = math.cos(theta), math.sin(theta)
    nx, ny = -dy, dx
    k = np.zeros((size, size), np.float64)
    steps = length * 4
    w = 1.0 / (steps + 1)
    for i in range(steps + 1):
        s = -length / 2.0 + length * i / steps
        x, y = c + s * dx + off * nx, c + s * dy + off * ny
        x0, y0 = math.floor(x), math.floor(y)
        fx, fy = x - x0, y - y0
        k[y0, x0] += w * (1 - fx) * (1 - fy)
        k[y0, x0 + 1] += w * fx * (1 - fy)
        k[y0 + 1, x0] += w * (1 - fx) * fy
        k[y0 + 1, x0 + 1] += w * fx * fy
    return k.astype(np.float32)


def _best_ball(img, x, y, ox, oy):
    """Strongest ball within 12 px inward/outward of an end, searched along the wire."""
    l = max(1e-6, math.hypot(ox - x, oy - y))
    ux, uy = (ox - x) / l, (oy - y) / l
    best = (-math.inf, x, y)
    for k in range(-12, 13, 3):
        b = ball_score(img, x + ux * k, y + uy * k)
        if b[0] > best[0]:
            best = b
    return best


def _refine(img, s, a):
    """Grow a candidate along its direction (inside the template) and trim it to where the wire response
    really is: the filter only sees the strong middle part of a faint wire."""
    for _ in range(3):
        l = _seg_len(s)
        ux, uy = (s[2] - s[0]) / l, (s[3] - s[1]) / l
        grow = max(10, 0.4 * l)

        def lim(v, n):
            return min(max(v, a.d + 1), n - a.d - 2)

        e = (lim(s[0] - ux * grow, img.w), lim(s[1] - uy * grow, img.h),
             lim(s[2] + ux * grow, img.w), lim(s[3] + uy * grow, img.h))
        r = inspect_wire(img, e[:2], e[2:], a)
        if r['gap1'] + r['gap2'] >= r['l'] - 4:
            return s
        el = _seg_len(e)
        ex, ey = (e[2] - e[0]) / el, (e[3] - e[1]) / el
        n = [e[0] + ex * r['gap1'], e[1] + ey * r['gap1'], e[2] - ex * r['gap2'], e[3] - ey * r['gap2']]

        def keep(ax, ay, bx, by):
            # keep an extension only where it is a wire on its own (not just faint texture)
            return math.hypot(bx - ax, by - ay) < 3 or inspect_wire(img, (ax, ay), (bx, by), a)['z_all'] >= a.z_seg

        grow1 = (n[0] - s[0]) * ux + (n[1] - s[1]) * uy < 0
        grow2 = (n[2] - s[2]) * ux + (n[3] - s[3]) * uy > 0
        if grow1 and not keep(n[0], n[1], s[0], s[1]):
            n[0], n[1] = s[0], s[1]
        if grow2 and not keep(s[2], s[3], n[2], n[3]):
            n[2], n[3] = s[2], s[3]
        n = tuple(n)
        if abs(_seg_len(n) - l) < 1:
            return n
        s = n
    return s


def auto_wires(tpl8, product):
    """Wires found in a unit template image (template coordinates), bond 1 = the end with the stronger ball."""
    a = product.params()
    f = tpl8.astype(np.float32)
    if not a.dark:
        f = 255 - f
    img = cv2.GaussianBlur(f, (0, 0), 0.7)

    length = max(9, a.along * 3)
    z_thr = 2.0   # loose: every candidate is checked again by inspect_wire below
    lowest = np.finfo(np.float32).min
    resp = np.full(img.shape, lowest, np.float32)
    # a wire is darker than BOTH sides and its two sides look alike; a frame edge (dark rim between bright
    # metal and dark background) is not symmetric, so |side+ - side-| is subtracted
    for deg in range(0, 180, 15):
        th = math.radians(deg)
        c, sp, sm, sp3, sm3 = (cv2.filter2D(img, cv2.CV_32F, _line_kernel(th, length, o))
                               for o in (0, a.d, -a.d, 3 * a.d, -3 * a.d))
        r = np.minimum(sp, sm) - c
        r = r - np.abs(sp - sm)
        r = r - 0.5 * np.abs(sp3 - sm3)
        resp = np.maximum(resp, r)
    margin = length // 2 + 3 * a.d + 1   # filter responses near the template border are not reliable
    H, W = resp.shape
    if W > 2 * margin and H > 2 * margin:
        inner = resp[margin:H - margin, margin:W - margin].copy()
        resp[:] = lowest
        resp[margin:H - margin, margin:W - margin] = inner
    vals = resp[resp > lowest].astype(np.float64)
    med = float(np.median(vals)) if vals.size else 0.0
    mad = 1.4826 * (float(np.median(np.abs(vals - med))) if vals.size else 0.0) + 1e-6
    z = (resp.astype(np.float32) - np.float32(med)) * np.float32(1 / mad)
    mask = (z > z_thr).astype(np.uint8) * 255

    min_len = max(12, 0.15 * min(tpl8.shape[1], tpl8.shape[0]))
    lines = cv2.HoughLinesP(mask, 1, math.pi / 180, int(max(10, min_len / 2)), minLineLength=min_len,
                            maxLineGap=max(3, a.along))
    segs = [tuple(float(v) for v in l) for l in lines.reshape(-1, 4)] if lines is not None else []
    wire_px = product.px(product.wire_diam_um)
    merged = [s for s in _merge(segs, max(3, 2 * wire_px), max(6, 2 * a.along)) if _seg_len(s) >= min_len]

    fimg = FImage(img)
    kept = []
    perp_tol = max(3, 2 * wire_px)
    for s in sorted((_refine(fimg, s0, a) for s0 in merged), key=_seg_len, reverse=True):
        if any(_same(k, s, perp_tol, -0.5 * _seg_len(s)) for k in kept):   # a piece of a wire already kept
            continue
        if inspect_wire(fimg, s[:2], s[2:], a)['z_all'] >= product.z_missing:
            kept.append(s)

    out = []
    cx, cy = tpl8.shape[1] / 2.0, tpl8.shape[0] / 2.0
    for s in kept:
        # bond 1 = the end with the stronger ball (the score the BOND1 check uses); the found end may stop
        # short of or run past the ball, so look a few px along the wire and snap to the ball
        e1 = _best_ball(fimg, s[0], s[1], s[2], s[3])
        e2 = _best_ball(fimg, s[2], s[3], s[0], s[1])
        d1 = (s[0] - cx) ** 2 + (s[1] - cy) ** 2
        d2 = (s[2] - cx) ** 2 + (s[3] - cy) ** 2
        if e2[0] > e1[0] + 1 or (abs(e2[0] - e1[0]) <= 1 and d2 < d1):
            w = Wire([e2[1], e2[2]], [s[0], s[1]])
        else:
            w = Wire([e1[1], e1[2]], [s[2], s[3]])
        w.p1 = [round(w.p1[0], 1), round(w.p1[1], 1)]
        w.p2 = [round(w.p2[0], 1), round(w.p2[1], 1)]
        out.append(w)
    out.sort(key=lambda w: (round((w.p1[1] + w.p2[1]) / 2 / 10), (w.p1[0] + w.p2[0]) / 2))
    return out[:64]


def calibrate_pitch(gray8, tpl8, match_thr, pitch_mm):
    """µm per pixel from the known horizontal unit pitch (mm): median spacing of side-by-side units."""
    units = find_units(gray8, tpl8, match_thr)
    th = tpl8.shape[0]
    dx = []
    for u in units:
        right = [v['box'][0] - u['box'][0] for v in units
                 if v['box'][0] > u['box'][0] and abs(v['box'][1] - u['box'][1]) < th / 2.0]
        if right:
            dx.append(min(right))
    return None if not dx or pitch_mm <= 0 else pitch_mm * 1000 / float(np.median(dx))
