"""Toolbar and application icons, drawn as inline SVG (the app-gpt icons + product / add / edit)."""
from PySide6.QtCore import QByteArray, QRectF, Qt
from PySide6.QtGui import QIcon, QPainter, QPixmap
from PySide6.QtSvg import QSvgRenderer

LINE = '#3a4458'

# 24x24 line icons; {c} is the stroke colour
_TOOL = {
    'open': '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" fill="#f4c542" '
            'stroke="#b8860b"/>',
    'start': '<path d="M7 4.5v15l12-7.5z" fill="#18a848" stroke="#128a3a"/>',
    'stop': '<rect x="5.5" y="5.5" width="13" height="13" rx="2" fill="#e02424" stroke="#b81c1c"/>',
    'rerun': '<path d="M20 11a8 8 0 0 0-14.3-4.9M4 13a8 8 0 0 0 14.3 4.9" stroke="{c}"/>'
             '<path d="M5 2.5v4h4M19 21.5v-4h-4" stroke="{c}"/>',
    'report': '<path d="M6 2.5h8l4.5 4.5v13.5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-17a1 1 0 0 1 1-1z" fill="#fff" '
              'stroke="{c}"/><path d="M14 2.5V7h4.5M8 12h8M8 15.5h8M8 19h5" stroke="{c}"/>',
    'clear': '<path d="M14.5 4.5 20 10l-8.5 8.5H6L3.5 16z" fill="#ffd6d6" stroke="#e02424"/>'
             '<path d="M9 10l5.5 5.5M11.5 18.5H20" stroke="{c}"/>',
    'recent': '<circle cx="12" cy="12" r="8.5" stroke="{c}"/><path d="M12 7.5V12l3 2" stroke="#2b7bd6"/>',
    'product': '<rect x="6.5" y="6.5" width="11" height="11" rx="1.5" fill="#e9eef6" stroke="{c}"/>'
               '<rect x="9.5" y="9.5" width="5" height="5" rx=".5" fill="#8fa3c0" stroke="#8fa3c0"/>'
               '<path d="M9 6.5V3.5M12 6.5V3.5M15 6.5V3.5M9 20.5v-3M12 20.5v-3M15 20.5v-3M3.5 9h3M3.5 12h3M3.5 15h3'
               'M17.5 9h3M17.5 12h3M17.5 15h3" stroke="{c}"/>',
    'add': '<circle cx="12" cy="12" r="9" fill="#e8f6ee" stroke="#18a848"/><path d="M12 7.5v9M7.5 12h9" stroke="#18a848"/>',
    'edit': '<path d="M4 20h4L19.5 8.5l-4-4L4 16z" fill="#fff4cc" stroke="#b8860b"/><path d="M13.5 6.5l4 4" stroke="{c}"/>',
}

APP_SVG = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#25324a"/><stop offset="1" stop-color="#121926"/>
  </linearGradient>
</defs>
<rect x="8" y="8" width="240" height="240" rx="52" fill="url(#bg)"/>
<g fill="#c9d3e3">
  <rect x="34" y="66" width="34" height="22" rx="4"/><rect x="34" y="117" width="34" height="22" rx="4"/>
  <rect x="188" y="66" width="34" height="22" rx="4"/><rect x="188" y="117" width="34" height="22" rx="4"/>
</g>
<rect x="96" y="82" width="64" height="92" rx="8" fill="#e9eef6"/>
<rect x="106" y="92" width="44" height="72" rx="4" fill="#8fa3c0"/>
<g fill="none" stroke="#f2b134" stroke-width="5" stroke-linecap="round">
  <path d="M110 100 C 92 72, 70 70, 58 77"/><path d="M146 100 C 164 72, 186 70, 198 77"/>
  <path d="M110 128 C 92 112, 74 118, 58 128"/>
</g>
<path d="M146 128 C 164 112, 182 118, 198 128" fill="none" stroke="#ff3b3b" stroke-width="5" stroke-dasharray="8 8"/>
<circle cx="182" cy="176" r="42" fill="none" stroke="#5fd3ff" stroke-width="10"/>
<path d="M212 206 L 232 226" stroke="#5fd3ff" stroke-width="17" stroke-linecap="round"/>
</svg>'''


def _svg(name, color):
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke-width="1.9" '
            'stroke-linecap="round" stroke-linejoin="round">%s</svg>' % _TOOL[name].format(c=color))


def render(svg, size):
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    QSvgRenderer(QByteArray(svg.encode())).render(p, QRectF(0, 0, size, size))
    p.end()
    return pm


def _icon(svg, sizes):
    ic = QIcon()
    for s in sizes:
        ic.addPixmap(render(svg, s))
    return ic


def tool_icon(name, color=LINE):
    return _icon(_svg(name, color), (16, 18, 24, 32, 36, 48))


def app_icon():
    return _icon(APP_SVG, (16, 24, 32, 48, 64, 128, 256))
