"""Offline license: a vendor-signed JSON file bound to one computer's machine code and an expiry date.

The license file is {"license": {...}, "sig": base64}. The signature covers the canonical JSON of
"license" and is checked against PUBLIC_KEY; only the holder of the private key can issue licenses
(tools/make_license.py).
"""
import base64
import datetime
import hashlib
import hmac
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

from . import ed25519

PUBLIC_KEY = bytes.fromhex('228c2e83575ded29b10bb68ba8412efa5593cfca598bbd025d47b29f1f7d4d09')
EDITIONS = {'demo': '演示版', 'full': '正式版'}
CLOCK_SLACK = 3600
TRIAL_DAYS = 7
_TRIAL_KEY = hashlib.sha256(b'wireinspect|trial|' + bytes.fromhex('9c41e7d20b5a86f3')).digest()
FIELDS = ('id', 'customer', 'machine', 'edition', 'issued', 'expires')


class LicenseError(Exception):
    pass


def required():
    """Licenses are enforced in packaged builds; WIREINSPECT_FORCE_LICENSE=1 enforces them when run from source."""
    return bool(getattr(sys, 'frozen', False) or os.environ.get('WIREINSPECT_FORCE_LICENSE'))


def state_dir():
    if sys.platform == 'darwin':
        base = Path.home() / 'Library/Application Support'
    elif sys.platform == 'win32':
        base = Path(os.environ.get('APPDATA') or Path.home())
    else:
        base = Path(os.environ.get('XDG_DATA_HOME') or Path.home() / '.local/share')
    return base / 'WireInspect'


def license_path():
    return state_dir() / 'license.lic'


def trial_paths():
    """Trial start is recorded in two places; the earliest valid record wins."""
    if sys.platform == 'darwin':
        second = Path.home() / 'Library/Preferences/.wireinspect_t'
    elif sys.platform == 'win32':
        second = Path(os.environ.get('LOCALAPPDATA') or Path.home()) / 'WireInspect' / '.t'
    else:
        second = Path.home() / '.config' / '.wireinspect_t'
    return [state_dir() / 'trial.dat', second]


def _platform_id():
    try:
        if sys.platform == 'darwin':
            out = subprocess.run(['ioreg', '-rd1', '-c', 'IOPlatformExpertDevice'], capture_output=True,
                                 text=True, timeout=10).stdout
            for line in out.splitlines():
                if 'IOPlatformUUID' in line:
                    return line.split('=', 1)[1].strip().strip('"')
        elif sys.platform == 'win32':
            import winreg
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Microsoft\Cryptography', 0,
                                winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as k:
                return winreg.QueryValueEx(k, 'MachineGuid')[0]
        else:
            for p in ('/etc/machine-id', '/var/lib/dbus/machine-id'):
                if os.path.exists(p):
                    return Path(p).read_text().strip()
    except Exception:
        pass
    return 'mac-%012x' % uuid.getnode()


_MACHINE = None


def machine_code():
    """16 hex digits in 4 groups, e.g. 3F2A-91C0-7B4D-E215."""
    global _MACHINE
    if _MACHINE is None:
        h = hashlib.sha256(('wireinspect|' + _platform_id().strip().upper()).encode()).hexdigest()[:16].upper()
        _MACHINE = '-'.join(h[i:i + 4] for i in range(0, 16, 4))
    return _MACHINE


def canonical(lic):
    return json.dumps({k: lic[k] for k in FIELDS}, ensure_ascii=False, sort_keys=True,
                      separators=(',', ':')).encode('utf-8')


def sign(lic, secret):
    return {'license': {k: lic[k] for k in FIELDS},
            'sig': base64.b64encode(ed25519.sign(secret, canonical(lic))).decode('ascii')}


class License:
    def __init__(self, data, expiry_epoch=None):
        self.data = data
        self.customer = data['customer']
        self.edition = data['edition']
        self.expires = datetime.date.fromisoformat(data['expires'])
        self.trial = expiry_epoch is not None
        self._expiry = expiry_epoch

    @property
    def demo(self):
        return self.edition != 'full'

    @property
    def edition_text(self):
        return EDITIONS.get(self.edition, self.edition)

    def expiry_epoch(self):
        """Valid through the whole expiry day, local time (trials: exactly TRIAL_DAYS after the start)."""
        if self._expiry is not None:
            return self._expiry
        end = datetime.datetime.combine(self.expires + datetime.timedelta(days=1), datetime.time())
        return end.timestamp()

    def days_left(self, now=None):
        now = time.time() if now is None else now
        return max(0, math.ceil((self.expiry_epoch() - now) / 86400))

    def summary(self, now=None):
        if self.trial:
            return '%s · %d 天试用，至 %s（剩 %d 天）' % (
                self.edition_text, TRIAL_DAYS,
                datetime.datetime.fromtimestamp(self.expiry_epoch()).strftime('%Y-%m-%d %H:%M'), self.days_left(now))
        return '%s · %s · 有效期至 %s（剩 %d 天）' % (self.customer, self.edition_text, self.expires.isoformat(),
                                                self.days_left(now))


def parse(text, machine=None):
    """Verify signature, machine binding and fields; does not look at the clock."""
    try:
        doc = json.loads(text)
        lic, sig = doc['license'], base64.b64decode(doc['sig'], validate=True)
        if not all(isinstance(lic.get(k), str) for k in FIELDS):
            raise ValueError
        msg = canonical(lic)
    except Exception:
        raise LicenseError('授权文件格式不正确。')
    if not ed25519.verify(PUBLIC_KEY, msg, sig):
        raise LicenseError('授权文件无效（签名校验失败）。')
    try:
        out = License(lic)
    except ValueError:
        raise LicenseError('授权文件格式不正确。')
    if lic['machine'].upper() != (machine or machine_code()):
        raise LicenseError('授权文件不属于这台电脑（机器码不符）。')
    return out


def _check_clock(now, directory):
    """Reject a clock set back to before the last seen time; remember the latest time seen."""
    path = directory / 'clock.json'
    last = 0.0
    try:
        last = float(json.loads(path.read_text(encoding='utf-8'))['last'])
        if not math.isfinite(last):
            last = 0.0
    except Exception:
        pass
    if now < last - CLOCK_SLACK:
        raise LicenseError('系统时间异常（早于上次使用时间），请恢复正确时间后重试。')
    if now > last:
        try:
            directory.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix('.tmp')
            tmp.write_text(json.dumps({'last': now}), encoding='utf-8')
            os.replace(tmp, path)
        except OSError:
            pass
    return max(now, last)


def check(path=None, now=None, machine=None):
    """Return the valid License or raise LicenseError with a message for the user."""
    path = Path(path) if path is not None else license_path()
    now = time.time() if now is None else float(now)
    if not path.exists():
        raise LicenseError('本机尚未授权。')
    try:
        text = path.read_text(encoding='utf-8')
    except (OSError, UnicodeDecodeError):
        raise LicenseError('无法读取授权文件。')
    lic = parse(text, machine)
    seen = _check_clock(now, path.parent)
    if seen >= lic.expiry_epoch():
        raise LicenseError('授权已于 %s 到期。' % lic.expires.isoformat())
    return lic


def install(src, now=None, machine=None):
    """Validate a license file chosen by the user and copy it into place; returns the License."""
    try:
        text = Path(src).read_text(encoding='utf-8')
    except (OSError, UnicodeDecodeError):
        raise LicenseError('无法读取所选文件。')
    lic = parse(text, machine)
    now = time.time() if now is None else float(now)
    if now >= lic.expiry_epoch():
        raise LicenseError('该授权文件已于 %s 到期。' % lic.expires.isoformat())
    dst = license_path()
    dst.parent.mkdir(parents=True, exist_ok=True)
    tmp = dst.with_suffix('.tmp')
    tmp.write_text(text, encoding='utf-8')
    os.replace(tmp, dst)
    return check(dst, now, machine)


def _trial_mac(first, machine):
    return hmac.new(_TRIAL_KEY, ('%.3f|%s' % (first, machine)).encode(), hashlib.sha256).hexdigest()


def _trial_first(machine):
    """Earliest recorded trial start; None if never started. Raises if a record was tampered with."""
    firsts = []
    for p in trial_paths():
        if not p.exists():
            continue
        try:
            rec = json.loads(p.read_text(encoding='utf-8'))
            first = float(rec['first'])
            ok = math.isfinite(first) and hmac.compare_digest(str(rec['mac']), _trial_mac(first, machine))
        except Exception:
            ok = False
        if not ok:
            raise LicenseError('演示期记录无效，请导入授权文件。')
        firsts.append(first)
    return min(firsts) if firsts else None


def _trial_license(first, machine):
    exp = first + TRIAL_DAYS * 86400
    data = {'id': 'trial', 'customer': '试用用户', 'machine': machine, 'edition': 'demo',
            'issued': datetime.date.fromtimestamp(first).isoformat(),
            'expires': datetime.date.fromtimestamp(exp).isoformat()}
    return License(data, expiry_epoch=exp)


def trial_status(now=None, machine=None):
    """('available', None) | ('active', License) | ('ended', message)."""
    machine = machine or machine_code()
    now = time.time() if now is None else float(now)
    try:
        first = _trial_first(machine)
        if first is None:
            return 'available', None
        seen = _check_clock(now, state_dir())
    except LicenseError as e:
        return 'ended', str(e)
    lic = _trial_license(first, machine)
    if seen >= lic.expiry_epoch():
        return 'ended', '%d 天演示期已结束，请导入授权文件。' % TRIAL_DAYS
    return 'active', lic


def start_trial(now=None, machine=None):
    machine = machine or machine_code()
    now = time.time() if now is None else float(now)
    state, value = trial_status(now, machine)
    if state == 'ended':
        raise LicenseError(value)
    if state == 'active':
        return value
    rec = json.dumps({'first': round(now, 3), 'mac': _trial_mac(round(now, 3), machine)})
    written = 0
    for p in trial_paths():
        try:
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(rec, encoding='utf-8')
            written += 1
        except OSError:
            pass
    if not written:
        raise LicenseError('无法写入演示期记录。')
    _check_clock(now, state_dir())
    return _trial_license(round(now, 3), machine)


def refresh(lic, now=None):
    """Re-validate a running license or trial; raises LicenseError when it is no longer valid."""
    if lic.trial:
        state, value = trial_status(now)
        if state != 'active':
            raise LicenseError(value if state == 'ended' else '演示期记录丢失。')
        return value
    return check(now=now)
