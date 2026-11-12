import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core import report  # noqa: E402
from core.autowires import auto_wires  # noqa: E402
from core.config import MISSING  # noqa: E402
from core.defect import Defect  # noqa: E402
from core.imgio import read_gray, write_png  # noqa: E402
from core.inspect import detect, inspect_wire, prepare  # noqa: E402
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

    def test_rotated_image_any_angle(self):
        good = syn.make()
        product = syn.make_product(good)
        for deg in (4, -7, 35, 90, 135, 180, -100):
            r = detect(syn.rotate(good, deg), product)
            self.assertAlmostEqual(0, (r['angle'] - deg + 540) % 360 - 180, delta=0.3)
            self.assertEqual(9, len(r['units']))
            self.assertFalse(bad(r), describe(r))
            self.assertTrue(all(w['shift_px'] < 1 for w in r['wires']))

    def test_rotated_missing_wire_found_in_place(self):
        good = syn.make()
        r = detect(syn.rotate(syn.make(missing=(4, 1)), 137), syn.make_product(good))
        b = bad(r)
        self.assertEqual([(4, 1)], [(w['unit'], w['index']) for w in b], describe(r))
        self.assertIn('MISSING', b[0]['flags'])
        # result in original image coordinates: the missing wire's box covers where the wire should be
        p1 = b[0]['p1']
        d = r['defects'][0]
        self.assertTrue(d['x0'] <= p1[0] <= d['x1'] and d['y0'] <= p1[1] <= d['y1'])

    def test_units_in_different_directions(self):
        good = syn.make()
        product = syn.make_product(good)
        angles = [0, 35, 90, -120, 180, 15, -60, 145, 270]
        for opts, flag in (({}, None), ({'missing': (4, 1)}, 'MISSING'), ({'broken': (2, 0)}, 'BROKEN'),
                           ({'sweep': (6, 0)}, 'SWEEP')):
            r = detect(syn.make_mixed(angles, **opts), product)
            self.assertEqual(9, len(r['units']))
            for u, want in zip(r['units'], angles):   # image row order = drawing order
                self.assertAlmostEqual(0, (u['angle'] - want + 180) % 360 - 180, delta=0.5)
            b = bad(r)
            if flag is None:
                self.assertFalse(b, describe(r))
            else:
                self.assertEqual([opts[next(iter(opts))]], [(w['unit'], w['index']) for w in b], describe(r))
                self.assertIn(flag, b[0]['flags'])

    def test_rotation_off_without_rotation_keeps_translation(self):
        good = syn.make()
        p = syn.make_product(good)
        p.local_align, p.max_angle_deg = False, 0
        r = detect(good, p)
        self.assertEqual(0, r['angle'])
        self.assertEqual([1, 0, syn.OX, 0, 1, syn.OY], r['units'][0]['affine'])

    def test_distorted_image_all_ok(self):
        good = syn.make()
        product = syn.make_product(good)
        for k in (0.03, -0.03):
            r = detect(syn.barrel(good, k), product)
            self.assertEqual(9, len(r['units']), k)
            self.assertFalse(bad(r), describe(r))

    def test_scaled_image_units_found_and_wires_ok(self):
        import cv2
        good = syn.make()
        for s in (1.25, 0.8):
            p = syn.make_product(good)
            p.max_angle_deg, p.scale_range = 0, [0.7, 1.5]
            r = detect(cv2.resize(good, (0, 0), fx=s, fy=s, interpolation=cv2.INTER_AREA), p)
            self.assertEqual(9, len(r['units']), s)
            self.assertAlmostEqual(s, r['scale'], delta=0.05)
            self.assertEqual(18, len(r['wires']))
            self.assertFalse(bad(r), describe(r))
            # wire ends must land on the zoomed picture: affine carries the scale
            self.assertAlmostEqual(syn.OX * s, r['units'][0]['box'][0], delta=3)

    def test_pixel_params(self):
        a = Product(corridor_px=7, end_tol_px=10, shift_max_px=3.5, wire_width_px=2.5).params()
        self.assertEqual((7, 3, 4, 2, 6, 7), (a.half_w, a.bg_off, a.bg_w, a.d, a.ext, a.along))
        self.assertAlmostEqual(10, a.end_tol)
        self.assertAlmostEqual(3.5, a.shift_max)
        b = Product(corridor_px=14, wire_width_px=5).params()
        self.assertEqual(14, b.half_w)
        self.assertTrue(b.along > a.along and b.d >= a.d)

    def test_old_micrometre_product_is_converted(self):
        p = Product.from_dict({'name': 'old', 'pixel_um': 5, 'wire_diam_um': 25, 'corridor_um': 70, 'end_tol_um': 100,
                               'gap_max_um': 80, 'shift_max_um': 35, 'align_search_um': 50})
        self.assertEqual((5, 14, 20, 16, 7, 10), (p.wire_width_px, p.corridor_px, p.end_tol_px, p.gap_max_px,
                                                  p.shift_max_px, p.align_search_px))
        self.assertNotIn('pixel_um', p.to_dict())

    def test_mirrored_units_found(self):
        good = syn.make()
        product = syn.make_product(good)
        r = detect(np.ascontiguousarray(syn.make(missing=(4, 1))[:, ::-1]), product)
        self.assertEqual(9, len(r['units']))
        self.assertTrue(all(u.get('mirror') for u in r['units']))
        b = bad(r)
        self.assertEqual(1, len(b), describe(r))
        self.assertIn('MISSING', b[0]['flags'])
        product.allow_mirror = False   # only rotations: a 180° turn fits, but clearly worse than the mirror
        r2 = detect(np.ascontiguousarray(syn.make(missing=(4, 1))[:, ::-1]), product)
        self.assertFalse(any(u.get('mirror') for u in r2['units']))
        self.assertGreater(min(u['match'] for u in r['units']), max(u['match'] for u in r2['units']))

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

    def test_slope_wire_is_measured_and_a_step_is_not(self):
        a = Product(wire_width_px=2.5, corridor_px=7, end_tol_px=30, gap_max_px=40, z_ball=0).params()
        h, w, y0 = 48, 90, 24
        yy = np.arange(h, dtype=np.float32)[:, None]
        ramp = 70 + 5.0 * (yy - y0) * np.ones((1, w), np.float32)
        ramp[y0, 12:78] -= 16
        ramp[y0 - 1, 12:78] -= 7
        ramp[y0 + 1, 12:78] -= 7
        step = np.where(yy >= y0, 190.0, 70.0).astype(np.float32) * np.ones((1, w), np.float32)
        rng = np.random.default_rng(1)
        def run(img):
            noisy = np.clip(np.round(img + rng.normal(0, 3, img.shape)), 0, 255).astype(np.uint8)
            return inspect_wire(prepare(noisy, True), (12, y0), (77, y0), a)
        wire, edge = run(ramp), run(step)
        self.assertNotIn('MISSING', wire['flags'], wire['z_all'])
        self.assertIn('MISSING', edge['flags'], edge['z_all'])


class StoreTests(unittest.TestCase):
    def test_product_save_load(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = syn.make_product(syn.make())
            p.name, p.corridor_px = '型号 A/1', 7.5
            p.save(tmp)
            self.assertEqual(['型号 A_1'], list_products(tmp))
            q = Product.load(os.path.join(tmp, '型号 A_1'))
            self.assertEqual('型号 A/1', q.name)
            self.assertEqual(7.5, q.corridor_px)
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
