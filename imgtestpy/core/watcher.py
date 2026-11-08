"""Hot-folder watching for live inspection next to the X-ray vendor software.

Polling (no OS file events: they are unreliable on network shares). A file is handed over only when
its size and mtime are unchanged between two polls and it has not been written for `settle` seconds,
so half-written images are never read. Originals are only read, never moved, renamed or locked.
Already detected files (session.json) are skipped, so a restart does not re-process anything.
"""
import csv
import datetime
import os
import time

from .session import scan

MAX_RETRIES = 3


def is_complete(path):
    """JPEG / PNG end markers present (a truncated JPEG still decodes, with a grey bottom part)."""
    ext = os.path.splitext(path)[1].lower()
    try:
        with open(path, 'rb') as f:
            f.seek(0, 2)
            n = f.tell()
            f.seek(max(0, n - 4096))
            tail = f.read()
    except OSError:
        return False
    if ext in ('.jpg', '.jpeg'):
        return b'\xff\xd9' in tail
    if ext == '.png':
        return b'IEND' in tail
    return True


class FolderWatcher:
    def __init__(self, session, settle=1.0):
        self.session = session
        self.settle = settle
        self.seen = {}      # rel -> (size, mtime) at the previous poll
        self.queued = set()
        self.retries = {}

    def poll(self):
        """Newly complete image files (relative paths), oldest first."""
        now = time.time()
        ready = []
        for rel in scan(self.session.root):
            if rel in self.queued or self.session.has_detection(rel):
                continue
            try:
                st = os.stat(os.path.join(self.session.root, rel))
            except OSError:
                continue
            sig = (st.st_size, st.st_mtime)
            if st.st_size > 0 and self.seen.get(rel) == sig and now - st.st_mtime >= self.settle \
                    and is_complete(os.path.join(self.session.root, rel)):
                ready.append((st.st_mtime, rel))
            self.seen[rel] = sig
        ready.sort()
        out = [r for _, r in ready]
        self.queued.update(out)
        return out

    def failed(self, rel):
        """A queued file could not be read; True if it should be retried on a later poll."""
        self.queued.discard(rel)
        self.seen.pop(rel, None)
        self.retries[rel] = self.retries.get(rel, 0) + 1
        return self.retries[rel] <= MAX_RETRIES

    def done(self, rel):
        self.queued.discard(rel)
        self.retries.pop(rel, None)


def log_result(session, rel, verdict, defects, product=''):
    """Append one line to <folder>/.xray_wire/watch_log.csv (traceability for production)."""
    from .session import STATE_DIR
    path = os.path.join(session.root, STATE_DIR, 'watch_log.csv')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    new = not os.path.isfile(path)
    with open(path, 'a', newline='', encoding='utf-8-sig') as f:
        w = csv.writer(f)
        if new:
            w.writerow(['时间', '图片', '结果', '疑似缺陷数', '缺陷类型', '产品'])
        w.writerow([datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'), rel, verdict, len(defects),
                    '/'.join(sorted({d.type for d in defects})), product])
