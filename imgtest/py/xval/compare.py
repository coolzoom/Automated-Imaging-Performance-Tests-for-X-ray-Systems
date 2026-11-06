"""Cross-check the Python port against the C# WireInspect.Core on the same images and product.

  .venv/bin/python xval/compare.py [图片文件夹] [--product 2_8_4_demo]

Builds xval/xval.csproj (C# harness), then compares per image: unit boxes, wire flags, every wire number,
defect boxes; plus auto_wires, pixel params, mirror / rotation, and session.json / CSV report both ways.
Synthetic images (tests/synthetic.py) are always included."""
import argparse
import glob
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PY = os.path.dirname(HERE)
sys.path[:0] = [PY, os.path.join(PY, 'tests')]

from core import report  # noqa: E402
from core.align import rotate_inscribed  # noqa: E402
from core.autowires import auto_wires  # noqa: E402
from core.config import PRODUCTS_DIR  # noqa: E402
from core.imgio import read_gray, write_png  # noqa: E402
from core.inspect import detect  # noqa: E402
from core.product import Product  # noqa: E402
from core.session import Session  # noqa: E402
import synthetic as syn  # noqa: E402

CS = ['dotnet', os.path.join(HERE, 'bin', 'Release', 'net10.0', 'xval.dll')]
NUM = ('z_all', 'z_ball', 'sweep_px', 'sweep_pct', 'shift_px', 'gap1', 'gap2', 'gap_mid', 'l')
TOL = 1e-6   # relative: |py - cs| / max(1, |py|, |cs|)


def rel(x, y):
    return abs(x - y) / max(1.0, abs(x), abs(y))


def run_case(name, product_dir, images, work):
    out = os.path.join(work, name + '_cs.json')
    subprocess.run(CS + [out, product_dir] + images, check=True)
    with open(out, encoding='utf-8') as f:
        cs = json.load(f)
    p = Product.load(product_dir)
    md = {k: 0.0 for k in NUM + ('z_seg', 'ball', 'curve', 'match', 'angle', 'affine')}
    n_units = n_wires = n_bad = n_mir_py = n_mir_cs = 0
    flags, units, defects = [], [], []
    for path in images:
        py, c = detect(read_gray(path), p), cs[path]
        if [u['box'] for u in py['units']] != [u['box'] for u in c['units']]:
            units.append(os.path.basename(path))
            continue
        n_units += len(py['units'])
        md['angle'] = max(md['angle'], abs(py['angle'] - c['angle']))
        n_mir_py += sum(1 for u in py['units'] if u.get('mirror'))
        n_mir_cs += sum(1 for u in c['units'] if u.get('mirror'))
        for a, b in zip(py['units'], c['units']):
            md['match'] = max(md['match'], abs(a['match'] - b['match']))
            md['affine'] = max([md['affine']] + [abs(x - y) for x, y in zip(a['affine'], b['affine'])])
            md['angle'] = max(md['angle'], abs(a['angle'] - b['angle']))
        for a, b in zip(py['wires'], c['wires']):
            n_wires += 1
            n_bad += bool(a['flags'])
            if a['flags'] != b['flags']:
                flags.append((os.path.basename(path), a['unit'], a['index'], a['flags'], b['flags']))
            for k in NUM:
                md[k] = max(md[k], rel(a[k], b[k]))
            for k in ('z_seg', 'ball'):
                md[k] = max([md[k]] + [rel(x, y) for x, y in zip(a[k], b[k])])
            md['curve'] = math.inf if len(a['curve']) != len(b['curve']) else max(
                [md['curve']] + [abs(x - y) for pa, pb in zip(a['curve'], b['curve']) for x, y in zip(pa, pb)])

        def key(d):
            return d['x0'], d['y0'], d['x1'], d['y1'], d['type'], d['score'], d['by'], d['source'], d['status']
        if sorted(map(key, py['defects'])) != sorted(map(key, c['defects'])):
            defects.append(os.path.basename(path))
    aw_py = [(w.p1, w.p2) for w in auto_wires(p.template, p)]
    aw_cs = [(w['p1'], w['p2']) for w in cs['__auto_wires']]
    pa = p.params()
    params_ok = all(getattr(pa, k) == v if isinstance(v, bool) else abs(getattr(pa, k) - v) < 1e-9
                    for k, v in cs['__params'].items())
    ok = (not units and not flags and not defects and max(md.values()) < TOL and aw_py == aw_cs and params_ok
          and n_mir_py == n_mir_cs)
    rotated = sum(1 for path in images if any(u['angle'] for u in cs[path]['units']))
    print('=== %s: %d 图 (其中 %d 张有旋转单元), %d 单元, %d 导线 (NG %d)  -> %s' % (name, len(images), rotated, n_units, n_wires, n_bad,
                                                         '一致' if ok else '不一致'))
    print('  单元框不同:', units or '无', ' 判定不同:', flags or '无', ' 缺陷框不同:', defects or '无')
    print('  最大相对差:', ', '.join('%s=%.1e' % kv for kv in md.items()))
    print('  auto_wires: py %s / cs %s' % (aw_py, aw_cs))
    print('  参数一致: %s   镜像单元 py %d / cs %d' % (params_ok, n_mir_py, n_mir_cs))
    return ok


def session_case(images, product_dir, work):
    """Python writes session.json -> C# reads it (status, report) and saves -> Python reads it back."""
    root = os.path.join(work, 'session')
    os.makedirs(root)
    for path in images:
        shutil.copy(path, root)
    p = Product.load(product_dir)
    s = Session(root)
    for rel in s.files:
        s.set_detection(rel, detect(read_gray(os.path.join(root, rel)), p))
    s.set_verdict(s.files[-1], 'PASS', '张三')
    s.save()

    def state(f):
        return {'status': s.status(f), 'live': len(s.live_defects(f)), 'pending': s.has_pending(f),
                'wires': len(s.rec(f).get('wires') or []), 'bad': sum(1 for w in s.rec(f).get('wires') or [] if w['flags'])}
    py = {f: state(f) for f in s.files}
    report.write(s, os.path.join(root, 'report_py.csv'))
    cs = json.loads(subprocess.run(CS + ['session', root], capture_output=True, text=True, check=True).stdout)
    same_csv = True
    for a, b in (('report_py.csv', 'report_cs.csv'), ('report_py_导线明细.csv', 'report_cs_导线明细.csv')):
        with open(os.path.join(root, a), encoding='utf-8-sig') as fa, open(os.path.join(root, b), encoding='utf-8-sig') as fb:
            same_csv &= fa.read().splitlines() == fb.read().splitlines()
    s2 = Session(root)
    back = (s2.status(s2.files[0]) == 'FAIL' and s2.rec(s2.files[0])['reviewer'] == 'cs'
            and all(len(s2.rec(f).get('wires') or []) == py[f]['wires'] for f in s2.files)
            and all(s2.status(f) == py[f]['status'] for f in s2.files[1:]))
    ok = py == cs and same_csv and back
    print('=== session.json / 报告互通: %s' % ('一致' if ok else '不一致'))
    print('  C# 读 Python session 状态一致: %s   CSV 报告逐行一致: %s   Python 读 C# 保存的 session: %s'
          % (py == cs, same_csv, back))
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('folder', nargs='?', default='/Users/mac/Downloads/2026-11-03-13-56-22')
    ap.add_argument('--product', default='2_8_4_demo')
    a = ap.parse_args()
    subprocess.run(['dotnet', 'build', '-c', 'Release', '-v', 'q', '--nologo', HERE], check=True, stdout=subprocess.DEVNULL)
    work = tempfile.mkdtemp(prefix='wi_xval_')
    product_dir = os.path.join(PRODUCTS_DIR, a.product)
    real = sorted(f for f in glob.glob(os.path.join(a.folder, '*')) if f.lower().endswith(('.jpg', '.png', '.bmp', '.tif'))
                  and os.path.getsize(f) > 0)
    results = []
    if real:
        results.append(run_case('real', product_dir, real, work))
        # the same pictures rotated / distorted (geometry correction must agree too)
        geo = os.path.join(work, 'geo')
        os.makedirs(geo)
        imgs = []
        for i, f in enumerate(real[:12]):
            g = read_gray(f)
            for tag, img in (('rot', rotate_inscribed(g, (3, -5, 7.5, 90, 180, -170)[i % 6])), ('barrel', syn.barrel(g, (0.03, -0.03)[i % 2]))):
                imgs.append(os.path.join(geo, '%s_%s.png' % (os.path.splitext(os.path.basename(f))[0], tag)))
                write_png(imgs[-1], img)
        results.append(run_case('real_rotated_distorted', product_dir, imgs, work))

    d = os.path.join(work, 'syn')
    os.makedirs(d)
    good = syn.make()
    syn.make_product(good).save(os.path.join(work, 'products'))
    cases = {'good': {}, 'missing': {'missing': (4, 1)}, 'broken': {'broken': (2, 0)}, 'sweep': {'sweep': (6, 0)},
             'shift': {'shift': (7, 5)}, 'sweep3': {'sweep': (1, 1), 'sweep_px': 3}, 'noise7': {'seed': 7}}
    imgs = []
    for k, o in cases.items():
        imgs.append(os.path.join(d, k + '.png'))
        write_png(imgs[-1], syn.make(**o) if o else good)
    for k, img in (('rot4', syn.rotate(good, 4)), ('rot-7_missing', syn.rotate(syn.make(missing=(4, 1)), -7)),
                   ('barrel', syn.barrel(good, 0.03)), ('rot6_barrel_sweep', syn.barrel(syn.rotate(syn.make(sweep=(6, 0)), 6), -0.03)),
                   ('rot135', syn.rotate(good, 135)), ('rot-100', syn.rotate(good, -100)),
                   ('mixed', syn.make_mixed([0, 35, 90, -120, 180, 15, -60, 145, 270])),
                   ('mixed_missing', syn.make_mixed([10, -80, 170, 45, 0, -150, 95, 30, -30], missing=(4, 1)))):
        imgs.append(os.path.join(d, k + '.png'))
        write_png(imgs[-1], img)
    syn_dir = os.path.join(work, 'products', 'synthetic')
    results.append(run_case('synthetic', syn_dir, imgs, work))
    results.append(session_case(imgs[:4], syn_dir, work))
    shutil.rmtree(work)
    print('\n总结: %s' % ('全部一致' if all(results) else '存在差异'))
    return 0 if all(results) else 1


if __name__ == '__main__':
    sys.exit(main())
