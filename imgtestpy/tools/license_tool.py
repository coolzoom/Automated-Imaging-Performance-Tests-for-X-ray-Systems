"""License issuing tool with a window (注册机), for colleagues who issue customer licenses.

run: python3 tools/license_tool.py   (packaged as XRayLicense by run.sh 7 / run.bat 7, private key included)
"""
import csv
import datetime
import os
import sys

TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
APP_DIR = os.path.dirname(TOOLS_DIR)
for p in (APP_DIR, TOOLS_DIR):
    if p not in sys.path:
        sys.path.insert(0, p)

import qtcompat  # noqa: F401,E402
from PySide2.QtCore import QDate, Qt, QUrl  # noqa: E402
from PySide2.QtGui import QDesktopServices, QGuiApplication  # noqa: E402
from PySide2.QtWidgets import (QApplication, QButtonGroup, QDateEdit, QFileDialog, QFormLayout, QGroupBox,  # noqa: E402
                               QHBoxLayout, QHeaderView, QLabel, QLineEdit, QMessageBox, QPushButton,
                               QRadioButton, QSpinBox, QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget)

import make_license as ml  # noqa: E402
from core import license  # noqa: E402

OK, BAD = '#15803d', '#b91c1c'


class LicenseTool(QWidget):
    def __init__(self, key_path=ml.DEFAULT_KEY, out_dir=ml.ISSUED_DIR):
        super().__init__()
        self.setWindowTitle('X-Ray 授权签发工具（注册机）')
        self.secret = None
        self.last_file = None
        lay = QVBoxLayout(self)

        self.key_label = QLabel()
        self.key_label.setWordWrap(True)
        lay.addWidget(self.key_label)

        box = QGroupBox('客户信息')
        form = QFormLayout(box)
        row = QHBoxLayout()
        self.machine = QLineEdit()
        self.machine.setPlaceholderText('客户授权窗口里显示的机器码，例如 3F2A-91C0-7B4D-E215')
        self.machine.textChanged.connect(self._update)
        paste = QPushButton('粘贴')
        paste.clicked.connect(lambda: self.machine.setText(QGuiApplication.clipboard().text()))
        row.addWidget(self.machine, 1)
        row.addWidget(paste)
        form.addRow('机器码', row)
        self.machine_hint = QLabel()
        form.addRow('', self.machine_hint)
        self.customer = QLineEdit()
        self.customer.setPlaceholderText('显示在客户程序和演示版报告水印里')
        self.customer.textChanged.connect(self._update)
        form.addRow('客户名称', self.customer)

        ed = QHBoxLayout()
        self.demo = QRadioButton('演示版（报告带水印，不能导出标注和训练）')
        self.full = QRadioButton('正式版')
        self.demo.setChecked(True)
        grp = QButtonGroup(self)
        for b in (self.demo, self.full):
            grp.addButton(b)
            ed.addWidget(b)
        ed.addStretch(1)
        form.addRow('版本', ed)

        ex = QHBoxLayout()
        self.by_days = QRadioButton('有效天数')
        self.days = QSpinBox()
        self.days.setRange(1, 3650)
        self.days.setValue(30)
        self.by_date = QRadioButton('到期日')
        self.date = QDateEdit(QDate.currentDate().addDays(29))
        self.date.setCalendarPopup(True)
        self.date.setDisplayFormat('yyyy-MM-dd')
        self.date.setMinimumDate(QDate.currentDate())
        self.by_days.setChecked(True)
        grp2 = QButtonGroup(self)
        for w in (self.by_days, self.by_date):
            grp2.addButton(w)
        for w in (self.by_days, self.days, self.by_date, self.date):
            ex.addWidget(w)
        ex.addStretch(1)
        for sig in (self.days.valueChanged, self.date.dateChanged, self.by_days.toggled):
            sig.connect(self._update)
        form.addRow('有效期', ex)
        self.expiry_hint = QLabel()
        form.addRow('', self.expiry_hint)

        out = QHBoxLayout()
        self.out_dir = QLineEdit(out_dir)
        self.out_dir.editingFinished.connect(self._load_history)
        choose = QPushButton('选择…')
        choose.clicked.connect(self._choose_dir)
        out.addWidget(self.out_dir, 1)
        out.addWidget(choose)
        form.addRow('保存到', out)
        lay.addWidget(box)

        btns = QHBoxLayout()
        self.issue_btn = QPushButton('生成授权文件')
        self.issue_btn.setDefault(True)
        self.issue_btn.setMinimumHeight(34)
        self.issue_btn.clicked.connect(self.issue)
        self.open_btn = QPushButton('打开所在文件夹')
        self.open_btn.clicked.connect(self._open_dir)
        btns.addWidget(self.issue_btn, 1)
        btns.addWidget(self.open_btn)
        lay.addLayout(btns)
        self.result = QLabel()
        self.result.setWordWrap(True)
        self.result.setTextInteractionFlags(Qt.TextSelectableByMouse)
        lay.addWidget(self.result)

        lay.addWidget(QLabel('签发记录（保存在上面文件夹的 签发记录.csv）'))
        self.table = QTableWidget(0, 6)
        self.table.setHorizontalHeaderLabels(['签发日期', '客户', '机器码', '版本', '到期日', '文件'])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeToContents)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.verticalHeader().setVisible(False)
        lay.addWidget(self.table, 1)

        self._load_key(key_path)
        self._load_history()
        self._update()
        self.resize(760, 640)

    # ------------------------------------------------------------------
    def _load_key(self, path):
        try:
            self.secret = ml.load_key(path)
            self.key_label.setText('<span style="color:%s">● 签名私钥已就绪</span>　'
                                   '<span style="color:#666">本工具含私钥，仅限内部使用，请勿发给客户。</span>' % OK)
        except ml.KeyFileError as e:
            self.secret = None
            self.key_label.setText('<span style="color:%s">● %s</span>' % (BAD, str(e).replace('\n', '<br>')))

    def expires(self):
        if self.by_days.isChecked():
            return datetime.date.today() + datetime.timedelta(days=self.days.value() - 1)
        return self.date.date().toPython()

    def _update(self, *_):
        self.days.setEnabled(self.by_days.isChecked())
        self.date.setEnabled(not self.by_days.isChecked())
        text = self.machine.text().strip()
        code = ml.find_machine(text)
        if not text:
            self.machine_hint.setText('')
        elif code:
            self.machine_hint.setText('<span style="color:%s">✓ %s</span>' % (OK, code))
        else:
            self.machine_hint.setText('<span style="color:%s">格式应为 XXXX-XXXX-XXXX-XXXX（16 位 0-9 / A-F）</span>' % BAD)
        exp = self.expires()
        self.expiry_hint.setText('到期日 %s（当天仍可用，共 %d 天）' % (exp.isoformat(),
                                                           (exp - datetime.date.today()).days + 1))
        self.issue_btn.setEnabled(bool(self.secret and code and self.customer.text().strip()))

    def _choose_dir(self):
        d = QFileDialog.getExistingDirectory(self, '选择保存授权文件的文件夹', self.out_dir.text())
        if d:
            self.out_dir.setText(d)
            self._load_history()

    def _open_dir(self):
        d = self.out_dir.text()
        os.makedirs(d, exist_ok=True)
        QDesktopServices.openUrl(QUrl.fromLocalFile(d))

    def _load_history(self):
        self.table.setRowCount(0)
        log = os.path.join(self.out_dir.text(), '签发记录.csv')
        if not os.path.exists(log):
            return
        with open(log, encoding='utf-8-sig') as f:
            rows = list(csv.reader(f))[1:]
        for r in reversed(rows):
            if len(r) < 7:
                continue
            i = self.table.rowCount()
            self.table.insertRow(i)
            for j, v in enumerate((r[0], r[1], r[2], r[3], r[4], r[6])):
                self.table.setItem(i, j, QTableWidgetItem(v))

    def issue(self):
        machine = ml.find_machine(self.machine.text())
        customer = self.customer.text().strip()
        if not (self.secret and machine and customer):
            return
        edition = 'full' if self.full.isChecked() else 'demo'
        exp = self.expires()
        if exp < datetime.date.today():
            QMessageBox.warning(self, '注册机', '到期日早于今天。')
            return
        if QMessageBox.question(self, '确认签发', '客户：%s\n机器码：%s\n版本：%s\n有效期至：%s（含当天）' % (
                customer, machine, license.EDITIONS[edition], exp.isoformat())) != QMessageBox.Yes:
            return
        try:
            path, _ = ml.issue(self.secret, machine, customer, exp, edition, out_dir=self.out_dir.text(), quiet=True)
        except Exception as e:
            QMessageBox.critical(self, '注册机', '生成失败：\n%s' % e)
            return
        self.last_file = path
        self.result.setText('<span style="color:%s"><b>已生成：</b></span>%s<br>把这个 .lic 文件发给客户，'
                            '在授权窗口点“导入授权文件”即可。' % (OK, path))
        self._load_history()


def main():
    app = QApplication(sys.argv)
    try:
        from ui.icons import app_icon
        app.setWindowIcon(app_icon())
    except Exception:
        pass
    w = LicenseTool()
    w.show()
    return app.exec_()


if __name__ == '__main__':
    sys.exit(main())
