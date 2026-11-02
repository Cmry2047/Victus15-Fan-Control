@echo off
:: ==============================================================================
:: HP Victus 15 Fan Control GUI Launcher (v3.0)
:: ==============================================================================
cd /d "%~dp0"

if exist "%~dp0VictusFanControl_Standalone.exe" (
    start "" "%~dp0VictusFanControl_Standalone.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_corebridge.exe" (
    start "" "%~dp0VictusFanControl_corebridge.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_finalfix.exe" (
    start "" "%~dp0VictusFanControl_finalfix.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_fixed_all.exe" (
    start "" "%~dp0VictusFanControl_fixed_all.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_restored.exe" (
    start "" "%~dp0VictusFanControl_restored.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_auto_victus.exe" (
    start "" "%~dp0VictusFanControl_auto_victus.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_auto_max.exe" (
    start "" "%~dp0VictusFanControl_auto_max.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_presets_v3.exe" (
    start "" "%~dp0VictusFanControl_presets_v3.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_presets_v2.exe" (
    start "" "%~dp0VictusFanControl_presets_v2.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_presets.exe" (
    start "" "%~dp0VictusFanControl_presets.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_fixed2.exe" (
    start "" "%~dp0VictusFanControl_fixed2.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_fixed.exe" (
    start "" "%~dp0VictusFanControl_fixed.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_v3.exe" (
    start "" "%~dp0VictusFanControl_v3.exe"
    exit /b
)

if exist "%~dp0VictusFanControl_v2.exe" (
    start "" "%~dp0VictusFanControl_v2.exe"
    exit /b
)

if exist "%~dp0VictusFanControl.exe" (
    start "" "%~dp0VictusFanControl.exe"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0VictusFanGUI.ps1"
exit /b
