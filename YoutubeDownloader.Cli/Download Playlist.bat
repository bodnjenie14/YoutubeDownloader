@echo off
setlocal
title YoutubeDownloader - playlist
set "CLI=%~dp0cli\YoutubeDownloader.Cli.exe"
if not exist "%CLI%" set "CLI=%~dp0YoutubeDownloader.Cli.exe"
set "LAST=%~dp0cli\last-folder.txt"

:ask
set "URL="
set /p "URL=Paste a playlist link (or drag a playlist .xml here): "
if not defined URL goto ask

set "START="
if exist "%LAST%" set /p START=<"%LAST%"

echo Choose where to put the playlist folder...
set "OUT="
for /f "usebackq delims=" %%i in (`powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form -Property @{TopMost = $true}; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Choose where to create the playlist folder'; if ($env:START) { $d.SelectedPath = $env:START }; if ($d.ShowDialog($f) -eq 'OK') { Set-Content -LiteralPath $env:LAST -Value $d.SelectedPath; $d.SelectedPath }"`) do set "OUT=%%i"
if not defined OUT (
    echo No folder chosen.
    pause
    exit /b 1
)

"%CLI%" "%URL%" "%OUT%" %*
echo.
pause
