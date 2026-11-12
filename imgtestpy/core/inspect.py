"""Bond-wire inspection per unit: find units by template matching, then measure every product wire
(missing / broken / sweep / bond 1 / bond 2 / shift). Numerically the same algorithm as the C# Inspector."""
import math

import cv2
import numpy as np

from . import align
from .config import FROM_FLAG
from .imgio import read_gray

FLAG_ORDER = ('MISSING', 'BROKEN', 'BOND1', 'BOND2', 'SWEEP', 'SHIFT')


class FImage:
    """Float image with bilinear sampling (edge values repeated outside), like the C# FImage."""

    def __init__(self, data):
        self.data = np.asarray(data, np.float64)
        self.h, self.w = self.data.shape

    def sample(self, x, y):
        W, H = self.w, self.h
        x = np.clip(np.asarray(x, np.float64), 0, W - 1)
        y = np.clip(np.asarray(y, np.float64), 0, H - 1)
        x0 = np.minimum(x.astype(np.int64), max(W - 2, 0))
        y0 = np.minimum(y.astype(np.int64), max(H - 2, 0))
        x1, y1 = np.minimum(x0 + 1, W - 1), np.minimum(y0 + 1, H - 1)
        fx, fy = x - x0, y - y0
        d = self.data
        return (d[y0, x0] * (1 - fx) + d[y0, x1] * fx) * (1 - fy) + (d[y1, x0] * (1 - fx) + d[y1, x1] * fx) * fy


def find_units(gray8, tpl8, thr):
    """Every place the unit template matches (score >= thr), sorted row by row: [{index, box, match}]."""
    h, w = tpl8.shape[:2]
    if w > gray8.shape[1] or h > gray8.shape[0]:
        return []
    res = cv2.matchTemplate(gray8, tpl8, cv2.TM_CCOEFF_NORMED)
    d, units = max(4, min(w, h) // 2), []
    while True:
        _, mx, _, (cx, cy) = cv2.minMaxLoc(res)
        if mx < thr or math.isnan(mx):
            break
        units.append({'index': 0, 'box': [int(cx), int(cy), w, h], 'match': float(mx)})
        res[max(0, cy - d):cy + d + 1, max(0, cx - d):cx + d + 1] = -1
    units.sort(key=lambda u: (round(u['box'][1] / h), u['box'][0]))
    for i, u in enumerate(units):
        u['index'] = i
    return units


def unit_affine(unit):
    """2×3 map template -> image of a unit (its own affine, else the box translation)."""
    if unit.get('affine'):
        return np.array(unit['affine'], np.float64).reshape(2, 3)
    return np.array([[1.0, 0.0, unit['box'][0]], [0.0, 1.0, unit['box'][1]]], np.float64)


def wire_points(unit, wire):
    m = unit_affine(unit)
    return align.apply(m, *wire.p1), align.apply(m, *wire.p2)


def prepare(gray8, dark):
    """Gray (uint8) -> float, wire made dark, light blur: the image every wire is measured on."""
    f = gray8.astype(np.float32)
    if not dark:
        f = 255 - f
    return FImage(cv2.GaussianBlur(f, (0, 0), 0.7))


def _find(gray8, tpl, a):
    """Units of one template (any angle, or upright only), each with affine template -> image and angle."""
    if a.max_angle > 0:
        return align.find_units_rotated(gray8, tpl, a.match_thr, a.max_angle, a.align_search, a.local_align)
    units = find_units(gray8, tpl, a.match_thr)
    for u in units:
        m = align.align_unit(gray8, tpl, u['box'], a.align_search) if a.local_align else unit_affine(u)
        u['affine'] = [float(v) for v in m.ravel()]
        u['box'][0], u['box'][1] = round(m[0, 2]), round(m[1, 2])
        u['angle'] = 0.0
    return units


def _find_all(gray8, tpl, a):
    """Upright-or-rotated unit search plus the mirror side, on one template image."""
    units = _find(gray8, tpl, a)
    if a.allow_mirror:   # a part flipped over shows up mirrored in the X-ray picture
        units = align.merge_mirrored(units, _find(gray8, np.ascontiguousarray(tpl[:, ::-1]), a), tpl.shape[1], tpl.shape[0])
    return units


def _match_best(probe8, tpl8, s):
    """Best single match score of the template scaled by s (half-size probe on large images)."""
    t = cv2.resize(tpl8, (0, 0), fx=s, fy=s, interpolation=cv2.INTER_AREA)
    if t.shape[1] > probe8.shape[1] or t.shape[0] > probe8.shape[0] or min(t.shape[:2]) < 8:
        return -1
    _, mx, _, _ = cv2.minMaxLoc(cv2.matchTemplate(probe8, t, cv2.TM_CCOEFF_NORMED))
    return mx


def best_scale(gray8, tpl8, thr, smin, smax):
    """Picture scale relative to the template: scans [smin, smax] coarsely then ±0.04 finely, on a
    half-size image for speed. Returns the best scale once it reaches thr, else 1 (native size)."""
    f = 0.5 if gray8.shape[1] >= 1200 else 1.0
    probe = gray8 if f == 1 else align._resize(gray8, f)
    best_s, best_v = 1.0, -1.0

    def scan(a, b, step):
        nonlocal best_s, best_v
        s = a
        while s <= b + 1e-9:
            v = _match_best(probe, tpl8, s * f)
            if v > best_v:
                best_v, best_s = v, s
            s += step

    scan(smin, smax, max(0.02, (smax - smin) / 20))
    scan(max(smin, best_s - 0.04), min(smax, best_s + 0.04), 0.01)
    return best_s if best_v >= thr else 1.0


def _scale_px(a, s):
    """Pixel lengths scaled by s for a picture zoomed by s (z scores and % thresholds unchanged)."""
    from dataclasses import replace
    along = max(5, round(a.along * s))
    if along % 2 == 0:
        along += 1
    return replace(a, half_w=max(3, round(a.half_w * s)), bg_off=max(2, math.ceil(a.bg_off * s)),
                   bg_w=max(3, math.ceil(a.bg_w * s)), d=max(2, round(a.d * s)),
                   ext=max(4, math.ceil(a.ext * s)), along=along, end_tol=a.end_tol * s,
                   gap_max=max(3, a.gap_max * s), shift_max=a.shift_max * s,
                   align_search=max(2, round(a.align_search * s)))


def detect(gray8, product):
    if product.template is None:
        raise ValueError('产品没有模板图')
    a = product.params()
    tpl = product.template
    units = _find_all(gray8, tpl, a)
    rng = product.scale_range or [1.0, 1.0]
    smin, smax = (min(rng), max(rng)) if len(rng) == 2 else (1.0, 1.0)
    scale = 1.0
    if not units and smax > smin + 1e-9:   # camera zoom differs per picture: find the scale, then re-run on the scaled template
        s = best_scale(gray8, tpl, a.match_thr, smin, smax)
        if abs(s - 1) > 0.02:
            tplS = align._resize(tpl, s)
            found = _find_all(gray8, tplS, a)
            if found:
                sm = np.array([[s, 0.0, 0.0], [0.0, s, 0.0]])   # affines came out scaledTpl -> image: pre-compose origTpl -> scaledTpl
                for u in found:
                    u['affine'] = [float(v) for v in align.compose(unit_affine(u), sm).ravel()]
                    u['box'][2], u['box'][3] = tpl.shape[1], tpl.shape[0]   # box w/h in template space, not scaled-template space
                units, scale, a = found, s, _scale_px(a, s)
    res = {'width': int(gray8.shape[1]), 'height': int(gray8.shape[0]), 'product': product.name, 'scale': scale,
           'angle': round(align.mean_angle([u['angle'] for u in units], align.SAME_DIRECTION) * 10) / 10,
           'units': units, 'wires': []}
    img = prepare(gray8, a.dark)
    for u in units:
        m = unit_affine(u)
        for i, w in enumerate(product.wires):
            r = inspect_wire(img, align.apply(m, *w.p1), align.apply(m, *w.p2), a)
            r['unit'], r['index'] = u['index'], i
            res['wires'].append(r)
    res['defects'] = make_defects(res['wires'], a)
    return res


def detect_image(path, product):
    return detect(read_gray(path), product)


def make_defects(wires, a):
    from .defect import Defect
    out = []
    for w in wires:
        if not w['flags']:
            continue
        pts = np.array(list(w['curve']) + [w['p1'], w['p2']], np.float64)
        pad = a.half_w + 4
        main = next(f for f in FLAG_ORDER if f in w['flags'])
        out.append(Defect(math.floor(pts[:, 0].min() - pad), math.floor(pts[:, 1].min() - pad),
                          math.ceil(pts[:, 0].max() + pad), math.ceil(pts[:, 1].max() + pad),
                          FROM_FLAG[main], round(severity(w, main, a), 3),
                          by='单元%d·线%d' % (w['unit'] + 1, w['index'] + 1)).to_dict())
    return out


def severity(w, flag, a):
    if flag in ('MISSING', 'BROKEN'):
        ratio = 2
    elif flag == 'SWEEP':
        ratio = w['sweep_pct'] / max(a.sweep_max_pct, 1e-6)
    elif flag == 'SHIFT':
        ratio = w['shift_px'] / max(a.shift_max, 1e-6)
    elif flag == 'BOND1':
        ratio = max(a.z_ball / max(w['z_ball'], 0.1), w['gap1'] / max(a.end_tol, 1e-6))
    else:
        last = w['z_seg'][-1] if w['z_seg'] else 0
        ratio = max(a.z_seg / max(last, 0.1), w['gap2'] / max(a.end_tol, 1e-6))
    return min(max(0.5 + 0.5 * (ratio - 1), 0.5), 1.0)


def _reflect101(i, n):
    if n == 1:
        return np.zeros_like(i)
    i = np.abs(i)
    period = 2 * n - 2
    i = i % period
    return np.where(i >= n, period - i, i)


def _blur_rows(src, k):
    cols, h = src.shape[1], k // 2
    j = np.arange(cols)
    s = np.zeros_like(src)
    for m in range(-h, h + 1):
        s = s + src[:, _reflect101(j + m, cols)]
    return s / k


def polyval(c, x):
    return (c[0] * x + c[1]) * x + c[2]


def polyfit2(x, y, w):
    """Weighted least-squares quadratic (highest power first, like numpy.polyfit), solved like the C#
    version (normal equations, Gauss-Jordan with partial pivoting)."""
    rows = np.stack([x * x, x, np.ones_like(x)], 1)
    A = (rows * w[:, None]).T @ rows
    b = (rows * (w * y)[:, None]).sum(0)
    M = np.hstack([A, b[:, None]])
    for c in range(3):
        p = c + int(np.argmax(np.abs(M[c:, c])))
        if abs(M[p, c]) < 1e-12:
            return np.zeros(3)
        M[[c, p]] = M[[p, c]]
        for r in range(3):
            if r != c:
                M[r, c:] -= M[r, c] / M[c, c] * M[c, c:]
    return M[:, 3] / np.diag(M)


def _median(v):
    v = np.asarray(v, np.float64)
    return float(np.median(v)) if v.size else 0.0


def _wire_ridge(patch, d):
    """Dark-line response along axis 0. A slope is removed when the slope estimates at
    several distances agree. A lead edge makes them disagree, and the response then falls
    back to the plain both-sides difference, which stays quiet on a step."""
    ns, _ = patch.shape
    rad = d + 2
    ridge = np.zeros_like(patch)
    n = ns - 2 * rad
    if n <= 0:
        return ridge

    def at(k):
        return patch[rad + k:rad + k + n]

    slopes = np.stack([(at(k) - at(-k)) / (2.0 * k) for k in (d, d + 1, d + 2)], 0)
    spread = slopes.max(0) - slopes.min(0)
    b = np.median(slopes, 0)
    center = at(0)
    old = np.minimum(at(-d), at(d)) - center
    corrected = np.minimum(at(-d) + b * d, at(d) - b * d) - center
    # linear backgrounds stay under ~5 gray/px of disagreement; a step shoulder sits near 9
    w = np.clip((8.0 - spread) / 4.0, 0.0, 1.0)
    ridge[rad:ns - rad] = old + w * (corrected - old)
    return ridge


def inspect_wire(img, p1, p2, a):
    vx, vy = p2[0] - p1[0], p2[1] - p1[1]
    L = max(1.0, math.sqrt(vx * vx + vy * vy))
    ux, uy = vx / L, vy / L
    nx, ny = -uy, ux
    rad = a.d + 2   # room for the slope samples outside the both-sides pair
    W = a.half_w + a.bg_off + a.bg_w + rad
    nt = math.ceil(L + 2 * a.ext + 1 - 1e-9)
    t = -a.ext + np.arange(nt, dtype=np.float64)
    ns = 2 * W + 1
    s = (np.arange(ns) - W).astype(np.float64)[:, None]
    patch = img.sample(p1[0] + t * ux + s * nx, p1[1] + t * uy + s * ny)
    ridge = _wire_ridge(patch, a.d)
    R = _blur_rows(ridge, a.along)

    inside = (t >= 0) & (t <= L)
    c0 = W
    lo = c0 - a.half_w - a.bg_off
    hi = c0 + a.half_w + a.bg_off + 1
    bg = np.concatenate([R[lo - a.bg_w:lo][:, inside].ravel(), R[hi:hi + a.bg_w][:, inside].ravel()])
    mu = _median(bg)
    sd = 1.4826 * _median(np.abs(bg - mu)) + 1e-6

    core = R[c0 - a.half_w:c0 + a.half_w + 1]
    nc = core.shape[0]
    cols = np.arange(nt)
    iy = core.argmax(0)
    y0 = core[np.maximum(iy - 1, 0), cols]
    y1 = core[iy, cols]
    y2 = core[np.minimum(iy + 1, nc - 1), cols]
    den = y0 - 2 * y1 + y2
    dy = np.where(np.abs(den) > 1e-6, 0.5 * (y0 - y2) / np.where(np.abs(den) > 1e-6, den, 1), 0)
    yc = iy + np.clip(dy, -0.5, 0.5) - a.half_w

    coef = np.zeros(3)
    sel = inside & ((y1 - mu) / sd > a.sel_z)
    if sel.sum() >= 10:
        tt, yy, w = t[sel], yc[sel], np.ones(int(sel.sum()))
        for _ in range(6):
            coef = polyfit2(tt, yy, w)
            r = yy - polyval(coef, tt)
            sc = 2 * (1.4826 * _median(np.abs(r)) + 0.3)
            w = 1 / (1 + (r / sc) ** 2)

    row = np.clip(polyval(coef, t) + c0, 0, ns - 1)
    r0 = np.minimum(row.astype(np.int64), ns - 2)
    f = row - r0
    zc = (R[r0, cols] * (1 - f) + R[r0 + 1, cols] * f - mu) / sd

    def eff(m):
        return math.sqrt(max(m, 1) / a.along)

    zi = zc[inside]
    z_all = float(zi.mean() * eff(len(zi))) if len(zi) else 0.0
    z_seg = [float(sg.mean() * eff(len(sg))) if len(sg) else 0.0 for sg in np.array_split(zi, a.n_seg)]

    ti = t[inside]
    e0, eL = float(polyval(coef, 0.0)), float(polyval(coef, L))
    sweep = float(np.abs(polyval(coef, ti) - (e0 + (eL - e0) * ti / L)).max()) if len(ti) else 0.0
    shift = max(abs(e0), abs(eL))

    cnt = np.convolve((zc > a.on_z).astype(np.float64), np.ones(a.along), 'same')
    on = np.flatnonzero((cnt / a.along > 0.6) & inside)
    gap1, gap2 = (float(t[on[0]]), float(L - t[on[-1]])) if on.size else (L, L)
    # longest stretch without response between the two ends; zc is averaged over `along` px, which
    # shrinks a real gap by along-1, so that is added back
    gap_mid, run = 0.0, 0
    if on.size:
        for j in range(on[0], on[-1] + 1):
            run = run + 1 if zc[j] <= a.on_z else 0
            if run > 0:
                gap_mid = max(gap_mid, float(run + a.along - 1))

    zb, bx, by = ball_score(img, p1[0] + e0 * nx, p1[1] + e0 * ny)

    flags = []
    if z_all < a.z_missing:
        flags.append('MISSING')
    else:
        mid = z_seg[1:-1] if len(z_seg) > 2 else z_seg
        if min(mid) < a.z_seg or gap_mid > a.gap_max:
            flags.append('BROKEN')
        if zb < a.z_ball or gap1 > a.end_tol:
            flags.append('BOND1')
        if z_seg[-1] < a.z_seg or gap2 > a.end_tol:
            flags.append('BOND2')
        if sweep / L * 100 > a.sweep_max_pct:
            flags.append('SWEEP')
        if shift > a.shift_max:
            flags.append('SHIFT')

    off = polyval(coef, ti)
    curve = [[round(float(x), 2), round(float(y), 2)]
             for x, y in zip(p1[0] + ti * ux + off * nx, p1[1] + ti * uy + off * ny)]
    return {'unit': 0, 'index': 0, 'flags': flags, 'z_all': z_all, 'z_seg': z_seg, 'z_ball': zb, 'ball': [bx, by],
            'sweep_px': sweep, 'sweep_pct': sweep / L * 100, 'shift_px': shift, 'gap1': gap1, 'gap2': gap2,
            'gap_mid': gap_mid, 'l': L, 'p1': [float(p1[0]), float(p1[1])], 'p2': [float(p2[0]), float(p2[1])],
            'curve': curve}


_BALL_R = 5
_yy, _xx = np.mgrid[-_BALL_R:_BALL_R + 1, -_BALL_R:_BALL_R + 1]
_BALL_OFF = np.stack([_xx.ravel(), _yy.ravel()], 1).astype(np.float64)
_BALL_RR = np.sqrt(_BALL_OFF[:, 0] ** 2 + _BALL_OFF[:, 1] ** 2)


def ball_score(img, px, py, rin=1.5, rout0=3.0, rout1=5.0, search=3):
    """Dark-blob score near a bond point: (ring median - core mean) / ring MAD, best within ±search px."""
    d = np.arange(-search, search + 1, dtype=np.float64)
    cy, cx = np.meshgrid(py + d, px + d, indexing='ij')
    cx, cy = cx.ravel(), cy.ravel()
    v = img.sample(cx[:, None] + _BALL_OFF[None, :, 0], cy[:, None] + _BALL_OFF[None, :, 1])
    core = v[:, _BALL_RR <= rin].mean(1)
    ring = v[:, (_BALL_RR >= rout0) & (_BALL_RR <= rout1)]
    med = np.median(ring, 1)
    sc = (med - core) / (1.4826 * np.median(np.abs(ring - med[:, None]), 1) + 1e-6)
    k = int(np.argmax(sc))
    return float(sc[k]), float(cx[k]), float(cy[k])
