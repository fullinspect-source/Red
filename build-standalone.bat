@echo off
echo ========================================
echo  RED - Building Standalone Release
echo ========================================
echo.

cd /d "%~dp0"

echo Building standalone (self-contained, win-x64)...
dotnet publish -c Release -r win-x64 --self-contained true

if %errorlevel% neq 0 (
    echo.
    echo BUILD FAILED! Check errors above.
    pause
    exit /b 1
)

echo.
echo Creating ZIP on Desktop...
set DATESTAMP=%date:~-4%-%date:~4,2%-%date:~7,2%
set ZIPPATH=%USERPROFILE%\Desktop\RED-%DATESTAMP%.zip
powershell -Command "Compress-Archive -Path 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*' -DestinationPath '%ZIPPATH%' -Force"

echo.
echo ========================================
echo  BUILD COMPLETE!
echo  ZIP: %ZIPPATH%
echo ========================================
echo.
pause
