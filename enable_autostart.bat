@echo off
chcp 65001 >nul
echo ===================================================
echo Poker Assistant - 윈도우 시작 시 자동 실행 등록
echo ===================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_autostart.ps1"

echo.
pause
