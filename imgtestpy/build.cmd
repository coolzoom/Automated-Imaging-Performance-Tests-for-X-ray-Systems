@echo off
chcp 65001 >nul
rem  X-Ray 焊线检测（Python/Win7 版）一键菜单
rem  目标机：Windows 7 SP1 x64 及以上。打包请在装了 Python 3.8 x64 的机器上执行。
setlocal EnableDelayedExpansion
pushd "%~dp0"

:menu
echo.
echo ==================  X-Ray 焊线检测（Python 版 / Win7） ==================
echo    [1] 安装/修复运行环境（需要 Python 3.8 x64）
echo    [2] 运行检测程序
echo    [3] 运行注册机（界面）
echo    [4] 运行测试
echo    [5] 打包（PyInstaller）
echo    [0] 退出
echo.
set /p M=请选择:
if "%M%"=="1" goto setup
if "%M%"=="2" goto run_app
if "%M%"=="3" goto run_tool
if "%M%"=="4" goto tests
if "%M%"=="5" goto pack
if "%M%"=="0" exit /b 0
goto menu

:: ------------------------------------------------------------------
:find_py
rem  优先 .venv-win（Windows 专用，避开 mac/Linux 的 .venv），其次 py -3.8 / python（要求 3.8.x）
if exist ".venv-win\Scripts\python.exe" (set PY=.venv-win\Scripts\python.exe& exit /b 0)
where py >nul 2>&1 && py -3.8 -c "import sys" >nul 2>&1 && (set "PY=py -3.8"& exit /b 0)
where python >nul 2>&1 && (set PY=python& exit /b 0)
echo ^!^! 找不到 Python。请安装 Python 3.8 x64: https://www.python.org/ftp/python/3.8.10/python-3.8.10-amd64.exe
exit /b 1

:setup
call :find_py || (pause& goto menu)
echo 解释器: %PY%
%PY% -c "import sys;print(sys.version); sys.exit(0 if sys.version_info[:2]==(3,8) else 1)"
if errorlevel 1 (echo ^!^! 需要 Python 3.8（Win7 支持的最后一个版本）& pause& goto menu)
if not exist ".venv-win\Scripts\python.exe" (
    echo -- 创建 .venv-win ...
    %PY% -m venv .venv-win || (echo ^!^! venv 创建失败& pause& goto menu)
)
set PY=.venv-win\Scripts\python.exe
%PY% -m pip install -U "pip<24.1" setuptools wheel
%PY% -m pip install -r requirements-win7.txt --only-binary=:all:
if errorlevel 1 (echo ^!^! 依赖安装失败& pause& goto menu)
echo 环境就绪。
pause & goto menu

:run_app
call :find_py || (pause& goto menu)
%PY% main.py
goto menu

:run_tool
call :find_py || (pause& goto menu)
%PY% tools\license_tool.py
goto menu

:tests
call :find_py || (pause& goto menu)
%PY% -m unittest discover -s tests -v
pause & goto menu

:pack
call :find_py || (pause& goto menu)
%PY% -c "import PyInstaller" >nul 2>&1 || %PY% -m pip install "pyinstaller==5.13.2"
echo.
echo  打包哪个程序?
echo    1^) 检测程序 (WireInspect)
echo    2^) 注册机 (WireInspectLicense)
echo    3^) 全部
set /p P=请选择:
if "%P%"=="1" (call :pack_app & goto pack_done)
if "%P%"=="2" (call :pack_tool & goto pack_done)
if "%P%"=="3" (call :pack_app & call :pack_tool & goto pack_done)
goto pack

:pack_app
echo -- 打包检测程序 ...
if exist build\WireInspectPy rmdir /s /q build\WireInspectPy
if exist dist\WireInspect rmdir /s /q dist\WireInspect
%PY% -m PyInstaller --noconfirm --clean WireInspectPy.spec || (echo ^!^! 打包失败& exit /b 1)
if exist products xcopy /E /I /Y products "dist\WireInspect\products" >nul
echo    输出: dist\WireInspect\WireInspect.exe
exit /b 0

:pack_tool
echo -- 打包注册机 ...
if exist build\LicenseTool rmdir /s /q build\LicenseTool
if exist dist\WireInspectLicense rmdir /s /q dist\WireInspectLicense
%PY% -m PyInstaller --noconfirm --clean LicenseTool.spec || (echo ^!^! 打包失败& exit /b 1)
rem  私钥自动拷贝到 <exe>\license_keys\（注册机只发内部，勿给客户）
set KEY=..\license_keys\vendor_ed25519.key
if exist "%KEY%" (
    mkdir "dist\WireInspectLicense\license_keys" >nul 2>&1
    copy /Y "%KEY%" "dist\WireInspectLicense\license_keys\" >nul
    echo    已拷贝签名私钥到 dist\WireInspectLicense\license_keys\
) else (
    echo    ^!^! 未找到 %KEY% —— 打包出的注册机不能签发，请先把私钥放到该位置
)
echo    输出: dist\WireInspectLicense\WireInspectLicense.exe
exit /b 0

:pack_done
echo.
echo 打包完成。产物在 dist\ 下，整个文件夹拷到目标机即可运行（支持 Win7 SP1 x64）。
pause & goto menu
