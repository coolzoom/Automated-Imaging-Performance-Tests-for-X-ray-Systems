# imgtest — X-ray bond-wire inspection (.NET 10 + Avalonia 11.3 + OpenCvSharp4 4.13)

## Layout
- `src/WireInspect.Core` — algorithm (Inspector, AutoWires, Simulator), Product, Session, CsvReport. No UI.
- `src/WireInspect.App` — Avalonia GUI, code-only (no XAML), layout copied from app-gpt `ui/main_window.py`.
- `src/WireInspect.Cli` — command line (`detect`, `make-product`, `list`).
- `tests/WireInspect.Tests` — synthetic-image unit tests; `tests/WireInspect.UiTests` — Avalonia.Headless smoke test,
  writes `out/ui_main.png`, `out/ui_product.png` (needs `/Users/mac/Downloads/2026-11-03-13-56-22/2_8_4.jpg`, else no-op).
- `products/<name>/tpl.png + product.json` — product library; `config.json` (git-ignored) — recent folders, operator, product.
- `wire_inspect.py` — original Python prototype, kept for numeric comparison (`.venv/bin/python wire_inspect.py <img>`).
- `py/` — separate Python/PySide6 variant, not part of the .NET build; do not touch from .NET work.

## Commands (run in imgtest/)
- Build: `dotnet build WireInspect.slnx`
- Tests: `dotnet test WireInspect.slnx`
- GUI: `dotnet run --project src/WireInspect.App [folder]`
- CLI: `dotnet run --project src/WireInspect.Cli -- detect <img> --product 2_8_4_demo [--simulate 1 4 7]`
- Windows build: `dotnet publish src/WireInspect.App -c Release -r win-x64 --self-contained` (copy `products/` next to the exe)

## Notes
- Root folder is found by walking up from the executable to a folder containing `products/`; override with `WIREINSPECT_HOME`.
- Reference result on 2_8_4.jpg (2_8_4_demo): 9 units, units 6–8 SHIFT (~4.2–4.9 px), others OK; `--simulate 1 4 7` -> MISSING / BROKEN / SWEEP.
- Keep the C# numbers in line with wire_inspect.py when changing Inspector.InspectWire.
