import os

from PySide6.QtCore import QSize, Qt, QTimer
from PySide6.QtGui import QAction, QColor, QIcon, QKeySequence, QPainter, QPixmap, QShortcut
from PySide6.QtWidgets import (QAbstractItemView, QApplication, QButtonGroup, QCheckBox, QComboBox, QFileDialog,
                               QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget, QListWidgetItem,
                               QMainWindow, QMenu, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
                               QRadioButton, QSplitter, QTabWidget, QTableWidget, QTableWidgetItem, QToolBar,
                               QToolButton, QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget)

from core import report
from core.config import DEFECT_TYPES, PRODUCTS_DIR, load_config, save_config, type_color
from core.defect import Defect
from core.imgio import read_gray
from core.product import Product, list_products
from core.session import AUTO_OK, FAIL, PASS, STATUS_COLORS, STATUS_TEXT, SUSPECT, UNCHECKED, Session
from core.worker import BatchWorker
from ui.icons import tool_icon
from ui.product_dialog import ProductDialog
from ui.viewer import ImageViewer, thumbnail

APP_TITLE = 'X-Ray 焊线检测复核'
PENDING_FILTER = 'pending'
FILTERS = [('全部', None), ('疑似缺陷', {SUSPECT}), ('FAIL', {FAIL}), ('PASS', {PASS}),
           ('未复核', {UNCHECKED, AUTO_OK, SUSPECT}), ('未检出候选（待复核）', {AUTO_OK}),
           ('有未处理候选框', PENDING_FILTER)]
RECENT_MAX = 10
ROLE_REL = Qt.UserRole
ROLE_ID = Qt.UserRole + 1
SPIN_FRAMES = 12
TREE_ICON = QSize(32, 14)
SRC_NAMES = {'auto': '算法', 'manual': '手动'}
ST_NAMES = {'pending': '待确认', 'accepted': '已确认', 'rejected': '误报'}


def status_brush(st):
    c = QColor(STATUS_COLORS[st])
    c.setAlpha(90)
    return c


def dot_icon(color, size=14, spin=None, width=None):
    """Status dot; with width, the dot sits at the right and the left slot shows a spinner frame
    (spin = 0..SPIN_FRAMES-1) or stays empty, so tree text does not jump when detection starts."""
    w = width or size
    pm = QPixmap(w, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    if spin is not None:
        pen = p.pen()
        pen.setColor(QColor('#1f6fd1'))
        pen.setWidthF(2.2)
        pen.setCapStyle(Qt.RoundCap)
        p.setPen(pen)
        p.drawArc(2, 2, size - 4, size - 4, -spin * 16 * 360 // SPIN_FRAMES, 270 * 16)
    p.setBrush(QColor(color))
    p.setPen(Qt.NoPen)
    p.drawEllipse(w - size + 1, 1, size - 2, size - 2)
    p.end()
    return QIcon(pm)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.cfg = load_config()
        self.session = None
        self.worker = None
        self.product = None
        self.current = None
        self.gray = None
        self.selected_defect = None
        self.tree_items = {}
        self.folders = {}
        self.thumb_cache = {}
        self._thumb_folder = None
        self._thumb_queue = []
        self.icons = {k: dot_icon(c, width=TREE_ICON.width()) for k, c in STATUS_COLORS.items()}
        self.spin_icons = {k: [dot_icon(c, spin=f, width=TREE_ICON.width()) for f in range(SPIN_FRAMES)]
                           for k, c in STATUS_COLORS.items()}
        self.busy = set()           # images being detected right now (spinner in the tree)
        self.spin_frame = 0
        self.spin_timer = QTimer(self, interval=80, timeout=self._spin)
        self.setWindowTitle(APP_TITLE)
        self._build()
        self._shortcuts()
        self._fill_products(self.cfg.get('product') or '')
        self.autosave = QTimer(self, interval=3000, timeout=self._autosave)
        self.autosave.start()
        self.thumb_timer = QTimer(self, interval=0, timeout=self._load_next_thumb)

    # ------------------------------------------------------------------ layout
    def _build(self):
        tb = QToolBar('主工具栏')
        tb.setIconSize(QSize(18, 18))
        tb.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
        tb.setMovable(False)
        self.addToolBar(tb)
        self.act_open = QAction('导入文件夹', self, triggered=lambda: self.open_folder())
        self.act_recent = QAction('最近文件夹', self)
        self.act_recent.setToolTip('重新打开最近用过的文件夹')
        self.recent_menu = QMenu(self)
        self.recent_menu.aboutToShow.connect(self._fill_recent_menu)
        self.act_recent.setMenu(self.recent_menu)
        self.act_start = QAction('开始检测', self, triggered=lambda: self.start_detection(False))
        self.act_stop = QAction('停止', self, triggered=self.stop_detection)
        self.act_rerun = QAction('重新检测全部', self, triggered=lambda: self.start_detection(True))
        self.act_clear = QAction('清除标记', self)
        self.act_clear.setToolTip('清除当前图片 / 当前子文件夹 / 整个文件夹的所有缺陷标记')
        clear_menu = QMenu(self)
        clear_menu.addAction('清除当前图片的所有标记', lambda: self.clear_marks('image'))
        self.act_clear_sub = clear_menu.addAction('清除当前子文件夹的所有标记', lambda: self.clear_marks('subfolder'))
        clear_menu.addAction('清除整个文件夹的所有标记', lambda: self.clear_marks('folder'))
        clear_menu.aboutToShow.connect(
            lambda: self.act_clear_sub.setEnabled(bool(self.current and '/' in self.current)))
        self.act_clear.setMenu(clear_menu)
        self.act_report = QAction('保存报告', self, triggered=self.save_report)
        self.act_report.setToolTip('整个文件夹的 CSV 报告：每图一行汇总 + 每根线明细')
        self.act_new_product = QAction('新建产品', self, triggered=lambda: self.edit_product(True))
        self.act_new_product.setToolTip('在当前图片上框选一个单元，半自动配置导线和阈值')
        self.act_edit_product = QAction('编辑产品', self, triggered=lambda: self.edit_product(False))
        for a, name in ((self.act_open, 'open'), (self.act_recent, 'recent'), (self.act_start, 'start'),
                        (self.act_stop, 'stop'), (self.act_rerun, 'rerun'), (self.act_clear, 'clear'),
                        (self.act_report, 'report'), (self.act_new_product, 'add'), (self.act_edit_product, 'edit')):
            a.setIcon(tool_icon(name))
        for a in (self.act_open, self.act_recent, None, self.act_start, self.act_stop, self.act_rerun, self.act_clear,
                  None, self.act_report, None):
            tb.addSeparator() if a is None else tb.addAction(a)
        for a in (self.act_recent, self.act_clear):
            tb.widgetForAction(a).setPopupMode(QToolButton.InstantPopup)
        icon = QLabel()
        icon.setPixmap(tool_icon('product').pixmap(18, 18))
        tb.addWidget(icon)
        tb.addWidget(QLabel(' 产品: '))
        self.product_combo = QComboBox()
        self.product_combo.setMinimumWidth(150)
        self.product_combo.setToolTip('检测用的产品（products/<名称>/tpl.png + product.json）')
        self.product_combo.currentTextChanged.connect(self._product_changed)
        tb.addWidget(self.product_combo)
        tb.addAction(self.act_new_product)
        tb.addAction(self.act_edit_product)
        tb.addSeparator()
        self.chk_follow = QCheckBox('自动显示最新')
        self.chk_follow.setChecked(True)
        self.chk_follow.setToolTip('检测时自动切到刚完成的图片')
        tb.addWidget(self.chk_follow)
        tb.addSeparator()
        tb.addWidget(QLabel(' 操作员: '))
        self.operator = QLineEdit(self.cfg.get('operator', ''))
        self.operator.setFixedWidth(120)
        self.operator.editingFinished.connect(self._operator_changed)
        tb.addWidget(self.operator)
        for a in (self.act_start, self.act_rerun, self.act_stop, self.act_report, self.act_clear):
            a.setEnabled(False)

        # left: filter + tree
        left = QWidget()
        lv = QVBoxLayout(left)
        lv.setContentsMargins(4, 4, 4, 4)
        self.filter = QComboBox()
        for name, _ in FILTERS:
            self.filter.addItem(name)
        self.filter.currentIndexChanged.connect(self._apply_filter)
        lv.addWidget(self.filter)
        self.tree = QTreeWidget()
        self.tree.setHeaderHidden(True)
        self.tree.setIconSize(TREE_ICON)
        self.tree.currentItemChanged.connect(self._tree_changed)
        lv.addWidget(self.tree)
        legend = QLabel('  '.join('<span style="color:%s">●</span>%s' % (STATUS_COLORS[k], v)
                                  for k, v in STATUS_TEXT.items()))
        legend.setWordWrap(True)
        lv.addWidget(legend)

        # center: viewer + thumbnails
        center = QWidget()
        cv = QVBoxLayout(center)
        cv.setContentsMargins(0, 0, 0, 0)
        bar = QHBoxLayout()
        self.mode_group = QButtonGroup(self)
        for k, t in (('original', '原图'), ('enhanced', '增强(CLAHE)'), ('wire', '导线高亮')):
            rb = QRadioButton(t)
            rb.setProperty('mode', k)
            rb.setChecked(k == 'wire')
            self.mode_group.addButton(rb)
            bar.addWidget(rb)
        self.mode_group.buttonClicked.connect(self._mode_clicked)
        bar.addSpacing(16)
        self.chk_marks = QCheckBox('显示标记')
        self.chk_marks.setChecked(True)
        self.chk_marks.toggled.connect(lambda on: self.viewer.set_show_marks(on))
        bar.addWidget(self.chk_marks)
        bar.addStretch(1)
        for t, fn in (('适应窗口(0)', lambda: self.viewer.fit()), ('1:1 (1)', lambda: self.viewer.actual_size()),
                      ('放大', lambda: self.viewer.zoom(1.25)), ('缩小', lambda: self.viewer.zoom(0.8))):
            b = QPushButton(t)
            b.clicked.connect(fn)
            bar.addWidget(b)
        self.zoom_label = QLabel('100%')
        self.zoom_label.setMinimumWidth(50)
        bar.addWidget(self.zoom_label)
        cv.addLayout(bar)
        self.banner = QLabel()
        self.banner.setAlignment(Qt.AlignCenter)
        self.banner.setMinimumHeight(40)
        self.banner.setWordWrap(True)
        self.banner.setVisible(False)
        cv.addWidget(self.banner)
        self.viewer = ImageViewer()
        self.viewer.box_drawn.connect(self._box_drawn)
        self.viewer.defect_clicked.connect(lambda i: self._select_defect(i, center=False))
        self.viewer.wire_clicked.connect(self._select_wire_row)
        self.viewer.zoom_changed.connect(lambda s: self.zoom_label.setText('%d%%' % round(s * 100)))
        self.thumbs = QListWidget()
        self.thumbs.setViewMode(QListWidget.IconMode)
        self.thumbs.setFlow(QListWidget.LeftToRight)
        self.thumbs.setWrapping(False)
        self.thumbs.setIconSize(QSize(140, 90))
        self.thumbs.setFixedHeight(140)
        self.thumbs.setMovement(QListWidget.Static)
        self.thumbs.setHorizontalScrollMode(QAbstractItemView.ScrollPerPixel)
        self.thumbs.currentItemChanged.connect(self._thumb_changed)
        vsplit = QSplitter(Qt.Vertical)
        vsplit.addWidget(self.viewer)
        vsplit.addWidget(self.thumbs)
        vsplit.setStretchFactor(0, 1)
        cv.addWidget(vsplit, 1)

        # right: info, defects / wires, verdict
        right = QWidget()
        rv = QVBoxLayout(right)
        rv.setContentsMargins(4, 4, 4, 4)
        self.info = QLabel('请先导入文件夹')
        self.info.setWordWrap(True)
        self.info.setTextInteractionFlags(Qt.TextSelectableByMouse)
        rv.addWidget(self.info)
        self.tabs = QTabWidget()
        self.tabs.addTab(self._build_defect_panel(), '缺陷标记')
        self.tabs.addTab(self._build_wire_panel(), '导线统计')
        rv.addWidget(self.tabs, 1)

        g = QGroupBox('判定')
        gv = QVBoxLayout(g)
        row = QHBoxLayout()
        self.btn_pass = QPushButton('PASS (P)')
        self.btn_fail = QPushButton('FAIL (F)')
        for b, c in ((self.btn_pass, '#18a848'), (self.btn_fail, '#e02424')):
            b.setMinimumHeight(54)
            b.setStyleSheet('QPushButton{background:%s;color:white;font-size:20px;font-weight:bold;border-radius:6px}'
                            'QPushButton:disabled{background:#888}' % c)
            row.addWidget(b)
        self.btn_pass.clicked.connect(lambda: self.set_verdict(PASS))
        self.btn_fail.clicked.connect(lambda: self.set_verdict(FAIL))
        gv.addLayout(row)
        self.btn_clear = QPushButton('清除判定')
        self.btn_clear.clicked.connect(lambda: self.set_verdict(None))
        gv.addWidget(self.btn_clear)
        self.chk_auto_next = QCheckBox('判定后自动跳到下一张')
        self.chk_auto_next.setChecked(True)
        gv.addWidget(self.chk_auto_next)
        gv.addWidget(QLabel('备注:'))
        self.comment = QPlainTextEdit()
        self.comment.setFixedHeight(70)
        self.comment.textChanged.connect(self._comment_changed)
        gv.addWidget(self.comment)
        rv.addWidget(g)
        rv.addWidget(QLabel('快捷键: P/F 判定, ←/→ 上/下一张, 空格 下一张未复核,\nB 框选, A 确认, X 误报, 0 适应, 1 原尺寸'))

        split = QSplitter(Qt.Horizontal)
        split.addWidget(left)
        split.addWidget(center)
        split.addWidget(right)
        split.setStretchFactor(1, 1)
        split.setSizes([300, 1000, 380])
        self.setCentralWidget(split)

        self.progress = QProgressBar()
        self.progress.setMaximumWidth(300)
        self.progress.setVisible(False)
        self.counts_label = QLabel()
        self.counts_label.setTextFormat(Qt.RichText)
        self.statusBar().addPermanentWidget(self.counts_label)
        self.statusBar().addPermanentWidget(self.progress)
        self._mode_clicked(self.mode_group.checkedButton())
        self._update_review_controls()

    def _build_defect_panel(self):
        g = QWidget()
        gv = QVBoxLayout(g)
        gv.setContentsMargins(2, 2, 2, 2)
        self.table = self._table(['类型', '分数', '来源', '状态'], stretch=0)
        self.table.itemSelectionChanged.connect(self._table_selected)
        gv.addWidget(self.table)
        row = QHBoxLayout()
        self.btn_accept = QPushButton('确认缺陷(A)')
        self.btn_reject = QPushButton('误报(X)')
        self.btn_delete = QPushButton('删除')
        self.btn_accept.clicked.connect(lambda: self._set_defect_status('accepted'))
        self.btn_reject.clicked.connect(lambda: self._set_defect_status('rejected'))
        self.btn_delete.clicked.connect(self._delete_defect)
        for b in (self.btn_accept, self.btn_reject, self.btn_delete):
            row.addWidget(b)
        gv.addLayout(row)
        row = QHBoxLayout()
        self.type_combo = QComboBox()
        self.type_combo.addItems(DEFECT_TYPES)
        self.btn_type = QPushButton('改为此类型')
        self.btn_type.clicked.connect(self._change_type)
        row.addWidget(self.type_combo, 1)
        row.addWidget(self.btn_type)
        gv.addLayout(row)
        self.btn_draw = QPushButton('手动框选(B)')
        self.btn_draw.setCheckable(True)
        self.btn_draw.toggled.connect(self._toggle_draw)
        gv.addWidget(self.btn_draw)
        return g

    def _build_wire_panel(self):
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(2, 2, 2, 2)
        self.wire_info = QLabel()
        self.wire_info.setWordWrap(True)
        self.wire_info.setTextInteractionFlags(Qt.TextSelectableByMouse)
        v.addWidget(self.wire_info)
        self.wire_table = self._table(['单元', '线号', '长度px', 'sweep%', '端点偏移px', 'z', '判定'], stretch=6)
        self.wire_table.itemSelectionChanged.connect(self._wire_row_selected)
        v.addWidget(self.wire_table, 1)
        row = QHBoxLayout()
        row.addStretch(1)
        self.btn_wire_csv = QPushButton('导出CSV')
        self.btn_wire_csv.setToolTip('把本图所有导线的长度 / sweep / 端点偏移等导出为 CSV')
        self.btn_wire_csv.clicked.connect(self.export_wires)
        row.addWidget(self.btn_wire_csv)
        v.addLayout(row)
        return page

    @staticmethod
    def _table(headers, stretch):
        t = QTableWidget(0, len(headers))
        t.setHorizontalHeaderLabels(headers)
        hh = t.horizontalHeader()
        hh.setStretchLastSection(False)
        for i in range(len(headers)):
            hh.setSectionResizeMode(i, QHeaderView.Stretch if i == stretch else QHeaderView.ResizeToContents)
        t.verticalHeader().setVisible(False)
        t.setSelectionBehavior(QAbstractItemView.SelectRows)
        t.setSelectionMode(QAbstractItemView.SingleSelection)
        t.setEditTriggers(QAbstractItemView.NoEditTriggers)
        return t

    def _shortcuts(self):
        for key, fn in (('P', lambda: self.set_verdict(PASS)), ('F', lambda: self.set_verdict(FAIL)),
                        (Qt.Key_Right, lambda: self.step(1)), (Qt.Key_Left, lambda: self.step(-1)),
                        (Qt.Key_Down, lambda: self.step(1)), (Qt.Key_Up, lambda: self.step(-1)),
                        (Qt.Key_Space, self.next_unreviewed), ('B', self.btn_draw.toggle),
                        ('A', lambda: self._set_defect_status('accepted')),
                        ('X', lambda: self._set_defect_status('rejected')),
                        (Qt.Key_Delete, self._delete_defect), (Qt.Key_Backspace, self._delete_defect),
                        ('0', lambda: self.viewer.fit()), ('1', lambda: self.viewer.actual_size()),
                        (QKeySequence.Save, self._save_session), (Qt.Key_Escape, lambda: self.btn_draw.setChecked(False))):
            QShortcut(QKeySequence(key), self, activated=fn)

    # ------------------------------------------------------------------ products
    def _fill_products(self, select):
        names = list_products()
        self.product_combo.blockSignals(True)
        self.product_combo.clear()
        self.product_combo.addItems(names)
        self.product_combo.blockSignals(False)
        if names:
            self.product_combo.setCurrentText(select if select in names else names[0])
            self._product_changed(self.product_combo.currentText())
        else:
            self._product_changed('')

    def _product_changed(self, name):
        self.product = None
        if name:
            try:
                self.product = Product.load(os.path.join(PRODUCTS_DIR, name))
            except Exception as e:
                QMessageBox.warning(self, '产品', '读取产品「%s」失败:\n%s' % (name, e))
            self.cfg['product'] = name
            save_config(self.cfg)
        self.act_edit_product.setEnabled(self.product is not None)
        if self.current:
            self._refresh_result()

    def edit_product(self, new):
        if self._busy():
            return
        if new and self.gray is None:
            QMessageBox.information(self, '新建产品', '请先导入文件夹并打开一张图片，在图上框选一个完整的单元。')
            return
        dlg = ProductDialog(self.gray, None if new else self.product, self)
        if dlg.exec() == ProductDialog.Accepted:
            self._fill_products(os.path.basename(dlg.p.dir))
            self.statusBar().showMessage('产品「%s」已保存: %s' % (dlg.p.name, dlg.p.dir), 8000)

    # ------------------------------------------------------------------ folder / tree
    def open_folder(self, path=None):
        if not path:
            path = QFileDialog.getExistingDirectory(self, '选择图片文件夹')
        if not path:
            return
        self._stop_and_wait()
        if self.session and self.session.dirty:
            self.session.save()
        self.session = Session(path)
        if self.session.empty:
            QMessageBox.warning(self, '提示', '有 %d 张图片是 0 字节的空文件（解压或拷贝不完整），已从列表中隐藏，请重新获取原图。\n例如：%s'
                                % (len(self.session.empty), '、'.join(self.session.empty[:3])))
        if not self.session.files:
            QMessageBox.warning(self, '提示', '该文件夹内没有图片。')
        if self.operator.text():
            self.session.data['operator'] = self.operator.text()
        self.thumb_cache.clear()
        self._thumb_folder = None
        self.current = None
        self._build_tree()
        for a in (self.act_start, self.act_rerun, self.act_report, self.act_clear):
            a.setEnabled(True)
        self.setWindowTitle('%s - %s' % (APP_TITLE, path))
        self._update_counts()
        self._add_recent(self.session.root)
        if self.session.files:
            self.select_rel(self.session.files[0])

    def _add_recent(self, path):
        rec = [p for p in self.cfg.get('recent_folders', []) if os.path.normpath(p) != os.path.normpath(path)]
        self.cfg['recent_folders'] = [path] + rec[:RECENT_MAX - 1]
        save_config(self.cfg)

    def _fill_recent_menu(self):
        m = self.recent_menu
        m.clear()
        rec = self.cfg.get('recent_folders', [])
        if not rec:
            m.addAction('（暂无记录）').setEnabled(False)
            return
        for p in rec:
            ok = os.path.isdir(p)
            a = m.addAction(('%s    %s' % (os.path.basename(p) or p, p)) + ('' if ok else '（不存在）'))
            a.setEnabled(ok and not self._busy())
            a.triggered.connect(lambda _=False, p=p: self.open_folder(p))
        m.addSeparator()
        m.addAction('清除最近记录', self._clear_recent)

    def _clear_recent(self):
        self.cfg['recent_folders'] = []
        save_config(self.cfg)

    def clear_marks(self, scope):
        if not self.session or self._busy():
            return
        if scope == 'image':
            if not self.current:
                return
            rels, what = [self.current], '当前图片「%s」' % self.current.rsplit('/', 1)[-1]
        elif scope == 'subfolder':
            if not self.current or '/' not in self.current:
                return
            sub = self.current.rsplit('/', 1)[0] + '/'
            rels, what = [r for r in self.session.files if r.startswith(sub)], '子文件夹「%s」' % sub[:-1]
        else:
            rels, what = list(self.session.files), '整个文件夹'
        n = sum(len(self.session.rec(r)['defects']) for r in rels)
        box = QMessageBox(QMessageBox.Warning, '清除标记',
                          '将清除%s共 %d 张图片的全部 %d 个缺陷标记（包括手动框选）和导线结果，图片恢复为“未检测”。\n'
                          '此操作不能撤销，确定继续？' % (what, len(rels), n),
                          QMessageBox.Yes | QMessageBox.Cancel, self)
        box.setDefaultButton(QMessageBox.Cancel)
        chk = QCheckBox('同时清除 PASS / FAIL 判定')
        box.setCheckBox(chk)
        if box.exec() != QMessageBox.Yes:
            return
        self.session.clear_marks(rels, verdicts=chk.isChecked())
        self.session.save()
        self.selected_defect = None
        self._apply_filter()
        self._update_counts()
        if self.current:
            self._refresh_result()
        self.statusBar().showMessage('已清除%s的标记（%d 张图片）' % (what, len(rels)), 8000)

    def _build_tree(self):
        self.tree.blockSignals(True)
        self.tree.clear()
        self.tree_items = {}
        root = QTreeWidgetItem(self.tree, [os.path.basename(self.session.root) or self.session.root])
        root.setData(0, ROLE_REL, None)
        folders = {'': root}
        for rel in self.session.files:
            parts = rel.split('/')
            parent = root
            for i in range(len(parts) - 1):
                key = '/'.join(parts[:i + 1])
                if key not in folders:
                    it = QTreeWidgetItem(parent, [parts[i]])
                    it.setData(0, ROLE_REL, None)
                    folders[key] = it
                parent = folders[key]
            it = QTreeWidgetItem(parent, [parts[-1]])
            it.setData(0, ROLE_REL, rel)
            self.tree_items[rel] = it
        self.folders = folders
        self.tree.expandAll()
        self.tree.blockSignals(False)
        self._apply_filter()

    def _update_item(self, rel):
        it = self.tree_items.get(rel)
        if it is None:
            return
        st = self.session.status(rel)
        it.setIcon(0, self.spin_icons[st][self.spin_frame] if rel in self.busy else self.icons[st])
        n = len(self.session.live_defects(rel)) if st in (SUSPECT, FAIL) else 0
        name = rel.rsplit('/', 1)[-1]
        it.setText(0, '%s  [%d]' % (name, n) if n else name)
        it.setToolTip(0, '%s - %s' % (rel, STATUS_TEXT[st]))
        allowed = FILTERS[self.filter.currentIndex()][1]
        if allowed == PENDING_FILTER:
            it.setHidden(not self.session.has_pending(rel))
        else:
            it.setHidden(allowed is not None and st not in allowed)
        t = self._thumb_item(rel)
        if t is not None:
            t.setBackground(status_brush(st))

    def _apply_filter(self):
        if not self.session:
            return
        for rel in self.session.files:
            self._update_item(rel)
        for key, it in sorted(self.folders.items(), key=lambda kv: -len(kv[0])):
            it.setHidden(key != '' and all(it.child(i).isHidden() for i in range(it.childCount())))

    def visible_files(self):
        return [r for r in self.session.files if not self.tree_items[r].isHidden()] if self.session else []

    def _tree_changed(self, cur, prev):
        rel = cur.data(0, ROLE_REL) if cur else None
        if rel and rel != self.current:
            self.show_image(rel)

    def select_rel(self, rel):
        it = self.tree_items.get(rel)
        if it is not None:
            self.tree.blockSignals(True)
            self.tree.setCurrentItem(it)
            self.tree.blockSignals(False)
            self.tree.scrollToItem(it)
        if rel != self.current:
            self.show_image(rel)

    def step(self, d):
        files = self.visible_files()
        if not files:
            return
        i = files.index(self.current) + d if self.current in files else 0
        if 0 <= i < len(files):
            self.select_rel(files[i])

    def next_unreviewed(self):
        if not self.session:
            return
        files = self.session.files
        start = files.index(self.current) + 1 if self.current in files else 0
        for rel in files[start:] + files[:start]:
            if self.session.status(rel) not in (PASS, FAIL) and not self.tree_items[rel].isHidden():
                self.select_rel(rel)
                return
        self.statusBar().showMessage('全部图片均已判定', 4000)

    # ------------------------------------------------------------------ image display
    def show_image(self, rel):
        self.btn_draw.setChecked(False)
        self.current = rel
        self.selected_defect = None
        try:
            self.gray = read_gray(os.path.join(self.session.root, rel))
        except Exception as e:
            self.gray = None
            self.viewer.clear()
            self.info.setText('<b>%s</b><br><span style="color:red">%s</span>' % (rel, e))
            self._fill_wire_table()
            self._update_review_controls()
            return
        self.viewer.set_image(self.gray, os.path.join(self.session.root, rel))
        self._refresh_result()
        self.comment.blockSignals(True)
        self.comment.setPlainText(self.session.rec(rel).get('comment', ''))
        self.comment.blockSignals(False)
        self._fill_thumbs(rel)
        self._update_review_controls()

    def _refresh_result(self):
        """Wires, units and defect boxes of the current image, straight from the session record."""
        if not self.current or self.gray is None:
            return
        rec = self.session.rec(self.current)
        self.viewer.set_result(rec)
        self._fill_wire_table()
        self._refresh_defects()
        made = rec.get('product')
        if made and self.product is not None and made != self.product.name:
            self._banner('结果来自产品「%s」（当前选择「%s」），需要时请重新检测' % (made, self.product.name), '#d97706')
        else:
            self.banner.setVisible(False)

    def _banner(self, text, color):
        self.banner.setText(text)
        self.banner.setStyleSheet('QLabel{background:%s;color:white;font-size:16px;font-weight:bold;'
                                  'border-radius:6px;padding:6px}' % color)
        self.banner.setVisible(True)

    def _refresh_info(self):
        rel = self.current
        r = self.session.rec(rel)
        st = self.session.status(rel)
        files = self.session.files
        lines = ['<b>%s</b>' % rel, '第 %d / %d 张' % (files.index(rel) + 1, len(files)),
                 '状态: <b style="color:%s">%s</b>' % (STATUS_COLORS[st], STATUS_TEXT[st])]
        if self.gray is not None:
            lines.append('尺寸: %d×%d' % (self.gray.shape[1], self.gray.shape[0]))
        if r.get('detected_at') and not r.get('error'):
            wires = r.get('wires') or []
            lines.append('产品: %s   单元 %d   导线 %d   NG %d' % (r.get('product') or '-', len(r.get('units') or []),
                                                          len(wires), sum(1 for w in wires if w['flags'])))
            if not r.get('units'):
                lines.append('<span style="color:#d08000">没有找到单元（产品不对 / 模板匹配阈值太高 / 旋转范围或镜像未开）</span>')
            angles = sorted({round(u.get('angle', 0)) for u in r.get('units') or []})
            if angles != [0] and angles:
                lines.append('单元方向: %s°' % ('、'.join('%d' % v for v in angles[:8]) + ('…' if len(angles) > 8 else '')))
        if r.get('error'):
            lines.append('<span style="color:red">检测出错: %s</span>' % r['error'])
        if r.get('verdict'):
            lines.append('判定人: %s  %s' % (r.get('reviewer') or '-', r.get('reviewed_at', '')))
        self.info.setText('<br>'.join(lines))

    def _refresh_defects(self):
        if not self.current:
            return
        defects = self.session.defects(self.current)
        self.viewer.set_defects(defects, self.selected_defect)
        self.table.blockSignals(True)
        self.table.setRowCount(len(defects))
        sel_row = -1
        for i, d in enumerate(defects):
            vals = [d.type, '%.2f' % d.score, d.by if d.source == 'auto' and d.by else SRC_NAMES.get(d.source, d.source),
                    ST_NAMES.get(d.status, d.status)]
            for j, v in enumerate(vals):
                item = QTableWidgetItem(v)
                item.setData(ROLE_ID, d.id)
                if j == 0:
                    item.setForeground(QColor(type_color(d.type)))
                if d.status == 'rejected':
                    item.setForeground(QColor('#999'))
                self.table.setItem(i, j, item)
            if d.id == self.selected_defect:
                sel_row = i
        if sel_row >= 0:
            self.table.selectRow(sel_row)
        else:
            self.table.clearSelection()
        self.table.blockSignals(False)
        self._refresh_info()

    def _table_selected(self):
        rows = self.table.selectionModel().selectedRows()
        if rows:
            self._select_defect(self.table.item(rows[0].row(), 0).data(ROLE_ID), center=True)

    def _select_defect(self, did, center=False):
        self.selected_defect = did
        self.tabs.setCurrentIndex(0)
        self.viewer.select(did, center)
        for i in range(self.table.rowCount()):
            if self.table.item(i, 0).data(ROLE_ID) == did:
                self.table.blockSignals(True)
                self.table.selectRow(i)
                self.table.blockSignals(False)
                break

    # ------------------------------------------------------------------ wires
    def _mode_clicked(self, btn):
        self.viewer.set_mode(btn.property('mode'))
        self.tabs.setCurrentIndex(1 if btn.property('mode') == 'wire' else 0)

    def _wires(self):
        return (self.session.rec(self.current).get('wires') or []) if self.session and self.current else []

    def _fill_wire_table(self):
        wires = self._wires()
        rec = self.session.rec(self.current) if self.session and self.current else {}
        self.wire_table.blockSignals(True)
        self.wire_table.setRowCount(len(wires))
        for r, w in enumerate(wires):
            vals = [str(w['unit'] + 1), str(w['index'] + 1), '%.0f' % w['l'], '%.1f' % w['sweep_pct'],
                    '%.1f' % w['shift_px'], '%.1f' % w['z_all'],
                    report.flag_text(w['flags']) if w['flags'] else '正常']
            for j, v in enumerate(vals):
                item = QTableWidgetItem(v)
                item.setData(ROLE_ID, r)
                if j == 6:
                    item.setToolTip('z 分段 %s  z_ball %.1f  起点缺口 %.0f px  终点缺口 %.0f px' % (
                        ' '.join('%.1f' % z for z in w['z_seg']), w['z_ball'], w['gap1'], w['gap2']))
                if w['flags']:
                    item.setForeground(QColor('#e02424'))
                self.wire_table.setItem(r, j, item)
        self.wire_table.blockSignals(False)
        if not wires:
            self.wire_info.setText('' if not self.current else
                                   '本图没有找到单元：检查产品是否选对、模板匹配阈值、是否镜像 / 旋转（产品参数）。'
                                   if rec.get('detected_at') and not rec.get('error') else
                                   '本图还没有检测结果；选择产品后点“开始检测”。')
        else:
            bad = sum(1 for w in wires if w['flags'])
            mirrored = sum(1 for u in rec.get('units') or [] if u.get('mirror'))
            self.wire_info.setText('产品 <b>%s</b>，%d 个单元%s，导线 <b>%d</b> 根，其中 <b style="color:#e02424">%d</b> 根 NG。'
                                   '长度单位：像素。' % (rec.get('product') or '-', len(rec.get('units') or []),
                                                    '（%d 个镜像）' % mirrored if mirrored else '', len(wires), bad))

    def _select_wire_row(self, index):
        """A wire clicked on the image: show its row (and the statistics tab) without moving the view."""
        self.tabs.setCurrentIndex(1)
        self.wire_table.blockSignals(True)
        self.wire_table.selectRow(index)
        self.wire_table.blockSignals(False)

    def _wire_row_selected(self):
        rows = self.wire_table.selectionModel().selectedRows()
        if not rows:
            return
        if self.viewer.mode != 'wire':
            for b in self.mode_group.buttons():
                if b.property('mode') == 'wire':
                    b.setChecked(True)
                    self.viewer.set_mode('wire')
        self.viewer.select_wire(rows[0].row())

    def export_wires(self):
        if not self._wires():
            QMessageBox.information(self, '导线统计', '当前图片还没有导线数据，请先检测。')
            return
        base = os.path.splitext(os.path.basename(self.current))[0]
        path, _ = QFileDialog.getSaveFileName(self, '导出导线统计', os.path.join(self.session.root, '%s_导线.csv' % base),
                                              'CSV (*.csv)')
        if not path:
            return
        try:
            report.write_wires(path, self.session.rec(self.current))
        except OSError as e:
            QMessageBox.warning(self, '导出失败', str(e))
            return
        self.statusBar().showMessage('已导出 %d 根导线: %s' % (len(self._wires()), path), 8000)

    # ------------------------------------------------------------------ editing
    def _edit_defects(self, fn):
        if not self.current or not self.selected_defect:
            return
        self.session.set_defects(self.current, fn(self.session.defects(self.current)))
        self._refresh_defects()
        self._update_item(self.current)
        self._update_counts()

    def _set_defect_status(self, status):
        def f(ds):
            for d in ds:
                if d.id == self.selected_defect:
                    d.status = 'pending' if d.status == status else status
            return ds
        self._edit_defects(f)

    def _delete_defect(self):
        self._edit_defects(lambda ds: [d for d in ds if d.id != self.selected_defect])
        self.selected_defect = None

    def _change_type(self):
        t = self.type_combo.currentText()

        def f(ds):
            for d in ds:
                if d.id == self.selected_defect:
                    d.type, d.status = t, 'accepted'
            return ds
        self._edit_defects(f)

    def _toggle_draw(self, on):
        if on and not self.current:
            self.btn_draw.setChecked(False)
            return
        self.viewer.set_draw_mode(on)

    def _box_drawn(self, x0, y0, x1, y1):
        d = Defect(x0, y0, x1, y1, self.type_combo.currentText(), 1.0, source='manual', status='accepted')
        self.session.set_defects(self.current, self.session.defects(self.current) + [d])
        self.selected_defect = d.id
        self.btn_draw.setChecked(False)
        self._refresh_defects()
        self._update_item(self.current)
        self._update_counts()

    def set_verdict(self, verdict):
        if not self.current or not self.session:
            return
        self.session.set_verdict(self.current, verdict, self.operator.text())
        self._update_item(self.current)
        self._update_counts()
        self._refresh_info()
        if verdict and self.chk_auto_next.isChecked():
            files = self.visible_files()
            if self.current in files and files.index(self.current) + 1 < len(files):
                self.step(1)
            elif self.current not in files:
                self.next_unreviewed()

    def _comment_changed(self):
        if self.current:
            self.session.set_comment(self.current, self.comment.toPlainText())

    def _update_review_controls(self):
        on = bool(self.session and self.current)
        for w in (self.btn_pass, self.btn_fail, self.btn_clear, self.btn_accept, self.btn_reject, self.btn_delete,
                  self.btn_type, self.btn_draw, self.comment):
            w.setEnabled(on)

    def _operator_changed(self):
        self.cfg['operator'] = self.operator.text()
        save_config(self.cfg)
        if self.session:
            self.session.data['operator'] = self.operator.text()
            self.session.dirty = True

    # ------------------------------------------------------------------ thumbnails
    def _thumb_item(self, rel):
        for i in range(self.thumbs.count()):
            it = self.thumbs.item(i)
            if it.data(ROLE_REL) == rel:
                return it
        return None

    def _fill_thumbs(self, rel):
        folder = rel.rsplit('/', 1)[0] if '/' in rel else ''
        if self._thumb_folder != folder:
            self._thumb_folder = folder
            self.thumbs.blockSignals(True)
            self.thumbs.clear()
            self._thumb_queue = []
            for f in self.session.files:
                if (f.rsplit('/', 1)[0] if '/' in f else '') == folder:
                    it = QListWidgetItem(f.rsplit('/', 1)[-1])
                    it.setData(ROLE_REL, f)
                    it.setSizeHint(QSize(150, 120))
                    it.setBackground(status_brush(self.session.status(f)))
                    if f in self.thumb_cache:
                        it.setIcon(self.thumb_cache[f])
                    else:
                        self._thumb_queue.append(it)
                    self.thumbs.addItem(it)
            self.thumbs.blockSignals(False)
            self.thumb_timer.start()
        it = self._thumb_item(rel)
        if it is not None:
            self.thumbs.blockSignals(True)
            self.thumbs.setCurrentItem(it)
            self.thumbs.scrollToItem(it)
            self.thumbs.blockSignals(False)

    def _load_next_thumb(self):
        if not self._thumb_queue:
            self.thumb_timer.stop()
            return
        it = self._thumb_queue.pop(0)
        rel = it.data(ROLE_REL)
        try:
            icon = QIcon(thumbnail(read_gray(os.path.join(self.session.root, rel))))
            self.thumb_cache[rel] = icon
            it.setIcon(icon)
        except Exception:
            pass

    def _thumb_changed(self, cur, prev):
        if cur is not None:
            self.select_rel(cur.data(ROLE_REL))

    # ------------------------------------------------------------------ detection
    def _busy(self):
        return bool(self.worker and self.worker.isRunning())

    def _set_running(self, on):
        for a in (self.act_start, self.act_rerun, self.act_open, self.act_recent, self.act_clear,
                  self.act_new_product, self.act_edit_product):
            a.setEnabled(not on)
        self.product_combo.setEnabled(not on)
        self.act_stop.setEnabled(on)
        if not on:
            self.act_edit_product.setEnabled(self.product is not None)

    def start_detection(self, rerun_all):
        if not self.session or self._busy():
            return
        if self.product is None:
            QMessageBox.information(self, '提示', '请先选择产品，或点“新建产品”在图上配置一个。')
            return
        s = self.session
        if rerun_all and any(s.has_detection(r) for r in s.files):
            if QMessageBox.question(self, '确认', '重新检测会替换算法标记和导线结果(手动标记、已复核的框和判定保留)，继续？') \
                    != QMessageBox.Yes:
                return
        rels = list(s.files) if rerun_all else [r for r in s.files if not s.has_detection(r)]
        if not rels:
            self.statusBar().showMessage('所有图片已检测完毕；如需重新检测请点“重新检测全部”。', 5000)
            return
        self.worker = BatchWorker(s.root, rels, self.product.dir, int(self.cfg.get('workers') or 0), self)
        self.worker.started.connect(self._on_started)
        self.worker.result.connect(self._on_result)
        self.worker.progress.connect(self._on_progress)
        self.worker.done.connect(self._on_done)
        self.progress.setVisible(True)
        self.progress.setRange(0, len(rels))
        self.progress.setValue(0)
        self.progress.setFormat('检测中 0 / %d' % len(rels))
        self._set_running(True)
        self.worker.start()

    def _on_started(self, rel):
        self.busy.add(rel)
        self._update_item(rel)
        if not self.spin_timer.isActive():
            self.spin_timer.start()

    def _spin(self):
        self.spin_frame = (self.spin_frame + 1) % SPIN_FRAMES
        for rel in self.busy:
            it = self.tree_items.get(rel)
            if it is not None:
                it.setIcon(0, self.spin_icons[self.session.status(rel)][self.spin_frame])

    def _on_result(self, rel, res):
        self.busy.discard(rel)
        if not self.busy:
            self.spin_timer.stop()
        if 'error' in res:
            self.session.set_error(rel, res['error'])
        else:
            self.session.set_detection(rel, res)
        self._update_item(rel)
        if self.chk_follow.isChecked() and rel != self.current:
            self.select_rel(rel)
        elif rel == self.current:
            self._refresh_result()

    def _on_progress(self, k, n):
        self.progress.setValue(k)
        self.progress.setFormat('检测中 %d / %d' % (k, n))
        if k % 5 == 0 or k == n:
            self._update_counts()

    def _on_done(self, finished):
        busy, self.busy = self.busy, set()
        self.spin_timer.stop()
        for rel in busy:
            self._update_item(rel)
        self.progress.setVisible(False)
        self._set_running(False)
        self.session.save()
        self._update_counts()
        self._apply_filter()
        self.statusBar().showMessage('检测完成' if finished else '检测已停止，可再次点“开始检测”继续', 6000)

    def stop_detection(self):
        if self.worker:
            self.worker.stop()
        self.act_stop.setEnabled(False)
        self.statusBar().showMessage('正在停止…')

    def _stop_and_wait(self):
        if self._busy():
            self.worker.stop()
            self.worker.wait()
            QApplication.processEvents()

    # ------------------------------------------------------------------ outputs
    def _update_counts(self):
        if not self.session:
            return
        c = self.session.counts()
        for i, (name, key) in enumerate(FILTERS):
            if key == PENDING_FILTER:
                self.filter.setItemText(i, '%s（%d 张）' % (name, sum(self.session.has_pending(f) for f in self.session.files)))
        self.counts_label.setText('共 %d 张 | ' % len(self.session.files) + '  '.join(
            '<span style="color:%s">●</span> %s %d' % (STATUS_COLORS[k], STATUS_TEXT[k], c[k]) for k in STATUS_TEXT))

    def save_report(self):
        if not self.session:
            return
        c = self.session.counts()
        pending = c[UNCHECKED] + c[AUTO_OK] + c[SUSPECT]
        if pending and QMessageBox.question(
                self, '确认', '还有 %d 张图片未人工判定(其中疑似 %d 张)，仍然保存报告？' % (pending, c[SUSPECT])) != QMessageBox.Yes:
            return
        out = os.path.join(self.session.root, 'report_wire')
        os.makedirs(out, exist_ok=True)
        path, _ = QFileDialog.getSaveFileName(self, '保存报告', os.path.join(out, '焊线检测报告.csv'), 'CSV (*.csv)')
        if not path:
            return
        self.session.data['operator'] = self.operator.text()
        self.session.save()
        try:
            summ, det = report.write(self.session, path)
        except OSError as e:
            QMessageBox.critical(self, '错误', '保存报告失败:\n%s' % e)
            return
        QMessageBox.information(self, '完成', '报告已保存:\n%s\n%s' % (summ, det))

    # ------------------------------------------------------------------ lifecycle
    def _save_session(self):
        if self.session:
            self.session.save()
            self.statusBar().showMessage('已保存进度', 3000)

    def _autosave(self):
        if self.session and self.session.dirty:
            self.session.save()

    def closeEvent(self, e):
        self._stop_and_wait()
        if self.session and self.session.dirty:
            self.session.save()
        super().closeEvent(e)
