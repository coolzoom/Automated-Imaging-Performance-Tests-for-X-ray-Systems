"""Paths, program settings (imgtest/config.json, shared with the C# version) and defect types."""
import json
import os

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
