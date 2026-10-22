@echo off
echo Building Inspection Editor...
echo.

REM Restore dependencies
echo [1/3] Restoring NuGet packages...
dotnet restore
if errorlevel 1 goto error

REM Build the project
echo [2/3] Building project...
dotnet build --configuration Release
if errorlevel 1 goto error

REM Keep the build output out of Dropbox sync (a rebuilt bin or obj folder loses the mark)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\mark-dropbox-ignored.ps1" -Root "%~dp0." -Quiet

REM Run the application
echo [3/3] Launching application...
echo.
dotnet run --configuration Release

goto end

:error
echo.
echo Build failed! Please check the errors above.
pause
exit /b 1

:end
echo.
echo Build complete!
pause
