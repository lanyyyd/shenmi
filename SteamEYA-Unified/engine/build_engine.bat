@echo off
setlocal
cd /d "%~dp0"

echo ==========================================
echo   SteamEYA - Cloud Game Engine build
echo ==========================================
echo.

set PY=python
where python >nul 2>nul
if errorlevel 1 (
  where py >nul 2>nul
  if errorlevel 1 (
    echo [ERROR] Python not found. Please install Python 3 and add it to PATH.
    goto :end
  )
  set PY=py -3
)

echo Using: %PY%
%PY% --version
echo.

echo [1/3] Installing dependencies ...
%PY% -m pip install -r requirements.txt pyinstaller selenium
if errorlevel 1 (
  echo [ERROR] Failed to install dependencies.
  goto :end
)
echo.

echo [2/3] Building exe ^(first run downloads PyInstaller, please wait^) ...
%PY% -m PyInstaller --noconfirm --clean --onefile --name steameya-engine --collect-submodules selenium local_service.py
if errorlevel 1 (
  echo [ERROR] Build failed. See the messages above.
  goto :end
)
echo.

echo [3/3] Done.
echo Output: %~dp0dist\steameya-engine.exe

:end
echo.
echo Press any key to close this window ...
pause >nul
endlocal