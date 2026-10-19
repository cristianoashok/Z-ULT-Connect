@echo off
cd /d "%~dp0"
title Z ULT Connect
where dotnet >/dev/null 2>&1
if errorlevel 1 (
  echo The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
rem Close a running copy so it can be replaced
taskkill /im ZUltConnect.exe /f >/dev/null 2>&1
echo Building Z ULT Connect... (the first time takes 1-2 minutes)
dotnet build ZUltConnect.csproj -c Release -o "%~dp0bin\run" -v q -nologo
if errorlevel 1 (
  echo.
  echo Build failed. Copy the text above and send it to Claude.
  pause
  exit /b 1
)
start "" "%~dp0bin\run\ZUltConnect.exe" %*
echo Started - look for the headphone icon by the clock.
timeout /t 3 >nul
