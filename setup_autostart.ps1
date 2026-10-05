# ==============================================================================
# Poker Assistant - 윈도우 시작 시 자동 실행 등록 스크립트
# ==============================================================================

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$targetExe = Join-Path $scriptDir "PokerAssistant.exe"

# If PokerAssistant.exe not in root, check bin/Release or publish
if (-not (Test-Path $targetExe)) {
    $altExe = Join-Path $scriptDir "bin\Release\net9.0-windows\win-x64\PokerAssistant.exe"
    if (Test-Path $altExe) {
        $targetExe = $altExe
    } else {
        $altExe = Join-Path $scriptDir "publish\PokerAssistant.exe"
        if (Test-Path $altExe) {
            $targetExe = $altExe
        }
    }
}

Write-Host "Target Executable: $targetExe" -ForegroundColor Cyan

if (-not (Test-Path $targetExe)) {
    Write-Error "PokerAssistant.exe 파일을 찾을 수 없습니다. 빌드가 완료되었는지 확인해주세요."
    exit 1
}

# 1. Register in HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
$registryPath = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$valueName = "PokerAssistant"
$commandValue = "`"$targetExe`""

try {
    Set-ItemProperty -Path $registryPath -Name $valueName -Value $commandValue -Type String
    Write-Host "[OK] 레지스트리 시작 프로그램 등록 완료: $registryPath\$valueName -> $commandValue" -ForegroundColor Green
} catch {
    Write-Warning "레지스트리 등록 실패: $_"
}

# 2. Also place a Shortcut in Windows Startup folder (shell:startup)
try {
    $startupFolder = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Startup)
    $shortcutPath = Join-Path $startupFolder "PokerAssistant.lnk"
    
    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $scriptDir
    $shortcut.Description = "Poker AI Realtime Advisor HUD"
    $shortcut.Save()

    Write-Host "[OK] 시작프로그램 폴더 바로가기 생성 완료: $shortcutPath" -ForegroundColor Green
} catch {
    Write-Warning "시작프로그램 바로가기 생성 실패: $_"
}

Write-Host "`n컴퓨터 부팅 시 포커 어시스턴트가 자동으로 실행되도록 설정되었습니다!" -ForegroundColor Yellow
