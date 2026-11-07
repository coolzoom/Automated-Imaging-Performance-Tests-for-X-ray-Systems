"""X-ray bond-wire inspection and review (PySide2).

run: .venv/bin/python main.py [folder]
"""
import multiprocessing
import os
import sys

APP_DIR = os.path.dirname(os.path.abspath(__file__))
if APP_DIR not in sys.path:
    sys.path.insert(0, APP_DIR)

import qtcompat  # noqa: F401,E402  PySide2 on Win7; PySide6 alias on dev machines


def main():
    from PySide2.QtWidgets import QApplication
    from ui.icons import app_icon
    from ui.main_window import APP_TITLE, MainWindow

    app = QApplication(sys.argv)
    app.setApplicationName(APP_TITLE)
    app.setWindowIcon(app_icon())
    from ui.theme import apply_theme
    apply_theme(app)
    w = MainWindow()
    from core import license as lic
    if lic.required():
        from ui.license_dialog import obtain
        if obtain(w) is None:
            return 1
    w.showMaximized()
    if len(sys.argv) > 1 and os.path.isdir(sys.argv[1]):
        w.open_folder(sys.argv[1])
    return app.exec_()


if __name__ == '__main__':
    multiprocessing.freeze_support()
    sys.exit(main())
