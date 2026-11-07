"""Paths, program settings (imgtest/config.json, shared with the C# version) and defect types."""
import json
import os
import sys

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _find_root():
    """imgtest/ (the folder holding products/ and config.json): WIREINSPECT_HOME, else the first ancestor
    of this program that has a products/ folder, else the program folder."""
    env = os.environ.get('WIREINSPECT_HOME')
    if env:
        return os.path.abspath(env)
    d = APP_DIR
    while True:
        if os.path.isdir(os.path.join(d, 'products')):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            return APP_DIR
        d = parent


ROOT = _find_root()
PRODUCTS_DIR = os.path.join(ROOT, 'products')
CONFIG_PATH = os.path.join(ROOT, 'config.json')

MISSING, BROKEN, SWEEP, BOND1, BOND2, SHIFT, OTHER = '缺线', '断线', '线弧异常', '第一焊点异常', '第二焊点异常', '焊点偏移', '其他'
DEFECT_TYPES = [MISSING, BROKEN, SWEEP, BOND1, BOND2, SHIFT, OTHER]
TYPE_COLORS = {MISSING: '#ff3030', BROKEN: '#ff6a00', SWEEP: '#ffa000', BOND1: '#e8336d', BOND2: '#ff00aa',
               SHIFT: '#5e7bff', OTHER: '#30d0ff'}
FROM_FLAG = {'MISSING': MISSING, 'BROKEN': BROKEN, 'BOND1': BOND1, 'BOND2': BOND2, 'SWEEP': SWEEP, 'SHIFT': SHIFT}
DEFAULT_CONFIG = {'recent_folders': [], 'operator': '', 'product': '', 'workers': 0}


def type_color(t):
    return TYPE_COLORS.get(t, '#30ff60')


def save_json(path, value, indent=2):
    """Write through a temp file so a crash never leaves half a file."""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False, indent=indent)
    os.replace(tmp, path)


def load_json(path, default):
    if not os.path.isfile(path):
        return default
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def load_config():
    cfg = dict(DEFAULT_CONFIG)
    cfg.update(load_json(CONFIG_PATH, {}))
    return cfg


def save_config(cfg):
    save_json(CONFIG_PATH, cfg)


RECENT_MAX = 10


def update_config(**changes):
    """Change settings on the file as it is now: config.json is shared with the C# version (AppConfig.Update),
    so saving a copy loaded at start-up would wipe what it recorded meanwhile."""
    cfg = load_config()
    cfg.update(changes)
    save_config(cfg)
    return cfg


def norm_path(p):
    """Absolute path without a trailing separator: 'a/b/' and 'a/b' are the same folder (AppConfig.NormPath)."""
    return os.path.normpath(os.path.abspath(p))


def recent_folders(cfg=None):
    """Recent folders from the file, normalised, newest first, each once (AppConfig.Recent)."""
    out, seen = [], set()
    for p in (cfg or load_config()).get('recent_folders') or []:
        if not isinstance(p, str) or not p.strip():
            continue
        p = norm_path(p)
        key = p if sys.platform.startswith('linux') else p.lower()   # macOS / Windows folders ignore case
        if key not in seen:
            seen.add(key)
            out.append(p)
    return out[:RECENT_MAX]


def add_recent(path):
    cfg = load_config()
    cfg['recent_folders'] = recent_folders(dict(cfg, recent_folders=[path] + list(cfg.get('recent_folders') or [])))
    save_config(cfg)
