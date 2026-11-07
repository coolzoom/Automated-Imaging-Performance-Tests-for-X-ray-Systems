"""A 3×3 grid of units (die + lead + two wires each), with optional defects (same picture as the C# tests)."""
import cv2
import numpy as np

from core.product import Product, Wire

UNIT_W, UNIT_H, PITCH_X, PITCH_Y, OX, OY = 120, 90, 150, 120, 20, 15
WIRES = [((30, 30), (92, 32)), ((30, 58), (92, 56))]


def _draw(layer, pts):
    if len(pts) > 1:
        cv2.polylines(layer, [np.array(pts, np.int32).reshape(-1, 1, 2)], False, 1.0, 2, cv2.LINE_AA, 2)


def _dark(img, layer, depth):
    layer = cv2.GaussianBlur(layer, (0, 0), 0.6)
    img -= layer * (depth / layer.max())


def _unit(img, x, y, u, missing, broken, sweep, sweep_px):
    """Draw unit u with its top-left corner at (x, y)."""
    cv2.rectangle(img, (x + 5, y + 5), (x + UNIT_W - 6, y + UNIT_H - 6), 185, -1)
    cv2.rectangle(img, (x + 15, y + 15), (x + 64, y + 74), 80, -1)
    cv2.rectangle(img, (x + 80, y + 20), (x + 104, y + 69), 90, -1)
    cv2.rectangle(img, (x + 68, y + 8), (x + 75, y + 13), 70, -1)   # marker: unit not symmetric, mirror != rotation
    for wi, (a, b) in enumerate(WIRES):
        if missing == (u, wi):
            continue
        p1, p2 = np.array([x + a[0], y + a[1]], float), np.array([x + b[0], y + b[1]], float)
        v = p2 - p1
        n = np.array([-v[1], v[0]]) / np.hypot(*v)
        bow = sweep_px if sweep == (u, wi) else 0
        layer, pts = np.zeros_like(img), []
        for i in range(201):
            t = i / 200
            if broken == (u, wi) and 0.4 < t < 0.6:
                _draw(layer, pts)
                pts = []
                continue
            p = p1 + v * t + n * (4 * t * (1 - t) * bow)
            pts.append((int(round(p[0] * 4)), int(round(p[1] * 4))))
        _draw(layer, pts)
        _dark(img, layer, 35)
        layer = np.zeros_like(img)
        # ball: ~1.5× wire width, darker than the wire
        cv2.circle(layer, (int(round(p1[0] * 4)), int(round(p1[1] * 4))), 7, 1.0, -1, cv2.LINE_AA, 2)
        _dark(img, layer, 30)


def _noisy(img, seed):
    img += np.random.default_rng(seed).normal(0, 5, img.shape).astype(np.float32)
    return np.clip(np.round(img), 0, 255).astype(np.uint8)


def make(missing=None, broken=None, sweep=None, sweep_px=6.0, shift=(0, 0), seed=0):
    img = np.full((PITCH_Y * 3 + 2 * OY + 20, PITCH_X * 3 + 2 * OX + 20), 110, np.float32)
    for u in range(9):
        _unit(img, OX + shift[0] + (u % 3) * PITCH_X, OY + shift[1] + (u // 3) * PITCH_Y, u, missing, broken, sweep,
              sweep_px)
    return _noisy(img, seed)


CELL = 200


def make_mixed(angles, missing=None, broken=None, sweep=None, sweep_px=6.0, seed=0):
    """3×3 units, unit u turned by angles[u] degrees (parts lying in different directions), each in a CELL² cell."""
    from core.align import rotation, warp
    img = np.full((3 * CELL, 3 * CELL), 110, np.float32)
    for u in range(9):
        cell = np.full((CELL, CELL), 110, np.float32)
        _unit(cell, (CELL - UNIT_W) // 2, (CELL - UNIT_H) // 2, u, missing, broken, sweep, sweep_px)
        cell = warp(np.clip(np.round(cell), 0, 255).astype(np.uint8), rotation(CELL / 2.0, CELL / 2.0, angles[u]),
                    CELL, CELL)
        r, c = divmod(u, 3)
        img[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = cell
    return _noisy(img, seed)


def make_product(gray):
    return Product(name='synthetic', wire_width_px=2.5, template=gray[OY:OY + UNIT_H, OX:OX + UNIT_W].copy(),
                   wires=[Wire(list(map(float, a)), list(map(float, b))) for a, b in WIRES])


def rotate(gray, deg):
    """The whole picture rotated by deg (counter-clockwise), canvas enlarged."""
    from core.align import rotate_image
    return rotate_image(gray, deg)[0]


def barrel(gray, k):
    """Radial lens distortion: k > 0 shrinks towards the corners (pincushion), k < 0 barrel."""
    h, w = gray.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    cx, cy = w / 2.0, h / 2.0
    f = 1 + k * ((xx - cx) ** 2 + (yy - cy) ** 2) / max(cx, cy) ** 2
    return cv2.remap(gray, (cx + (xx - cx) * f).astype(np.float32), (cy + (yy - cy) * f).astype(np.float32),
                     cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
