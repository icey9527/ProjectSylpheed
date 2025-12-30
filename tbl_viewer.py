from __future__ import annotations

import configparser
import re
import sys
from pathlib import Path
from typing import Dict, Optional

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QFont
from PySide6.QtWidgets import (
    QApplication, QFileDialog, QGridLayout, QHBoxLayout, QLabel, QLineEdit,
    QListWidget, QListWidgetItem, QMainWindow, QMessageBox, QPlainTextEdit,
    QPushButton, QSplitter, QVBoxLayout, QWidget
)

LANGS: Dict[str, str] = {
    "GP_MAIN_GAME_J": "中文",
    "GP_MAIN_GAME_OJ": "日语",
    "GP_MAIN_GAME_E": "英语",
    "GP_MAIN_GAME_I": "意大利语",
    "GP_MAIN_GAME_D": "德语",
    "GP_MAIN_GAME_F": "法语",
    "GP_MAIN_GAME_S": "西班牙语",
    
}

SUF_RE = re.compile(r"^(.*)_(\d{2}|L\d+)$")


def log(s: str):
    print(s, flush=True)


def read_text(p: Path) -> str:
    b = p.read_bytes()
    return b.decode("utf-16" if b[:2] in (b"\xfe\xff", b"\xff\xfe") else "utf-16-be", errors="replace")


def parse_tbl(p: Path) -> Dict[str, str]:
    cp = configparser.ConfigParser(interpolation=None)
    cp.optionxform = str
    s = read_text(p)
    try:
        cp.read_string(s)
    except Exception as e:
        log(f"[parse] fallback section err={e}")
        cp.read_string("[LocalString]\n" + s)
    sec = "LocalString" if "LocalString" in cp else (cp.sections()[0] if cp.sections() else None)
    d = dict(cp[sec].items()) if sec else {}
    log(f"[parse] {p.name} keys={len(d)}")
    return d


def split_group(k: str):
    m = SUF_RE.match(k)
    if not m:
        return k, None
    suf = m.group(2)
    if suf.isdigit():
        return m.group(1), int(suf)
    return m.group(1), int(suf[1:]) if suf[1:].isdigit() else None


def detect_lang_seg(p: Path) -> Optional[str]:
    s = {str(x) for x in p.parts}
    return next((k for k in LANGS if k in s), None)


def replace_seg(p: Path, old: str, new: str) -> Path:
    parts = [str(x) for x in p.parts]
    done = False
    for i, seg in enumerate(parts):
        if seg == old and not done:
            parts[i] = new
            done = True
    return Path(*parts)


class Main(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("IXUD/TBL 多语言查看器")
        self.resize(1200, 860)

        self.base: Optional[Path] = None
        self.base_seg: Optional[str] = None
        self.tables: Dict[str, Dict[str, str]] = {}
        self.files: Dict[str, Optional[Path]] = {}

        self.gid_cache: Dict[str, Dict[str, str]] = {}
        self.search_cache: Dict[str, str] = {}

        self._ui()

        self._search_timer = QTimer(self)
        self._search_timer.setSingleShot(True)
        self._search_timer.setInterval(150)
        self._search_timer.timeout.connect(self.apply_filter_now)

    def _ui(self):
        top = QWidget()
        th = QHBoxLayout(top)
        th.setContentsMargins(10, 10, 10, 6)
        th.setSpacing(8)

        b_open = QPushButton("打开")
        b_open.clicked.connect(self.open_file)
        b_reload = QPushButton("重载")
        b_reload.clicked.connect(self.reload_all)

        self.search = QLineEdit()
        self.search.setPlaceholderText("搜索：ID 或 文本（任意语言，包含匹配）")
        self.search.textChanged.connect(lambda: self._search_timer.start())

        self.path = QLabel("未打开文件")
        self.path.setTextInteractionFlags(Qt.TextSelectableByMouse)

        th.addWidget(b_open)
        th.addWidget(b_reload)
        th.addWidget(self.search, 1)
        th.addWidget(self.path, 2)

        self.listw = QListWidget()
        self.listw.itemSelectionChanged.connect(self.on_select)

        right = QWidget()
        self.grid = QGridLayout(right)
        self.grid.setContentsMargins(10, 6, 10, 10)
        self.grid.setHorizontalSpacing(10)
        self.grid.setVerticalSpacing(6)

        self.id_label = QLabel("选择左侧一个 ID")
        f = QFont()
        f.setPointSize(13)
        f.setBold(True)
        self.id_label.setFont(f)
        self.id_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        self.grid.addWidget(self.id_label, 0, 0, 1, 2)

        self.lang_widgets: Dict[str, tuple[QLabel, QPlainTextEdit]] = {}
        cols = 2
        row = 1
        col = 0

        for folder, name in LANGS.items():
            title = QLabel(f"{name} ({folder})")
            edit = QPlainTextEdit()
            edit.setReadOnly(True)
            edit.setFixedHeight(110)
            self.lang_widgets[folder] = (title, edit)

            self.grid.addWidget(title, row * 2 - 1, col, 1, 1)
            self.grid.addWidget(edit, row * 2, col, 1, 1)

            col += 1
            if col >= cols:
                col = 0
                row += 1

        splitter = QSplitter(Qt.Horizontal)
        left = QWidget()
        lv = QVBoxLayout(left)
        lv.setContentsMargins(10, 6, 10, 10)
        lv.addWidget(self.listw, 1)

        splitter.addWidget(left)
        splitter.addWidget(right)
        splitter.setStretchFactor(0, 1)
        splitter.setStretchFactor(1, 4)
        splitter.setSizes([420, 780])

        cw = QWidget()
        v = QVBoxLayout(cw)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        v.addWidget(top)
        v.addWidget(splitter, 1)
        self.setCentralWidget(cw)

        self.setStyleSheet("""
            QMainWindow { background: #f6f7fb; color: #111; }
            QLabel { color: #111; }
            QLineEdit, QListWidget, QPlainTextEdit {
                background: #ffffff;
                border: 1px solid #d0d6e0;
                border-radius: 10px;
                padding: 8px;
                color: #111111;
                selection-background-color: #2f6fed;
            }
            QPushButton {
                background: #2f6fed;
                color: white;
                border: none;
                padding: 8px 12px;
                border-radius: 10px;
            }
            QPushButton:hover { background: #265ddb; }
            QPushButton:pressed { background: #1f4fbf; }
        """)

    def open_file(self):
        fn, _ = QFileDialog.getOpenFileName(self, "选择 .tbl 或 .ixud", "", "Text (*.tbl *.ixud *.TBL *.IXUD);;All Files (*)")
        if not fn:
            return
        self.base = Path(fn)
        self.base_seg = detect_lang_seg(self.base)
        log(f"[open] {self.base}")
        log(f"[open] seg={self.base_seg}")
        if not self.base_seg:
            QMessageBox.warning(self, "错误", f"路径中找不到语言目录段：{list(LANGS.keys())}")
            return
        self.path.setText(str(self.base))
        self.reload_all()

    def reload_all(self):
        if not self.base or not self.base_seg:
            return

        self.tables.clear()
        self.files.clear()
        self.gid_cache.clear()
        self.search_cache.clear()

        for folder in LANGS:
            p = replace_seg(self.base, self.base_seg, folder)
            ok = p.exists()
            self.files[folder] = p if ok else None
            log(f"[map] {folder} exists={ok} -> {p}")
            self.tables[folder] = parse_tbl(p) if ok else {}

        base_tbl = self.tables.get(self.base_seg, {})
        keys = set(base_tbl.keys()) or set().union(*[set(d.keys()) for d in self.tables.values()])
        gids = sorted({split_group(k)[0] for k in keys})

        group_parts: Dict[str, Dict[str, Dict[int, str]]] = {g: {} for g in gids}
        direct: Dict[str, Dict[str, str]] = {g: {} for g in gids}

        for folder, tbl in self.tables.items():
            for k, v in tbl.items():
                g, idx = split_group(k)
                if g not in group_parts:
                    group_parts[g] = {}
                    direct[g] = {}
                    gids.append(g)
                if idx is None:
                    direct[g][folder] = v
                else:
                    group_parts[g].setdefault(folder, {})[idx] = v

        gids = sorted(set(gids))

        for g in gids:
            self.gid_cache[g] = {}
            all_text = [g.lower()]
            for folder in LANGS:
                mp = group_parts.get(g, {}).get(folder, {})
                if mp:
                    txt = "\n".join(v for _, v in sorted(mp.items(), key=lambda x: x[0]))
                else:
                    txt = direct.get(g, {}).get(folder, "")
                self.gid_cache[g][folder] = txt
                if txt:
                    all_text.append(txt.lower())
            self.search_cache[g] = "\n".join(all_text)

        self.listw.clear()
        for gid in gids:
            it = QListWidgetItem(gid)
            it.setData(Qt.UserRole, gid)
            self.listw.addItem(it)

        if self.listw.count():
            self.listw.setCurrentRow(0)

        self.apply_filter_now()

    def apply_filter_now(self):
        q = self.search.text().strip().lower()
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            gid = it.data(Qt.UserRole)
            it.setHidden(bool(q) and q not in self.search_cache.get(gid, ""))

    def on_select(self):
        items = self.listw.selectedItems()
        if not items:
            return
        gid = items[0].data(Qt.UserRole)
        self.id_label.setText(gid)

        for folder, (title, edit) in self.lang_widgets.items():
            p = self.files.get(folder)
            if not p:
                title.setText(f"{LANGS[folder]} ({folder}) - 缺文件")
                edit.setPlainText("")
                continue
            title.setText(f"{LANGS[folder]} ({folder})")
            edit.setPlainText(self.gid_cache.get(gid, {}).get(folder, ""))


def main():
    app = QApplication(sys.argv)
    w = Main()
    w.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()