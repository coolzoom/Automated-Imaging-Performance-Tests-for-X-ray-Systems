# imgtestpy — X-ray 焊线检测（Python 版，支持 Windows 7）

C# Avalonia 版（`../imgtest`）功能等价重写，跑在 Win7 SP1 x64 / Win10+。算法与
`core/` 里各文件和 C# `WireInspect.Core` 一一对应，product.json / 会话 / 授权文件格式
两边通用。

## 技术栈（版本冻结，勿升级）

- Python 3.8.x — 最后一个支持 Win7 的 CPython
- PySide2 5.15.2.1（Qt 5.15）— Qt6 系不支持 Win7
- opencv-python-headless 4.6.0.66 — 更新的 opencv 需要 Win10
- numpy 1.24.4 — 最后一个 py3.8 兼容版本
- PyInstaller 5.13.2 — 6.x 产物不能在 Win7 启动

代码同时兼容 PySide6（`exec_()`、`QAction`/`QShortcut` 双导入兜底），
方便在开发机上用新 Qt 调试。

## 常用命令

- `build.cmd`（Windows）/ `run.sh`（mac、Linux）：一键菜单——装环境、跑程序、跑测试、打包
- 依赖：`requirements.txt` dev 机（PySide6 via qtcompat 兜底，Win 上 py<3.9 才拉 PySide2）；`requirements-win7.txt` Win7 打包机冻结版
- 本机已装 `pyenv install 3.8.20`（arm64 源码编译）；`.venv38` = py3.8 + cv2 4.6.0 + numpy 1.24.4（Win7 同钉版），跑非 Qt 测试验证 3.8 兼容；`.venv` = py3.9 + PySide6 dev
- 测试：`python -m unittest discover -s tests -v`
- 开发运行：`python main.py [图片文件夹]`；CLI：`python wire_inspect.py --help`
- 注册机：`python tools/license_tool.py`（GUI）或 `python tools/make_license.py --help`
- 打包：`build.cmd` → 5（PyInstaller，`WireInspectPy.spec` / `LicenseTool.spec` → `dist/`）

## 目录

- `core/` 纯算法（无 Qt）：inspect/align/autowires/product/session/report/imgio + license/ed25519 + watcher（实时监控）
- `ui/` PySide2 界面：main_window、product_dialog、license_dialog、viewer、icons、theme
- `tools/` make_license.py（CLI 签发）、license_tool.py（GUI 注册机）
- `products/<名>/` tpl.png + product.json（与 C# 版互换使用）
- `assets/` app.png / app.ico / logo.png

## 授权（与 C# 版共用密钥和 .lic 格式）

- 纯 Python Ed25519 验签（`core/ed25519.py`），签名域 = `json.dumps(license, ensure_ascii=False, sort_keys=True, separators=(',',':'))` —— C# `License.Canonical` 产生**完全相同**的字节，任一实现签发的 .lic 两个程序都能验。
- 公钥内嵌 `core/license.py: PUBLIC_KEY`；私钥在仓库外 `../license_keys/vendor_ed25519.key`，打包注册机时 `build.cmd` 自动拷到 `dist/WireInspectLicense/license_keys/`。
- 机器码：sha256("wireinspect|" + 平台ID) 前 16 位 → `XXXX-XXXX-XXXX-XXXX`，与 C# 逐位一致（Win 读 MachineGuid，mac 读 IOPlatformUUID，Linux 读 machine-id）。
- 演示期 7 天，双记录 + HMAC（`trial.dat` + `~/Library/Preferences/.wireinspect_t` 等），防时钟回拨（`clock.json`）。
- 源码运行不强制授权；`WIREINSPECT_FORCE_LICENSE=1` 或 PyInstaller 打包后启用。状态栏「授权」按钮可看/换授权。

## 关键参数

- `product.json: scale_range`（默认 `[0.9,1.4]`，`[1,1]` 关闭）：正常匹配 0 单元时在区间内扫缩放，命中后用缩放模板重跑，像素参数随缩放调整（`core/inspect.py: best_scale/_scale_px`）。
- 其余参数见 `core/product.py: Product` 注释。

## 实时监控

- `core/watcher.py`：轮询式热文件夹（不用 OS 事件，网络盘不可靠）。文件需「两次轮询 size+mtime 不变 + 闲置 ≥ watch_settle 秒 + JPEG `FF D9` / PNG `IEND` 尾标记」才交给检测，绝不读半成品；原图只读不动。已检过的（session.json）跳过，重启不重复检。
- `ui/main_window.py`：侧栏「实时监控」开关 → `_toggle_watch/_watch_tick/_watch_run`；发现新图自动进树并逐张检测（workers=1），横幅报 OK/NG/ERROR，NG 响铃+弹窗；结果追加 `<文件夹>/.xray_wire/watch_log.csv`。
- 监控中禁用 打开/最近/开始/重检/设置/清除/产品 等按钮；`config.json` 键 `watch_settle`（默认 1.0s）、`watch_interval`（默认 2.0s）与 C# 版共用。

## 注意

- 无 MCP（用户明确不要）；需要时再加。
- 私钥不要进 git；换密钥对要同时改两边 PUBLIC_KEY 并重发所有程序。
- Win7 实测只能在真实 Win7/虚拟机验证——开发机无 PySide2 arm64 轮子时用 PySide6 冒烟。
