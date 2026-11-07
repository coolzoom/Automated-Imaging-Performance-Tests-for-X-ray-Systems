"""Issue a license file for one customer computer (vendor side only) - the "注册机".

  python3 tools/make_license.py          interactive (run.sh 6 / run.bat 6)
  python3 tools/make_license.py --machine 3F2A-91C0-7B4D-E215 --customer "某某公司" --days 30
  python3 tools/make_license.py --machine ... --customer ... --expires 2027-06-30 --edition full

The private key defaults to ../license_keys/vendor_ed25519.key (outside this repo). Keep it secret
and backed up: losing it means new licenses can no longer be issued for already shipped programs.
"""
import argparse
import csv
import datetime
import json
import os
import re
import sys
import uuid

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, APP_DIR)

from core import ed25519, license  # noqa: E402

KEY_NAME = 'vendor_ed25519.key'
KEY_DIR = os.path.join(os.path.dirname(APP_DIR), 'license_keys')
# packaged tool ships the key at <exe>/license_keys/vendor_ed25519.key (same as the C# layout)
_BESIDE_EXE = os.path.join(os.path.dirname(sys.executable if getattr(sys, 'frozen', False) else __file__),
                           'license_keys')
DEFAULT_KEY = (os.path.join(_BESIDE_EXE, KEY_NAME) if os.path.isfile(os.path.join(_BESIDE_EXE, KEY_NAME))
               else os.path.join(KEY_DIR, KEY_NAME))
ISSUED_DIR = os.path.join(os.path.dirname(DEFAULT_KEY), 'issued')
MACHINE_RE = r'[0-9A-F]{4}(-[0-9A-F]{4}){3}'


class KeyFileError(Exception):
    pass


def load_key(path):
    if not os.path.isfile(path):
        raise KeyFileError('未找到签名私钥: %s\n请把 %s 放到该位置，或用 --key 指定。' % (path, KEY_NAME))
    try:
        with open(path) as f:
            secret = bytes.fromhex(f.read().strip())
    except (OSError, ValueError):
        raise KeyFileError('私钥文件无法读取: %s' % path)
    if len(secret) != 32 or ed25519.public_key(secret) != license.PUBLIC_KEY:
        raise KeyFileError('私钥与程序内置公钥不匹配: %s' % path)
    return secret


def find_machine(text):
    """Machine code from user input; tolerates lowercase, spaces and surrounding text."""
    m = re.search(r'[0-9A-F]{4}[\s-]*[0-9A-F]{4}[\s-]*[0-9A-F]{4}[\s-]*[0-9A-F]{4}', text.upper())
    if not m:
        return None
    h = re.sub(r'[^0-9A-F]', '', m.group(0))
    return '-'.join(h[i:i + 4] for i in range(0, 16, 4))


def log_issued(out_dir, lic, path):
    log = os.path.join(out_dir, '签发记录.csv')
    new = not os.path.exists(log)
    with open(log, 'a', newline='', encoding='utf-8-sig') as f:
        w = csv.writer(f)
        if new:
            w.writerow(['签发日期', '客户', '机器码', '版本', '到期日', '授权编号', '文件'])
        w.writerow([lic['issued'], lic['customer'], lic['machine'], license.EDITIONS[lic['edition']],
                    lic['expires'], lic['id'], os.path.basename(path)])


def issue(secret, machine, customer, expires, edition, out=None, out_dir=None, quiet=False):
    """Sign and write the license; returns (path, license dict)."""
    today = datetime.date.today()
    lic = {'id': uuid.uuid4().hex[:12], 'customer': customer, 'machine': machine,
           'edition': edition, 'issued': today.isoformat(), 'expires': expires.isoformat()}
    text = json.dumps(license.sign(lic, secret), ensure_ascii=False, indent=1)
    license.parse(text, machine)
    if not out:
        out_dir = out_dir or ISSUED_DIR
        os.makedirs(out_dir, exist_ok=True)
        name = 'license_%s_%s_%s_%s.lic' % (re.sub(r'[\\/:*?"<>|\s]+', '_', customer), machine[:4], edition,
                                             expires.strftime('%Y%m%d'))
        out = os.path.join(out_dir, name)
    with open(out, 'w', encoding='utf-8') as f:
        f.write(text + '\n')
    log_issued(os.path.dirname(os.path.abspath(out)), lic, out)
    if quiet:
        return out, lic
    print('\n已生成授权文件: %s' % os.path.abspath(out))
    print('  客户 %s · %s · 机器码 %s · 有效期至 %s（含当天）' % (customer, license.EDITIONS[edition], machine,
                                                     expires.isoformat()))
    return out, lic


def ask(prompt, check, default=None):
    while True:
        try:
            v = input(prompt + ('[%s] ' % default if default else '')).strip()
        except EOFError:
            sys.exit(1)
        v = v or (default or '')
        err = check(v)
        if not err:
            return v
        print('  !! ' + err)


def parse_expiry(v, today):
    if re.fullmatch(r'\d+', v):
        if int(v) < 1:
            raise ValueError('天数至少为 1')
        return today + datetime.timedelta(days=int(v) - 1)
    d = datetime.date.fromisoformat(v)
    if d < today:
        raise ValueError('到期日早于今天')
    return d


def interactive(key):
    print('=========== 签发授权文件（注册机） ===========')
    try:
        secret = load_key(key)
    except KeyFileError as e:
        sys.exit('!! %s' % e)
    print('私钥: %s' % key)
    today = datetime.date.today()

    def chk_machine(v):
        return None if re.fullmatch(MACHINE_RE, v.upper()) else '机器码格式应为 XXXX-XXXX-XXXX-XXXX（客户授权窗口里显示的）'

    def chk_expiry(v):
        try:
            parse_expiry(v, today)
        except ValueError as e:
            return str(e) if '早于' in str(e) or '至少' in str(e) else '请输入天数（如 30）或日期（如 2027-06-30）'

    machine = ask('客户机器码: ', chk_machine).upper()
    customer = ask('客户名称（显示在程序和报告水印里）: ', lambda v: None if v else '不能为空')
    edition = ask('版本 1=演示版 2=正式版: ', lambda v: None if v in ('1', '2') else '请输入 1 或 2', '1')
    edition = 'demo' if edition == '1' else 'full'
    expires = parse_expiry(ask('有效天数或到期日 YYYY-MM-DD: ', chk_expiry, '30'), today)
    print('\n即将签发: %s · %s · %s · 有效期至 %s' % (customer, license.EDITIONS[edition], machine, expires))
    if ask('确认? (y/n): ', lambda v: None if v.lower() in ('y', 'n') else '请输入 y 或 n', 'y').lower() != 'y':
        print('已取消')
        return
    issue(secret, machine, customer, expires, edition)
    print('把这个 .lic 文件发给客户，在授权窗口点“导入授权文件”即可。')


def main():
    ap = argparse.ArgumentParser(description='签发离线授权文件；不带参数运行为交互模式')
    ap.add_argument('--machine', help='客户电脑上显示的机器码')
    ap.add_argument('--customer', help='客户名称（会显示在程序和报告水印里）')
    g = ap.add_mutually_exclusive_group()
    g.add_argument('--days', type=int, help='从今天起有效天数（含今天）')
    g.add_argument('--expires', help='到期日 YYYY-MM-DD（当天仍可用）')
    ap.add_argument('--edition', choices=sorted(license.EDITIONS), default='demo')
    ap.add_argument('--key', default=DEFAULT_KEY)
    ap.add_argument('--out', help='输出文件，默认放在 %s' % ISSUED_DIR)
    a = ap.parse_args()
    has_days = a.days is not None
    if not (a.machine or a.customer or has_days or a.expires):
        return interactive(a.key)
    if not (a.machine and a.customer and (has_days or a.expires)):
        ap.error('需要 --machine、--customer 以及 --days 或 --expires（或不带参数进入交互模式）')
    machine = a.machine.strip().upper()
    if not re.fullmatch(MACHINE_RE, machine):
        ap.error('机器码格式应为 XXXX-XXXX-XXXX-XXXX')
    try:
        expires = parse_expiry(str(a.days) if has_days else a.expires, datetime.date.today())
    except ValueError as e:
        ap.error(str(e))
    try:
        secret = load_key(a.key)
    except KeyFileError as e:
        sys.exit('!! %s' % e)
    issue(secret, machine, a.customer.strip(), expires, a.edition, a.out)


if __name__ == '__main__':
    main()
