"""PySide2 is the Win7 target; on dev machines without PySide2 wheels (e.g. arm64 macOS),
fall back to PySide6 by aliasing it under the PySide2 name. Code stays written against the
PySide2 API (exec_(), QtWidgets.QAction fallback is handled where imported).
"""
import sys

try:
    import PySide2  # noqa: F401
except ImportError:
    import PySide6  # noqa: F401  — clear error if neither is installed
    sys.modules.setdefault('PySide2', PySide6)
    for _mod in ('QtCore', 'QtGui', 'QtWidgets', 'QtSvg', 'QtNetwork', 'QtPrintSupport'):
        try:
            sys.modules.setdefault('PySide2.' + _mod, __import__('PySide6.' + _mod, fromlist=['*']))
        except ImportError:
            pass
