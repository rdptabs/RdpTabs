@echo off
rem ============================================================================
rem  RdpTabs zero-install build script
rem
rem  This machine has no .NET SDK, but it does have the .NET Framework 4.8 runtime and the Roslyn
rem  compiler that ships with Visual Studio, so we invoke csc.exe directly -- no dotnet build, no MSBuild
rem  project file.
rem
rem  Usage:  build.cmd          build bin\RdpTabs.exe
rem          build.cmd run      build, then run it
rem          build.cmd test     build, then run the self test (--selftest)
rem ============================================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\mscorlib.dll" set "FX=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FX%\mscorlib.dll" (
    echo [error] Could not find the .NET Framework 4.x runtime directory.
    exit /b 1
)

rem ---- Locate a compiler: env var, then vswhere, then common paths, then the in-box csc (C# 5 only) ----
set "CSC="
if defined RDPTABS_CSC if exist "%RDPTABS_CSC%" set "CSC=%RDPTABS_CSC%"

if not defined CSC (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "!VSWHERE!" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "!VSWHERE!" (
        for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -property installationPath 2^>nul`) do (
            if exist "%%i\MSBuild\Current\Bin\Roslyn\csc.exe" set "CSC=%%i\MSBuild\Current\Bin\Roslyn\csc.exe"
        )
    )
)

if not defined CSC (
    for %%r in ("%ProgramFiles%\Microsoft Visual Studio" "%ProgramFiles(x86)%\Microsoft Visual Studio") do (
        for /f "delims=" %%f in ('dir /b /s "%%~r\csc.exe" 2^>nul ^| findstr /i "MSBuild\\Current\\Bin\\Roslyn\\csc.exe"') do (
            if not defined CSC set "CSC=%%f"
        )
    )
)

if not defined CSC (
    if exist "%FX%\csc.exe" (
        echo [note] No Visual Studio Roslyn compiler found; falling back to the in-box csc.exe, C# 5 only.
        set "CSC=%FX%\csc.exe"
    )
)

if not defined CSC (
    echo [error] No C# compiler found. Set RDPTABS_CSC to a csc.exe and try again.
    exit /b 1
)

echo Compiler: %CSC%
echo Framework: %FX%

if not exist bin mkdir bin

set "REFS=-r:"%FX%\mscorlib.dll" -r:"%FX%\System.dll" -r:"%FX%\System.Core.dll" -r:"%FX%\System.Drawing.dll" -r:"%FX%\System.Windows.Forms.dll""

rem ---- Icon: RdpTabs.ico is a byte-for-byte copy of mstsc.exe's Remote Desktop icon ----
rem Embedded both as the exe's win32 icon (Explorer / taskbar) and as a managed resource (sizes at run time)
set "ICON="
if exist RdpTabs.ico set "ICON=-win32icon:RdpTabs.ico -resource:RdpTabs.ico,RdpTabs.ico"

"%CSC%" -nologo -noconfig -nostdlib+ -target:winexe -platform:anycpu -optimize+ ^
    -langversion:latest -codepage:65001 -utf8output -debug:pdbonly ^
    -define:NET48 ^
    -out:bin\RdpTabs.exe -win32manifest:app.manifest %ICON% ^
    %REFS% src\*.cs
if errorlevel 1 (
    echo.
    echo [failed] Build did not succeed.
    exit /b 1
)

echo [done] bin\RdpTabs.exe

if /i "%~1"=="run" start "" "bin\RdpTabs.exe"
if /i "%~1"=="test" "bin\RdpTabs.exe" --selftest
exit /b 0
