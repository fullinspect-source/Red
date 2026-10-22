<#
Marks RED's build output and old snapshot folders in this source tree as ignored by Dropbox (the com.dropbox.ignored
stream, Dropbox's own mechanism), so they stay on this PC and are not synced to dropbox.com or other computers.
A folder that a clean deletes and a build recreates loses the mark, so the build scripts and the harness runner call
this after building. It only adds the mark: it never moves, deletes or changes a file. Missing folders are skipped.

  powershell -NoProfile -ExecutionPolicy Bypass -File tools\mark-dropbox-ignored.ps1 [-Root <source folder>] [-Quiet]

Folders: bin, obj, publish, Publish-current, red-release-pre-sync-backups, work, artifacts, RedCamera.Core\bin and
obj, tests\artifacts, and every tests\*\bin and tests\*\obj. Exit code 1 only when a folder could not be marked.
#>
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$Quiet
)

$targets = New-Object System.Collections.Generic.List[string]
foreach ($name in 'bin', 'obj', 'publish', 'Publish-current', 'red-release-pre-sync-backups', 'work', 'artifacts') {
    $targets.Add((Join-Path $Root $name))
}
foreach ($name in 'bin', 'obj') { $targets.Add((Join-Path $Root (Join-Path 'RedCamera.Core' $name))) }
$targets.Add((Join-Path $Root (Join-Path 'tests' 'artifacts')))
$tests = Join-Path $Root 'tests'
if (Test-Path -LiteralPath $tests -PathType Container) {
    foreach ($harness in Get-ChildItem -LiteralPath $tests -Directory) {
        foreach ($name in 'bin', 'obj') { $targets.Add((Join-Path $harness.FullName $name)) }
    }
}

$marked = 0; $already = 0; $failed = 0
foreach ($folder in $targets) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
    $current = $null
    try { $current = Get-Content -LiteralPath $folder -Stream com.dropbox.ignored -ErrorAction Stop } catch { }
    if ("$current".Trim() -eq '1') { $already++; continue }
    try {
        Set-Content -LiteralPath $folder -Stream com.dropbox.ignored -Value 1 -ErrorAction Stop
        $marked++
    }
    catch {
        $failed++
        Write-Warning ("Could not mark {0}: {1}" -f $folder, $_.Exception.Message)
    }
}
if (-not $Quiet -or $marked -gt 0 -or $failed -gt 0) {
    Write-Output ("Dropbox ignore mark: {0} folder(s) newly marked, {1} already marked, {2} failed." -f $marked, $already, $failed)
}
if ($failed -gt 0) { exit 1 }
exit 0
