import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.defect import Defect  # noqa: E402
from core.session import Session  # noqa: E402
from core.watcher import MAX_RETRIES, FolderWatcher, is_complete, log_result  # noqa: E402


def jpg(complete):
    b = bytearray(8192)
    b[0:2] = b'\xff\xd8'
    if complete:
        b[-2:] = b'\xff\xd9'
    return bytes(b)


def put(path, data):
    with open(path, 'wb') as f:
        f.write(data)


class WatcherTests(unittest.TestCase):
    def test_stable_complete_file_only(self):
        with tempfile.TemporaryDirectory() as d:
            put(os.path.join(d, 'ok.jpg'), jpg(True))
            put(os.path.join(d, 'half.jpg'), jpg(False))   # no FFD9 end marker
            s = Session(d)
            w = FolderWatcher(s, settle=0)
            self.assertEqual([], w.poll())          # first poll only records signatures
            self.assertEqual(['ok.jpg'], w.poll())  # stable + complete on the second poll
            self.assertEqual([], w.poll())          # queued files are not handed out twice
            self.assertEqual([], w.poll())          # half.jpg never becomes ready

    def test_skips_detected_and_retries_cap(self):
        with tempfile.TemporaryDirectory() as d:
            put(os.path.join(d, 'done.png'), jpg(True))
            s = Session(d)
            s.rec('done.png')['detected_at'] = '2026-01-01 00:00:00'
            w = FolderWatcher(s, settle=0)
            w.poll()
            self.assertEqual([], w.poll())          # has_detection -> skipped
            for i in range(MAX_RETRIES):
                self.assertTrue(w.failed('x.jpg'))  # 1..3 may be retried on a later poll
            self.assertFalse(w.failed('x.jpg'))     # 4th -> caller records the error permanently
            w.done('x.jpg')
            self.assertTrue(w.failed('x.jpg'))      # done resets the counter

    def test_is_complete_markers(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'a.png')
            put(p, b'\x89PNG' + b'x' * 100 + b'IEND' + b'\x00' * 8)
            self.assertTrue(is_complete(p))
            put(p, b'\x89PNG' + b'x' * 100)
            self.assertFalse(is_complete(p))
            put(os.path.join(d, 'a.bmp'), b'BM' + b'x' * 100)
            self.assertTrue(is_complete(os.path.join(d, 'a.bmp')))   # other formats pass

    def test_log_result_writes_header_and_row(self):
        with tempfile.TemporaryDirectory() as d:
            s = Session(d)
            log_result(s, 'sub/a.jpg', 'NG', [Defect(0, 0, 1, 1, '断线'), Defect(0, 0, 1, 1, '第一焊点异常')], 'P1')
            path = os.path.join(d, '.xray_wire', 'watch_log.csv')
            raw = open(path, 'rb').read()
            self.assertEqual(b'\xef\xbb\xbf', raw[:3])               # utf-8-sig for Excel
            lines = raw.decode('utf-8-sig').splitlines()
            self.assertEqual('时间,图片,结果,疑似缺陷数,缺陷类型,产品', lines[0])
            self.assertIn('sub/a.jpg,NG,2,', lines[1])
            self.assertTrue(lines[1].endswith(',P1'))
            log_result(s, 'b.jpg', 'OK', [], 'P1')
            self.assertEqual(3, len(open(path, encoding='utf-8-sig').read().splitlines()))


if __name__ == '__main__':
    unittest.main()
