@echo off
setlocal
cd /d "%~dp0"

REM Generate a fresh token once, save it to token.txt, and show it.
for /f %%i in ('python gen_token.py') do set "T=%%i"
> token.txt echo(%T%

echo.
echo ==================================================
echo   Your token (paste into phone operit Bearer Token):
echo.
echo   %T%
echo.
echo   Saved to token.txt. Now double-click launcher.bat
echo ==================================================
echo.
pause
