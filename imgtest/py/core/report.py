"""Folder CSV report: one row per image (summary) + one row per wire (details file); same columns as C#."""
import csv
import os

from .config import FROM_FLAG
from .session import STATUS_TEXT


def flag_text(flags):
    return '、'.join(FROM_FLAG.get(f, f) for f in flags)


def _write(path, rows):
    with open(path, 'w', encoding='utf-8-sig', newline='') as f:
        csv.writer(f, lineterminator='\n').writerows(rows)


def _num(v):
    return ('%g' % v) if isinstance(v, float) else v


def write(session, summary_path):
    s = session
    summary = [['文件', '状态', '判定', '判定人', '判定时间', '产品', '像素µm', '单元数', '导线数', 'NG导线', '有效缺陷数',
                '缺陷类型', '备注', '检测时间', '错误']]
    details = [['文件', '单元', '线', '结果', 'z', 'z_ball', '长度µm', 'sweep%', 'sweep µm', '端点偏移µm', '起点缺口µm',
                '终点缺口µm', '焊点1x', '焊点1y', '焊点2x', '焊点2y']]
    for rel in s.files:
        r = s.rec(rel)
        live = s.live_defects(rel)
        wires = r.get('wires') or []
        pu = r.get('pixel_um') or 0
        um = pu if pu > 0 else 1
        types = {}
        for d in live:
            types[d.type] = types.get(d.type, 0) + 1
        summary.append([rel, STATUS_TEXT[s.status(rel)], r.get('verdict') or '', r.get('reviewer', ''),
                        r.get('reviewed_at', ''), r.get('product') or '', _num(pu) if pu > 0 else '',
                        len(r.get('units') or []), len(wires), sum(1 for w in wires if w['flags']), len(live),
                        '、'.join('%s×%d' % kv for kv in types.items()), r.get('comment', ''),
                        r.get('detected_at') or '', r.get('error') or ''])
        for w in wires:
            details.append([rel, w['unit'] + 1, w['index'] + 1, flag_text(w['flags']) if w['flags'] else '正常',
                            '%.1f' % w['z_all'], '%.1f' % w['z_ball'], '%.0f' % (w['l'] * um), '%.1f' % w['sweep_pct'],
                            '%.1f' % (w['sweep_px'] * um), '%.1f' % (w['shift_px'] * um), '%.0f' % (w['gap1'] * um),
                            '%.0f' % (w['gap2'] * um), '%.1f' % w['ball'][0], '%.1f' % w['ball'][1],
                            '%.1f' % w['p2'][0], '%.1f' % w['p2'][1]])
    details_path = os.path.join(os.path.dirname(os.path.abspath(summary_path)),
                                os.path.splitext(os.path.basename(summary_path))[0] + '_导线明细.csv')
    _write(summary_path, summary)
    _write(details_path, details)
    return summary_path, details_path


def write_wires(path, rec):
    """Wires of one image (the 「导线统计」 export)."""
    um = rec.get('pixel_um') or 1
    rows = [['单元', '线', '起点x', '起点y', '终点x', '终点y', '长度µm', 'sweep%', '端点偏移µm', 'z', '判定']]
    for w in rec.get('wires') or []:
        rows.append([w['unit'] + 1, w['index'] + 1, '%.1f' % w['p1'][0], '%.1f' % w['p1'][1], '%.1f' % w['p2'][0],
                     '%.1f' % w['p2'][1], '%.0f' % (w['l'] * um), '%.1f' % w['sweep_pct'], '%.1f' % (w['shift_px'] * um),
                     '%.1f' % w['z_all'], flag_text(w['flags']) if w['flags'] else '正常'])
    _write(path, rows)
