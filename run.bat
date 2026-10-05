@echo off
cd /d "%~dp0"

echo Stopping existing PokerAssistant processes...
taskkill /F /IM PokerAssistant.exe >nul 2>&1

if exist "%~dp0bin\Debug\net9.0-windows\PokerAssistant.exe" (
    echo Starting PokerAssistant from bin\Debug\net9.0-windows...
    start "" /d "%~dp0bin\Debug\net9.0-windows" "%~dp0bin\Debug\net9.0-windows\PokerAssistant.exe"
    exit /b 0
)

if exist "%~dp0PokerAssistant.exe" (
    echo Starting PokerAssistant from project root...
    start "" /d "%~dp0" "%~dp0PokerAssistant.exe"
    exit /b 0
)

echo ERROR: PokerAssistant.exe not found! Please build the project first.
pause
exit /b 1
