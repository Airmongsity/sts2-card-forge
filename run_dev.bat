@echo off
rem Run the app straight from the source tree for testing: reuses this folder's ComfyUI, models and data\,
rem so nothing is downloaded. Rebuilds first (incremental, a few seconds when nothing changed).
set EXE=%~dp0app\CardForge\bin\Release\net10.0-windows10.0.19041.0\win-x64\CardForge.exe
dotnet build "%~dp0app\CardForge" -c Release -v q -nologo
if errorlevel 1 (
    pause
    exit /b 1
)
start "" "%EXE%"
