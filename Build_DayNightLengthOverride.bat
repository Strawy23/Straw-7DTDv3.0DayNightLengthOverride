@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem -----------------------------------------------------------------------------
rem Build script for DayNightLengthOverride
rem
rem Auto-detects a local 7 Days to Die install when possible.
rem Override manually by setting GAME_DIR before running this script, e.g.:
rem   set GAME_DIR=G:\SteamLibrary\steamapps\common\7 Days To Die
rem   Build_DayNightLengthOverride.bat
rem -----------------------------------------------------------------------------

set "OUT=dayNightLengthOverride.dll"
set "SCRIPT_DIR=%~dp0"
set "AUTO_GAME_DIR="

rem 1) Respect user-supplied GAME_DIR first.
if not "%GAME_DIR%"=="" (
  call :TryGameDir "%GAME_DIR%"
  if errorlevel 1 (
    echo GAME_DIR is set but does not look like a 7 Days to Die install:
    echo   %GAME_DIR%
    echo.
  ) else (
    set "AUTO_GAME_DIR=%GAME_DIR%"
  )
)

rem 2) Detect relative to this script. This handles source folders placed inside the game/mod tree.
if not defined AUTO_GAME_DIR (
  for %%P in ("%SCRIPT_DIR%." "%SCRIPT_DIR%.." "%SCRIPT_DIR%..\.." "%SCRIPT_DIR%..\..\..") do (
    if not defined AUTO_GAME_DIR (
      call :TryGameDir "%%~fP"
      if not errorlevel 1 set "AUTO_GAME_DIR=%%~fP"
    )
  )
)

rem 3) Detect from Steam registry, then default Steam common path.
if not defined AUTO_GAME_DIR (
  call :FindSteamPath
  if defined STEAM_PATH (
    call :TryGameDir "%STEAM_PATH%\steamapps\common\7 Days To Die"
    if not errorlevel 1 set "AUTO_GAME_DIR=%STEAM_PATH%\steamapps\common\7 Days To Die"
  )
)

rem 4) Search common Steam library locations on local drive letters.
if not defined AUTO_GAME_DIR (
  for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if not defined AUTO_GAME_DIR (
      call :TryGameDir "%%D:\SteamLibrary\steamapps\common\7 Days To Die"
      if not errorlevel 1 set "AUTO_GAME_DIR=%%D:\SteamLibrary\steamapps\common\7 Days To Die"
    )
    if not defined AUTO_GAME_DIR (
      call :TryGameDir "%%D:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
      if not errorlevel 1 set "AUTO_GAME_DIR=%%D:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
    )
    if not defined AUTO_GAME_DIR (
      call :TryGameDir "%%D:\Program Files\Steam\steamapps\common\7 Days To Die"
      if not errorlevel 1 set "AUTO_GAME_DIR=%%D:\Program Files\Steam\steamapps\common\7 Days To Die"
    )
  )
)

if not defined AUTO_GAME_DIR (
  echo Could not auto-detect the 7 Days to Die install folder.
  echo.
  echo Set GAME_DIR manually and run again, for example:
  echo   set GAME_DIR=G:\SteamLibrary\steamapps\common\7 Days To Die
  echo   Build_DayNightLengthOverride.bat
  pause
  exit /b 1
)

set "GAME_DIR=%AUTO_GAME_DIR%"

rem Locate Managed folder. Client installs use 7DaysToDie_Data, dedicated installs use 7DaysToDieServer_Data.
set "MANAGED="
for %%P in (
  "%GAME_DIR%\7DaysToDie_Data\Managed"
  "%GAME_DIR%\7DaysToDieServer_Data\Managed"
) do (
  if exist "%%~P\Assembly-CSharp.dll" if not defined MANAGED set "MANAGED=%%~P"
)

if not defined MANAGED (
  echo Could not find Assembly-CSharp.dll under detected GAME_DIR:
  echo   %GAME_DIR%
  pause
  exit /b 1
)

rem Locate C# compiler. Prefer Roslyn if installed, fall back to .NET Framework csc.
set "CSC="
for %%P in (
  "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) do (
  if exist "%%~P" if not defined CSC set "CSC=%%~P"
)

if not defined CSC (
  echo Could not find csc.exe. Install Visual Studio Build Tools or .NET Framework Developer Pack.
  pause
  exit /b 1
)

rem Locate netstandard.dll. Prefer the game's Managed folder if present; Unity layouts vary.
set "NETSTANDARD="
for %%P in (
  "%MANAGED%\netstandard.dll"
  "%GAME_DIR%\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\Managed\netstandard.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\Managed\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\4.8-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\4.7.2-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\4.7.1-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\4.7-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\unityjit-win32\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDie_Data\MonoBleedingEdge\lib\mono\unityjit-win64\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\MonoBleedingEdge\lib\mono\4.8-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\MonoBleedingEdge\lib\mono\4.7.2-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\MonoBleedingEdge\lib\mono\4.7.1-api\Facades\netstandard.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\MonoBleedingEdge\lib\mono\4.7-api\Facades\netstandard.dll"
  "%ProgramFiles%\dotnet\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1\netstandard.dll"
  "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll"
  "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll"
) do (
  if exist "%%~P" if not defined NETSTANDARD set "NETSTANDARD=%%~P"
)

if not defined NETSTANDARD (
  echo Searching recursively for netstandard.dll under:
  echo   %GAME_DIR%
  for /r "%GAME_DIR%" %%F in (netstandard.dll) do (
    if not defined NETSTANDARD set "NETSTANDARD=%%F"
  )
)

if not defined NETSTANDARD (
  echo Could not find netstandard.dll facade.
  echo.
  echo Please locate it manually, for example run this in CMD:
  echo   dir /s /b "%GAME_DIR%\netstandard.dll"
  echo.
  echo Then edit NETSTANDARD in this bat file to the full path.
  pause
  exit /b 1
)


rem Locate 0Harmony.dll. This mod uses Harmony only on the host/server process.
set "HARMONY="
for %%P in (
  "%GAME_DIR%\Mods\0_TFP_Harmony\0Harmony.dll"
  "%GAME_DIR%\Mods\0_TFP_Harmony\Harmony\0Harmony.dll"
  "%GAME_DIR%\Mods\0_TFP_Harmony\bin\0Harmony.dll"
  "%GAME_DIR%\7DaysToDie_Data\Managed\0Harmony.dll"
  "%GAME_DIR%\7DaysToDieServer_Data\Managed\0Harmony.dll"
  "%SCRIPT_DIR%0Harmony.dll"
  "%SCRIPT_DIR%..\0_TFP_Harmony\0Harmony.dll"
  "%SCRIPT_DIR%..\..\0_TFP_Harmony\0Harmony.dll"
) do (
  if exist "%%~P" if not defined HARMONY set "HARMONY=%%~P"
)

if not defined HARMONY (
  echo Could not find 0Harmony.dll.
  echo.
  echo Make sure the vanilla 0_TFP_Harmony mod is present, usually here:
  echo   %GAME_DIR%\Mods\0_TFP_Harmony\0Harmony.dll
  echo.
  echo Or copy 0Harmony.dll next to this build script and run again.
  pause
  exit /b 1
)

echo Game dir:   %GAME_DIR%
echo Compiler:   %CSC%
echo Managed:    %MANAGED%
echo netstandard:%NETSTANDARD%
echo Harmony:    %HARMONY%
echo.

rem Do not force /langversion here: the legacy .NET Framework csc.exe only supports up to C# 5.
rem The source stays C# 5 compatible, while Roslyn also builds it fine without an explicit version.
"%CSC%" ^
  /nologo ^
  /target:library ^
  /out:"%OUT%" ^
  /reference:"%NETSTANDARD%" ^
  /reference:"%HARMONY%" ^
  /reference:"%MANAGED%\Assembly-CSharp.dll" ^
  /reference:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  /reference:"%MANAGED%\UnityEngine.dll" ^
  src\dayNightLengthOverride.cs

if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

echo.
echo Build succeeded: %OUT%
echo Copy the DLL into the mod root next to ModInfo.xml.
pause
exit /b 0

:TryGameDir
set "_DIR=%~1"
if exist "%_DIR%\7DaysToDie_Data\Managed\Assembly-CSharp.dll" exit /b 0
if exist "%_DIR%\7DaysToDieServer_Data\Managed\Assembly-CSharp.dll" exit /b 0
exit /b 1

:FindSteamPath
set "STEAM_PATH="
for /f "tokens=2,*" %%A in ('reg query "HKCU\Software\Valve\Steam" /v SteamPath 2^>nul') do (
  if not defined STEAM_PATH set "STEAM_PATH=%%B"
)
for /f "tokens=2,*" %%A in ('reg query "HKLM\Software\WOW6432Node\Valve\Steam" /v InstallPath 2^>nul') do (
  if not defined STEAM_PATH set "STEAM_PATH=%%B"
)
for /f "tokens=2,*" %%A in ('reg query "HKLM\Software\Valve\Steam" /v InstallPath 2^>nul') do (
  if not defined STEAM_PATH set "STEAM_PATH=%%B"
)
exit /b 0
