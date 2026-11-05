#!/usr/bin/env python3
"""Bond-wire X-ray inspection demo: missing / broken wire, sweep, bond-point check per unit."""
import argparse, csv, json, os, sys
import cv2
import numpy as np
from scipy.ndimage import map_coordinates

DEFAULT_CFG = {"tpl": [300, 140, 220, 150], "p1": [379.0, 209.6], "p2": [441.0, 211.0]}


def load_gray(path):
    img = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if img is None:
        sys.exit(f"cannot read {path}")
    if img.ndim == 3:
        img = cv2.cvtColor(img[..., :3], cv2.COLOR_BGR2GRAY)
    return img.astype(np.float32)


def find_units(gray, tpl_box, thr):
    x, y, w, h = tpl_box
    res = cv2.matchTemplate(gray, gray[y:y + h, x:x + w], cv2.TM_CCOEFF_NORMED)
    d, units = max(4, min(w, h) // 2), []
    while True:
        _, mx, _, (cx, cy) = cv2.minMaxLoc(res)
        if mx < thr:
            break
        units.append((cx, cy, float(mx)))
        res[max(0, cy - d):cy + d + 1, max(0, cx - d):cx + d + 1] = -1
    return sorted(units, key=lambda u: (round(u[1] / h), u[0]))


def unit_points(u, cfg):
    off = np.array([u[0] - cfg["tpl"][0], u[1] - cfg["tpl"][1]], np.float32)
    return np.array(cfg["p1"], np.float32) + off, np.array(cfg["p2"], np.float32) + off


def simulate(gray, units, cfg, miss_id, broken_id, sweep_id, sweep_px):
    g, mask = np.clip(gray, 0, 255).astype(np.uint8), np.zeros(gray.shape, np.uint8)
    jobs = [(miss_id, 0.0, 1.0), (broken_id, 0.35, 0.65), (sweep_id, 0.0, 1.0)]
    for idx, f0, f1 in jobs:
        if 0 <= idx < len(units):
            p1, p2 = unit_points(units[idx], cfg)
            a, b = p1 + (p2 - p1) * f0, p1 + (p2 - p1) * f1
            a, b = a + (p2 - p1) * 0.06 * (f0 > 0), b - (p2 - p1) * 0.0
            cv2.line(mask, tuple(np.round(a).astype(int)), tuple(np.round(b).astype(int)), 255, 9)
            if idx == miss_id or idx == sweep_id:
                cv2.circle(mask, tuple(np.round(p1).astype(int)), 3, 255, -1)
    out = cv2.inpaint(g, mask, 4, cv2.INPAINT_TELEA).astype(np.float32)
    hp = g.astype(np.float32) - cv2.GaussianBlur(g.astype(np.float32), (0, 0), 2)
    noise = np.random.default_rng(0).normal(0, float(np.std(hp[mask == 0])), g.shape)
    out = np.where(mask > 0, cv2.GaussianBlur(out, (0, 0), 2) + noise, out).astype(np.float32)
    if 0 <= sweep_id < len(units):
        p1, p2 = unit_points(units[sweep_id], cfg)
        v = p2 - p1
        L = np.hypot(*v)
        n = np.array([-v[1], v[0]]) / L
        t = np.linspace(0, 1, int(L * 4))
        pts = p1 + np.outer(t, v) + np.outer(4 * t * (1 - t) * sweep_px, n)
        layer = np.zeros_like(out)
        cv2.polylines(layer, [np.round(pts * 4).astype(np.int32).reshape(-1, 1, 2)], False, 1.0, 2,
                      cv2.LINE_AA, shift=2)
        layer = cv2.GaussianBlur(layer, (0, 0), 0.6)
        out -= layer / (layer.max() + 1e-6) * 35
        cv2.circle(out, tuple(np.round(p1).astype(int)), 1, float(np.median(out[int(p1[1]) - 3:int(p1[1]) + 4, int(p1[0]) - 3:int(p1[0]) + 4]) - 40), -1)
    return out


def sample(img, p1, u, n, t, s):
    T, S = np.meshgrid(t, s)
    return map_coordinates(img, [p1[1] + T * u[1] + S * n[1], p1[0] + T * u[0] + S * n[0]],
                           order=1, mode="nearest").astype(np.float32)


def ridge(patch, d):
    up, dn = np.roll(patch, d, 0), np.roll(patch, -d, 0)
    r = np.minimum(up, dn) - patch
    r[:d], r[-d:] = 0, 0
    return r


def ball_score(img, p, rin=1.5, rout=(3, 5), search=3):
    best = (-1e9, p)
    for dy in range(-search, search + 1):
        for dx in range(-search, search + 1):
            c = (p[0] + dx, p[1] + dy)
            yy, xx = np.mgrid[-rout[1]:rout[1] + 1, -rout[1]:rout[1] + 1]
            rr = np.hypot(xx, yy)
            v = map_coordinates(img, [c[1] + yy, c[0] + xx], order=1, mode="nearest")
            ring = v[(rr >= rout[0]) & (rr <= rout[1])]
            sc = (np.median(ring) - v[rr <= rin].mean()) / (1.4826 * np.median(np.abs(ring - np.median(ring))) + 1e-6)
            if sc > best[0]:
                best = (float(sc), c)
    return best


def inspect(img, p1, p2, a):
    v = p2 - p1
    L = float(np.hypot(*v))
    u = v / L
    n = np.array([-u[1], u[0]], np.float32)
    W = a.half_w + a.bg_off + a.bg_w + a.d
    t = np.arange(-a.ext, L + a.ext + 1, 1.0, dtype=np.float32)
    s = np.arange(-W, W + 1, 1.0, dtype=np.float32)
    R = cv2.blur(ridge(sample(img, p1, u, n, t, s), a.d), (a.along, 1))
    inside = (t >= 0) & (t <= L)
    c0 = W
    core = R[c0 - a.half_w:c0 + a.half_w + 1]
    bg = np.vstack([R[c0 - a.half_w - a.bg_off - a.bg_w:c0 - a.half_w - a.bg_off],
                    R[c0 + a.half_w + a.bg_off + 1:c0 + a.half_w + a.bg_off + a.bg_w + 1]])[:, inside]
    mu = np.median(bg)
    sd = 1.4826 * np.median(np.abs(bg - mu)) + 1e-6

    cols = np.arange(core.shape[1])
    iy = core.argmax(0)
    y0 = core[np.clip(iy - 1, 0, None), cols]
    y1 = core[iy, cols]
    y2 = core[np.clip(iy + 1, None, core.shape[0] - 1), cols]
    den = y0 - 2 * y1 + y2
    dy = np.where(np.abs(den) > 1e-6, 0.5 * (y0 - y2) / np.where(den == 0, 1, den), 0)
    yc = iy + np.clip(dy, -0.5, 0.5) - a.half_w

    sel = inside & ((y1 - mu) / sd > a.sel_z)
    coef = np.zeros(3)
    if sel.sum() >= 10:
        tt, yy, w = t[sel], yc[sel], np.ones(sel.sum())
        for _ in range(6):
            coef = np.polyfit(tt, yy, 2, w=np.sqrt(w))
            r = yy - np.polyval(coef, tt)
            w = 1 / (1 + (r / (2 * (1.4826 * np.median(np.abs(r)) + 0.3))) ** 2)
    curve = np.polyval(coef, t)
    resp = map_coordinates(R, [np.clip(curve + c0, 0, R.shape[0] - 1), np.arange(len(t), dtype=np.float32)], order=1)
    zc = (resp - mu) / sd
    eff = lambda m: np.sqrt(max(m, 1) / a.along)
    zi = zc[inside]
    z_all = float(zi.mean() * eff(len(zi)))
    z_seg = [float(sg.mean() * eff(len(sg))) for sg in np.array_split(zi, a.nseg)]

    ti = t[inside]
    e0, eL = np.polyval(coef, 0), np.polyval(coef, L)
    dev = np.polyval(coef, ti) - (e0 + (eL - e0) * ti / L)
    sweep_px = float(np.abs(dev).max())
    shift_px = float(max(abs(e0), abs(eL)))

    on = np.convolve((zc > a.on_z).astype(float), np.ones(a.along) / a.along, "same") > 0.6
    idx = np.flatnonzero(on & inside)
    gap1, gap2 = (float(t[idx[0]]), float(L - t[idx[-1]])) if idx.size else (L, L)

    zb, bpos = ball_score(img, (float(p1[0] + e0 * n[0]), float(p1[1] + e0 * n[1])))

    flags = []
    if z_all < a.z_missing:
        flags.append("MISSING")
    else:
        if min(z_seg[1:-1] if a.nseg > 2 else z_seg) < a.z_seg:
            flags.append("BROKEN")
        if zb < a.z_ball or gap1 > a.end_tol:
            flags.append("BOND1")
        if z_seg[-1] < a.z_seg or gap2 > a.end_tol:
            flags.append("BOND2")
        if sweep_px / L * 100 > a.sweep_max:
            flags.append("SWEEP")
        if shift_px > a.shift_max:
            flags.append("SHIFT")

    xy = np.stack([p1[0] + ti * u[0] + np.polyval(coef, ti) * n[0],
                   p1[1] + ti * u[1] + np.polyval(coef, ti) * n[1]], 1)
    return dict(flags=flags, z_all=z_all, z_seg=z_seg, z_ball=zb, ball=bpos, sweep_px=sweep_px,
                sweep_pct=sweep_px / L * 100, shift_px=shift_px, gap1=gap1, gap2=gap2, L=L,
                curve_xy=xy, R=R)


def draw(gray, results, scale, path):
    g8 = np.clip(gray, 0, 255).astype(np.uint8)
    vis = cv2.resize(cv2.cvtColor(g8, cv2.COLOR_GRAY2BGR), None, fx=scale, fy=scale,
                     interpolation=cv2.INTER_LINEAR)
    S = lambda p: tuple(int(round(c * scale)) for c in p)
    for r in results:
        ok = not r["flags"]
        col = (0, 200, 0) if ok else (0, 0, 255)
        x, y, w, h = r["box"]
        cv2.rectangle(vis, S((x, y)), S((x + w, y + h)), col, 2)
        if "MISSING" in r["flags"]:
            cv2.line(vis, S(r["p1"]), S(r["p2"]), (0, 0, 255), 1)
        else:
            cv2.polylines(vis, [np.round(r["curve_xy"] * scale).astype(np.int32).reshape(-1, 1, 2)],
                          False, (0, 255, 255), 1, cv2.LINE_AA)
        cv2.circle(vis, S(r["ball"]), 4 * scale, (255, 120, 0) if r["z_ball"] >= 0 else (0, 0, 255), 1)
        cv2.circle(vis, S(r["p2"]), 4 * scale, (255, 0, 255), 1)
        lab = f'#{r["id"]} ' + ("OK" if ok else "/".join(r["flags"]))
        org = S((x + 4, y + 16))
        cv2.putText(vis, lab, org, cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 0), 4)
        cv2.putText(vis, lab, org, cv2.FONT_HERSHEY_SIMPLEX, 0.8, col, 2)
        info = f'z={r["z_all"]:.1f} ball={r["z_ball"]:.1f} sw={r["sweep_px"]:.1f}px'
        org = S((x + 4, y + h - 6))
        cv2.putText(vis, info, org, cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 3)
        cv2.putText(vis, info, org, cv2.FONT_HERSHEY_SIMPLEX, 0.55, col, 1)
    cv2.imwrite(path, vis)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("image")
    ap.add_argument("--config", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "wire_cfg.json"))
    ap.add_argument("--match-thr", type=float, default=0.6)
    ap.add_argument("--d", type=int, default=2)
    ap.add_argument("--half-w", type=int, default=7)
    ap.add_argument("--bg-off", type=int, default=3)
    ap.add_argument("--bg-w", type=int, default=4)
    ap.add_argument("--ext", type=int, default=6)
    ap.add_argument("--along", type=int, default=7)
    ap.add_argument("--nseg", type=int, default=5)
    ap.add_argument("--sel-z", type=float, default=2.5)
    ap.add_argument("--on-z", type=float, default=1.5)
    ap.add_argument("--z-missing", type=float, default=6.0)
    ap.add_argument("--z-seg", type=float, default=2.5)
    ap.add_argument("--z-ball", type=float, default=2.5)
    ap.add_argument("--end-tol", type=float, default=10.0)
    ap.add_argument("--sweep-max", type=float, default=6.0)
    ap.add_argument("--shift-max", type=float, default=3.5)
    ap.add_argument("--simulate", nargs=3, type=int, metavar=("MISS_ID", "BROKEN_ID", "SWEEP_ID"))
    ap.add_argument("--sim-sweep-px", type=float, default=5.0)
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    a = ap.parse_args()

    if not os.path.exists(a.config):
        json.dump(DEFAULT_CFG, open(a.config, "w"), indent=2)
    cfg = json.load(open(a.config))

    gray = load_gray(a.image)
    units = find_units(gray, cfg["tpl"], a.match_thr)
    print(f"units found: {len(units)}")
    stem = os.path.splitext(os.path.basename(a.image))[0]
    os.makedirs(a.out, exist_ok=True)
    if a.simulate:
        gray = simulate(gray, units, cfg, *a.simulate, a.sim_sweep_px)
        stem += "_sim"
        cv2.imwrite(os.path.join(a.out, f"{stem}.png"), np.clip(gray, 0, 255).astype(np.uint8))

    img = cv2.GaussianBlur(gray, (0, 0), 0.7)
    _, _, w, h = cfg["tpl"]
    results = []
    for i, uu in enumerate(units):
        p1, p2 = unit_points(uu, cfg)
        r = inspect(img, p1, p2, a)
        r.update(id=i, box=(uu[0], uu[1], w, h), p1=tuple(p1), p2=tuple(p2), match=uu[2])
        results.append(r)

    draw(gray, results, a.scale, os.path.join(a.out, f"{stem}_annot.png"))
    strips = [cv2.resize(cv2.normalize(r["R"], None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8), None, fx=4, fy=4,
                         interpolation=cv2.INTER_NEAREST) for r in results]
    if strips:
        mw = max(s.shape[1] for s in strips)
        cv2.imwrite(os.path.join(a.out, f"{stem}_ridge.png"),
                    np.vstack([np.pad(s, ((0, 4), (0, mw - s.shape[1])), constant_values=128) for s in strips]))
    with open(os.path.join(a.out, f"{stem}_result.csv"), "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["id", "x", "y", "match", "result", "z_all", "z_seg", "z_ball", "sweep_px", "sweep_pct",
                     "shift_px", "gap1", "gap2", "L"])
        for r in results:
            row = [r["id"], r["box"][0], r["box"][1], f'{r["match"]:.2f}', "|".join(r["flags"]) or "OK",
                   f'{r["z_all"]:.1f}', " ".join(f"{z:.1f}" for z in r["z_seg"]), f'{r["z_ball"]:.1f}',
                   f'{r["sweep_px"]:.2f}', f'{r["sweep_pct"]:.1f}', f'{r["shift_px"]:.2f}',
                   f'{r["gap1"]:.0f}', f'{r["gap2"]:.0f}', f'{r["L"]:.0f}']
            wr.writerow(row)
            print("  ".join(map(str, row)))
    print(f"NG: {sum(bool(r['flags']) for r in results)}/{len(results)}  -> {a.out}/{stem}_annot.png")


if __name__ == "__main__":
    main()
