@echo off
cd /d "%~dp0"
title Build Z ULT Connect
taskkill /im ZUltConnect.exe /f >/dev/null 2>&1
echo Building ZUltConnect.exe - this takes a minute or two...
echo.
dotnet publish ZUltConnect.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish" -nologo
if errorlevel 1 (
  echo.
  echo Build failed. Copy the text above and send it to Claude.
  pause
  exit /b 1
)
echo.
echo Done! Your app is publish\ZUltConnect.exe - copy it anywhere and double-click it.
explorer "%~dp0publish"
pause
