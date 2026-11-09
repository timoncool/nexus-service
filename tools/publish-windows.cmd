@echo off
setlocal
rem Publishes the Windows service together with the desktop-widget overlay host.
rem Needs: the .NET 10 SDK, Visual Studio Build Tools with the C++ workload,
rem and the nexus-overlay repository checked out next to this one.
rem   %1            output folder (default: publish\win-x64 in the repository root)
rem   NEXUS_DOTNET  folder of a .NET 10 SDK, when the dotnet on PATH is an older one

set "ROOT=%~dp0.."
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%ROOT%\publish\win-x64"

if defined NEXUS_DOTNET set "DOTNET_ROOT=%NEXUS_DOTNET%"
if defined NEXUS_DOTNET set "PATH=%NEXUS_DOTNET%;%PATH%"

if not exist "%ROOT%\..\nexus-overlay\Nexus.Overlay.csproj" if not exist "%ROOT%\..\overlay\Nexus.Overlay.csproj" goto :no_overlay

if defined VCINSTALLDIR goto :build
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :no_vs
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT goto :no_vs
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul

:build
rem The overlay is published by a nested dotnet call, so the linker choice goes through the environment.
set IlcUseEnvironmentalTools=true
dotnet publish "%ROOT%\Nexus.Service.csproj" -c Release -r win-x64 -p:BuildWeb=false -o "%OUT%"
if errorlevel 1 goto :failed
echo [OK] published to %OUT%
exit /b 0

:no_overlay
echo [ERROR] nexus-overlay is not checked out next to this repository
exit /b 1

:no_vs
echo [ERROR] Visual Studio C++ build tools were not found
exit /b 1

:failed
echo [ERROR] dotnet publish failed
exit /b 1
