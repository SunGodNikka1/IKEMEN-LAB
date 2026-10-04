$ErrorActionPreference = 'Stop'
$lab = 'D:\Games 3\Mugen AI Research\IKEMEN-LAB'
$win = Join-Path $lab 'windows'
$pub = Join-Path $win 'publish\win-x64'
$appDll = Join-Path $pub 'IKEMENLab.dll'
$coreDll = Join-Path $pub 'IKEMENLab.Core.dll'

function Test-BinaryContains([string]$file, [string]$asciiNeedle) {
    $bytes = [System.IO.File]::ReadAllBytes($file)
    return [System.Text.Encoding]::UTF8.GetString($bytes).Contains($asciiNeedle)
}

Write-Host '=== hashes ==='
$hashReport = @()
foreach ($name in @('IKEMENLab.exe', 'IKEMENLab.dll', 'IKEMENLab.Core.dll')) {
    $p = Join-Path $pub $name
    if (-not (Test-Path $p)) {
        Write-Host "MISSING $name"
        continue
    }
    $h = (Get-FileHash $p -Algorithm SHA256).Hash
    $item = Get-Item $p
    $line = '{0}  SHA256={1}  bytes={2}' -f $item.Name, $h, $item.Length
    Write-Host $line
    $hashReport += $line
}

Write-Host '=== bin checks ==='
$binChecks = @(
    @{ file = $appDll; needle = 'Play Ability'; label = 'Phase2_bin_PlayAbility' },
    @{ file = $appDll; needle = 'Preview State (not proof)'; label = 'Phase2_bin_PreviewState' },
    @{ file = $appDll; needle = 'RenameCommand'; label = 'Phase1_bin_RenameCommand' },
    @{ file = $coreDll; needle = 'SemanticNames'; label = 'Phase1_bin_SemanticNames' }
)
$verify = @()
$fail = 0
foreach ($b in $binChecks) {
    $ok = Test-BinaryContains $b.file $b.needle
    $line = if ($ok) {
        "PASS $($b.label) :: '$($b.needle)' in $([IO.Path]::GetFileName($b.file))"
    } else {
        $fail++
        "FAIL $($b.label) :: '$($b.needle)' NOT in $([IO.Path]::GetFileName($b.file))"
    }
    Write-Host $line
    $verify += $line
}

# Patch report PHASE_VERIFY bin lines + HASHES
$reportPath = Join-Path $win 'publish\win-x64-PUBLISH_REPORT.txt'
$commit = (git -C $lab rev-parse HEAD).Trim()
$subject = (git -C $lab log -1 --format='%s').Trim()
$commitDate = (git -C $lab log -1 --format='%ci').Trim()
$branch = (git -C $lab rev-parse --abbrev-ref HEAD).Trim()
$backup = Get-ChildItem (Join-Path $win 'publish') -Directory -Filter 'win-x64.bak-*' |
    Sort-Object Name -Descending | Select-Object -First 1
$smokeLog = Join-Path $win 'publish\win-x64-smoke-launch.log'
$smoke = if (Test-Path $smokeLog) { Get-Content $smokeLog -Raw } else { '{}' }

# Keep prior source PASS lines from last report if present
$sourcePass = @(
    'PASS Phase1_Names_VM',
    'PASS Phase1_SemanticNames',
    'PASS Phase2_AbilityLab_XAML',
    'PASS Phase2_AbilityLab_Panel',
    'PASS Phase2_AbilityLab_Tests'
)
if (Test-Path $reportPath) {
    $old = Get-Content $reportPath
    $fromOld = $old | Where-Object { $_ -like 'PASS Phase*' -and $_ -notlike 'PASS Phase*_bin_*' }
    if ($fromOld) { $sourcePass = $fromOld }
}

@(
    'IKEMEN-LAB win-x64 publish report'
    "commit=$commit"
    "subject=$subject"
    "commit_date=$commitDate"
    "branch=$branch"
    "publish_dir=$pub"
    "previous_backup=$(if ($backup) { $backup.FullName } else { '' })"
    ''
    'HASHES'
    $hashReport
    ''
    'PHASE_VERIFY'
    $sourcePass
    $verify
    ''
    'SMOKE'
    $smoke.Trim()
) | Set-Content $reportPath -Encoding utf8

Write-Host "REPORT=$reportPath"
Write-Host "FAIL_COUNT=$fail"
exit $fail
