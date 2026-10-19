@echo off
cd /d "%~dp0"
title Build the Z ULT Connect installer
where dotnet >nul 2>&1
if errorlevel 1 (
  echo The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
taskkill /im ZUltConnect.exe /f >nul 2>&1

echo Step 1 of 2: building the app...
echo.
if exist "%~dp0publish" rmdir /s /q "%~dp0publish"
dotnet publish ZUltConnect.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "%~dp0publish" -nologo
if errorlevel 1 goto failed

echo.
echo Step 2 of 2: building the installer...
echo (The first time, this downloads the free WiX toolset - about a minute.)
echo.
if exist "%~dp0installer-out" rmdir /s /q "%~dp0installer-out"
dotnet build Installer\ZUltConnect.Installer.wixproj -c Release -o "%~dp0installer-out" -nologo
if errorlevel 1 goto failed

for /r "%~dp0installer-out" %%f in (*.msi) do copy /y "%%f" "%~dp0Z-ULT-Connect-Setup.msi" >nul
if not exist "%~dp0Z-ULT-Connect-Setup.msi" goto failed

echo.
echo Done! Your installer is Z-ULT-Connect-Setup.msi
echo Double-click it to install. You can share this one file with anyone.
explorer /select,"%~dp0Z-ULT-Connect-Setup.msi"
pause
exit /b 0

:failed
echo.
echo Something went wrong. Copy the text above and send it to Claude.
pause
exit /b 1
