"""One product: a unit template image (tpl.png) plus its wires and thresholds (product.json).
Same files as the C# version: products/<name>/tpl.png + product.json, coordinates relative to tpl.png."""
import math
import os
from dataclasses import dataclass, field

from .config import PRODUCTS_DIR, load_json, save_json
from .imgio import read_gray, write_png

TEMPLATE_FILE, CONFIG_FILE = 'tpl.png', 'product.json'


@dataclass
class Wire:
    p1: list            # bond 1 (chip side), template coordinates
    p2: list            # bond 2 (lead side)

    @property
    def length(self):
        return math.hypot(self.p2[0] - self.p1[0], self.p2[1] - self.p1[1])

    def reversed(self):
        return Wire(list(self.p2), list(self.p1))


@dataclass(frozen=True)
class PixelParams:
    """Pixel-unit parameters of one inspection, derived from a product's µm settings."""
    half_w: int
    bg_off: int
    bg_w: int
    d: int
    ext: int
    along: int
    n_seg: int
    sel_z: float
    on_z: float
    z_missing: float
    z_seg: float
    z_ball: float
    end_tol: float
    gap_max: float
    sweep_max_pct: float
    shift_max: float
    match_thr: float
    dark: bool


_FIELDS = ('name', 'pixel_um', 'tpl_size', 'match_thr', 'polarity', 'wire_diam_um', 'corridor_um', 'end_tol_um',
           'gap_max_um', 'shift_max_um', 'sweep_max_pct', 'z_missing', 'z_seg', 'z_ball', 'n_seg', 'note')


@dataclass
class Product:
    name: str = ''
    pixel_um: float = 10.0
    tpl_size: list = field(default_factory=lambda: [0, 0])
    wires: list = field(default_factory=list)
    match_thr: float = 0.6
    polarity: str = 'dark'
    wire_diam_um: float = 25
    corridor_um: float = 70
    end_tol_um: float = 100
    gap_max_um: float = 80
    shift_max_um: float = 35
    sweep_max_pct: float = 6.0
    z_missing: float = 6.0
    z_seg: float = 2.5
    z_ball: float = 2.5
    n_seg: int = 5
    note: str = ''
    dir: str = ''
    template: object = None   # uint8 numpy array

    def px(self, um):
        return um / self.pixel_um

    def um(self, px):
        return px * self.pixel_um

    def params(self):
        wpx = max(0.5, self.wire_diam_um / self.pixel_um)
        along = max(5, round(wpx * 2.8))
        if along % 2 == 0:
            along += 1
        return PixelParams(
            half_w=max(3, round(self.px(self.corridor_um))), bg_off=max(2, math.ceil(wpx)),
            bg_w=max(3, math.ceil(wpx * 1.5)), d=max(2, round(wpx * 0.8)), ext=max(4, math.ceil(wpx * 2.4)),
            along=along, n_seg=max(3, int(self.n_seg)), sel_z=2.5, on_z=1.5,
            z_missing=self.z_missing, z_seg=self.z_seg, z_ball=self.z_ball,
            end_tol=self.px(self.end_tol_um), gap_max=max(3, self.px(self.gap_max_um)),
            sweep_max_pct=self.sweep_max_pct, shift_max=self.px(self.shift_max_um),
            match_thr=self.match_thr, dark=self.polarity != 'bright')

    def clone(self):
        p = Product(**{k: getattr(self, k) for k in _FIELDS}, dir=self.dir)
        p.tpl_size = list(self.tpl_size)
        p.wires = [Wire(list(w.p1), list(w.p2)) for w in self.wires]
        p.template = None if self.template is None else self.template.copy()
        return p

    def to_dict(self):
        d = {k: getattr(self, k) for k in _FIELDS}
        d['wires'] = [{'p1': list(w.p1), 'p2': list(w.p2)} for w in self.wires]
        return d

    @staticmethod
    def from_dict(d):
        p = Product(**{k: d[k] for k in _FIELDS if k in d})
        p.wires = [Wire([float(v) for v in w['p1']], [float(v) for v in w['p2']]) for w in d.get('wires') or []]
        return p

    @staticmethod
    def load(path):
        p = Product.from_dict(load_json(os.path.join(path, CONFIG_FILE), {}))
        p.dir = path
        if not p.name:
            p.name = os.path.basename(path)
        tpl = os.path.join(path, TEMPLATE_FILE)
        if os.path.isfile(tpl):
            p.template = read_gray(tpl)
        return p

    @staticmethod
    def load_named(name, root=None):
        return Product.load(os.path.join(root or PRODUCTS_DIR, name))

    def save(self, root=None):
        if self.template is None or self.template.size == 0:
            raise ValueError('产品没有模板图')
        self.dir = os.path.join(root or PRODUCTS_DIR, safe_name(self.name))
        os.makedirs(self.dir, exist_ok=True)
        self.tpl_size = [int(self.template.shape[1]), int(self.template.shape[0])]
        write_png(os.path.join(self.dir, TEMPLATE_FILE), self.template)
        save_json(os.path.join(self.dir, CONFIG_FILE), self.to_dict())


def safe_name(name):
    s = ''.join('_' if c in '/\\:\0' else c for c in name.strip())
    return s or 'product'


def list_products(root=None):
    root = root or PRODUCTS_DIR
    if not os.path.isdir(root):
        return []
    return sorted(n for n in os.listdir(root)
                  if os.path.isfile(os.path.join(root, n, CONFIG_FILE)) and os.path.isfile(os.path.join(root, n, TEMPLATE_FILE)))
