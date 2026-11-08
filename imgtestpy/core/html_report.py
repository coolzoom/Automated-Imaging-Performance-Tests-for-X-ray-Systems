"""Self-contained interactive HTML report (same layout as app-gpt).

Each image is a card: embedded (or linked) JPEG with an SVG overlay of unit boxes,
wire curves and defect boxes; checkboxes in the header toggle all marks, labels and
individual defect types via CSS classes on <body>; radios filter by verdict.
"""
import base64
import datetime
import html
import os

import cv2
import numpy as np

from .config import DEFECT_TYPES, TYPE_COLORS, FROM_FLAG
from .imgio import read_gray
from .session import AUTO_OK, FAIL, PASS, STATUS_TEXT, SUSPECT, UNCHECKED

E = html.escape
VERDICT_CLASS = {PASS: 'pass', FAIL: 'fail', SUSPECT: 'suspect', AUTO_OK: 'ok', UNCHECKED: 'none'}
SRC = {'auto': '算法', 'manual': '手动', 'import': '导入'}
STATE = {'pending': '待确认', 'accepted': '已确认', 'rejected': '误报'}
FLAG_TEXT = FROM_FLAG

CSS = """
*{box-sizing:border-box}body{font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;margin:0;background:#f2f3f5;color:#222}
header{background:#1f2933;color:#fff;padding:14px 22px}h1{margin:0 0 6px;font-size:22px}
.meta{font-size:13px;color:#cbd2d9}.stats{display:flex;flex-wrap:wrap;gap:10px;margin-top:10px}
.stat{background:#323f4b;border-radius:6px;padding:6px 12px;font-size:13px}.stat b{font-size:18px;margin-left:4px}
.controls{position:sticky;top:0;z-index:5;background:#fff;border-bottom:1px solid #ddd;padding:10px 22px;display:flex;flex-wrap:wrap;gap:18px;align-items:center;font-size:14px}
.controls fieldset{border:1px solid #ddd;border-radius:6px;padding:4px 10px;margin:0}.controls legend{font-size:12px;color:#666}
.controls label{margin-right:10px;white-space:nowrap;cursor:pointer}.sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:3px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(460px,1fr));gap:16px;padding:18px 22px}
.card{background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.12);overflow:hidden;border-top:5px solid #9aa5b1}
.card.pass{border-top-color:#18a848}.card.fail{border-top-color:#e02424}.card.suspect{border-top-color:#ff9900}.card.ok{border-top-color:#7fc8a0}
.ch{display:flex;justify-content:space-between;gap:8px;padding:8px 12px;font-size:13px;align-items:center}.ch .path{word-break:break-all}
.badge{font-weight:bold;color:#fff;border-radius:4px;padding:2px 8px;white-space:nowrap}.badge.pass{background:#18a848}.badge.fail{background:#e02424}
.badge.suspect{background:#ff9900}.badge.ok{background:#7fc8a0}.badge.none{background:#9aa5b1}
.fig{position:relative;background:#111;cursor:zoom-in}.fig img{display:block;width:100%}.fig svg{position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none}
.mk rect{fill:none;stroke-width:3;vector-effect:non-scaling-stroke}.mk.pending.auto rect{stroke-dasharray:6 4}
.mk text{font-size:var(--fs,22px);font-weight:bold;paint-order:stroke;stroke:#000;stroke-width:3px}
.wk polyline{fill:none;stroke-width:2.5;vector-effect:non-scaling-stroke}
.wk circle{fill:none;stroke-width:2;vector-effect:non-scaling-stroke}
.wk text{font-size:var(--fs,20px);font-weight:bold;paint-order:stroke;stroke:#000;stroke-width:3px}
.ub rect{fill:none;stroke-width:1.5;stroke-dasharray:8 4;vector-effect:non-scaling-stroke}
.ub text{font-size:var(--fs,18px);paint-order:stroke;stroke:#000;stroke-width:3px}
.cb{padding:8px 12px;font-size:13px}.cb table{border-collapse:collapse;width:100%}.cb td,.cb th{border-bottom:1px solid #eee;padding:3px 4px;text-align:left}
.cb .cm{margin-top:6px;color:#444;white-space:pre-wrap}.muted{color:#888}
body.hide-marks .mk,body.hide-marks .wk,body.hide-marks .ub{display:none}body.hide-labels .mk text,body.hide-labels .wk text,body.hide-labels .ub text{display:none}
#lb{display:none;position:fixed;inset:0;background:rgba(0,0,0,.9);z-index:10;align-items:center;justify-content:center;cursor:zoom-out}
#lb.on{display:flex}#lb .fig{max-width:96vw;max-height:94vh;cursor:zoom-out}#lb .fig img{max-width:96vw;max-height:94vh;width:auto}
@media print{.controls{display:none}.card{break-inside:avoid}}
"""

JS = """
const B=document.body;
function tog(cls,on){B.classList.toggle(cls,!on)}
document.querySelectorAll('[data-type]').forEach(c=>c.onchange=()=>tog('hide-t'+c.dataset.type,c.checked));
function applyFilter(){
  const v=document.querySelector('input[name=vf]:checked').value, q=document.getElementById('q').value.trim().toLowerCase();
  let n=0;document.querySelectorAll('.card').forEach(c=>{
    const ok=(v==='all'||c.dataset.status===v||(v==='review'&&c.dataset.status!=='pass'&&c.dataset.status!=='fail'))&&(!q||c.dataset.path.toLowerCase().includes(q));
    c.style.display=ok?'':'none';if(ok)n++;});
  document.getElementById('shown').textContent=n;
}
document.querySelectorAll('input[name=vf]').forEach(r=>r.onchange=applyFilter);document.getElementById('q').oninput=applyFilter;
const lb=document.getElementById('lb');
document.querySelectorAll('.card .fig').forEach(f=>f.onclick=()=>{lb.innerHTML='';lb.appendChild(f.cloneNode(true));lb.classList.add('on')});
lb.onclick=()=>lb.classList.remove('on');document.onkeydown=e=>{if(e.key==='Escape')lb.classList.remove('on')};
applyFilter();
"""


def _type_index(t):
    return DEFECT_TYPES.index(t) if t in DEFECT_TYPES else len(DEFECT_TYPES)


def _flag_text(flags):
    return '、'.join(FLAG_TEXT.get(f, f) for f in flags)


def _encode_jpeg(gray, side=1600, quality=80):
    h, w = gray.shape[:2]
    if max(h, w) > side:
        f = side / float(max(h, w))
        gray = cv2.resize(gray, (max(1, int(w * f)), max(1, int(h * f))), interpolation=cv2.INTER_AREA)
    ok, buf = cv2.imencode('.jpg', gray, [cv2.IMWRITE_JPEG_QUALITY, int(quality)])
    if not ok:
        raise IOError('JPEG 编码失败')
    return buf.tobytes()


def _image_src(session, rel, out_dir, embed):
    path = os.path.join(session.root, rel)
    if embed:
        g = read_gray(path)
        data = _encode_jpeg(g)
        return 'data:image/jpeg;base64,' + base64.b64encode(data).decode('ascii'), g.shape[1], g.shape[0]
    r = session.data['images'].get(rel, {})
    w, h = r.get('width'), r.get('height')
    if not w:
        g = read_gray(path)
        h, w = g.shape
    return os.path.relpath(path, out_dir).replace(os.sep, '/'), w, h


def _card(session, rel, out_dir, embed, idx):
    r = session.data['images'].get(rel, {})
    st = session.status(rel)
    cls = VERDICT_CLASS[st]
    src, W, H = _image_src(session, rel, out_dir, embed)
    live = session.live_defects(rel)
    wires = r.get('wires') or []
    units = r.get('units') or []
    fs = max(14, int(max(W, H) / 60))
    marks = []
    for u in units:
        marks.append('<g class="ub"><rect x="%.1f" y="%.1f" width="%d" height="%d" stroke="#31b8ff"/>'
                     '<text x="%.1f" y="%.1f" fill="#31b8ff">%d</text></g>'
                     % (u['box'][0], u['box'][1], u['box'][2], u['box'][3],
                        u['box'][0] + 4, u['box'][1] + max(18, fs), u['index'] + 1))
    for w in wires:
        c = '#ff4d4d' if w['flags'] else '#3fd070'
        pts = ' '.join('%.1f,%.1f' % (p[0], p[1]) for p in (w.get('curve') or [w['p1'], w['p2']]))
        lbl = '单元%d 线%d %s' % (w['unit'] + 1, w['index'] + 1, _flag_text(w['flags']) if w['flags'] else 'OK')
        bx, by = w['ball']
        marks.append('<g class="wk%s"><polyline points="%s" stroke="%s"/>'
                     '<circle cx="%.1f" cy="%.1f" r="4" stroke="%s"/>'
                     '<text x="%.1f" y="%.1f" fill="%s">%s</text></g>'
                     % (' bad' if w['flags'] else '', pts, c, bx, by, c,
                        w['p1'][0] + 4, w['p1'][1] - 6, c, E(lbl)))
    for d in live:
        c = TYPE_COLORS.get(d.type, '#30ff60')
        lbl = d.type + ('' if d.source == 'manual' else ' %.2f' % d.score)
        marks.append('<g class="mk t-%d %s %s"><rect x="%d" y="%d" width="%d" height="%d" stroke="%s"/>'
                     '<text x="%d" y="%d" fill="%s">%s</text></g>' % (
                         _type_index(d.type), d.status, d.source, d.x0, d.y0, d.x1 - d.x0, d.y1 - d.y0, c,
                         d.x0 + 2, max(fs, d.y0 - 4), c, E(lbl)))
    bad = [w for w in wires if w['flags']]
    wrows = ''.join('<tr><td>%d</td><td>%d</td><td>%s</td><td>%.1f</td><td>%.1f%%</td><td>%.1f</td></tr>' % (
        w['unit'] + 1, w['index'] + 1, E(_flag_text(w['flags'])), w['z_all'], w['sweep_pct'], w['shift_px']) for w in bad)
    wtable = ('<table><tr><th>单元</th><th>线</th><th>缺陷</th><th>z</th><th>sweep</th><th>偏移px</th></tr>%s</table>' % wrows
              if bad else '')
    drows = ''.join('<tr><td><span class="sw" style="background:%s"></span>%s</td><td>%.2f</td><td>%s</td>'
                    '<td>%s</td><td class="muted">%d,%d-%d,%d</td></tr>' % (
                        TYPE_COLORS.get(d.type, '#30ff60'), E(d.type), d.score, SRC.get(d.source, d.source),
                        STATE.get(d.status, d.status), d.x0, d.y0, d.x1, d.y1) for d in live)
    dtable = ('<table><tr><th>类型</th><th>分数</th><th>来源</th><th>状态</th><th>位置(像素)</th></tr>%s</table>' % drows
              if live else '')
    table = wtable + dtable or '<div class="muted">无缺陷标记</div>'
    who = ''
    if r.get('verdict'):
        who = '<div class="muted">判定: %s %s</div>' % (E(r.get('reviewer') or '-'), E(r.get('reviewed_at', '')))
    comment = r.get('comment', '')
    meta = '单元 %d · 导线 %d · NG <b style="color:#c00">%d</b>' % (len(units), len(wires), len(bad))
    if r.get('product'):
        meta += ' · %s' % E(r['product'])
    if r.get('error'):
        meta += ' · <b style="color:#c00">错误: %s</b>' % E(r['error'])
    if r.get('scale') and abs(r['scale'] - 1) > 0.01:
        meta += ' · 缩放 %.2f' % r['scale']
    return ('<div class="card %s" data-status="%s" data-path="%s"><div class="ch"><span class="path">%d. %s</span>'
            '<span class="badge %s">%s</span></div><div class="fig"><img loading="lazy" src="%s" alt="">'
            '<svg viewBox="0 0 %d %d" preserveAspectRatio="none" style="--fs:%dpx">%s</svg></div>'
            '<div class="cb"><div class="muted">%s · 检测 %s</div>%s%s%s</div></div>' % (
                cls, cls, E(rel), idx, E(rel), cls, STATUS_TEXT[st] if st in (PASS, FAIL) else '未判定(%s)' % STATUS_TEXT[st],
                src, W, H, fs, ''.join(marks), meta, E(r.get('detected_at') or '-'), table,
                '<div class="cm">备注: %s</div>' % E(comment) if comment else '', who))


def write_html(session, rels, path, embed=True):
    """Write the interactive HTML report for `rels` to `path`. Returns path."""
    out_dir = os.path.dirname(os.path.abspath(path))
    c = {k: 0 for k in STATUS_TEXT}
    type_count, ng_wires = {}, 0
    for rel in rels:
        c[session.status(rel)] += 1
        r = session.data['images'].get(rel, {})
        ng_wires += sum(1 for w in (r.get('wires') or []) if w['flags'])
        for d in session.live_defects(rel):
            type_count[d.type] = type_count.get(d.type, 0) + 1
    total = len(rels)
    reviewed = c[PASS] + c[FAIL]
    stats = [('图片总数', total), ('PASS', c[PASS]), ('FAIL', c[FAIL]), ('未判定', total - reviewed),
             ('其中疑似', c[SUSPECT]), ('NG导线', ng_wires), ('不良率', '%.1f%%' % (100.0 * c[FAIL] / reviewed) if reviewed else '-')]
    stats += [(t, n) for t, n in sorted(type_count.items(), key=lambda kv: -kv[1])]
    types_present = [t for t in DEFECT_TYPES if t in type_count] + sorted(t for t in type_count if t not in DEFECT_TYPES)
    type_boxes = ''.join('<label><input type="checkbox" checked data-type="%d"><span class="sw" style="background:%s">'
                         '</span>%s (%d)</label>' % (_type_index(t), TYPE_COLORS.get(t, '#30ff60'), E(t), type_count[t])
                         for t in types_present) or '<span class="muted">无</span>'
    type_css = ''.join('body.hide-t%d .t-%d{display:none}' % (i, i) for i in range(len(DEFECT_TYPES) + 1))
    filters = [('all', '全部'), ('fail', 'FAIL'), ('pass', 'PASS'), ('suspect', '疑似未判定'), ('review', '未判定')]
    vf = ''.join('<label><input type="radio" name="vf" value="%s"%s>%s</label>' % (v, ' checked' if v == 'all' else '', t)
                 for v, t in filters)
    cards = ''.join(_card(session, rel, out_dir, embed, i) for i, rel in enumerate(rels, 1))
    products = sorted({(session.data['images'].get(rel, {}) or {}).get('product') for rel in rels} - {None})
    doc = """<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>X-Ray 焊线检测报告 - {name}</title><style>{css}{type_css}</style></head><body>
<header><h1>X-Ray 焊线检测报告</h1><div class="meta">文件夹: {root}<br>操作员: {op} · 生成时间: {time} · 产品: {prod}</div>
<div class="stats">{stats}</div></header>
<div class="controls">
<fieldset><legend>标记显示</legend><label><input type="checkbox" checked onchange="tog('hide-marks',this.checked)">显示标记</label>
<label><input type="checkbox" checked onchange="tog('hide-labels',this.checked)">显示标签文字</label></fieldset>
<fieldset><legend>缺陷类型</legend>{types}</fieldset>
<fieldset><legend>筛选</legend>{vf}</fieldset>
<label>搜索文件 <input id="q" type="search" placeholder="文件名"></label><span>显示 <b id="shown"></b> / {total}</span>
</div><div class="grid">{cards}</div><div id="lb"></div><script>{js}</script></body></html>""".format(
        name=E(os.path.basename(session.root.rstrip('/\\'))), css=CSS, type_css=type_css, root=E(session.root),
        op=E(session.data.get('operator') or '-'), time=datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        prod=E('、'.join(products) or '-'),
        stats=''.join('<div class="stat">%s<b>%s</b></div>' % (E(str(k)), v) for k, v in stats),
        types=type_boxes, vf=vf, total=total, cards=cards, js=JS)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(doc)
    return path
