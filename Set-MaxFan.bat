@echo off
:: ==============================================================================
:: Pintasan 1-Klik: Aktifkan MAX FAN (100% Turbo)
:: ==============================================================================
title HP Victus 15 - Set MAX FAN

net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
echo Mengaktifkan MAX FAN (100% Turbo)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { . '%~dp0VictusFanCore.ps1'; $r = Set-VictusMaxFan -Enable $true; Write-Host $r.Message -ForegroundColor Red; Start-Sleep -Seconds 2 }"
