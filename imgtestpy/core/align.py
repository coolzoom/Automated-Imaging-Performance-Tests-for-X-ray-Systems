"""Geometry correction for rotated / distorted images (same steps as the C# Align class).

1. Units at any angle: the centre square of the unit template (side min(w, h)/√2, inside the template at every
   rotation) is matched on a half-size image at every `step`° over ±max_angle (up to the full circle); the
   per-pixel best score over all angles gives unit candidates (plus the upright template matches). Each
   candidate is cut out upright (own bilinear warp), its angle refined (0.5°, then 0.1°) with the full template,
   kept when the score reaches match_thr. Every unit gets its own angle: parts may lie in any direction.
2. Local distortion: each unit is split into 2×2 template blocks, every block is matched within ±search px
   (sub-pixel peak) in the upright cut-out, and an affine map is fitted to the block centres, so the wire end
   points follow the local rotation / scale / shear of that unit (perspective, lens distortion).
Result per unit: the affine template -> original image; wires are then measured on the original image."""
import math

import cv2
import numpy as np

MIN_ANGLE, MIN_GAIN = 0.2, 0.01         # a unit counts as upright unless rotated by more and matching clearly better
CAND_FACTOR, MAX_CANDIDATES = 0.75, 256  # coarse candidates: square-crop score >= CAND_FACTOR × match_thr
EARLY_FACTOR = 0.7                      # a candidate scoring below EARLY_FACTOR × match_thr at its coarse angle is dropped
SAME_DIRECTION = 0.9                    # units count as one direction when |mean unit vector| reaches this
BLOCK_MIN_SCORE, MAX_LINEAR_DEV, MAX_RESIDUAL = 0.5, 0.08, 1.5


def rotation(cx, cy, deg):
    """2×3 matrix rotating by deg (counter-clockwise on screen, like cv2.getRotationMatrix2D) about (cx, cy)."""
    t = math.radians(deg)
    a, b = math.cos(t), math.sin(t)
    return np.array([[a, b, (1 - a) * cx - b * cy], [-b, a, b * cx + (1 - a) * cy]], np.float64)


def invert(m):
    a, b, c, d = m[0, 0], m[0, 1], m[1, 0], m[1, 1]
    det = a * d - b * c
    ia, ib, ic, id_ = d / det, -b / det, -c / det, a / det
    return np.array([[ia, ib, -(ia * m[0, 2] + ib * m[1, 2])], [ic, id_, -(ic * m[0, 2] + id_ * m[1, 2])]], np.float64)


def compose(m2, m1):
    """m2 ∘ m1 (apply m1 first)."""
    return np.array([[m2[0, 0] * m1[0, 0] + m2[0, 1] * m1[1, 0], m2[0, 0] * m1[0, 1] + m2[0, 1] * m1[1, 1],
                      m2[0, 0] * m1[0, 2] + m2[0, 1] * m1[1, 2] + m2[0, 2]],
                     [m2[1, 0] * m1[0, 0] + m2[1, 1] * m1[1, 0], m2[1, 0] * m1[0, 1] + m2[1, 1] * m1[1, 1],
                      m2[1, 0] * m1[0, 2] + m2[1, 1] * m1[1, 2] + m2[1, 2]]], np.float64)


def apply(m, x, y):
    return m[0, 0] * x + m[0, 1] * y + m[0, 2], m[1, 0] * x + m[1, 1] * y + m[1, 2]


def warp(gray8, m, w, h):
    """Affine warp (m = source -> destination) with bilinear sampling and edge replication; done here instead
    of cv2.warpAffine so the C# version gives the very same pixels (OpenCV versions differ)."""
    return warp_back(gray8, invert(m), w, h)


def warp_back(gray8, mi, w, h):
    """Like warp, with mi = destination -> source; gray8 may already be an FImage."""
    from .inspect import FImage
    v, u = np.mgrid[0:h, 0:w].astype(np.float64)
    src = gray8 if isinstance(gray8, FImage) else FImage(gray8)
    out = src.sample(mi[0, 0] * u + mi[0, 1] * v + mi[0, 2], mi[1, 0] * u + mi[1, 1] * v + mi[1, 2])
    return np.clip(np.floor(out + 0.5), 0, 255).astype(np.uint8)


def rotate_image(gray8, deg):
    """Rotate the whole image by deg on an enlarged canvas (nothing cut off); returns (image, M original->new)."""
    h, w = gray8.shape
    t = math.radians(deg)
    nw = int(math.ceil(w * abs(math.cos(t)) + h * abs(math.sin(t)) - 1e-9))
    nh = int(math.ceil(w * abs(math.sin(t)) + h * abs(math.cos(t)) - 1e-9))
    m = rotation(w / 2.0, h / 2.0, deg)
    m[0, 2] += nw / 2.0 - w / 2.0
    m[1, 2] += nh / 2.0 - h / 2.0
    return warp(gray8, m, nw, nh), m


def _solve3(A, b):
    M = np.hstack([np.array(A, np.float64), np.array(b, np.float64)[:, None]])
    for c in range(3):
        p = c + int(np.argmax(np.abs(M[c:, c])))
        if abs(M[p, c]) < 1e-12:
            return None
        M[[c, p]] = M[[p, c]]
        for r in range(3):
            if r != c:
                M[r, c:] -= M[r, c] / M[c, c] * M[c, c:]
    return M[:, 3] / np.diag(M)


def _fit_affine(pts):
    """Weighted least squares dst = A·src + t over [(sx, sy, dx, dy, w)]; None if degenerate."""
    A = np.zeros((3, 3))
    bx, by = np.zeros(3), np.zeros(3)
    for sx, sy, dx, dy, w in pts:
        row = np.array([sx, sy, 1.0])
        A += w * np.outer(row, row)
        bx += w * dx * row
        by += w * dy * row
    ux, uy = _solve3(A, bx), _solve3(A, by)
    return None if ux is None or uy is None else np.array([ux, uy], np.float64)


def align_unit(gray8, tpl8, box, search):
    """Affine template -> image for one unit found at box (x, y, w, h); the pure translation when the
    blocks do not give a trustworthy fit."""
    x, y, tw, th = box
    base = np.array([[1.0, 0.0, x], [0.0, 1.0, y]], np.float64)
    bw, bh = tw // 2, th // 2
    if search <= 0 or bw < 8 or bh < 8:
        return base
    H, W = gray8.shape
    pts = []
    for i in range(2):
        for j in range(2):
            bx, by = j * bw, i * bh
            x0, y0 = x + bx - search, y + by - search
            if x0 < 0 or y0 < 0 or x0 + bw + 2 * search > W or y0 + bh + 2 * search > H:
                continue
            res = cv2.matchTemplate(gray8[y0:y0 + bh + 2 * search, x0:x0 + bw + 2 * search],
                                    tpl8[by:by + bh, bx:bx + bw], cv2.TM_CCOEFF_NORMED)
            _, mx, _, (lx, ly) = cv2.minMaxLoc(res)
            if math.isnan(mx) or mx < BLOCK_MIN_SCORE:
                continue

            def peak(a, b, c):
                den = a - 2 * b + c
                return min(max(0.5 * (a - c) / den, -0.5), 0.5) if abs(den) > 1e-9 else 0.0
            n = 2 * search
            dx = peak(*(float(v) for v in res[ly, lx - 1:lx + 2])) if 0 < lx < n else 0.0
            dy = peak(*(float(v) for v in res[ly - 1:ly + 2, lx])) if 0 < ly < n else 0.0
            sx, sy = bx + (bw - 1) / 2.0, by + (bh - 1) / 2.0
            pts.append((sx, sy, x + sx + lx + dx - search, y + sy + ly + dy - search, float(mx)))
    if len(pts) < 3:
        return base
    m = _fit_affine(pts)
    if m is not None and len(pts) >= 4:
        res = []
        for p in pts:
            qx, qy = apply(m, p[0], p[1])
            res.append(math.sqrt((qx - p[2]) ** 2 + (qy - p[3]) ** 2))
        k = int(np.argmax(res))
        if res[k] > MAX_RESIDUAL:
            m = _fit_affine(pts[:k] + pts[k + 1:])
    if m is None or max(abs(m[0, 0] - 1), abs(m[0, 1]), abs(m[1, 0]), abs(m[1, 1] - 1)) > MAX_LINEAR_DEV:
        return base
    cx, cy = apply(m, tw / 2.0, th / 2.0)
    if abs(cx - (x + tw / 2.0)) > search + 1 or abs(cy - (y + th / 2.0)) > search + 1:
        return base
    return m


def rotate_inscribed(gray8, deg):
    """Test aid: the image rotated by deg and cut to the largest centred upright rectangle with no border fill,
    like a photo of a rotated part."""
    r = rotate_image(gray8, deg)[0]
    h, w = gray8.shape
    c, s = abs(math.cos(math.radians(deg))), abs(math.sin(math.radians(deg)))
    cw, ch = (int(w * c - h * s), int(h * c - w * s)) if c >= s else (int(h * s - w * c), int(w * s - h * c))
    if cw < 16 or ch < 16:
        raise ValueError('旋转角度太大，裁剪后没有图像')
    y0, x0 = (r.shape[0] - ch) // 2, (r.shape[1] - cw) // 2
    return r[y0:y0 + ch, x0:x0 + cw].copy()


def _resize(img, f):
    h, w = img.shape
    return cv2.resize(img, (max(1, round(w * f)), max(1, round(h * f))), interpolation=cv2.INTER_AREA)


def _norm10(k):
    """Angle in tenths of a degree -> (-1800, 1800]."""
    while k <= -1800:
        k += 3600
    while k > 1800:
        k -= 3600
    return k


def patch_map(cx, cy, deg, pw, ph):
    """Patch (pw × ph, upright unit) -> image: the patch centre lands on (cx, cy), content rotated by deg."""
    t = math.radians(deg)
    a, b = math.cos(t), math.sin(t)
    pcx, pcy = pw / 2.0, ph / 2.0
    return np.array([[a, b, cx - a * pcx - b * pcy], [-b, a, cy + b * pcx - a * pcy]], np.float64)


def coarse_step(s):
    """Coarse angle step (whole degrees) for a centre square of side s: its edge turns by about 2 px per step."""
    return max(1, min(6, int(math.degrees(math.atan(2.0 / max(s / 2.0, 1.0))))))


def find_units_rotated(gray8, tpl8, match_thr, max_angle, search, local_align):
    """Units at any angle within ±max_angle: [{index, box, match, angle, affine}], affine = template -> image."""
    from .inspect import find_units
    th, tw = tpl8.shape
    H, W = gray8.shape
    if tw > W or th > H:
        return []
    # ---- 1. coarse: centre square at every step°, best score per pixel
    f = 0.5 if min(tw, th) >= 80 else 1.0
    small, tsm = (_resize(gray8, f), _resize(tpl8, f)) if f != 1.0 else (gray8, tpl8)
    sth, stw = tsm.shape
    s = max(8, int(math.floor(min(stw, sth) / math.sqrt(2))))
    s = min(s, stw, sth)
    cx, cy, h0 = stw // 2, sth // 2, s // 2
    x0, y0 = max(0, cx - h0), max(0, cy - h0)
    step = coarse_step(s)
    n = int(math.floor(min(max_angle, 180.0) / step + 1e-9))
    angles = sorted({_norm10(k * step * 10) for k in range(-n, n + 1)})
    best = None
    for k10 in angles:
        crop = warp(tsm, rotation(cx, cy, k10 / 10.0), stw, sth)[y0:y0 + s, x0:x0 + s]
        res = cv2.matchTemplate(small, crop, cv2.TM_CCOEFF_NORMED)
        res = np.where(np.isnan(res), -1, res).astype(np.float32)
        if best is None:
            best, ang = res.copy(), np.full(res.shape, k10, np.int32)
        else:
            up = res > best
            best[up], ang[up] = res[up], k10
    cands = []
    if best is not None:
        d = max(2, int(0.5 * min(stw, sth)))
        while len(cands) < MAX_CANDIDATES:
            _, mx, _, (px, py) = cv2.minMaxLoc(best)
            if mx < CAND_FACTOR * match_thr:
                break
            # the crop was cut from the rotated template: the template centre sits at (cx - x0, cy - y0) in it
            cands.append((((px + cx - x0) / f, (py + cy - y0) / f), int(ang[py, px])))
            best[max(0, py - d):py + d + 1, max(0, px - d):px + d + 1] = -1
    # upright matches, after the peaks: they only rescue units the coarse search missed
    for u in find_units(gray8, tpl8, match_thr):
        ux, uy = u['box'][0] + tw / 2.0, u['box'][1] + th / 2.0
        k0 = 0
        if best is not None:
            px = min(max(round(ux * f - cx + x0), 0), best.shape[1] - 1)
            py = min(max(round(uy * f - cy + y0), 0), best.shape[0] - 1)
            k0 = int(ang[py, px])
        cands.append(((ux, uy), k0))
    # ---- 2. refine each candidate upright, full template
    R = max(8, int(round(0.1 * min(tw, th))))
    pw, ph = tw + 2 * R, th + 2 * R
    half = int(math.ceil(step * 10 / 2.0 / 5))
    from .inspect import FImage
    fimg = FImage(gray8)
    dmin2 = (0.5 * min(tw, th)) ** 2
    found = []
    for (ux, uy), k0 in cands:
        if any((ux - q[5]) ** 2 + (uy - q[6]) ** 2 < dmin2 for q in found):
            continue   # a unit already found here
        cache = {}

        def pose(k10):
            if k10 not in cache:
                mp = patch_map(ux, uy, k10 / 10.0, pw, ph)
                patch = warp_back(fimg, mp, pw, ph)
                res = cv2.matchTemplate(patch, tpl8, cv2.TM_CCOEFF_NORMED)
                _, mx, _, loc = cv2.minMaxLoc(res)
                cache[k10] = (-1.0 if math.isnan(mx) else float(mx), loc, mp, patch)
            return cache[k10]

        def pick(ks):
            b = None
            for k in ks:
                if b is None or pose(k)[0] > pose(b)[0] or (pose(k)[0] == pose(b)[0] and abs(k) < abs(b)):
                    b = k
            return b
        if pose(_norm10(k0 + 1800))[0] > pose(k0)[0]:   # nearly symmetric units: the other way round fits better
            k0 = _norm10(k0 + 1800)
        if pose(k0)[0] < EARLY_FACTOR * match_thr:
            continue
        k = pick([k0 + j * 5 for j in range(-half, half + 1)])
        k = pick([k + j for j in range(-3, 4)])
        if abs(k) <= 20 and pose(0)[0] + MIN_GAIN >= pose(k)[0]:
            k = 0
        sc, loc, mp, patch = pose(k)
        if sc < match_thr:
            continue
        ccx, ccy = apply(mp, loc[0] + tw / 2.0, loc[1] + th / 2.0)
        found.append((sc, _norm10(k), loc, mp, patch, ccx, ccy))
    # ---- 3. one unit per place, local distortion, keep units fully inside the image
    found.sort(key=lambda q: -q[0])
    units, kept = [], []
    for sc, k, loc, mp, patch, ccx, ccy in found:
        if any((ccx - qx) ** 2 + (ccy - qy) ** 2 < dmin2 for qx, qy in kept):
            continue
        kept.append((ccx, ccy))
        lm = align_unit(patch, tpl8, [loc[0], loc[1], tw, th], search) if local_align else \
            np.array([[1.0, 0.0, loc[0]], [0.0, 1.0, loc[1]]], np.float64)
        m = compose(mp, lm)
        if not all(-1 <= qx <= W and -1 <= qy <= H for qx, qy in
                   (apply(m, px, py) for px, py in ((0, 0), (tw, 0), (0, th), (tw, th)))):
            continue
        units.append({'index': 0, 'box': [round(m[0, 2]), round(m[1, 2]), tw, th], 'match': sc, 'angle': k / 10.0,
                      'affine': [float(v) for v in m.ravel()]})
    return sort_units(units, tw, th)


def sort_units(units, tw, th):
    """Row by row, in the frame of the common unit direction (image frame when the units point every way)."""
    if units:
        phi = math.radians(mean_angle([u['angle'] for u in units], SAME_DIRECTION))
        a, b = math.cos(phi), math.sin(phi)

        def key(u):
            qx, qy = apply(np.array(u['affine']).reshape(2, 3), tw / 2.0, th / 2.0)
            return round((b * qx + a * qy) / th), a * qx - b * qy
        units.sort(key=key)
    for i, u in enumerate(units):
        u['index'] = i
    return units


def merge_mirrored(units, mirrored, tw, th):
    """Units found with the template and with its mirror image (strip flipped over): one side per picture, the
    mirrored one only when it matches clearly better where both sides found a unit (no common place: more
    units). Deciding per unit is not safe: a unit cut off by the image border, or a nearly symmetric frame, often
    fits the mirrored template too. Mirrored affines are made to take original template coordinates."""
    flip = np.array([[-1.0, 0.0, tw - 1.0], [0.0, 1.0, 0.0]], np.float64)
    for u in mirrored:
        m = compose(np.array(u['affine'], np.float64).reshape(2, 3), flip)
        u['affine'] = [float(v) for v in m.ravel()]
        u['box'][0], u['box'][1] = round(m[0, 2]), round(m[1, 2])
        u['mirror'] = True
    def centre(u):
        return apply(np.array(u['affine']).reshape(2, 3), tw / 2.0, th / 2.0)
    dmin2 = (0.5 * min(tw, th)) ** 2
    pairs = []   # places both sides found: (normal score, mirrored score)
    for u in units:
        ux, uy = centre(u)
        for v in mirrored:
            vx, vy = centre(v)
            if (ux - vx) ** 2 + (uy - vy) ** 2 < dmin2:
                pairs.append((u['match'], v['match']))
                break
    if pairs:
        use_mirror = sum(q[1] for q in pairs) / len(pairs) > sum(q[0] for q in pairs) / len(pairs) + MIN_GAIN
    else:
        use_mirror = len(mirrored) > len(units)
    if use_mirror:
        return sort_units(mirrored, tw, th)
    return units

def mean_angle(angles, min_resultant=0.0):
    """Circular mean (deg) of unit angles; 0 for none, or when the angles scatter (|mean vector| < min_resultant)."""
    if not angles:
        return 0.0
    sn = sum(math.sin(math.radians(v)) for v in angles)
    cs = sum(math.cos(math.radians(v)) for v in angles)
    if math.sqrt(sn * sn + cs * cs) < min_resultant * len(angles):
        return 0.0
    return math.degrees(math.atan2(sn, cs))
