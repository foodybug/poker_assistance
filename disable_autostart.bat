@echo off
chcp 65001 >nul
echo ===================================================
echo Poker Assistant - 윈도우 시작 시 자동 실행 해제
echo ===================================================

powershell -NoProfile -ExecutionPolicy Bypass -Command "Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'PokerAssistant' -ErrorAction SilentlyContinue; $lnk = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Startup)) 'PokerAssistant.lnk'; if (Test-Path $lnk) { Remove-Item $lnk -Force }; Write-Host '[OK] 자동 실행 등록이 성공적으로 해제되었습니다.' -ForegroundColor Green"

echo.
pause
