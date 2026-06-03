@echo off
:: Ultra Creative Suite — Installer Launcher
:: Desni klik -> Pokreni kao administrator

title Ultra Creative Suite - Installer

echo.
echo  Ultra Creative Suite — Installer
echo  ----------------------------------
echo.

:: Provjeri administratorska prava
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo  GRESKA: Pokrenite kao administrator!
    echo  Desni klik na install.bat - "Pokreni kao administrator"
    echo.
    pause
    exit /b 1
)

:: Pokreni PowerShell installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0UltraInstaller.ps1"

exit /b 0
