@echo off
title Fix_L1 - DirectInput to XInput Controller Converter
cd /d "%~dp0"

set "EXE_DIR=%~dp0Fix_L1\bin\Debug\net10.0-windows"
set "EXE_PATH=%EXE_DIR%\Fix_L1.exe"

if not exist "%EXE_PATH%" (
    echo ==================================================================
    echo   Fix_L1 - DirectInput to XInput Controller (Xbox 360 Emulator)
    echo ==================================================================
    echo [Info] Menyiapkan dan membangun project...
    dotnet build Fix_L1\Fix_L1.csproj -c Debug
    if errorlevel 1 (
        echo.
        echo [Error] Gagal melakukan build project. Pastikan .NET 10 SDK terpasang.
        echo.
        pause
        exit /b 1
    )
)

cd /d "%EXE_DIR%"
"%EXE_PATH%" %*

if errorlevel 1 (
    echo.
    echo [Info] Aplikasi berhenti dengan kode: %errorlevel%
    pause
)
