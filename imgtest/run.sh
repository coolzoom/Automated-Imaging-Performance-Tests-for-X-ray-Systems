#!/usr/bin/env bash
# WireInspect 一键脚本：环境配置 / 编译 / 运行 / 打包 native 单文件
set -euo pipefail
cd "$(dirname "$0")"

SLN="WireInspect.slnx"
APP="src/WireInspect.App/WireInspect.App.csproj"
CLI="src/WireInspect.Cli/WireInspect.Cli.csproj"
SDK_CHANNEL="10.0"
PUB_ROOT="out/publish"

say()  { printf '\033[1;36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m[!] %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31m[x] %s\033[0m\n' "$*" >&2; exit 1; }

# ---------- helpers ----------

have_dotnet() { command -v dotnet >/dev/null 2>&1; }

sdk_ok() {
    have_dotnet || return 1
    local v
    v=$(dotnet --version 2>/dev/null | cut -d. -f1)
    [ "${v:-0}" -ge 10 ]
}

install_dotnet() {
    say "未检测到 .NET SDK >= $SDK_CHANNEL，开始安装..."
    case "$(uname -s)" in
        Darwin)
            if command -v brew >/dev/null 2>&1; then
                brew install --cask dotnet-sdk || brew install dotnet
            else
                warn "未找到 Homebrew，使用官方 dotnet-install 脚本安装到 ~/.dotnet"
                curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
                bash /tmp/dotnet-install.sh --channel "$SDK_CHANNEL" --install-dir "$HOME/.dotnet"
                export PATH="$HOME/.dotnet:$PATH"
                warn "请将 export PATH=\"\$HOME/.dotnet:\$PATH\" 加入 ~/.zshrc"
            fi
            ;;
        Linux)
            curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
            bash /tmp/dotnet-install.sh --channel "$SDK_CHANNEL" --install-dir "$HOME/.dotnet"
            export PATH="$HOME/.dotnet:$PATH"
            warn "请将 export PATH=\"\$HOME/.dotnet:\$PATH\" 加入 ~/.bashrc"
            ;;
        *) die "不支持的系统，请手动安装 .NET SDK $SDK_CHANNEL: https://dot.net/download" ;;
    esac
    sdk_ok || die ".NET SDK 安装失败，请手动安装: https://dot.net/download"
    say "dotnet $(dotnet --version) 安装完成"
}

current_rid() {
    case "$(uname -s)-$(uname -m)" in
        Darwin-arm64)   echo osx-arm64 ;;
        Darwin-x86_64)  echo osx-x64 ;;
        Linux-x86_64)   echo linux-x64 ;;
        Linux-aarch64)  echo linux-arm64 ;;
        MINGW*_ARM64|MSYS*_ARM64|CYGWIN*_ARM64) echo win-arm64 ;;
        MINGW*|MSYS*|CYGWIN*) echo win-x64 ;;
        *)              echo "" ;;
    esac
}

bundle_app() { # $1=publish dir — wrap single-file exe into WireInspect.app with Dock icon
    local dir="$1" exe="$1/WireInspect" app="$1/WireInspect.app"
    [ -f "$exe" ] || return 0
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    mv "$exe" "$app/Contents/MacOS/WireInspect"
    cp src/WireInspect.App/Assets/app.icns "$app/Contents/Resources/"
    cat > "$app/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>WireInspect</string>
  <key>CFBundleIconFile</key><string>app.icns</string>
  <key>CFBundleIdentifier</key><string>com.wireinspect.app</string>
  <key>CFBundleName</key><string>WireInspect</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
    chmod +x "$app/Contents/MacOS/WireInspect"
    say "已生成 $app (Dock 图标: app.icns；如需分发请再签名/去隔离)"
}

publish() { # $1=csproj $2=rid $3=name
    local out="$PUB_ROOT/$3-$2"
    say "发布 $3 -> $out ($2, self-contained single file)"
    dotnet publish "$1" -c Release -r "$2" --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$out"
    cp -R products "$out/" 2>/dev/null || true
    case "$2-$3" in osx-*-WireInspect) bundle_app "$out" ;; esac
    say "完成: $out/  (products/ 已复制到可执行文件旁)"
}

pick_rid() {
    local def; def=$(current_rid)
    echo "选择目标平台 RID:" >&2
    echo "  1) 本机 (${def:-未知})" >&2
    echo "  2) osx-arm64   3) osx-x64" >&2
    echo "  4) win-x64     5) win-arm64" >&2
    echo "  6) linux-x64   7) linux-arm64" >&2
    read -rp "选择 [1]: " c
    case "${c:-1}" in
        1) [ -n "$def" ] && echo "$def" || return 1 ;;
        2) echo osx-arm64 ;; 3) echo osx-x64 ;;
        4) echo win-x64 ;;  5) echo win-arm64 ;;
        6) echo linux-x64 ;; 7) echo linux-arm64 ;;
        *) return 1 ;;
    esac
}

# ---------- actions ----------

do_env()     { sdk_ok || install_dotnet; say "还原 NuGet 依赖..."; dotnet restore "$SLN"; say "环境就绪: dotnet $(dotnet --version)"; }
do_build()   { sdk_ok || die "请先运行 [1] 环境配置"; say "编译 Release..."; dotnet build "$SLN" -c Release; }
do_test()    { sdk_ok || die "请先运行 [1] 环境配置"; dotnet test "$SLN" -c Release; }
do_gui()     { sdk_ok || die "请先运行 [1] 环境配置"; read -rp "图片文件夹路径(可留空): " f; dotnet run --project "$APP" -- ${f:+"$f"}; }
do_cli()     { sdk_ok || die "请先运行 [1] 环境配置"; echo "例: detect <img> --product 2_8_4_demo [--simulate 1 4 7]"; read -rp "CLI 参数: " a; dotnet run --project "$CLI" -- $a; }
do_pack() {
    sdk_ok || die "请先运行 [1] 环境配置"
    local rid
    rid=$(pick_rid) || { warn "RID 选择无效"; return; }
    echo "打包哪个项目?  1) GUI (WireInspect)  2) CLI (WireInspect.Cli)  3) 两者"
    read -rp "选择 [1]: " p
    case "${p:-1}" in
        1) publish "$APP" "$rid" "WireInspect" ;;
        2) publish "$CLI" "$rid" "WireInspect.Cli" ;;
        3) publish "$APP" "$rid" "WireInspect"; publish "$CLI" "$rid" "WireInspect.Cli" ;;
        *) warn "无效选择" ;;
    esac
}

# ---------- menu ----------

while true; do
    cat <<'EOF'

========== WireInspect 一键菜单 ==========
 1) 环境配置 (检查/安装 .NET SDK + restore)
 2) 编译 (Release)
 3) 运行 GUI
 4) 运行 CLI
 5) 打包 native 单文件
 6) 运行测试
 7) 一键全流程 (1 -> 2 -> 5)
 0) 退出
=========================================
EOF
    read -rp "请选择: " c
    case "$c" in
        1) do_env ;;
        2) do_build ;;
        3) do_gui ;;
        4) do_cli ;;
        5) do_pack ;;
        6) do_test ;;
        7) do_env; do_build; do_pack ;;
        0) exit 0 ;;
        *) warn "无效选项" ;;
    esac
done
