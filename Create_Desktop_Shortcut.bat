@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $d = [Environment]::GetFolderPath('Desktop'); $s = $ws.CreateShortcut((Join-Path $d 'Fix_L1 - Gamepad Converter.lnk')); $s.TargetPath = '%~dp0Run_Fix_L1.cmd'; $s.WorkingDirectory = '%~dp0Fix_L1\bin\Debug\net10.0-windows'; $s.Description = 'Fix_L1 - DirectInput to XInput Controller Converter'; if (Test-Path '%~dp0gamepads.ico') { $s.IconLocation = '%~dp0gamepads.ico,0' }; $s.Save(); Write-Host 'Shortcut berhasil diperbarui di Desktop!'"
pause
