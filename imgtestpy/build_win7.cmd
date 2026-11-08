@echo off
chcp 65001 >nul
rem ============================================================
rem  Win7 一键构建脚本（无需交互）
rem    1. 查找/下载安装 Python 3.8 x64（Win7 支持的最后一版）
rem    2. 源码拷到本地临时目录（PyInstaller 在网络盘上会失败）
rem    3. 建 venv 装 requirements-win7.txt 冻结依赖
rem    4. PyInstaller 打 WireInspect + WireInspectLicense
rem    5. 产物拷回本目录 dist\，整个文件夹可拷到 Win7 SP1 x64 运行
rem  依赖见 requirements-win7.txt：PySide2 5.15.2.1 / opencv 4.6.0.66 /
rem    numpy 1.24.4 / pyinstaller 5.13.2（版本冻结，勿升级）
rem ============================================================
setlocal
pushd "%~dp0"
set "SRC=%CD%"
set "WORK=%TEMP%\wireinspect_win7_build"
set "PY38="

echo == [1/6] 查找 Python 3.8 ...
where py >nul 2>&1
if errorlevel 1 goto no_pylauncher
for /f "delims=" %%i in ('py -3.8 -c "import sys;print(sys.executable)" 2^>nul') do set "PY38=%%i"
:no_pylauncher
if not defined PY38 if exist "%LOCALAPPDATA%\Programs\Python\Python38\python.exe" set "PY38=%LOCALAPPDATA%\Programs\Python\Python38\python.exe"
if defined PY38 goto have_py

echo -- 未找到 Python 3.8，下载安装包 ...
set "PYINST=%TEMP%\python-3.8.10-amd64.exe"
set "PYURL=https://www.python.org/ftp/python/3.8.10/python-3.8.10-amd64.exe"
powershell -NoProfile -Command "try { (New-Object Net.WebClient).DownloadFile('%PYURL%','%PYINST%'); exit 0 } catch { exit 1 }"
if errorlevel 1 certutil -urlcache -split -f "%PYURL%" "%PYINST%" >nul
if not exist "%PYINST%" (echo ^!^! 下载失败，请手动安装 %PYURL% & goto fail)
echo -- 静默安装 Python 3.8.10（per-user）...
start "" /wait "%PYINST%" /quiet InstallAllUsers=0 PrependPath=0 Include_test=0 AssociateFiles=0 Shortcuts=0
set "PY38=%LOCALAPPDATA%\Programs\Python\Python38\python.exe"

:have_py
if not exist "%PY38%" (echo ^!^! Python 3.8 不可用 & goto fail)
"%PY38%" -c "import sys;print(sys.version.split()[0]);sys.exit(0 if sys.version_info[:2]==(3,8) else 1)" || (echo ^!^! 需要 Python 3.8 & goto fail)
echo    解释器: %PY38%

echo == [2/6] 拷贝源码到本地目录 %WORK%\src ...
if exist "%WORK%" rmdir /s /q "%WORK%"
robocopy "%SRC%" "%WORK%\src" /E /XD .git .venv .venv38 .venv-win build dist out __pycache__ /XF *.lic /NFL /NDL /NP >nul
if errorlevel 8 (echo ^!^! 源码拷贝失败 & goto fail)
pushd "%WORK%\src"

echo == [3/6] 创建虚拟环境 ...
"%PY38%" -m venv .venv || (echo ^!^! venv 创建失败 & goto fail)
set "PY=%WORK%\src\.venv\Scripts\python.exe"

echo == [4/6] 安装冻结依赖 ...
"%PY%" -m pip install -U "pip<24.1" setuptools wheel || goto dep_fail
"%PY%" -m pip install -r requirements-win7.txt --only-binary=:all: || goto dep_fail
goto deps_ok
:dep_fail
echo ^!^! 依赖安装失败 & goto fail
:deps_ok

echo == [5/6] PyInstaller 打包 ...
"%PY%" -m PyInstaller --noconfirm --clean WireInspectPy.spec || (echo ^!^! 检测程序打包失败 & goto fail)
"%PY%" -m PyInstaller --noconfirm --clean LicenseTool.spec || (echo ^!^! 注册机打包失败 & goto fail)
if exist products xcopy /E /I /Y products "dist\WireInspect\products" >nul
set "KEY=%SRC%\..\license_keys\vendor_ed25519.key"
if exist "%KEY%" (
    mkdir "dist\WireInspectLicense\license_keys" >nul 2>&1
    copy /Y "%KEY%" "dist\WireInspectLicense\license_keys\" >nul
    echo    已拷贝签名私钥到 dist\WireInspectLicense\license_keys\
) else (
    echo    ^!^! 未找到私钥 %KEY%，注册机不能签发
)

rem  附带 KB2999226 UCRT 补丁和说明，供 Win7 客户安装
if exist "%SRC%\redist" (
    copy /Y "%SRC%\redist\*.*" "dist\" >nul
    echo    已附带 redist\（KB2999226 补丁）到 dist\
)

echo == [6/6] 拷回产物到 %SRC%\dist ...
robocopy "%WORK%\src\dist" "%SRC%\dist" /E /NFL /NDL /NP >nul
if errorlevel 8 (echo ^!^! 产物拷回失败 & goto fail)
echo.
echo 构建完成：
echo    %SRC%\dist\WireInspect\WireInspect.exe
echo    %SRC%\dist\WireInspectLicense\WireInspectLicense.exe
goto done

:fail
echo.
echo ^!^! 构建失败
:done
popd
popd
endlocal
pause
