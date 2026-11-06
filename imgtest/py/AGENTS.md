# imgtest/py — Python (PySide6) version of WireInspect

- Venv: `imgtest/py/.venv` (python3.14 + PySide6, numpy, opencv-python-headless): `.venv/bin/pip install -r requirements.txt`
- Shares `imgtest/products/` and `imgtest/config.json` with the C# version (root = first ancestor with `products/`, or `WIREINSPECT_HOME`); session file `<folder>/.xray_wire/session.json` is the same format as C#.
- `core/inspect.py`, `core/autowires.py`, `core/align.py` mirror `src/WireInspect.Core/Inspector.cs`, `AutoWires.cs`, `Align.cs` step by step; keep them in sync (no `cv2.warpAffine`: OpenCV 4.13 vs 5.0 differ, use `align.warp`).

## Verify (run from imgtest/py)
- Unit tests: `.venv/bin/python -m unittest discover -s tests`
- CLI: `.venv/bin/python wire_inspect.py /Users/mac/Downloads/2026-11-03-13-56-22/2_8_4.jpg --product 2_8_4_demo` (9 units, bottom row SHIFT; CSV identical to C# CLI)
- Cross-validation vs C#: `.venv/bin/python xval/compare.py [image folder]` (builds `xval/xval.csproj`, expects "全部一致")
- GUI smoke: `QT_QPA_PLATFORM=offscreen` script creating `ui.main_window.MainWindow`; guard it with `if __name__ == '__main__':` (detection uses a spawn process pool), and avoid folders with 0-byte images (modal warning blocks).
