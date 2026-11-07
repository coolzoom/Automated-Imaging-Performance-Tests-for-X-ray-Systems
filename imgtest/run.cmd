@echo off
rem WireInspect 一键脚本：环境配置 / 编译 / 运行 / 打包 native 单文件
setlocal
chcp 65001 >nul 2>&1
pushd "%~dp0"

set "SLN=WireInspect.slnx"
set "APP=src\WireInspect.App\WireInspect.App.csproj"
set "CLI=src\WireInspect.Cli\WireInspect.Cli.csproj"
set "PUB_ROOT=out\publish"

:menu
echo.
echo ========== WireInspect 一键菜单 ==========
echo  1^) 环境配置 (检查/安装 .NET SDK + restore)
echo  2^) 编译 (Release)
echo  3^) 运行 GUI
echo  4^) 运行 CLI
echo  5^) 打包 native 单文件
echo  6^) 运行测试
echo  7^) 一键全流程 (1 -^> 2 -^> 5)
echo  0^) 退出
echo =========================================
set "c="
set /p "c=请选择: "
if not defined c goto end
if "%c%"=="1" goto env
if "%c%"=="2" goto build
if "%c%"=="3" goto gui
if "%c%"=="4" goto cli
if "%c%"=="5" goto pack
if "%c%"=="6" goto test
if "%c%"=="7" goto all
if "%c%"=="0" goto end
echo [!] 无效选项
goto menu

rem ---------- helpers ----------

:check_sdk
where dotnet >nul 2>&1 || exit /b 1
for /f "tokens=1 delims=." %%v in ('dotnet --version 2^>nul') do (
    if %%v GEQ 10 exit /b 0
)
exit /b 1

:need_sdk
call :check_sdk
if errorlevel 1 (
    echo [x] 未检测到 .NET SDK ^>= 10，请先运行 [1] 环境配置
    exit /b 1
)
exit /b 0

rem ---------- actions ----------

:env
call :check_sdk
if errorlevel 1 (
    echo ==^> 未检测到 .NET SDK 10，尝试用 winget 安装...
    where winget >nul 2>&1 && (
        winget install -e --id Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements
    ) || (
        echo [!] 未找到 winget，请手动安装 .NET SDK 10: https://dot.net/download
        pause & goto menu
    )
    rem winget 安装后需刷新 PATH
    for /f "delims=" %%p in ('where /r "%ProgramFiles%\dotnet" dotnet.exe 2^>nul') do set "PATH=%%~dp;%PATH%"
    call :check_sdk || ( echo [x] 安装后仍未找到 dotnet，请重开终端或手动安装 & pause & goto menu )
)
echo ==^> 还原 NuGet 依赖...
dotnet restore "%SLN%" || ( echo [x] restore 失败 & pause & goto menu )
echo ==^> 环境就绪
for /f %%v in ('dotnet --version') do echo     dotnet %%v
pause & goto menu

:build
call :need_sdk || ( pause & goto menu )
echo ==^> 编译 Release...
dotnet build "%SLN%" -c Release
pause & goto menu

:gui
call :need_sdk || ( pause & goto menu )
set "f="
set /p "f=图片文件夹路径(可留空): "
if "%f%"=="" ( dotnet run --project "%APP%" ) else ( dotnet run --project "%APP%" -- "%f%" )
pause & goto menu

:cli
call :need_sdk || ( pause & goto menu )
echo 例: detect ^<img^> --product 2_8_4_demo [--simulate 1 4 7]
set "a="
set /p "a=CLI 参数: "
dotnet run --project "%CLI%" -- %a%
pause & goto menu

:test
call :need_sdk || ( pause & goto menu )
dotnet test "%SLN%" -c Release
pause & goto menu

:pack
call :need_sdk || ( pause & goto menu )
echo 选择目标平台 RID:
echo  1^) win-x64    2^) win-arm64
echo  3^) osx-arm64  4^) osx-x64
echo  5^) linux-x64  6^) linux-arm64
set "r="
set /p "r=选择 [1]: "
if "%r%"=="" set "r=1"
if "%r%"=="1" set "RID=win-x64"
if "%r%"=="2" set "RID=win-arm64"
if "%r%"=="3" set "RID=osx-arm64"
if "%r%"=="4" set "RID=osx-x64"
if "%r%"=="5" set "RID=linux-x64"
if "%r%"=="6" set "RID=linux-arm64"
if not defined RID ( echo [x] 无效选择 & pause & goto menu )
echo 打包哪个项目?  1^) GUI (WireInspect)  2^) CLI (WireInspect.Cli)  3^) 两者
set "p="
set /p "p=选择 [1]: "
if "%p%"=="" set "p=1"
if "%p%"=="1" ( call :publish "%APP%" "WireInspect" & goto pack_done )
if "%p%"=="2" ( call :publish "%CLI%" "WireInspect.Cli" & goto pack_done )
if "%p%"=="3" ( call :publish "%APP%" "WireInspect" & call :publish "%CLI%" "WireInspect.Cli" & goto pack_done )
echo [x] 无效选择
:pack_done
pause & goto menu

:publish
set "OUTDIR=%PUB_ROOT%\%~2-%RID%"
echo ==^> 发布 %~2 -^> %OUTDIR% (%RID%, self-contained single file)
dotnet publish "%~1" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUTDIR%" || exit /b 1
xcopy /E /I /Y products "%OUTDIR%\products" >nul
echo ==^> 完成: %OUTDIR%\  (products\ 已复制到可执行文件旁)
exit /b 0

:all
call :check_sdk || goto env_next
goto all_build
:env_next
call :check_sdk || ( echo ==^> 安装 .NET SDK... & winget install -e --id Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements )
dotnet restore "%SLN%"
:all_build
dotnet build "%SLN%" -c Release && goto pack
goto menu

:end
popd
endlocal
