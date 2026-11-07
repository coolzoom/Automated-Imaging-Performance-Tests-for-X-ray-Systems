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
    """Parameters of one inspection derived from a product's settings (all lengths in pixels)."""
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
    max_angle: float
    local_align: bool
    align_search: int
    allow_mirror: bool


_FIELDS = ('name', 'tpl_size', 'match_thr', 'polarity', 'wire_width_px', 'corridor_px', 'end_tol_px', 'gap_max_px',
           'shift_max_px', 'sweep_max_pct', 'z_missing', 'z_seg', 'z_ball', 'n_seg', 'max_angle_deg', 'local_align',
           'align_search_px', 'allow_mirror', 'scale_range', 'note')
# product.json written before lengths were in pixels: <name>_um / pixel_um
_LEGACY_UM = {'wire_diam_um': 'wire_width_px', 'corridor_um': 'corridor_px', 'end_tol_um': 'end_tol_px',
              'gap_max_um': 'gap_max_px', 'shift_max_um': 'shift_max_px', 'align_search_um': 'align_search_px'}


@dataclass
class Product:
    """Lengths are in pixels of the inspected pictures (the image scale differs between machines, so no µm)."""
    name: str = ''
    tpl_size: list = field(default_factory=lambda: [0, 0])
    wires: list = field(default_factory=list)
    match_thr: float = 0.6
    polarity: str = 'dark'
    wire_width_px: float = 2.5      # wire width on the picture
    corridor_px: float = 7          # half width of the search corridor around the expected wire
    end_tol_px: float = 10          # allowed gap at either end before BOND1 / BOND2
    gap_max_px: float = 8           # longest interior gap before BROKEN
    shift_max_px: float = 3.5       # largest end point offset before SHIFT
    sweep_max_pct: float = 6.0
    z_missing: float = 6.0
    z_seg: float = 2.5
    z_ball: float = 2.5
    n_seg: int = 5
    max_angle_deg: float = 180.0    # units may lie rotated up to ± this (180 = any direction, 0 = upright only)
    local_align: bool = True        # per-unit affine correction for distortion
    align_search_px: float = 5.0    # search range of the per-unit block matching
    allow_mirror: bool = True       # also find mirror-image units (part flipped over)
    scale_range: list = field(default_factory=lambda: [0.9, 1.4])   # picture/template zoom to try when nothing matches; [1,1] = off
    note: str = ''
    dir: str = ''
    template: object = None   # uint8 numpy array

    def params(self):
        wpx = max(0.5, self.wire_width_px)
        along = max(5, round(wpx * 2.8))
        if along % 2 == 0:
            along += 1
        return PixelParams(
            half_w=max(3, round(self.corridor_px)), bg_off=max(2, math.ceil(wpx)),
            bg_w=max(3, math.ceil(wpx * 1.5)), d=max(2, round(wpx * 0.8)), ext=max(4, math.ceil(wpx * 2.4)),
            along=along, n_seg=max(3, int(self.n_seg)), sel_z=2.5, on_z=1.5,
            z_missing=self.z_missing, z_seg=self.z_seg, z_ball=self.z_ball,
            end_tol=self.end_tol_px, gap_max=max(3, self.gap_max_px),
            sweep_max_pct=self.sweep_max_pct, shift_max=self.shift_max_px,
            match_thr=self.match_thr, dark=self.polarity != 'bright', max_angle=max(0.0, self.max_angle_deg),
            local_align=bool(self.local_align), align_search=max(2, round(self.align_search_px)),
            allow_mirror=bool(self.allow_mirror))

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
        um = d.get('pixel_um') or 10.0
        for old, new in _LEGACY_UM.items():
            if new not in d and old in d:
                setattr(p, new, d[old] / um)
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
