#!/usr/bin/env python3
"""Bond-wire X-ray inspection, command line (same commands and CSV as the C# wireinspect).

  wire_inspect.py [detect] <图片> [--product 名称] [--out 目录] [--simulate 缺线单元 断线单元 弯线单元]
                  [--sim-sweep-px 5] [--json 结果.json]
  wire_inspect.py make-product --image <图片> --box x,y,w,h --name 名称 [--pixel-um 10] [--wire x1,y1,x2,y2 ...] [--auto]
  wire_inspect.py list
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.autowires import auto_wires  # noqa: E402
from core.config import PRODUCTS_DIR, ROOT, load_config  # noqa: E402
from core.imgio import read_gray, write_png  # noqa: E402
from core.inspect import detect, find_units  # noqa: E402
from core.product import Product, Wire, list_products  # noqa: E402
from core.simulate import annotate, simulate  # noqa: E402

CSV_HEAD = 'unit,wire,x,y,match,result,z_all,z_seg,z_ball,sweep_px,sweep_pct,shift_px,gap1,gap2,L'


def csv_row(w, u):
    return ','.join(str(v) for v in (
        w['unit'], w['index'], u['box'][0], u['box'][1], '%.2f' % u['match'], '|'.join(w['flags']) or 'OK',
        '%.1f' % w['z_all'], ' '.join('%.1f' % z for z in w['z_seg']), '%.1f' % w['z_ball'], '%.2f' % w['sweep_px'],
        '%.1f' % w['sweep_pct'], '%.2f' % w['shift_px'], '%.0f' % w['gap1'], '%.0f' % w['gap2'], '%.0f' % w['l']))


def cmd_detect(a):
    name = a.product or load_config().get('product') or next(iter(list_products()), None)
    if not name:
        sys.exit('没有产品，请先 make-product')
    product = Product.load(os.path.join(PRODUCTS_DIR, name))
    os.makedirs(a.out, exist_ok=True)
    stem = os.path.splitext(os.path.basename(a.image))[0]
    gray = read_gray(a.image)
    if a.simulate:
        units = find_units(gray, product.template, product.match_thr)
        gray = simulate(gray, product, units, *a.simulate, a.sim_sweep_px)
        stem += '_sim'
        write_png(os.path.join(a.out, stem + '.png'), gray)
    t0 = time.perf_counter()
    res = detect(gray, product)
    ms = (time.perf_counter() - t0) * 1000
    print('产品 %s  单元 %d  导线 %d  用时 %d ms' % (product.name, len(res['units']), len(res['wires']), ms))
    lines = [CSV_HEAD]
    for w in res['wires']:
        row = csv_row(w, res['units'][w['unit']])
        lines.append(row)
        print(row.replace(',', '  '))
    with open(os.path.join(a.out, stem + '_result.csv'), 'w', encoding='utf-8-sig', newline='') as f:
        f.write('\n'.join(lines) + '\n')
    annot = os.path.join(a.out, stem + '_annot.png')
    write_png(annot, annotate(gray, res, 2))
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as f:
            json.dump(res, f, ensure_ascii=False, indent=1)
    print('NG: %d/%d  -> %s' % (sum(bool(w['flags']) for w in res['wires']), len(res['wires']), annot))


def cmd_make_product(a):
    gray = read_gray(a.image)
    x, y, w, h = (int(v) for v in a.box.split(','))
    p = Product(name=a.name, pixel_um=a.pixel_um, template=gray[y:y + h, x:x + w].copy())
    if a.wire:
        for s in a.wire:
            v = [float(c) for c in s.split(',')]
            p.wires.append(Wire([v[0] - x, v[1] - y], [v[2] - x, v[3] - y]))
    elif a.auto:
        p.wires = auto_wires(p.template, p)
    p.save()
    print('已保存产品 %s: %s，%d 根线' % (p.name, p.dir, len(p.wires)))
    for wi in p.wires:
        print('  (%.1f,%.1f) -> (%.1f,%.1f)  L=%.1fpx' % (*wi.p1, *wi.p2, wi.length))


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] not in ('detect', 'make-product', 'list', '-h', '--help'):
        argv.insert(0, 'detect')
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    d = sub.add_parser('detect')
    d.add_argument('image')
    d.add_argument('--product')
    d.add_argument('--out', default=os.path.join(ROOT, 'out'))
    d.add_argument('--simulate', nargs=3, type=int, metavar=('MISS', 'BROKEN', 'SWEEP'))
    d.add_argument('--sim-sweep-px', type=float, default=5.0)
    d.add_argument('--json', help='同时把完整结果（全精度）写成 JSON')
    m = sub.add_parser('make-product')
    m.add_argument('--image', required=True)
    m.add_argument('--box', required=True)
    m.add_argument('--name', required=True)
    m.add_argument('--pixel-um', type=float, default=10.0)
    m.add_argument('--wire', nargs='+')
    m.add_argument('--auto', action='store_true')
    sub.add_parser('list')
    a = ap.parse_args(argv)
    try:
        if a.cmd == 'list':
            print('\n'.join(list_products()))
        elif a.cmd == 'make-product':
            cmd_make_product(a)
        else:
            cmd_detect(a)
    except (OSError, ValueError) as e:
        print('错误: %s' % e, file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
