"""Test aids: paint synthetic defects into a good image, and draw an annotated result image (CLI)."""
import cv2
import numpy as np

from .inspect import wire_points


def _pt(p):
    return int(round(p[0])), int(round(p[1]))


def simulate(gray8, product, units, miss_unit, broken_unit, sweep_unit, sweep_px=5.0, seed=0):
    """A missing wire, a broken wire and a swept wire on the given units (-1 = none)."""
    mask = np.zeros(gray8.shape, np.uint8)
    for unit, f0, f1 in ((miss_unit, 0.0, 1.0), (broken_unit, 0.35, 0.65), (sweep_unit, 0.0, 1.0)):
        if not 0 <= unit < len(units):
            continue
        for w in product.wires:
            p1, p2 = (np.array(p, np.float64) for p in wire_points(units[unit], w))
            v = p2 - p1
            a = p1 + v * f0 + (v * 0.06 if f0 > 0 else 0)
            cv2.line(mask, _pt(a), _pt(p1 + v * f1), 255, 9)
            if unit in (miss_unit, sweep_unit):
                cv2.circle(mask, _pt(p1), 3, 255, -1)
    inpainted = cv2.inpaint(gray8, mask, 4, cv2.INPAINT_TELEA)
    g = gray8.astype(np.float32)
    hp = g - cv2.GaussianBlur(g, (0, 0), 2)
    std = float(np.std(hp[mask == 0]))
    out = inpainted.astype(np.float32)
    noise = np.random.default_rng(seed).normal(0, std, g.shape).astype(np.float32)
    out = np.where(mask > 0, cv2.GaussianBlur(out, (0, 0), 2) + noise, out).astype(np.float32)
    if 0 <= sweep_unit < len(units):
        for w in product.wires:
            p1, p2 = (np.array(p, np.float64) for p in wire_points(units[sweep_unit], w))
            v = p2 - p1
            L = float(np.hypot(*v))
            n = np.array([-v[1], v[0]]) / L
            t = np.linspace(0, 1, int(L * 4))
            pts = p1 + np.outer(t, v) + np.outer(4 * t * (1 - t) * sweep_px, n)
            layer = np.zeros_like(out)
            cv2.polylines(layer, [np.round(pts * 4).astype(np.int32).reshape(-1, 1, 2)], False, 1.0, 2, cv2.LINE_AA, 2)
            layer = cv2.GaussianBlur(layer, (0, 0), 0.6)
            out -= layer * (35.0 / (layer.max() + 1e-6))
            cx, cy = _pt(p1)
            patch = out[max(cy - 3, 0):cy + 4, max(cx - 3, 0):cx + 4]
            cv2.circle(out, (cx, cy), 1, float(np.median(patch)) - 40, -1)
    return np.clip(np.round(out), 0, 255).astype(np.uint8)


def annotate(gray8, res, scale=2):
    vis = cv2.resize(cv2.cvtColor(gray8, cv2.COLOR_GRAY2BGR), None, fx=scale, fy=scale, interpolation=cv2.INTER_LINEAR)

    def S(x, y):
        return int(round(x * scale)), int(round(y * scale))

    ok, ng = (0, 200, 0), (0, 0, 255)
    for u in res['units']:
        wires = [w for w in res['wires'] if w['unit'] == u['index']]
        bad = [w for w in wires if w['flags']]
        col = ng if bad else ok
        x, y, bw, bh = u['box']
        cv2.rectangle(vis, S(x, y), S(x + bw, y + bh), col, 2)
        for w in wires:
            if 'MISSING' in w['flags']:
                cv2.line(vis, S(*w['p1']), S(*w['p2']), ng, 1)
            elif len(w['curve']) > 1:
                pts = np.array([S(*p) for p in w['curve']], np.int32).reshape(-1, 1, 2)
                cv2.polylines(vis, [pts], False, ng if w['flags'] else (0, 255, 255), 1, cv2.LINE_AA)
            cv2.circle(vis, S(*w['ball']), 4 * scale, (255, 120, 0), 1)
            cv2.circle(vis, S(*w['p2']), 4 * scale, (255, 0, 255), 1)
        flags = list(dict.fromkeys(f for w in bad for f in w['flags']))
        label = '#%d %s' % (u['index'], '/'.join(flags) if bad else 'OK')
        for th, c in ((4, (0, 0, 0)), (2, col)):
            cv2.putText(vis, label, S(x + 4, y + 16), cv2.FONT_HERSHEY_SIMPLEX, 0.8, c, th)
        if wires:
            w0 = min(wires, key=lambda w: w['z_all'])
            info = 'z=%.1f ball=%.1f sw=%.1fpx' % (w0['z_all'], w0['z_ball'], w0['sweep_px'])
            for th, c in ((3, (0, 0, 0)), (1, col)):
                cv2.putText(vis, info, S(x + 4, y + bh - 6), cv2.FONT_HERSHEY_SIMPLEX, 0.55, c, th)
    return vis
