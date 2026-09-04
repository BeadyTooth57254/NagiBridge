@echo off
setlocal
cd /d "%~dp0"

set "TOKEN="
if exist token.txt set /p TOKEN=<token.txt
if "%TOKEN%"=="" (
  echo [x] No token yet. Double-click gen_token.bat first.
  pause
  exit /b 1
)

echo.
echo   [REMINDER] Make sure Stardew Valley is ALREADY running and you have
echo              loaded your save (you are IN the farm), then continue.
echo              If the game is not up yet, the client will just keep
echo              retrying http://127.0.0.1:58331 and stay disconnected.
echo.
echo   Press any key once the game is ready...
pause >nul

echo.
echo Starting NagiBridge MCP bridge and tunnel client (2 windows)...
echo.

start "NagiBridge MCP bridge (8000)" cmd /k "set NAGI_BRIDGE_TOKEN=%TOKEN% && python server.py"
start "NagiBridge tunnel client" cmd /k "set NAGI_BRIDGE_TOKEN=%TOKEN% && set NAGI_GAME_URL=http://127.0.0.1:58331 && python client.py"

echo.
echo   Phone operit  -  streamable HTTP  -  http://192.168.100.236:8000/mcp
echo   Auth Bearer token = %TOKEN%
echo   Keep BOTH windows open while you play. This launcher can stay closed.
echo.
pause
