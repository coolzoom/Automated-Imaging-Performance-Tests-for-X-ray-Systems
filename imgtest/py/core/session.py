"""Per-folder inspection state, stored in <folder>/.xray_wire/session.json (same file as the C# version)."""
import datetime
import os

from .config import load_json, save_json
from .defect import Defect, overlaps
from .scanner import scan

STATE_DIR = '.xray_wire'
UNCHECKED, AUTO_OK, SUSPECT, PASS, FAIL = 'unchecked', 'ok', 'suspect', 'PASS', 'FAIL'
STATUS_TEXT = {UNCHECKED: '未检测', AUTO_OK: '未检出候选（待复核）', SUSPECT: '疑似缺陷', PASS: 'PASS', FAIL: 'FAIL'}
STATUS_COLORS = {UNCHECKED: '#9a9a9a', AUTO_OK: '#7fc8a0', SUSPECT: '#ff9900', PASS: '#18a848', FAIL: '#e02424'}
REVIEWED = ('accepted', 'rejected')
_RESULT_KEYS = ('detected_at', 'error', 'product', 'angle', 'units', 'wires')


def now():
    return datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')


class Session:
    def __init__(self, root):
        self.root = os.path.abspath(root)
        self.path = os.path.join(self.root, STATE_DIR, 'session.json')
        self.data = {'root': self.root, 'created': now(), 'operator': '', 'saved': '', 'images': {}}
        self.data.update(load_json(self.path, {}))
        self.data['root'] = self.root
        self.data['created'] = self.data.get('created') or now()
        self.empty = []    # 0-byte image files, not listed
        self.files = scan(self.root, self.empty)
        self.dirty = False

    # ---- records
    def rec(self, rel):
        r = self.data['images'].setdefault(rel, {})
        for k, v in (('defects', []), ('verdict', None), ('comment', ''), ('reviewer', ''), ('reviewed_at', '')):
            r.setdefault(k, v)
        return r

    def _peek(self, rel):
        return self.data['images'].get(rel)

    def defects(self, rel):
        return [Defect.from_dict(d) for d in self.rec(rel)['defects']]

    def set_defects(self, rel, defects):
        self.rec(rel)['defects'] = [d.to_dict() for d in defects]
        self.dirty = True

    def has_detection(self, rel):
        return bool((self._peek(rel) or {}).get('detected_at'))

    def set_detection(self, rel, res):
        """Keeps manual marks and every reviewed box; a new box landing on a reviewed one is dropped."""
        r = self.rec(rel)
        keep = [d for d in r['defects'] if d.get('source') == 'manual' or d.get('status') in REVIEWED]
        reviewed = [d for d in keep if d.get('status') in REVIEWED]
        r['defects'] = keep + [d for d in res['defects'] if not any(overlaps(d, k) for k in reviewed)]
        for k in ('width', 'height', 'product', 'angle', 'units', 'wires'):
            r[k] = res.get(k)
        r['detected_at'] = now()
        r.pop('error', None)
        self.dirty = True

    def set_error(self, rel, msg):
        r = self.rec(rel)
        r['error'] = msg
        r['detected_at'] = now()
        self.dirty = True

    def clear_marks(self, rels, verdicts=False):
        """Remove every box and the detection result ("未检测" again); comments stay, verdicts only on request."""
        for rel in rels:
            r = self.rec(rel)
            r['defects'] = []
            for k in _RESULT_KEYS:
                r.pop(k, None)
            if verdicts:
                r['verdict'], r['reviewer'], r['reviewed_at'] = None, '', ''
        self.dirty = True

    def set_verdict(self, rel, verdict, reviewer=''):
        r = self.rec(rel)
        r['verdict'] = verdict
        r['reviewer'] = reviewer
        r['reviewed_at'] = now() if verdict else ''
        self.dirty = True

    def set_comment(self, rel, text):
        self.rec(rel)['comment'] = text
        self.dirty = True

    def live_defects(self, rel):
        return [Defect.from_dict(d) for d in (self._peek(rel) or {}).get('defects', []) if d.get('status') != 'rejected']

    def has_pending(self, rel):
        r = self._peek(rel) or {}
        return r.get('verdict') != PASS and any(d.status == 'pending' and d.source != 'manual'
                                                for d in self.live_defects(rel))

    def auto_state(self, rel):
        r = self._peek(rel)
        if not r or not r.get('detected_at'):
            return UNCHECKED
        if r.get('error') or not r.get('units'):   # no unit found: wrong product, or the picture needs a look
            return SUSPECT
        return SUSPECT if self.live_defects(rel) else AUTO_OK

    def status(self, rel):
        v = (self._peek(rel) or {}).get('verdict')
        return v if v in (PASS, FAIL) else self.auto_state(rel)

    def counts(self):
        c = {k: 0 for k in STATUS_TEXT}
        for f in self.files:
            c[self.status(f)] += 1
        return c

    def save(self):
        self.data['saved'] = now()
        # nulls are left out, like the C# writer (WhenWritingNull)
        images = {k: {kk: vv for kk, vv in v.items() if vv is not None} for k, v in self.data['images'].items()}
        save_json(self.path, dict(self.data, images=images), indent=1)
        self.dirty = False
