"""License dialog: shows the machine code, imports a .lic file and optionally starts the 7-day demo."""
import os

from PySide2.QtCore import Qt
from PySide2.QtGui import QFont
from PySide2.QtWidgets import (QApplication, QDialog, QFileDialog, QHBoxLayout, QLabel, QLineEdit,
                               QMessageBox, QPushButton, QVBoxLayout)

from core import license as lic


class LicenseDialog(QDialog):
    """Result: the validated License (license.py License object) or None if the user quits."""

    def __init__(self, reason, trial, parent=None):
        """trial: None (no trial button), 'available', or a message why not."""
        super().__init__(parent)
        self.result = None
        self.setWindowTitle('软件授权')
        self.setModal(True)
        self.setMinimumWidth(560)

        text = reason + '\n\n'
        if trial == 'available':
            text += '· 先体验：点“试用 %d 天演示版”直接使用。\n· 正式使用：' % lic.TRIAL_DAYS
        msg = QLabel(text + '把下面的机器码发给供应商，收到授权文件（.lic）后点“导入授权文件”。')
        msg.setWordWrap(True)

        code = QLineEdit(lic.machine_code())
        code.setReadOnly(True)
        code.setAlignment(Qt.AlignCenter)
        f = QFont()
        f.setPointSize(14)
        f.setBold(True)
        code.setFont(f)
        copy = QPushButton('复制')
        copy.clicked.connect(lambda: self._copy(code, copy))
        code_row = QHBoxLayout()
        code_row.addWidget(QLabel('机器码'))
        code_row.addWidget(code, 1)
        code_row.addWidget(copy)

        btns = QHBoxLayout()
        btns.addStretch(1)
        if trial is not None:
            t = QPushButton('试用 %d 天演示版' % lic.TRIAL_DAYS)
            t.setEnabled(trial == 'available')
            if trial != 'available':
                t.setToolTip(trial)
            t.clicked.connect(self._start_trial)
            btns.addWidget(t)
        imp = QPushButton('导入授权文件…')
        imp.setDefault(True)
        imp.clicked.connect(self._import_file)
        quit = QPushButton('退出' if trial is not None else '取消')
        quit.clicked.connect(self.reject)
        btns.addWidget(imp)
        btns.addWidget(quit)

        lay = QVBoxLayout(self)
        lay.setContentsMargins(18, 18, 18, 18)
        lay.setSpacing(14)
        lay.addWidget(msg)
        lay.addLayout(code_row)
        lay.addLayout(btns)

    def _copy(self, code, btn):
        QApplication.clipboard().setText(code.text())
        btn.setText('已复制')

    def _start_trial(self):
        if QMessageBox.question(self, '演示版',
                                '演示版从现在起可使用 %d 天，期间可检测、复核和保存报告。\n'
                                '演示期只能开始一次，确定开始吗？' % lic.TRIAL_DAYS,
                                QMessageBox.Yes | QMessageBox.Cancel, QMessageBox.Cancel) != QMessageBox.Yes:
            return
        try:
            self.result = lic.start_trial()
        except lic.LicenseError as e:
            QMessageBox.information(self, '演示版', str(e))
            return
        self.accept()

    def _import_file(self):
        path, _ = QFileDialog.getOpenFileName(self, '选择授权文件', '', '授权文件 (*.lic);;所有文件 (*)')
        if not path:
            return
        try:
            self.result = lic.install(path)
        except lic.LicenseError as e:
            QMessageBox.information(self, '授权', str(e))
            return
        QMessageBox.information(self, '授权', '授权成功：' + self.result.summary())
        self.accept()


def obtain(parent=None):
    """A valid license file wins; otherwise a running demo period; otherwise ask. None if the user quits."""
    try:
        return lic.check()
    except lic.LicenseError as e:
        reason = str(e)
    state, value = lic.trial_status()
    if state == 'active':
        return value
    if state == 'ended' and not os.path.exists(str(lic.license_path())):
        reason = value
    dlg = LicenseDialog(reason, 'available' if state == 'available' else value, parent)
    dlg.exec_()
    return dlg.result
