"""Zoom / pan image view with vector overlays (units, wires, bond points, defect boxes), box drawing and
point picking: a simplified app-gpt ImageViewer."""
import cv2
import numpy as np
from PySide6.QtCore import QPointF, QRectF, Qt, Signal
from PySide6.QtGui import (QBrush, QColor, QFont, QImage, QPainter, QPainterPath, QPainterPathStroker, QPen, QPixmap,
                           QTransform)
from PySide6.QtWidgets import (QGraphicsEllipseItem, QGraphicsPathItem, QGraphicsPixmapItem, QGraphicsRectItem,
                               QGraphicsScene, QGraphicsSimpleTextItem, QGraphicsView)

from core.config import FROM_FLAG, type_color
from core.imgio import clahe

WIRE_OK = '#2ee6ff'
WIRE_BAD = '#ff4d4f'
UNIT_OK = '#67d69c'
BALL_COLOR = '#1f9fff'
BOND2_COLOR = '#ff00ff'
TEMPLATE_COLOR = '#1f9fff'


def gray_to_qimage(g):
    g = np.ascontiguousarray(g)
    return QImage(g.data, g.shape[1], g.shape[0], g.strides[0], QImage.Format_Grayscale8).copy()


def thumbnail(gray, side=160):
    h, w = gray.shape
    s = side / float(max(h, w))
    small = cv2.resize(gray, (max(1, int(w * s)), max(1, int(h * s))), interpolation=cv2.INTER_AREA)
    return QPixmap.fromImage(gray_to_qimage(clahe(small, 2.0)))


def _pen(color, width, style=Qt.SolidLine):
    pen = QPen(QColor(color), width, style)
    pen.setCosmetic(True)
    pen.setCapStyle(Qt.RoundCap)
    pen.setJoinStyle(Qt.RoundJoin)
    return pen


def _label(text, color, parent, pos, size=9, badge=True):
    """A screen-size text label (with a dark badge) attached to a scene item."""
    label = QGraphicsSimpleTextItem(text)
    label.setBrush(QColor(color))
    label.setFont(QFont('PingFang SC', size))
    holder = label
    if badge:
        holder = QGraphicsRectItem(label.boundingRect().adjusted(-3, -1, 3, 1))
        holder.setPen(QPen(Qt.NoPen))
        holder.setBrush(QColor(25, 28, 30, 220))
        label.setParentItem(holder)
    holder.setParentItem(parent)
    holder.setFlag(QGraphicsRectItem.ItemIgnoresTransformations)
    holder.setPos(*pos)
    return holder


class WireItem(QGraphicsPathItem):
    """One wire: the fitted curve (OK cyan / NG red); a missing wire is a dashed straight line."""

    def __init__(self, w, index, pixel_um, label=False):
        super().__init__()
        self.index = index
        self.missing = 'MISSING' in w['flags']
        pts = [w['p1'], w['p2']] if self.missing or len(w['curve']) < 2 else w['curve']
        path = QPainterPath(QPointF(*pts[0]))
        for p in pts[1:]:
            path.lineTo(QPointF(*p))
        self.setPath(path)
        self.color = QColor(WIRE_BAD if w['flags'] else WIRE_OK)
        self.base_width = 2.3 if w['flags'] else 1.8
        self.width = self.base_width
        self.setZValue(8)
        um = pixel_um or 1
        reasons = '、'.join(FROM_FLAG.get(f, f) for f in w['flags'])
        self.setToolTip('单元 %d · 线 %d  长度 %.0f µm  sweep %.1f%%  端点偏移 %.1f µm  z %.1f%s' % (
            w['unit'] + 1, w['index'] + 1, w['l'] * um, w['sweep_pct'], w['shift_px'] * um, w['z_all'],
            ('\n' + reasons) if reasons else ''))
        if reasons and label:
            _label(reasons, WIRE_BAD, self, (pts[0][0] + 6, pts[0][1] - 6))

    def shape(self):
        stroker = QPainterPathStroker()
        stroker.setWidth(7.0)
        return stroker.createStroke(self.path())

    def paint(self, painter, option, widget=None):
        painter.setRenderHint(QPainter.Antialiasing, True)
        glow = QColor(self.color)
        glow.setAlpha(50)
        painter.setPen(_pen(glow, self.width * 3.2))
        painter.drawPath(self.path())
        painter.setPen(_pen(self.color, self.width, Qt.DashLine if self.missing else Qt.SolidLine))
        painter.drawPath(self.path())

    def set_selected(self, on):
        self.width = 3.6 if on else self.base_width
        self.update()


class DefectItem(QGraphicsRectItem):
    def __init__(self, d):
        super().__init__(QRectF(d.x0, d.y0, d.x1 - d.x0, d.y1 - d.y0))
        self.defect = d
        pen = _pen(type_color(d.type), 2)
        if d.status == 'rejected':
            pen.setColor(QColor(150, 150, 150))
            pen.setStyle(Qt.DotLine)
        self.setPen(pen)
        self.setZValue(10)
        src = '手动' if d.source == 'manual' else (d.by or '%.2f' % d.score)
        self.label = QGraphicsSimpleTextItem('%s %s' % (d.type, src), self)
        self.label.setBrush(QBrush(pen.color()))
        self.label.setFont(QFont('PingFang SC', 11, QFont.Bold))
        self.label.setFlag(QGraphicsSimpleTextItem.ItemIgnoresTransformations)
        self.label.setPos(d.x0, d.y0)

    def set_selected(self, on):
        p = self.pen()
        p.setWidth(4 if on else 2)
        self.setPen(p)
        self.setBrush(QBrush(QColor(255, 255, 0, 40)) if on else QBrush(Qt.NoBrush))


class ImageViewer(QGraphicsView):
    box_drawn = Signal(int, int, int, int)
    point_picked = Signal(float, float)
    defect_clicked = Signal(str)
    wire_clicked = Signal(int)
    zoom_changed = Signal(float)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setScene(QGraphicsScene(self))
        self.setRenderHints(QPainter.Antialiasing | QPainter.SmoothPixmapTransform)
        self.setTransformationAnchor(QGraphicsView.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.AnchorViewCenter)
        self.setDragMode(QGraphicsView.ScrollHandDrag)
        self.setBackgroundBrush(QColor(30, 30, 30))
        self.pix = QGraphicsPixmapItem()
        self.pix.setTransformationMode(Qt.SmoothTransformation)
        self.scene().addItem(self.pix)
        self.gray = None
        self._key = None
        self._cache = {}
        self.mode = 'original'
        self.items = []           # defect boxes
        self._overlay = []        # units / wires / bond points (shown in 「导线高亮」)
        self._wire_items = []
        self._extra = []          # template box, pending points (always shown)
        self.show_marks = True
        self.tool = 'pan'         # pan | box | point
        self._drag_start = None
        self._rubber = None
        self._fit = True

    # ---- content
    def set_image(self, gray, key):
        self.gray = gray
        if key != self._key:
            self._cache = {}
        self._key = key
        self._refresh_pixmap()
        self.scene().setSceneRect(QRectF(0, 0, gray.shape[1], gray.shape[0]))
        self.fit()

    def clear(self):
        self.gray = self._key = None
        self.pix.setPixmap(QPixmap())
        self.set_defects([])
        self.set_result(None)
        self.set_extra()

    def set_mode(self, mode):
        self.mode = mode
        if self.gray is not None:
            self._refresh_pixmap()
        for it in self._overlay:
            it.setVisible(mode == 'wire')

    def _refresh_pixmap(self):
        if self.mode not in self._cache:
            img = clahe(self.gray, 2.0) if self.mode in ('enhanced', 'wire') else self.gray
            self._cache[self.mode] = QPixmap.fromImage(gray_to_qimage(img))
        self.pix.setPixmap(self._cache[self.mode])

    def _remove(self, items):
        for it in items:
            if it.scene() is self.scene():
                self.scene().removeItem(it)
        items.clear()

    def set_result(self, rec, always=False, labels=False):
        """Units, wire curves and bond points of a detection record (session record / detect() result);
        always = shown in every mode, labels = NG reasons next to the wires."""
        self._remove(self._overlay)
        self._wire_items = []
        if not rec or not rec.get('wires'):
            return
        wires = rec['wires']
        bad_units = {w['unit'] for w in wires if w['flags']}
        for u in rec.get('units') or []:
            x, y, w, h = u['box']
            bad = u['index'] in bad_units
            color = WIRE_BAD if bad else UNIT_OK
            box = self.scene().addRect(QRectF(x, y, w, h), _pen(color, 1.5, Qt.DashLine))
            box.setZValue(5)
            _label('单元 %d%s' % (u['index'] + 1, ' · NG' if bad else ''), color, box, (x + 5, y + 3))
            self._overlay.append(box)
        for i, w in enumerate(wires):
            it = WireItem(w, i, rec.get('pixel_um'), labels)
            self.scene().addItem(it)
            self._overlay.append(it)
            self._wire_items.append(it)
            for (cx, cy), color in ((w['ball'], BALL_COLOR), (w['p2'], BOND2_COLOR)):
                dot = self.scene().addEllipse(QRectF(cx - 4, cy - 4, 8, 8), _pen(color, 1.4))
                dot.setZValue(7)
                self._overlay.append(dot)
        for it in self._overlay:
            it.setVisible(always or self.mode == 'wire')

    def set_extra(self, template_box=None, points=()):
        """Template box (x, y, w, h) and picked points, shown in every mode."""
        self._remove(self._extra)
        if template_box:
            x, y, w, h = template_box
            r = self.scene().addRect(QRectF(x, y, w, h), _pen(TEMPLATE_COLOR, 2, Qt.DashLine))
            r.setZValue(6)
            r.setToolTip('单元模板')
            self._extra.append(r)
        for x, y in points:
            e = QGraphicsEllipseItem(-4, -4, 8, 8)
            e.setBrush(QColor('red'))
            e.setPen(QPen(Qt.NoPen))
            e.setFlag(QGraphicsEllipseItem.ItemIgnoresTransformations)
            e.setPos(x, y)
            e.setZValue(20)
            self.scene().addItem(e)
            self._extra.append(e)

    def select_wire(self, index, center=True):
        for it in self._wire_items:
            on = it.index == index
            it.set_selected(on)
            if on and center:
                self._center(it.path().boundingRect(), 1)

    def set_defects(self, defects, selected=None):
        self._remove(self.items)
        for d in defects:
            it = DefectItem(d)
            it.setVisible(self.show_marks)
            it.set_selected(d.id == selected)
            self.scene().addItem(it)
            self.items.append(it)

    def set_show_marks(self, on):
        self.show_marks = on
        for it in self.items:
            it.setVisible(on)

    def select(self, did, center=False):
        for it in self.items:
            on = it.defect.id == did
            it.set_selected(on)
            if on and center:
                self._center(it.rect(), 2)

    def _center(self, r, margin):
        r = QRectF(r.x(), r.y(), max(r.width(), 8), max(r.height(), 8))
        if r.width() < self.scene().width() * 0.9:
            self.fitInView(r.adjusted(-r.width() * margin, -r.height() * margin, r.width() * margin,
                                      r.height() * margin), Qt.KeepAspectRatio)
            self._fit = False
            self.zoom_changed.emit(self.transform().m11())

    # ---- zoom
    def fit(self):
        if self.gray is not None:
            self.fitInView(self.scene().sceneRect(), Qt.KeepAspectRatio)
            self._fit = True
            self.zoom_changed.emit(self.transform().m11())

    def actual_size(self):
        self.setTransform(QTransform())
        self._fit = False
        self.zoom_changed.emit(1.0)

    def zoom(self, f):
        s = self.transform().m11() * f
        if 0.02 < s < 40:
            self.scale(f, f)
            self._fit = False
            self.zoom_changed.emit(s)

    def wheelEvent(self, e):
        self.zoom(1.25 if e.angleDelta().y() > 0 else 0.8)

    def resizeEvent(self, e):
        super().resizeEvent(e)
        if self._fit:
            self.fit()

    # ---- tools / clicks
    def set_tool(self, tool):
        self.tool = tool
        self.setDragMode(QGraphicsView.ScrollHandDrag if tool == 'pan' else QGraphicsView.NoDrag)
        self.viewport().setCursor(Qt.OpenHandCursor if tool == 'pan' else Qt.CrossCursor)

    def set_draw_mode(self, on):
        self.set_tool('box' if on else 'pan')

    def mousePressEvent(self, e):
        if self.gray is not None and e.button() == Qt.LeftButton:
            p = self.mapToScene(e.position().toPoint())
            if self.tool == 'box':
                self._drag_start = p
                self._rubber = self.scene().addRect(QRectF(p, p), _pen('#30ff60', 2, Qt.DashLine))
                self._rubber.setZValue(20)
                return
            if self.tool == 'point':
                self.point_picked.emit(p.x(), p.y())
                return
            for it in self._wire_items:
                if it.isVisible() and it.shape().contains(p):
                    self.select_wire(it.index, center=False)
                    self.wire_clicked.emit(it.index)
                    break
            else:
                for it in reversed(self.items):
                    if it.isVisible() and it.rect().contains(p):
                        self.defect_clicked.emit(it.defect.id)
                        break
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._rubber is not None:
            self._rubber.setRect(QRectF(self._drag_start, self.mapToScene(e.position().toPoint())).normalized())
            return
        super().mouseMoveEvent(e)

    def mouseReleaseEvent(self, e):
        if self._rubber is not None:
            r = self._rubber.rect().intersected(self.scene().sceneRect())
            self.scene().removeItem(self._rubber)
            self._rubber = None
            if r.width() >= 4 and r.height() >= 4:
                self.box_drawn.emit(int(r.left()), int(r.top()), int(r.right()), int(r.bottom()))
            return
        super().mouseReleaseEvent(e)
