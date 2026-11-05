import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core import report  # noqa: E402
from core.autowires import auto_wires, calibrate_pitch  # noqa: E402
from core.config import MISSING  # noqa: E402
from core.defect import Defect  # noqa: E402
from core.imgio import read_gray, write_png  # noqa: E402
from core.inspect import detect  # noqa: E402
from core.product import Product, list_products  # noqa: E402
from core.session import AUTO_OK, PASS, SUSPECT, UNCHECKED, Session  # noqa: E402
import synthetic as syn  # noqa: E402


def run(**opts):
    good = syn.make()
    return detect(syn.make(**opts) if opts else good, syn.make_product(good))


def bad(r):
    return [w for w in r['wires'] if w['flags']]


def describe(r):
    return '; '.join('u%dw%d:%s' % (w['unit'], w['index'], '|'.join(w['flags'])) for w in bad(r))


class InspectTests(unittest.TestCase):
    def test_good_image_all_ok(self):
        r = run()
        self.assertEqual(9, len(r['units']))
        self.assertEqual(18, len(r['wires']))
        self.assertFalse(bad(r), describe(r))
        self.assertEqual([], r['defects'])

    def test_missing_wire(self):
        r = run(missing=(4, 1))
        b = bad(r)
        self.assertEqual(1, len(b), describe(r))
        self.assertEqual((4, 1), (b[0]['unit'], b[0]['index']))
        self.assertIn('MISSING', b[0]['flags'])
        self.assertEqual([MISSING], [d['type'] for d in r['defects']])

    def test_broken_wire(self):
        r = run(broken=(2, 0))
        b = bad(r)
        self.assertEqual(1, len(b), describe(r))
        self.assertEqual((2, 0), (b[0]['unit'], b[0]['index']))
        self.assertIn('BROKEN', b[0]['flags'])

    def test_swept_wire(self):
        r = run(sweep=(6, 0), sweep_px=6)
        b = bad(r)
        self.assertEqual(1, len(b), describe(r))
        self.assertEqual((6, 0), (b[0]['unit'], b[0]['index']))
        self.assertIn('SWEEP', b[0]['flags'])
        self.assertTrue(5.0 <= b[0]['sweep_px'] <= 7.0, b[0]['sweep_px'])

    def test_shifted_image_still_matches(self):
        r = run(shift=(7, 5))
        self.assertEqual(9, len(r['units']))
        self.assertFalse(bad(r), describe(r))
        self.assertEqual(syn.OX + 7, r['units'][0]['box'][0])

    def test_pixel_params_follow_micrometres(self):
        a = Product(pixel_um=10, corridor_um=70, end_tol_um=100, shift_max_um=35, wire_diam_um=25).params()
        self.assertEqual((7, 3, 4, 2, 6, 7), (a.half_w, a.bg_off, a.bg_w, a.d, a.ext, a.along))
        self.assertAlmostEqual(10, a.end_tol)
        self.assertAlmostEqual(3.5, a.shift_max)
        b = Product(pixel_um=5, corridor_um=70, wire_diam_um=25).params()
        self.assertEqual(14, b.half_w)
        self.assertTrue(b.along > a.along and b.d >= a.d)

    def test_auto_wires_finds_both(self):
        good = syn.make()
        p = syn.make_product(good)
        found = auto_wires(p.template, p)
        self.assertEqual(2, len(found), found)
        for t1, t2 in syn.WIRES:
            w = min((f if f.p1[0] < f.p2[0] else f.reversed() for f in found),
                    key=lambda f: abs(f.p1[1] - t1[1]) + abs(f.p2[1] - t2[1]))
            self.assertLessEqual(abs(w.p1[0] - t1[0]), 6)
            self.assertLessEqual(abs(w.p1[1] - t1[1]), 3)
            self.assertLessEqual(abs(w.p2[0] - t2[0]), 8)
            self.assertLessEqual(abs(w.p2[1] - t2[1]), 3)

    def test_calibrate_pitch(self):
        good = syn.make()
        p = syn.make_product(good)
        self.assertAlmostEqual(1500.0 / syn.PITCH_X, calibrate_pitch(good, p.template, p.match_thr, 1.5), 3)


class StoreTests(unittest.TestCase):
    def test_product_save_load(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = syn.make_product(syn.make())
            p.name, p.pixel_um = '型号 A/1', 7.5
            p.save(tmp)
            self.assertEqual(['型号 A_1'], list_products(tmp))
            q = Product.load(os.path.join(tmp, '型号 A_1'))
            self.assertEqual('型号 A/1', q.name)
            self.assertEqual(7.5, q.pixel_um)
            self.assertEqual(2, len(q.wires))
            self.assertEqual([syn.UNIT_W, syn.UNIT_H], q.tpl_size)
            self.assertEqual(syn.UNIT_W, q.template.shape[1])

    def test_session_keeps_manual_and_reviewed(self):
        with tempfile.TemporaryDirectory() as tmp:
            write_png(os.path.join(tmp, 'a.png'), syn.make(missing=(0, 0)))
            open(os.path.join(tmp, 'empty.jpg'), 'wb').close()
            os.makedirs(os.path.join(tmp, 'sub'))
            write_png(os.path.join(tmp, 'sub', 'b.png'), syn.make())
            s = Session(tmp)
            self.assertEqual(['a.png', 'sub/b.png'], s.files)
            self.assertEqual(['empty.jpg'], s.empty)
            self.assertEqual(UNCHECKED, s.status('a.png'))

            product = syn.make_product(syn.make())
            for rel in s.files:
                s.set_detection(rel, detect(read_gray(os.path.join(tmp, rel)), product))
            self.assertEqual(SUSPECT, s.status('a.png'))
            self.assertEqual(AUTO_OK, s.status('sub/b.png'))
            self.assertTrue(s.has_pending('a.png'))

            auto = s.rec('a.png')['defects'][0]
            auto['status'] = 'rejected'
            s.rec('a.png')['defects'].append(Defect(1, 1, 9, 9, source='manual', status='accepted').to_dict())
            self.assertEqual(SUSPECT, s.status('a.png'))
            s.set_detection('a.png', detect(read_gray(os.path.join(tmp, 'a.png')), product))
            ds = s.rec('a.png')['defects']
            self.assertEqual(2, len(ds))   # reviewed auto box kept, the same new box dropped, manual kept
            self.assertTrue(any(d['id'] == auto['id'] and d['status'] == 'rejected' for d in ds))

            s.set_verdict('sub/b.png', PASS, '张三')
            s.save()
            s2 = Session(tmp)
            self.assertEqual(PASS, s2.status('sub/b.png'))
            self.assertEqual('张三', s2.rec('sub/b.png')['reviewer'])
            self.assertEqual(18, len(s2.rec('a.png')['wires']))

            summ, det = report.write(s2, os.path.join(tmp, 'report.csv'))
            for path, n in ((summ, 3), (det, 1 + 36)):
                with open(path, encoding='utf-8-sig') as f:
                    self.assertEqual(n, len(f.read().splitlines()))

            s2.clear_marks(['a.png'])
            self.assertEqual(UNCHECKED, s2.status('a.png'))


if __name__ == '__main__':
    unittest.main()
