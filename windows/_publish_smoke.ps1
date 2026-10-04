$ErrorActionPreference = 'Stop'
$lab = 'D:\Games 3\Mugen AI Research\IKEMEN-LAB'
$win = Join-Path $lab 'windows'
$pub = Join-Path $win 'publish\win-x64'
$staging = Join-Path $win 'publish\win-x64.staging'
$backup = Join-Path $win ('publish\win-x64.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$commit = (git -C $lab rev-parse HEAD).Trim()
$subject = (git -C $lab log -1 --format='%s').Trim()
$commitDate = (git -C $lab log -1 --format='%ci').Trim()

Write-Host "COMMIT=$commit"
Write-Host "SUBJECT=$subject"
Write-Host "DATE=$commitDate"

# Stop running app so publish replace is safe
Get-Process -Name 'IKEMENLab' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping IKEMENLab pid=$($_.Id)"
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Seconds 1

Set-Location $win

Write-Host '=== restore + build + test ==='
dotnet restore .\IKEMENLab.Windows.sln
dotnet build .\IKEMENLab.Windows.sln -c Release --no-restore
dotnet test .\IKEMENLab.Windows.sln -c Release --no-build --verbosity minimal

Write-Host '=== publish to staging ==='
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
# RID restore required for net8.0-windows/win-x64 assets (NETSDK1047 if --no-restore after plain restore)
dotnet restore .\IKEMENLab.App\IKEMENLab.App.csproj -r win-x64
dotnet publish .\IKEMENLab.App\IKEMENLab.App.csproj -c Release -r win-x64 --self-contained false -o $staging --no-restore

if (-not (Test-Path (Join-Path $staging 'IKEMENLab.exe'))) {
    throw "Publish staging missing IKEMENLab.exe"
}

Write-Host '=== replace publish\win-x64 safely ==='
if (Test-Path $pub) {
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Move-Item $pub $backup
    Write-Host "Backed up previous publish -> $backup"
}
Move-Item $staging $pub
Write-Host "Published to $pub"

$exe = Join-Path $pub 'IKEMENLab.exe'
# App AssemblyName is IKEMENLab -> IKEMENLab.dll (not IKEMENLab.App.dll)
$dlls = @(
    'IKEMENLab.exe',
    'IKEMENLab.dll',
    'IKEMENLab.Core.dll',
    'IKEMENLab.Cli.dll'
) | ForEach-Object { Join-Path $pub $_ } | Where-Object { Test-Path $_ }

Write-Host '=== hashes ==='
$hashReport = @()
foreach ($p in $dlls) {
    $h = (Get-FileHash $p -Algorithm SHA256).Hash
    $item = Get-Item $p
    $line = "{0}  SHA256={1}  bytes={2}" -f $item.Name, $h, $item.Length
    Write-Host $line
    $hashReport += $line
}

# Verify Phase 1 / Phase 2 presence in published binaries (string scan) and source
Write-Host '=== Phase 1 / Phase 2 presence (published + source) ==='
$appDll = Join-Path $pub 'IKEMENLab.dll'
$coreDll = Join-Path $pub 'IKEMENLab.Core.dll'
$phaseChecks = [ordered]@{
    'Phase1_Names_VM' = @{ path = (Join-Path $win 'IKEMENLab.App\ViewModels\XRayViewModel.cs'); needles = @('RenameCommand', 'CanRenameSelected', 'CharacterNames', 'Your names', 'X-Ray name') }
    'Phase1_SemanticNames' = @{ path = (Join-Path $win 'IKEMENLab.Core'); needles = @('class SemanticNames', 'IsRenamed', 'CanRename') }
    'Phase2_AbilityLab_XAML' = @{ path = (Join-Path $win 'IKEMENLab.App\Views\XRayWindow.xaml'); needles = @('Play Ability', 'Preview State (not proof)', 'Ability Lab') }
    'Phase2_AbilityLab_Panel' = @{ path = (Join-Path $win 'IKEMENLab.App\ViewModels\AbilityPlaybackPanel.cs'); needles = @('PlayAbilityCommand', 'PreviewStateCommand') }
    'Phase2_AbilityLab_Tests' = @{ path = (Join-Path $win 'IKEMENLab.App.Tests\AbilityLabUiTests.cs'); needles = @('Ability Lab', 'Play Ability') }
}

$verify = @()
function Test-NeedlesInTree([string]$path, [string[]]$needles) {
    $hits = @{}
    foreach ($n in $needles) { $hits[$n] = $false }
    if (Test-Path $path -PathType Leaf) {
        $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        foreach ($n in $needles) {
            if ($text -and $text.Contains($n)) { $hits[$n] = $true }
        }
    } else {
        $files = Get-ChildItem -LiteralPath $path -Recurse -Include *.cs,*.xaml -ErrorAction SilentlyContinue
        foreach ($f in $files) {
            $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $text) { continue }
            foreach ($n in $needles) {
                if (-not $hits[$n] -and $text.Contains($n)) { $hits[$n] = $true }
            }
        }
    }
    return $hits
}

foreach ($key in $phaseChecks.Keys) {
    $c = $phaseChecks[$key]
    $hits = Test-NeedlesInTree $c.path $c.needles
    $ok = ($hits.Values | Where-Object { -not $_ }).Count -eq 0
    $detail = ($hits.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join '; '
    $line = if ($ok) { "PASS $key :: $detail" } else { "FAIL $key :: $detail" }
    Write-Host $line
    $verify += $line
}

# Also confirm Ability Lab / rename strings exist inside published App DLL via strings-ish scan
function Test-BinaryContains([string]$file, [string]$asciiNeedle) {
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $enc = [System.Text.Encoding]::UTF8
    $hay = $enc.GetString($bytes)
    return $hay.Contains($asciiNeedle)
}

$binChecks = @(
    @{ file = $appDll; needle = 'Play Ability'; label = 'Phase2_bin_PlayAbility' },
    @{ file = $appDll; needle = 'Preview State (not proof)'; label = 'Phase2_bin_PreviewState' },
    @{ file = $appDll; needle = 'RenameCommand'; label = 'Phase1_bin_RenameCommand' },
    @{ file = $coreDll; needle = 'SemanticNames'; label = 'Phase1_bin_SemanticNames' }
)
foreach ($b in $binChecks) {
    if (-not (Test-Path $b.file)) {
        $line = "FAIL $($b.label) :: missing $($b.file)"
        Write-Host $line
        $verify += $line
        continue
    }
    $ok = Test-BinaryContains $b.file $b.needle
    $line = if ($ok) { "PASS $($b.label) :: '$($b.needle)' in $([IO.Path]::GetFileName($b.file))" } else { "FAIL $($b.label) :: '$($b.needle)' NOT in $([IO.Path]::GetFileName($b.file))" }
    Write-Host $line
    $verify += $line
}

Write-Host '=== launch smoke test ==='
$smokeLog = Join-Path $win 'publish\win-x64-smoke-launch.log'
if (Test-Path $smokeLog) { Remove-Item $smokeLog -Force }
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
Start-Sleep -Seconds 5
$alive = -not $proc.HasExited
$exitCode = if ($proc.HasExited) { $proc.ExitCode } else { $null }
$smoke = @{
    started = $true
    pid = $proc.Id
    alive_after_5s = $alive
    exit_code = $exitCode
    path = $exe
}
$smoke | ConvertTo-Json | Set-Content $smokeLog -Encoding utf8
Write-Host ("SMOKE alive_after_5s={0} pid={1} exit={2}" -f $alive, $proc.Id, $exitCode)

if ($alive) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# Write report
$reportPath = Join-Path $win 'publish\win-x64-PUBLISH_REPORT.txt'
@(
    "IKEMEN-LAB win-x64 publish report"
    "commit=$commit"
    "subject=$subject"
    "commit_date=$commitDate"
    "branch=$(git -C $lab rev-parse --abbrev-ref HEAD)"
    "publish_dir=$pub"
    "previous_backup=$backup"
    ""
    "HASHES"
    $hashReport
    ""
    "PHASE_VERIFY"
    $verify
    ""
    "SMOKE"
    "alive_after_5s=$alive"
    "pid=$($proc.Id)"
    "exit_code=$exitCode"
) | Set-Content $reportPath -Encoding utf8

Write-Host "REPORT=$reportPath"
if (($verify | Where-Object { $_ -like 'FAIL*' }).Count -gt 0) { exit 2 }
if (-not $alive -and $exitCode -ne 0) { exit 3 }
exit 0
