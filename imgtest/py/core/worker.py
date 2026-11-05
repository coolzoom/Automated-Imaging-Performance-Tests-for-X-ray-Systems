import os
import traceback
from concurrent.futures import FIRST_COMPLETED, ProcessPoolExecutor, wait

from PySide6.QtCore import QThread, Signal

_PRODUCT_CACHE = {}


def detect_one(path, product_dir):
    """Worker-process job: one image with the product stored in product_dir; errors come back as a dict."""
    from core.inspect import detect_image
    from core.product import CONFIG_FILE, TEMPLATE_FILE, Product
    try:
        key = (product_dir,) + tuple(os.path.getmtime(os.path.join(product_dir, f)) for f in (CONFIG_FILE, TEMPLATE_FILE))
        p = _PRODUCT_CACHE.get(key)
        if p is None:
            p = _PRODUCT_CACHE[key] = Product.load(product_dir)
        return detect_image(path, p)
    except Exception as e:  # reported per image, never kills the batch
        return {'error': '%s: %s' % (type(e).__name__, e), 'trace': traceback.format_exc()}


class BatchWorker(QThread):
    started = Signal(str)            # rel path now being detected
    result = Signal(str, dict)       # rel path, result dict
    progress = Signal(int, int)      # done, total
    done = Signal(bool)              # True if finished, False if stopped

    def __init__(self, root, rels, product_dir, workers=0, parent=None):
        super().__init__(parent)
        self.root, self.rels, self.product_dir, self.workers = root, list(rels), product_dir, workers
        self._stop = False

    def stop(self):
        self._stop = True

    def run(self):
        n = int(self.workers or 0) or max(1, (os.cpu_count() or 2) - 1)
        n = min(n, len(self.rels)) or 1
        self.progress.emit(0, len(self.rels))
        try:
            if n > 1:
                try:
                    ex = ProcessPoolExecutor(max_workers=n)
                except (OSError, NotImplementedError):
                    ex = None  # restricted environment: fall back to this thread
                if ex is not None:
                    with ex:
                        self._run_pool(ex, n)
                    return
            self._run_serial()
        finally:
            self.done.emit(not self._stop)

    def _run_pool(self, ex, n):
        # at most n images in flight, so 'started' means the image is really being detected
        total, k, todo, futs = len(self.rels), 0, iter(self.rels), {}

        def submit():
            r = next(todo, None)
            if r is not None:
                futs[ex.submit(detect_one, os.path.join(self.root, r), self.product_dir)] = r
                self.started.emit(r)

        for _ in range(n):
            submit()
        while futs and not self._stop:
            finished, _ = wait(futs, return_when=FIRST_COMPLETED)
            for fut in finished:
                r = futs.pop(fut)
                k += 1
                self.result.emit(r, fut.result())
                self.progress.emit(k, total)
                if not self._stop:
                    submit()
        for f in futs:
            f.cancel()

    def _run_serial(self):
        total = len(self.rels)
        for k, r in enumerate(self.rels, 1):
            if self._stop:
                break
            self.started.emit(r)
            self.result.emit(r, detect_one(os.path.join(self.root, r), self.product_dir))
            self.progress.emit(k, total)
