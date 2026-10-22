<#
Runs RED's harnesses one after another (dotnet run in each harness folder) and prints one PASS or FAIL line per
harness, then re-applies the Dropbox ignore mark to the build output (tools\mark-dropbox-ignored.ps1).

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-harnesses.ps1 [-Only Name1,Name2] [-LogFolder <folder>]

Left out, as in the release steps: the *Check projects (CameraLiveCheck drives the real camera), DataUpdateHarness,
ItemRequirementHarness and WalkTemplateEvidenceHarness. Exit code 1 when any harness fails.
#>
param(
    [string[]]$Only = @(),
    [string]$LogFolder = (Join-Path ([IO.Path]::GetTempPath()) 'red-harness-logs')
)

# "-Only A,B" arrives as one string through powershell -File.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $root 'tests'
$skip = 'CameraLiveCheck', 'InspectConfigReadOnlyCheck', 'DataUpdateHarness', 'ItemRequirementHarness', 'WalkTemplateEvidenceHarness'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
New-Item -ItemType Directory -Force -Path $LogFolder | Out-Null

$harnesses = Get-ChildItem -LiteralPath $tests -Directory |
    Where-Object { (Get-ChildItem -LiteralPath $_.FullName -Filter *.csproj -File) -and ($skip -notcontains $_.Name) } |
    Where-Object { $Only.Count -eq 0 -or $Only -contains $_.Name } |
    Sort-Object Name

$failed = @()
foreach ($harness in $harnesses) {
    $log = Join-Path $LogFolder ($harness.Name + '.log')
    Push-Location -LiteralPath $harness.FullName
    try {
        & $dotnet run *> $log
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    $summary = (Get-Content -LiteralPath $log -Tail 3) -join ' | '
    if ($code -eq 0) { Write-Output ("PASS {0}  {1}" -f $harness.Name, $summary) }
    else { Write-Output ("FAIL {0} (exit {1})  {2}" -f $harness.Name, $code, $summary); $failed += $harness.Name }
}

& (Join-Path $root 'tools\mark-dropbox-ignored.ps1') -Root $root -Quiet

Write-Output ("{0} harness(es) run, {1} failed{2}" -f $harnesses.Count, $failed.Count, $(if ($failed.Count) { ': ' + ($failed -join ', ') } else { '' }))
Write-Output ("Logs: {0}" -f $LogFolder)
if ($failed.Count -gt 0) { exit 1 }
exit 0
