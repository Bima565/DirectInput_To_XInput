#!/usr/bin/env bash
# Fix_L1 - DirectInput to XInput Controller Converter Launcher

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE_DIR="$SCRIPT_DIR/Fix_L1/bin/Debug/net10.0-windows"
EXE_PATH="$EXE_DIR/Fix_L1.exe"

if [ ! -f "$EXE_PATH" ]; then
    echo "[Info] Membangun project..."
    dotnet build "$SCRIPT_DIR/Fix_L1/Fix_L1.csproj" -c Debug
    if [ $? -ne 0 ]; then
        echo "[Error] Build gagal. Pastikan .NET 10 SDK terpasang."
        read -p "Tekan Enter untuk keluar..."
        exit 1
    fi
fi

cd "$EXE_DIR" || exit 1
"$EXE_PATH" "$@"
