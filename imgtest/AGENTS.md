# imgtest — X-ray bond-wire inspection (.NET 10 + Avalonia 11.3 + OpenCvSharp4 4.13)

## Layout
- `src/WireInspect.Core` — algorithm (Inspector, AutoWires, Simulator), Product, Session, CsvReport. No UI.
- `src/WireInspect.App` — Avalonia GUI, code-only (no XAML), layout copied from app-gpt `ui/main_window.py`.
- `src/WireInspect.Core/Mcp` — MCP server (hand-rolled JSON-RPC): `HttpTransport` (loopback :3918/mcp, port 0 = ephemeral),
  `StdioTransport` + `StdioHttpProxy` bridge, `McpHost.RunStdio`, `WireInspectSession`/`HeadlessSession` (tool-facing ops,
  GUI subclass lives nested in MainWindow.Mcp.cs), `WireInspectTools` (26 tools, Chinese descriptions),
  `AgentConfigurator` (merges `wireinspect` + `wireinspect-headless` entries into agent configs, `.wireinspect.bak` backups).
- `src/WireInspect.Cli` — command line (`detect`, `make-product`, `list`, `mcp` = headless MCP stdio server).
- `src/WireInspect.App` — sidebar button 设置|MCP/AI → `McpDialog` (port/start/stop/auto-start/test/copy + per-agent
  配置/移除/一键配置全部); `Program.Main` handles `--mcp-stdio [--gui] [--port N] [--folder DIR]` before Avalonia starts.
- `tests/WireInspect.Tests` — synthetic-image unit tests; `tests/WireInspect.UiTests` — Avalonia.Headless smoke test,
  writes `out/ui_main.png`, `out/ui_product.png` (needs `/Users/mac/Downloads/2026-11-03-13-56-22/2_8_4.jpg`, else no-op).
- `products/<name>/tpl.png + product.json` — product library; `config.json` (git-ignored) — recent folders, operator, product.
- `wire_inspect.py` — original Python prototype, kept for numeric comparison (`.venv/bin/python wire_inspect.py <img>`).
- `py/` — separate Python/PySide6 variant, not part of the .NET build; do not touch from .NET work.

## Commands (run in imgtest/)
- One-click menu (env setup / build / run / test / single-file publish): `./run.sh` (macOS/Linux) or `run.cmd` (Windows).
  Published output lands in `out/publish/<name>-<rid>/` with `products/` copied next to the exe.
- Build: `dotnet build WireInspect.slnx`
- Tests: `dotnet test WireInspect.slnx`
- GUI: `dotnet run --project src/WireInspect.App [folder]`
- CLI: `dotnet run --project src/WireInspect.Cli -- detect <img> --product 2_8_4_demo [--simulate 1 4 7]`
- Windows build: `dotnet publish src/WireInspect.App -c Release -r win-x64 --self-contained` (copy `products/` next to the exe)
- License issuer (注册机): `dotnet run --project tools/WireInspect.LicenseTool` (Avalonia GUI; `--cli` = console interactive) or `--machine/--customer/--days`;
  `--keygen --write` makes a new keypair (public key then goes into `License.PublicKey`); `--mymachine` prints this PC's code.
  Private key lives in `../license_keys/` (repo-external, git-ignored) — never commit it.

## Notes
- Root folder is found by walking up from the executable to a folder containing `products/`; override with `WIREINSPECT_HOME`.
- Reference result on 2_8_4.jpg (2_8_4_demo): 9 units, units 6–8 SHIFT (~3.8–4.0 px), others OK; `--simulate 1 4 7` -> MISSING / BROKEN / SWEEP;
  `--rotate 90` / `--rotate -4` (test aid) -> same verdicts on the rotated picture; ~1.1 s per image.
- Rotation / distortion: `Align.FindUnitsRotated` — every unit has its own angle (parts may lie any way, `max_angle_deg`
  default 180, 0 = old upright-only path, ~4× faster). Centre square of the template (side min/√2) matched every few
  degrees on a half-size image -> per-pixel best -> candidates; each cut out upright (own bilinear `Align.WarpBack`, not
  `Cv2.WarpAffine`: OpenCV 4.13 vs 5.0 differ), angle refined 0.5° / 0.1°, 180° flip checked, 2×2 block affine for
  distortion -> `UnitResult.Affine` (template -> image) + `Angle`; wires measured on the original image.
- Zoom (`scale_range`, default [0.9,1.4], [1,1] = off): when the native match finds 0 units, `Inspector.BestScale`
  scans the range on a half-size image; at scale s the pipeline re-runs on the resized template, every unit affine
  is pre-composed with origTpl -> scaledTpl, and all pixel params are scaled by s (`ImageResult.Scale` records it).
- Mirror (`allow_mirror`, default on): units also searched with the flipped template; one side per picture
  (`Align.MergeMirrored`: mirrored only if it scores clearly better where both sides found units).
- All lengths in pixels (`*_px` in product.json; old `*_um` + `pixel_um` files are converted on load). No µm anywhere.
- `py/core/align.py` + `py/core/inspect.py` mirror `Align.cs` + `Inspector.cs`; check both with `cd py && .venv/bin/python xval/compare.py`.
- License: `License.cs` — Ed25519-signed `{"license":{id,customer,machine,edition,issued,expires},"sig":b64}` bound to
  `MachineCode()` (ioreg/MachineGuid/machine-id, first NIC as fallback). Enforced only in single-file bundles or with
  `WIREINSPECT_FORCE_LICENSE=1` (dev runs are free). State in `~/Library/Application Support/WireInspect/` (license.lic,
  trial.dat, clock.json); 7-day trial dual-recorded + clock-rollback reject. `edition` demo|full, `Lic.Demo` for gating.
- Keep the C# numbers in line with wire_inspect.py when changing Inspector.InspectWire.

## Debugging unit detection (MCP workflow)
- 0 units → `wireinspect_probe_units`: best_score vs match_thr. Slightly below → lower `match_thr`
  (`wireinspect_set_product_params {product, patch}` — pass `product` name, headless stdio has no cross-call state
  and dispatch is concurrent, so select-then-patch races). Peaks ≪ threshold → wrong template, rebuild product.
- Units found but all wires MISSING with stable z≈1 → wire coordinates land on wrong phase, NOT a threshold issue.
- Ghost-phase matching: leadframe pad edges dominate CCoeffNormed (2 px wire contributes ~nothing), template locks
  onto pad lattice offset by ~(−50,−25). Fix = crop tpl on unique wire+bond region, then set wire endpoints in the
  MATCHED lattice frame (rel coords = abs_wire − found_unit_xy), not the crop's own frame.
- Working example `products/2_8_4_fix`: rect [300,170,160,80] on 2_8_4.jpg, wires p1[128.6,64.2]→p2[189.5,67],
  match_thr 0.75 → 12 units (0.89–0.92), 9 OK / 1 BOND1 / 2 SHIFT.
