"""Dark navy theme matching the C# app (dark Fluent + #0e2138 chrome, #274a75 accents, #8fa8c8 dim text)."""
from PySide2.QtGui import QColor, QPalette
from PySide2.QtWidgets import QApplication

_BG = '#0e2138'
_BG2 = '#0b1c33'
_PANEL = '#14273f'
_LINE = '#274a75'
_TEXT = '#d7e0ee'
_DIM = '#8fa8c8'
_ACCENT = '#1f6fd1'

QSS = '''
QWidget { background: %(BG)s; color: %(TEXT)s; font-size: 13px; }
QMainWindow, QDialog { background: %(BG)s; }
QToolBar { background: %(BG2)s; border: 0; spacing: 4px; padding: 3px; }
QToolBar::separator { background: %(LINE)s; width: 1px; margin: 4px 2px; }
QStatusBar { background: %(BG2)s; color: %(DIM)s; }
QStatusBar::item { border: 0; }
QMenuBar, QMenu { background: %(BG2)s; color: %(TEXT)s; }
QMenu::item:selected { background: %(ACCENT)s; }
QPushButton, QToolButton {
    background: %(PANEL)s; color: %(TEXT)s; border: 1px solid %(LINE)s;
    border-radius: 4px; padding: 4px 10px; }
QPushButton:hover, QToolButton:hover { background: #1c3a61; }
QPushButton:pressed, QToolButton:pressed, QToolButton:checked { background: %(ACCENT)s; }
QPushButton:disabled, QToolButton:disabled { color: #4a5c78; background: #101e30; }
QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox, QPlainTextEdit, QTableWidget, QListWidget, QTreeWidget {
    background: %(BG2)s; color: %(TEXT)s; border: 1px solid %(LINE)s; border-radius: 3px; }
QComboBox::drop-down { border: 0; width: 20px; }
QComboBox QAbstractItemView { background: %(BG2)s; color: %(TEXT)s; selection-background-color: %(ACCENT)s; }
QTreeWidget::item:selected, QListWidget::item:selected, QTableWidget::item:selected { background: %(ACCENT)s; }
QTreeWidget::item:hover, QListWidget::item:hover { background: #1c3a61; }
QHeaderView::section { background: #1a3357; color: %(TEXT)s; border: 0; padding: 3px; }
QTabWidget::pane { border: 1px solid %(LINE)s; }
QTabBar::tab { background: %(PANEL)s; color: %(DIM)s; padding: 5px 12px; border: 1px solid %(LINE)s; }
QTabBar::tab:selected { background: #1c3a61; color: %(TEXT)s; }
QGroupBox { border: 1px solid %(LINE)s; border-radius: 4px; margin-top: 12px; padding-top: 8px; }
QGroupBox::title { subcontrol-origin: margin; left: 8px; color: %(DIM)s; }
QProgressBar { background: %(BG2)s; border: 1px solid %(LINE)s; border-radius: 3px; text-align: center; color: %(TEXT)s; }
QProgressBar::chunk { background: %(ACCENT)s; }
QSplitter::handle { background: #1c3a61; }
QSplitter::handle:horizontal { width: 3px; }
QSplitter::handle:vertical { height: 3px; }
QScrollBar { background: %(BG2)s; }
QScrollBar:vertical { width: 10px; }
QScrollBar:horizontal { height: 10px; }
QScrollBar::handle { background: %(LINE)s; border-radius: 4px; min-height: 24px; min-width: 24px; }
QScrollBar::handle:hover { background: %(ACCENT)s; }
QScrollBar::add-line, QScrollBar::sub-line { height: 0; width: 0; }
QCheckBox, QRadioButton { color: %(TEXT)s; spacing: 6px; }
QLabel { color: %(TEXT)s; }
QToolTip { background: %(BG2)s; color: %(TEXT)s; border: 1px solid %(LINE)s; }
QGraphicsView { background: #05080f; border: 1px solid %(LINE)s; }
''' % {'BG': _BG, 'BG2': _BG2, 'PANEL': _PANEL, 'LINE': _LINE, 'TEXT': _TEXT, 'DIM': _DIM, 'ACCENT': _ACCENT}


def apply_theme(app):
    app.setStyle('Fusion')
    p = QPalette()
    for role, c in ((QPalette.Window, _BG), (QPalette.WindowText, _TEXT), (QPalette.Base, _BG2),
                    (QPalette.AlternateBase, _PANEL), (QPalette.Text, _TEXT), (QPalette.Button, _PANEL),
                    (QPalette.ButtonText, _TEXT), (QPalette.ToolTipBase, _BG2), (QPalette.ToolTipText, _TEXT),
                    (QPalette.Highlight, _ACCENT), (QPalette.HighlightedText, '#ffffff'),
                    (QPalette.Link, _ACCENT)):
        p.setColor(role, QColor(c))
    if hasattr(QPalette, 'PlaceholderText'):   # Qt >= 5.12
        p.setColor(QPalette.PlaceholderText, QColor(_DIM))
    p.setColor(QPalette.Disabled, QPalette.Text, QColor('#4a5c78'))
    p.setColor(QPalette.Disabled, QPalette.ButtonText, QColor('#4a5c78'))
    app.setPalette(p)
    app.setStyleSheet(QSS)
