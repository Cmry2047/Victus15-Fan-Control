@echo off
:: ==============================================================================
:: Pintasan 1-Klik: Kembalikan Kipas ke Mode AUTO (Default BIOS)
:: ==============================================================================
title HP Victus 15 - Reset ke AUTO FAN

net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
echo Mengembalikan kipas ke mode AUTO (Default BIOS)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { . '%~dp0VictusFanCore.ps1'; Set-VictusMaxFan -Enable $false | Out-Null; $r = Set-VictusFanProfile -ProfileName 'Default'; Write-Host $r.Message -ForegroundColor Green; Start-Sleep -Seconds 2 }"
