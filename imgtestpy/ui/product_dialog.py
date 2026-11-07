"""New / edit product: box one unit -> template, find wires automatically, fix them by hand, set the
pixel size and thresholds, preview on the whole image, save to products/<name>/."""
import os

from PySide2.QtCore import Qt
from PySide2.QtWidgets import (QCheckBox, QComboBox, QDialog, QDialogButtonBox, QDoubleSpinBox, QFormLayout, QGroupBox,
                               QHBoxLayout, QLabel, QLineEdit, QListWidget, QListWidgetItem, QMessageBox, QPushButton,
                               QSpinBox, QSplitter, QVBoxLayout, QWidget)

from core.autowires import auto_wires
from core.config import PRODUCTS_DIR
from core.inspect import detect, find_units
from core.product import Product, Wire, safe_name
from ui.viewer import ImageViewer

# product attribute, label, minimum, maximum, step, decimals
NUMBERS = [('match_thr', '模板匹配阈值', 0.2, 0.99, 0.05, 2), ('wire_width_px', '线宽 px', 0.5, 50, 0.5, 1),
           ('corridor_px', '搜索走廊半宽 px', 1, 200, 1, 1), ('end_tol_px', '端点缺口容限 px', 0, 500, 1, 1),
           ('gap_max_px', '中段断开容限 px', 0, 500, 1, 1), ('shift_max_px', '焊点偏移上限 px', 0, 500, 0.5, 1),
           ('sweep_max_pct', 'sweep 上限 %', 0, 100, 0.5, 1), ('z_missing', 'z 缺线阈值', 0, 100, 0.5, 1),
           ('z_seg', 'z 分段阈值', 0, 100, 0.5, 1), ('z_ball', 'z 焊点阈值', 0, 100, 0.5, 1),
           ('max_angle_deg', '单元旋转范围 ±°', 0, 180, 5, 1), ('align_search_px', '局部校正搜索 px', 0, 200, 1, 1)]
TIPS = {'max_angle_deg': '单元相对模板最多转多少度：180 = 任意方向（每个单元各自找角度），0 = 只找正放的单元（最快）',
        'align_search_px': '每个单元分 2×2 块在这个范围内重新匹配，拟合仿射，修正局部扭曲 / 缩放'}


class ProductDialog(QDialog):
    def __init__(self, gray=None, product=None, parent=None):
        super().__init__(parent)
        self.setWindowTitle('编辑产品' if product else '新建产品')
        self.resize(1250, 800)
        self.editing = product
        self.p = product.clone() if product else Product()
        self.gray = gray
        self.box = None          # template box (x, y, w, h) in the shown image
        self.picks = []
        self.preview = None
        if gray is None and self.p.template is not None:
            self.gray = self.p.template
        if self.gray is not None and self.p.template is not None:
            units = find_units(self.gray, self.p.template, 0.3)
            if units:
                self.box = tuple(max(units, key=lambda u: u['match'])['box'])
        self._build()
        self._load_form()
        self._refresh()

    # ---- layout
    def _build(self):
        self.viewer = ImageViewer()
        self.viewer.box_drawn.connect(self._box_drawn)
        self.viewer.point_picked.connect(self._point_picked)
        if self.gray is not None:
            self.viewer.set_image(self.gray, id(self.gray))
        self.hint = QLabel()
        self.hint.setWordWrap(True)
        left = QWidget()
        lv = QVBoxLayout(left)
        lv.setContentsMargins(0, 0, 0, 0)
        lv.addWidget(self.hint)
        lv.addWidget(self.viewer, 1)

        right = QWidget()
        rv = QVBoxLayout(right)
        g = QGroupBox('1. 单元模板')
        gv = QHBoxLayout(g)
        self.btn_box = QPushButton('框选单元')
        self.btn_box.setCheckable(True)
        self.btn_box.setToolTip('在图上拖框框住一个完整的单元，裁出模板 tpl.png')
        self.btn_box.toggled.connect(lambda on: self._set_tool('box' if on else 'pan'))
        self.tpl_label = QLabel()
        gv.addWidget(self.btn_box)
        gv.addWidget(self.tpl_label, 1)
        rv.addWidget(g)

        g = QGroupBox('2. 导线（模板坐标，起点 = 第一焊点）')
        gv = QVBoxLayout(g)
        self.wire_list = QListWidget()
        self.wire_list.currentRowChanged.connect(lambda _: self._draw())
        gv.addWidget(self.wire_list, 1)
        row = QHBoxLayout()
        for text, fn, tip in (('自动找线', self._auto, '在模板里自动找线（会替换列表）'),
                              ('手动添加', None, '在图上依次点第一焊点、第二焊点'),
                              ('交换方向', self._swap, '交换选中导线的两个焊点'),
                              ('删除', self._delete, '删除选中导线')):
            b = QPushButton(text)
            b.setToolTip(tip)
            if fn is None:
                b.setCheckable(True)
                b.toggled.connect(lambda on: self._set_tool('point' if on else 'pan'))
                self.btn_pick = b
            else:
                b.clicked.connect(fn)
            row.addWidget(b)
        gv.addLayout(row)
        rv.addWidget(g, 1)

        g = QGroupBox('3. 产品参数')
        form = QFormLayout(g)
        self.name = QLineEdit()
        form.addRow('名称', self.name)
        self.polarity = QComboBox()
        self.polarity.addItem('导线偏暗', 'dark')
        self.polarity.addItem('导线偏亮', 'bright')
        form.addRow('极性', self.polarity)
        self.spins = {}
        for key, label, lo, hi, step, dec in NUMBERS:
            s = QDoubleSpinBox(decimals=dec, minimum=lo, maximum=hi, singleStep=step)
            s.setToolTip(TIPS.get(key, ''))
            self.spins[key] = s
            form.addRow(label, s)
        self.n_seg = QSpinBox(minimum=3, maximum=20)
        form.addRow('分段数', self.n_seg)
        self.local_align = QCheckBox('每个单元做局部扭曲校正')
        self.local_align.setToolTip(TIPS['align_search_px'])
        form.addRow('局部校正', self.local_align)
        self.allow_mirror = QCheckBox('也找镜像单元（零件翻面放）')
        self.allow_mirror.setToolTip('零件翻面后 X 光图是镜像；勾选后同时用镜像模板找单元')
        form.addRow('镜像', self.allow_mirror)
        self.note = QLineEdit()
        form.addRow('备注', self.note)
        rv.addWidget(g)

        row = QHBoxLayout()
        b = QPushButton('预览检测')
        b.setToolTip('用当前设置在整张图上试检测并标注')
        b.clicked.connect(self._preview)
        row.addWidget(b)
        self.preview_label = QLabel()
        self.preview_label.setWordWrap(True)
        row.addWidget(self.preview_label, 1)
        rv.addLayout(row)
        bb = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        bb.button(QDialogButtonBox.Save).setText('保存')
        bb.button(QDialogButtonBox.Cancel).setText('取消')
        bb.accepted.connect(self._save)
        bb.rejected.connect(self.reject)
        rv.addWidget(bb)

        split = QSplitter(Qt.Horizontal)
        split.addWidget(left)
        split.addWidget(right)
        split.setStretchFactor(0, 1)
        split.setSizes([820, 430])
        QVBoxLayout(self).addWidget(split)

    def _load_form(self):
        p = self.p
        self.name.setText(p.name)
        self.polarity.setCurrentIndex(1 if p.polarity == 'bright' else 0)
        for key, s in self.spins.items():
            s.setValue(float(getattr(p, key)))
        self.n_seg.setValue(int(p.n_seg))
        self.local_align.setChecked(bool(p.local_align))
        self.allow_mirror.setChecked(bool(p.allow_mirror))
        self.note.setText(p.note)

    def _read_form(self):
        p = self.p
        p.name = self.name.text().strip()
        p.polarity = self.polarity.currentData()
        for key, s in self.spins.items():
            setattr(p, key, s.value())
        p.n_seg = self.n_seg.value()
        p.local_align = self.local_align.isChecked()
        p.allow_mirror = self.allow_mirror.isChecked()
        p.note = self.note.text()
        return p

    # ---- state / drawing
    def _set_tool(self, tool):
        for b, t in ((self.btn_box, 'box'), (self.btn_pick, 'point')):
            if t != tool and b.isChecked():
                b.blockSignals(True)
                b.setChecked(False)
                b.blockSignals(False)
        self.picks = []
        self.viewer.set_tool(tool)
        self._refresh()

    def _refresh(self):
        t = self.p.template
        self.tpl_label.setText('%d×%d px' % (t.shape[1], t.shape[0]) if t is not None else '未设置')
        self.wire_list.blockSignals(True)
        row = self.wire_list.currentRow()
        self.wire_list.clear()
        for i, w in enumerate(self.p.wires):
            QListWidgetItem('线 %d  (%.1f, %.1f) → (%.1f, %.1f)  L=%.1f px' % (i + 1, *w.p1, *w.p2, w.length),
                            self.wire_list)
        self.wire_list.setCurrentRow(min(row, self.wire_list.count() - 1))
        self.wire_list.blockSignals(False)
        tool = self.viewer.tool
        if self.gray is None:
            hint = '没有可用的图片：请先在主窗口打开一张图片再新建产品。'
        elif tool == 'box':
            hint = '在图上拖框，框住一个完整的单元（Esc 取消）'
        elif tool == 'point':
            hint = '点击第%s焊点' % ('一' if not self.picks else '二')
        elif t is None:
            hint = '第 1 步：点“框选单元”，在图上框住一个完整的单元。'
        else:
            hint = '蓝色虚线框 = 模板；青色 = 导线（蓝圈第一焊点，紫圈第二焊点）。可自动找线或手动添加，再预览检测。'
        self.hint.setText(hint)
        self._draw()

    def _draw(self):
        bx, by = (self.box[0], self.box[1]) if self.box else (0, 0)
        if self.preview is not None:
            rec = self.preview
        else:
            wires = []
            for i, w in enumerate(self.p.wires):
                p1, p2 = [w.p1[0] + bx, w.p1[1] + by], [w.p2[0] + bx, w.p2[1] + by]
                wires.append({'unit': 0, 'index': i, 'flags': [], 'curve': [p1, p2], 'p1': p1, 'p2': p2, 'ball': p1,
                              'l': w.length, 'sweep_pct': 0, 'shift_px': 0, 'z_all': 0})
            rec = {'wires': wires, 'units': []}
        self.viewer.set_result(rec, always=True, labels=True)
        self.viewer.select_wire(self.wire_list.currentRow() if self.preview is None else -1, center=False)
        self.viewer.set_extra(self.box, self.picks)

    def _changed(self):
        self.preview = None
        self.preview_label.setText('')
        self._refresh()

    # ---- actions
    def _box_drawn(self, x0, y0, x1, y1):
        if x1 - x0 < 16 or y1 - y0 < 16:
            self.hint.setText('框太小，请重新框住一个完整的单元')
            return
        old = self.box
        self.box = (x0, y0, x1 - x0, y1 - y0)
        self.p.template = self.gray[y0:y1, x0:x1].copy()
        if old:   # keep the wires where they are on the image
            dx, dy = old[0] - x0, old[1] - y0
            for w in self.p.wires:
                w.p1 = [w.p1[0] + dx, w.p1[1] + dy]
                w.p2 = [w.p2[0] + dx, w.p2[1] + dy]
        self.btn_box.setChecked(False)
        self._changed()
        if not self.p.wires:
            self._auto()

    def _point_picked(self, x, y):
        if not self.box:
            self.hint.setText('请先框选单元模板')
            return
        self.picks.append((x, y))
        if len(self.picks) == 2:
            (x1, y1), (x2, y2) = self.picks
            bx, by = self.box[0], self.box[1]
            self.p.wires.append(Wire([round(x1 - bx, 1), round(y1 - by, 1)], [round(x2 - bx, 1), round(y2 - by, 1)]))
            self.btn_pick.setChecked(False)
            self.wire_list.setCurrentRow(len(self.p.wires) - 1)
            self._changed()
        else:
            self._refresh()

    def _auto(self):
        if self.p.template is None:
            QMessageBox.information(self, '自动找线', '请先框选单元模板。')
            return
        self.p.wires = auto_wires(self.p.template, self._read_form())
        self._changed()
        self.hint.setText('自动找到 %d 根线；请检查方向（起点应在第一焊点），多余的删除、漏掉的手动添加。'
                          % len(self.p.wires))

    def _swap(self):
        i = self.wire_list.currentRow()
        if 0 <= i < len(self.p.wires):
            self.p.wires[i] = self.p.wires[i].reversed()
            self._changed()

    def _delete(self):
        i = self.wire_list.currentRow()
        if 0 <= i < len(self.p.wires):
            del self.p.wires[i]
            self._changed()

    def _preview(self):
        if self.p.template is None or self.gray is None or not self.p.wires:
            QMessageBox.information(self, '预览', '请先设置模板和导线。')
            return
        res = detect(self.gray, self._read_form())
        self.preview = res
        bad = [w for w in res['wires'] if w['flags']]
        dirs = sorted({round(u['angle']) for u in res['units']})
        self.preview_label.setText('单元 %d，导线 %d，NG <b style="color:#e02424">%d</b>，单元方向 %s°' %
                                   (len(res['units']), len(res['wires']), len(bad), '、'.join(map(str, dirs[:8])) or '-'))
        self._draw()

    def _save(self):
        p = self._read_form()
        if not p.name:
            QMessageBox.warning(self, '保存', '请填写产品名称。')
            return
        if p.template is None or not p.wires:
            QMessageBox.warning(self, '保存', '请先设置模板并至少添加一根导线。')
            return
        target = os.path.join(PRODUCTS_DIR, safe_name(p.name))
        same = self.editing is not None and os.path.normpath(self.editing.dir) == os.path.normpath(target)
        if os.path.exists(target) and not same and QMessageBox.question(
                self, '保存', '产品「%s」已存在，覆盖？' % p.name) != QMessageBox.Yes:
            return
        try:
            p.save()
        except (OSError, ValueError) as e:
            QMessageBox.critical(self, '保存失败', str(e))
            return
        self.accept()

    def keyPressEvent(self, e):
        if e.key() == Qt.Key_Escape and self.viewer.tool != 'pan':
            self._set_tool('pan')
            return
        super().keyPressEvent(e)
