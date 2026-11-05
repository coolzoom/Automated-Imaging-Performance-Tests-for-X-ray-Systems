import os

from .imgio import IMAGE_EXTS

SKIP_DIRS = ('report', 'report_wire')


def scan(root, empty=None):
    """Image paths under root, relative and '/'-separated, in folder-tree order (files first, then
    sub-folders). 0-byte files (broken unzip / copy) are left out; pass a list as `empty` to collect them."""
    out = []

    def walk(d, rel):
        try:
            entries = sorted(os.listdir(d))
        except OSError:
            return
        dirs = []
        for name in entries:
            p = os.path.join(d, name)
            if name.startswith('.'):
                continue
            if os.path.isdir(p):
                if name not in SKIP_DIRS:
                    dirs.append(name)
                continue
            if not name.lower().endswith(IMAGE_EXTS):
                continue
            r = name if not rel else rel + '/' + name
            try:
                size = os.path.getsize(p)
            except OSError:
                continue
            if size == 0:
                if empty is not None:
                    empty.append(r)
            else:
                out.append(r)
        for name in dirs:
            walk(os.path.join(d, name), name if not rel else rel + '/' + name)

    walk(root, '')
    return out
