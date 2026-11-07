import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core import ed25519, license as lic  # noqa: E402

SECRET = bytes((i * 7 + 3) % 256 for i in range(32))
PUB = ed25519.public_key(SECRET)
MACHINE = 'AAAA-BBBB-CCCC-DDDD'


def lic_data(**kw):
    d = {'id': 't1', 'customer': '测试', 'machine': MACHINE, 'edition': 'demo',
         'issued': '2026-01-01', 'expires': '2027-01-01'}
    d.update(kw)
    return d


def epoch(iso):
    import datetime
    return datetime.datetime.fromisoformat(iso).timestamp()


class LicenseTests(unittest.TestCase):
    def setUp(self):
        self._pub = lic.PUBLIC_KEY
        lic.PUBLIC_KEY = PUB
        self.dir = tempfile.mkdtemp()

    def tearDown(self):
        lic.PUBLIC_KEY = self._pub

    def _write(self, name, text):
        p = os.path.join(self.dir, name)
        with open(p, 'w', encoding='utf-8') as f:
            f.write(text)
        return p

    def test_sign_parse_roundtrip(self):
        text = json.dumps(lic.sign(lic_data(), SECRET), ensure_ascii=False)
        out = lic.parse(text, machine=MACHINE)
        self.assertEqual('测试', out.customer)
        self.assertFalse(out.trial)

    def test_parse_wrong_machine_rejected(self):
        text = json.dumps(lic.sign(lic_data(), SECRET))
        self.assertRaises(lic.LicenseError, lic.parse, text, 'EEEE-0000-0000-0000')

    def test_parse_tampered_rejected(self):
        text = json.dumps(lic.sign(lic_data(), SECRET)).replace('demo', 'full')
        self.assertRaises(lic.LicenseError, lic.parse, text, MACHINE)

    def test_check_expired_rejected(self):
        path = self._write('license.lic', json.dumps(lic.sign(lic_data(expires='2026-01-10'), SECRET)))
        self.assertIsNotNone(lic.check(path, now=epoch('2026-01-10'), machine=MACHINE))   # expiry day still valid
        with self.assertRaises(lic.LicenseError) as cm:
            lic.check(path, epoch('2026-01-11'), MACHINE)
        self.assertIn('到期', str(cm.exception))

    def test_check_clock_rollback_rejected(self):
        path = self._write('license.lic', json.dumps(lic.sign(lic_data(), SECRET)))
        lic.check(path, now=epoch('2026-06-01'), machine=MACHINE)
        self.assertRaises(lic.LicenseError, lic.check, path, epoch('2026-05-20'), MACHINE)

    def test_canonical_format(self):
        # bytes signed here must equal C# License.Canonical (json.dumps sort_keys, compact, raw UTF-8)
        d = {'id': 'abc', 'customer': '测试 A/B+"x"', 'machine': MACHINE, 'edition': 'full',
             'issued': '2026-01-01', 'expires': '2027-01-01'}
        self.assertEqual('{"customer":"测试 A/B+\\"x\\"","edition":"full","expires":"2027-01-01",'
                         '"id":"abc","issued":"2026-01-01","machine":"AAAA-BBBB-CCCC-DDDD"}',
                         lic.canonical(d).decode('utf-8'))


if __name__ == '__main__':
    unittest.main()
