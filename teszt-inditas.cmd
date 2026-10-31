@echo off
rem Builds the mod and starts Vintage Story with a separate test data folder (.devdata),
rem so your normal mods and saves are not loaded or touched.
cd /d "%~dp0"
if not defined VINTAGE_STORY set "VINTAGE_STORY=D:\Vintagestory"

echo Building mod...
dotnet build livestockwelfare\livestockwelfare.csproj -c Debug
if errorlevel 1 (
    echo.
    echo Build failed - see the errors above.
    pause
    exit /b 1
)

echo Starting Vintage Story (test data folder: %~dp0.devdata)...
start "" "%VINTAGE_STORY%\Vintagestory.exe" --tracelog --dataPath "%~dp0.devdata" --addModPath "%~dp0livestockwelfare\bin\Debug\Mods" --addOrigin "%~dp0livestockwelfare\assets"
