@echo off
:: HP Victus 15 Fan Control GUI Launcher
cd /d "%~dp0"

if exist "%~dp0dist\.use-new-build" (
    if exist "%~dp0dist\VictusFanControl-new.exe" (
        start "" "%~dp0dist\VictusFanControl-new.exe"
        exit /b
    )
)

if exist "%~dp0dist\VictusFanControl.exe" (
    start "" "%~dp0dist\VictusFanControl.exe"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\VictusFanGUI.ps1"
exit /b
