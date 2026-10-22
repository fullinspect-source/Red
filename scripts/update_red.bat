@echo off
setlocal EnableExtensions EnableDelayedExpansion
title RED Updater

:: Self-elevate to Administrator if needed
fltmc >nul 2>&1
if not errorlevel 1 goto :elevated_start

echo.
echo  =====================================================
echo    RED Updater  ^|  Administrator access required
echo  =====================================================
echo.
echo  This updater needs Administrator access to update C:\Red.
echo.
echo  Press any key, then click YES on the Windows prompt.
echo  If no prompt appears, right-click this file and choose
echo  Run as administrator.
echo.
pause >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:elevated_start
color 0A
cls

set "INSTALL_DIR=C:\Red"
set "DOWNLOAD_ZIP=%TEMP%\red2_update.zip"
set "EXTRACT_DIR=%TEMP%\red2_extract"
:: The backup goes into the Windows account that approved the Administrator prompt (its %LOCALAPPDATA%).
set "BACKUP_ROOT=%LOCALAPPDATA%\RED_Backups"
set "BACKUPS_KEPT=3"
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%I"
set "BACKUP_DIR=%BACKUP_ROOT%\before-red2-%STAMP%"
set "LOG_FILE=%BACKUP_DIR%\update-red2.log"

echo.
echo  =====================================================
echo    RED Updater
echo  =====================================================
echo.
echo  This will close RED, back up your RED license, settings
echo  and user data, download the latest RED release, install
echo  it, repair the Desktop shortcut, and launch RED.
echo.
echo  Backup/log folder, in the Windows account that approved
echo  the Administrator prompt (the newest %BACKUPS_KEPT% are kept):
echo  %BACKUP_DIR%
echo.
echo  Close any open inspection work first. Press any key to continue.
echo  Close this window now to cancel. Nothing has been changed yet.
pause >nul

:: Storage audit F3: nothing is written before this point, so a cancelled run leaves nothing behind.
mkdir "%BACKUP_DIR%" >nul 2>&1
call :log "Starting RED 2.0 updater"
call :log "Install dir: %INSTALL_DIR%"
call :log "Backup dir: %BACKUP_DIR%"

call :log "Closing running RED processes"
echo.
echo  [1 of 7] Closing RED if it is open...
taskkill /IM Red.exe /F >>"%LOG_FILE%" 2>&1
taskkill /IM Red2Dev.exe /F >>"%LOG_FILE%" 2>&1
taskkill /IM InspectionEditor.exe /F >>"%LOG_FILE%" 2>&1
timeout /t 2 /nobreak >nul
echo          Done.

echo  [2 of 7] Backing up licenses, settings, and user data...
call :backup_preserved
call :prune_backups
call :log "Backup complete"
echo          Backup complete.

echo  [3 of 7] Downloading latest RED release from GitHub...
if exist "%DOWNLOAD_ZIP%" del /f /q "%DOWNLOAD_ZIP%" >>"%LOG_FILE%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $r=Invoke-RestMethod 'https://api.github.com/repos/fullinspect-source/Red/releases/latest' -Headers @{'User-Agent'='RED-2-Updater'}; $asset=$r.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1; if(-not $asset){ throw 'No ZIP asset found on latest RED release.' }; Write-Host ('Latest release: ' + $r.tag_name + ' / ' + $asset.name); Invoke-WebRequest -Uri $asset.browser_download_url -OutFile '%DOWNLOAD_ZIP%' -UseBasicParsing" >>"%LOG_FILE%" 2>&1
if errorlevel 1 goto :download_failed
if not exist "%DOWNLOAD_ZIP%" goto :download_failed
for %%A in ("%DOWNLOAD_ZIP%") do if %%~zA LSS 1000000 goto :download_failed
echo          Download complete.

echo  [4 of 7] Extracting and verifying RED...
if exist "%EXTRACT_DIR%" rd /s /q "%EXTRACT_DIR%" >>"%LOG_FILE%" 2>&1
mkdir "%EXTRACT_DIR%" >>"%LOG_FILE%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Expand-Archive -Path '%DOWNLOAD_ZIP%' -DestinationPath '%EXTRACT_DIR%' -Force" >>"%LOG_FILE%" 2>&1
if errorlevel 1 goto :extract_failed
if not exist "%EXTRACT_DIR%\Red.exe" goto :verify_failed
if not exist "%EXTRACT_DIR%\SixLabors.ImageSharp.dll" goto :verify_failed
if not exist "%EXTRACT_DIR%\version.txt" call :log "WARNING: version.txt missing from release ZIP"
if not exist "%EXTRACT_DIR%\quick_comments.json" call :log "WARNING: quick_comments.json missing from release ZIP"
if not exist "%EXTRACT_DIR%\inspector_stats.json" call :log "WARNING: inspector_stats.json missing from release ZIP"
if not exist "%EXTRACT_DIR%\inspection_types.csv" call :log "WARNING: inspection_types.csv missing from release ZIP"
echo          Verified Red.exe.

echo  [5 of 7] Installing RED...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%" >>"%LOG_FILE%" 2>&1
xcopy /E /Y /Q "%EXTRACT_DIR%\*" "%INSTALL_DIR%\" >>"%LOG_FILE%" 2>&1
if errorlevel 1 goto :install_failed
if exist "%INSTALL_DIR%\InspectionEditor.exe" del /f /q "%INSTALL_DIR%\InspectionEditor.exe" >>"%LOG_FILE%" 2>&1
if exist "%INSTALL_DIR%\Red2Dev.exe" del /f /q "%INSTALL_DIR%\Red2Dev.exe" >>"%LOG_FILE%" 2>&1
if not exist "%INSTALL_DIR%\Red.exe" goto :install_failed
call :restore_preserved_files
call :cleanup_download
echo          Installed.

echo  [6 of 7] Repairing Desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws=New-Object -ComObject WScript.Shell; $desktop=[Environment]::GetFolderPath('Desktop'); $s=$ws.CreateShortcut([IO.Path]::Combine($desktop,'RED.lnk')); $s.TargetPath='C:\Red\Red.exe'; $s.WorkingDirectory='C:\Red'; $s.Description='RED - The Inspection Editor'; $s.IconLocation='C:\Red\Red.exe,0'; $s.Save()" >>"%LOG_FILE%" 2>&1
if errorlevel 1 call :log "WARNING: shortcut repair failed"
echo          Shortcut ready.

echo  [7 of 7] Launching RED...
start "" "%INSTALL_DIR%\Red.exe"
call :log "RED update completed successfully"

echo.
echo  =====================================================
echo    Done. RED is installed and launching.
echo.
echo    Your license/settings/user data were backed up here:
echo    %BACKUP_DIR%
echo  =====================================================
echo.
timeout /t 8 /nobreak >nul
exit /b 0

:backup_preserved
:: Storage audit F3: only what :restore_preserved_files puts back, plus RED's own small settings files, signature and
:: user data; not the whole program folder, nor RED's recovery copies, PDFs, caches or logs (those were ~220 MB a run).
set "SAVED=%BACKUP_DIR%\InstallFolder-C-Red"
mkdir "%SAVED%" >nul 2>&1
for %%F in (license.lic preferences.txt notify_custom_message.txt settings.txt) do (
    if exist "%INSTALL_DIR%\%%F" copy /Y "%INSTALL_DIR%\%%F" "%SAVED%\%%F" >>"%LOG_FILE%" 2>&1
)
if exist "%INSTALL_DIR%\userdata" call :copy_tree "%INSTALL_DIR%\userdata" "%SAVED%\userdata" /E
set "SAVED=%BACKUP_DIR%\LocalAppData-RED"
if exist "%LOCALAPPDATA%\RED" (
    call :log "Backing up the top-level files, userdata and Signature of %LOCALAPPDATA%\RED"
    call :copy_tree "%LOCALAPPDATA%\RED" "%SAVED%"
    if exist "%LOCALAPPDATA%\RED\userdata" call :copy_tree "%LOCALAPPDATA%\RED\userdata" "%SAVED%\userdata" /E
    if exist "%LOCALAPPDATA%\RED\Signature" call :copy_tree "%LOCALAPPDATA%\RED\Signature" "%SAVED%\Signature" /E
) else (
    call :log "Not found, skipped backup: %LOCALAPPDATA%\RED"
)
exit /b 0

:copy_tree
robocopy "%~1" "%~2" %3 /R:1 /W:1 >>"%LOG_FILE%" 2>&1
if !errorlevel! GEQ 8 call :log "WARNING: backup had errors for %~1"
exit /b 0

:prune_backups
:: Storage audit F3: keep the newest %BACKUPS_KEPT% backup folders made by this updater; older ones are deleted. Only
:: folders named before-red2-yyyyMMdd-HHmmss, never a link.
set /a FOUND=0
for /f "delims=" %%D in ('dir /b /a:d-l /o-n "%BACKUP_ROOT%\before-red2-*" 2^>nul') do (
    echo %%D| findstr /r /x "before-red2-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]" >nul
    if not errorlevel 1 (
        set /a FOUND+=1
        if !FOUND! GTR %BACKUPS_KEPT% (
            call :log "Removing old backup %%D"
            rd /s /q "%BACKUP_ROOT%\%%D" >>"%LOG_FILE%" 2>&1
        )
    )
)
exit /b 0

:cleanup_download
:: Storage audit F3: the downloaded zip and its unpacked copy (about 315 MB) are deleted, whether the update worked or not.
if exist "%DOWNLOAD_ZIP%" del /f /q "%DOWNLOAD_ZIP%" >>"%LOG_FILE%" 2>&1
if exist "%EXTRACT_DIR%" rd /s /q "%EXTRACT_DIR%" >>"%LOG_FILE%" 2>&1
exit /b 0

:restore_preserved_files
call :log "Restoring preserved license/settings/user files where safe"
if exist "%BACKUP_DIR%\InstallFolder-C-Red\license.lic" copy /Y "%BACKUP_DIR%\InstallFolder-C-Red\license.lic" "%INSTALL_DIR%\license.lic" >>"%LOG_FILE%" 2>&1
if not exist "%INSTALL_DIR%\license.lic" if exist "%BACKUP_DIR%\LocalAppData-RED\license.lic" copy /Y "%BACKUP_DIR%\LocalAppData-RED\license.lic" "%INSTALL_DIR%\license.lic" >>"%LOG_FILE%" 2>&1
if exist "%BACKUP_DIR%\InstallFolder-C-Red\preferences.txt" copy /Y "%BACKUP_DIR%\InstallFolder-C-Red\preferences.txt" "%INSTALL_DIR%\preferences.txt" >>"%LOG_FILE%" 2>&1
if exist "%BACKUP_DIR%\InstallFolder-C-Red\notify_custom_message.txt" copy /Y "%BACKUP_DIR%\InstallFolder-C-Red\notify_custom_message.txt" "%INSTALL_DIR%\notify_custom_message.txt" >>"%LOG_FILE%" 2>&1
if exist "%BACKUP_DIR%\InstallFolder-C-Red\userdata" robocopy "%BACKUP_DIR%\InstallFolder-C-Red\userdata" "%INSTALL_DIR%\userdata" /E /R:1 /W:1 >>"%LOG_FILE%" 2>&1
if exist "%BACKUP_DIR%\InstallFolder-C-Red\settings.txt" (
    findstr /B /I "xai-" "%BACKUP_DIR%\InstallFolder-C-Red\settings.txt" >nul 2>&1
    if errorlevel 1 (
        copy /Y "%BACKUP_DIR%\InstallFolder-C-Red\settings.txt" "%INSTALL_DIR%\settings.txt" >>"%LOG_FILE%" 2>&1
    ) else (
        call :log "Old xAI settings.txt was not restored; AI remains disabled until a Gemini key is added manually."
    )
)
exit /b 0

:download_failed
call :log "ERROR: download failed"
echo.
echo  ERROR: Download failed. Nothing was installed.
goto :fail

:extract_failed
call :log "ERROR: extract failed"
echo.
echo  ERROR: The download could not be extracted. Nothing was installed.
goto :fail

:verify_failed
call :log "ERROR: Red.exe or SixLabors.ImageSharp.dll missing after extract"
echo.
echo  ERROR: The download is missing RED or its photo processor. Nothing was installed.
goto :fail

:install_failed
call :log "ERROR: install failed"
echo.
echo  ERROR: Install failed. Your backup is safe at:
echo  %BACKUP_DIR%
goto :fail

:fail
call :cleanup_download
echo.
echo  Log file:
echo  %LOG_FILE%
echo.
echo  Please send that log file to Trent if this keeps happening.
echo.
pause
exit /b 1

:log
echo [%DATE% %TIME%] %~1>>"%LOG_FILE%"
exit /b 0
