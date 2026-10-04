$ErrorActionPreference = 'Stop'
$lab = 'D:\Games 3\Mugen AI Research\IKEMEN-LAB'
$win = Join-Path $lab 'windows'
$pubApp = Join-Path $win 'publish\win-x64'
$pubMcp = Join-Path $win 'publish\mcp'
$stagingApp = Join-Path $win 'publish\win-x64.staging'
$stagingMcp = Join-Path $win 'publish\mcp.staging'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupApp = Join-Path $win ("publish\win-x64.bak-$stamp")
$backupMcp = Join-Path $win ("publish\mcp.bak-$stamp")
$commit = (git -C $lab rev-parse HEAD).Trim()
$subject = (git -C $lab log -1 --format='%s').Trim()
$commitDate = (git -C $lab log -1 --format='%ci').Trim()
$branch = (git -C $lab rev-parse --abbrev-ref HEAD).Trim()

Write-Host "COMMIT=$commit"
Write-Host "SUBJECT=$subject"
Write-Host "DATE=$commitDate"
Write-Host "BRANCH=$branch"

foreach ($name in @('IKEMENLab', 'ikemenlab-mcp')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping $name pid=$($_.Id)"
        Stop-Process -Id $_.Id -Force
    }
}
Start-Sleep -Seconds 1

Set-Location $win

Write-Host '=== restore + build + test ==='
dotnet restore .\IKEMENLab.Windows.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $LASTEXITCODE" }
dotnet build .\IKEMENLab.Windows.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }
dotnet test .\IKEMENLab.Windows.sln -c Release --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed: $LASTEXITCODE" }

Write-Host '=== publish App win-x64 to staging ==='
if (Test-Path $stagingApp) { Remove-Item $stagingApp -Recurse -Force }
dotnet restore .\IKEMENLab.App\IKEMENLab.App.csproj -r win-x64
if ($LASTEXITCODE -ne 0) { throw "App RID restore failed: $LASTEXITCODE" }
dotnet publish .\IKEMENLab.App\IKEMENLab.App.csproj -c Release -r win-x64 --self-contained false -o $stagingApp --no-restore
if ($LASTEXITCODE -ne 0) { throw "App publish failed: $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $stagingApp 'IKEMENLab.exe'))) {
    throw 'Publish staging missing IKEMENLab.exe'
}

Write-Host '=== publish MCP to staging ==='
if (Test-Path $stagingMcp) { Remove-Item $stagingMcp -Recurse -Force }
dotnet restore .\IKEMENLab.Mcp\IKEMENLab.Mcp.csproj -r win-x64
if ($LASTEXITCODE -ne 0) { throw "MCP RID restore failed: $LASTEXITCODE" }
dotnet publish .\IKEMENLab.Mcp\IKEMENLab.Mcp.csproj -c Release -r win-x64 --self-contained false -o $stagingMcp --no-restore
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed: $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $stagingMcp 'ikemenlab-mcp.exe'))) {
    throw 'Publish staging missing ikemenlab-mcp.exe'
}

Write-Host '=== replace publish outputs safely ==='
if (Test-Path $pubApp) {
    if (Test-Path $backupApp) { Remove-Item $backupApp -Recurse -Force }
    Move-Item $pubApp $backupApp
    Write-Host "Backed up App publish -> $backupApp"
}
Move-Item $stagingApp $pubApp
Write-Host "Published App to $pubApp"

if (Test-Path $pubMcp) {
    if (Test-Path $backupMcp) { Remove-Item $backupMcp -Recurse -Force }
    Move-Item $pubMcp $backupMcp
    Write-Host "Backed up MCP publish -> $backupMcp"
}
Move-Item $stagingMcp $pubMcp
Write-Host "Published MCP to $pubMcp"

function Get-Sha256Line([string]$path) {
    $h = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $item = Get-Item -LiteralPath $path
    return ('{0}  SHA256={1}  bytes={2}' -f $item.Name, $h, $item.Length)
}

Write-Host '=== hashes ==='
$hashPaths = @(
    (Join-Path $pubApp 'IKEMENLab.exe'),
    (Join-Path $pubApp 'IKEMENLab.dll'),
    (Join-Path $pubApp 'IKEMENLab.Core.dll'),
    (Join-Path $pubMcp 'ikemenlab-mcp.exe'),
    (Join-Path $pubMcp 'IKEMENLab.Core.dll'),
    (Join-Path $pubMcp 'ikemenlab-mcp.dll')
) | Where-Object { Test-Path $_ }
$hashReport = @()
foreach ($p in $hashPaths) {
    $line = Get-Sha256Line $p
    Write-Host $line
    $hashReport += $line
}

function Test-NeedlesInTree([string]$path, [string[]]$needles) {
    $hits = @{}
    foreach ($n in $needles) { $hits[$n] = $false }
    if (-not (Test-Path $path)) { return $hits }
    if (Test-Path $path -PathType Leaf) {
        $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        foreach ($n in $needles) {
            if ($text -and $text.Contains($n)) { $hits[$n] = $true }
        }
    } else {
        $files = Get-ChildItem -LiteralPath $path -Recurse -Include *.cs, *.xaml -ErrorAction SilentlyContinue
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

function Test-BinaryContains([string]$file, [string]$needle) {
    $bytes = [System.IO.File]::ReadAllBytes($file)
    # .NET IL often stores literals as UTF-16; also check UTF-8 for mixed metadata
    $utf8 = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($utf8.Contains($needle)) { return $true }
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    return $utf16.Contains($needle)
}

Write-Host '=== Phase 1-4 presence (source + binaries) ==='
$appDll = Join-Path $pubApp 'IKEMENLab.dll'
$coreDllApp = Join-Path $pubApp 'IKEMENLab.Core.dll'
$mcpExe = Join-Path $pubMcp 'ikemenlab-mcp.exe'
$mcpDll = Join-Path $pubMcp 'ikemenlab-mcp.dll'
$coreDllMcp = Join-Path $pubMcp 'IKEMENLab.Core.dll'

$phaseChecks = [ordered]@{
    'Phase1_Names_VM' = @{ path = (Join-Path $win 'IKEMENLab.App\ViewModels\XRayViewModel.cs'); needles = @('RenameCommand', 'CanRenameSelected', 'CharacterNames', 'Your names', 'X-Ray name') }
    'Phase1_SemanticNames' = @{ path = (Join-Path $win 'IKEMENLab.Core'); needles = @('class SemanticNames', 'IsRenamed', 'CanRename') }
    'Phase2_AbilityLab_XAML' = @{ path = (Join-Path $win 'IKEMENLab.App\Views\XRayWindow.xaml'); needles = @('Play Ability', 'Preview State (not proof)', 'Ability Lab') }
    'Phase2_AbilityLab_Panel' = @{ path = (Join-Path $win 'IKEMENLab.App\ViewModels\AbilityPlaybackPanel.cs'); needles = @('PlayAbilityCommand', 'PreviewStateCommand') }
    'Phase3_SequenceLab_XAML' = @{ path = (Join-Path $win 'IKEMENLab.App\Views\XRayWindow.xaml'); needles = @('Sequence Lab', 'Try a follow-up in the Sequence Lab') }
    'Phase3_SequenceLab_VM' = @{ path = (Join-Path $win 'IKEMENLab.App\ViewModels'); needles = @('SequenceLab', 'class SequenceLabLens') }
    'Phase4_Mcp_Host' = @{ path = (Join-Path $win 'IKEMENLab.Mcp'); needles = @('class McpApp', 'class McpServer', 'RuntimeBroker', 'ObserveTools', 'ExperimentTools') }
    'Phase4_RuntimeBroker' = @{ path = (Join-Path $win 'IKEMENLab.Core\XRay\Runtime\RuntimeBroker.cs'); needles = @('class RuntimeBroker', 'Shared') }
}

$verify = @()
foreach ($key in $phaseChecks.Keys) {
    $c = $phaseChecks[$key]
    $hits = Test-NeedlesInTree $c.path $c.needles
    $ok = ($hits.Values | Where-Object { -not $_ }).Count -eq 0
    $detail = ($hits.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join '; '
    $line = if ($ok) { "PASS $key :: $detail" } else { "FAIL $key :: $detail" }
    Write-Host $line
    $verify += $line
}

$binChecks = @(
    @{ file = $appDll; needle = 'Play Ability'; label = 'Phase2_bin_PlayAbility' },
    @{ file = $appDll; needle = 'Preview State (not proof)'; label = 'Phase2_bin_PreviewState' },
    @{ file = $appDll; needle = 'Sequence Lab'; label = 'Phase3_bin_SequenceLab' },
    @{ file = $appDll; needle = 'RenameCommand'; label = 'Phase1_bin_RenameCommand' },
    @{ file = $coreDllApp; needle = 'SemanticNames'; label = 'Phase1_bin_SemanticNames' },
    @{ file = $coreDllApp; needle = 'RuntimeBroker'; label = 'Phase4_bin_RuntimeBroker_AppCore' },
    @{ file = $mcpDll; needle = 'McpApp'; label = 'Phase4_bin_McpApp' },
    @{ file = $mcpDll; needle = 'inspect_character'; label = 'Phase4_bin_inspect_character' },
    @{ file = $coreDllMcp; needle = 'RuntimeBroker'; label = 'Phase4_bin_RuntimeBroker_McpCore' }
)
foreach ($b in $binChecks) {
    if (-not (Test-Path $b.file)) {
        $line = "FAIL $($b.label) :: missing $($b.file)"
        Write-Host $line
        $verify += $line
        continue
    }
    $ok = Test-BinaryContains $b.file $b.needle
    $line = if ($ok) {
        "PASS $($b.label) :: '$($b.needle)' in $([IO.Path]::GetFileName($b.file))"
    } else {
        "FAIL $($b.label) :: '$($b.needle)' NOT in $([IO.Path]::GetFileName($b.file))"
    }
    Write-Host $line
    $verify += $line
}

Write-Host '=== launch smoke test: IKEMENLab.exe ==='
$appExe = Join-Path $pubApp 'IKEMENLab.exe'
$appProc = Start-Process -FilePath $appExe -PassThru -WindowStyle Normal
Start-Sleep -Seconds 5
$appAlive = -not $appProc.HasExited
$appExit = if ($appProc.HasExited) { $appProc.ExitCode } else { $null }
Write-Host ("SMOKE_APP alive_after_5s={0} pid={1} exit={2}" -f $appAlive, $appProc.Id, $appExit)
if ($appAlive) {
    Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Write-Host '=== launch smoke test: ikemenlab-mcp.exe ==='
# stdio host: JSON-RPC initialize handshake proves protocol + banner on stderr
$mcpErr = Join-Path $win 'publish\mcp-smoke-stderr.log'
$mcpOutLog = Join-Path $win 'publish\mcp-smoke-stdout.log'
if (Test-Path $mcpErr) { Remove-Item $mcpErr -Force }
if (Test-Path $mcpOutLog) { Remove-Item $mcpOutLog -Force }
$mcpPsi = New-Object System.Diagnostics.ProcessStartInfo
$mcpPsi.FileName = $mcpExe
$mcpPsi.UseShellExecute = $false
$mcpPsi.RedirectStandardInput = $true
$mcpPsi.RedirectStandardOutput = $true
$mcpPsi.RedirectStandardError = $true
$mcpPsi.CreateNoWindow = $true
$mcpProc = [System.Diagnostics.Process]::Start($mcpPsi)
Start-Sleep -Milliseconds 500
$mcpAlive = -not $mcpProc.HasExited
$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
$mcpProc.StandardInput.WriteLine($init)
$mcpProc.StandardInput.Flush()
$outTask = $mcpProc.StandardOutput.ReadLineAsync()
$errTask = $mcpProc.StandardError.ReadLineAsync()
$gotOut = $outTask.Wait(5000)
$gotErr = $errTask.Wait(2000)
$stdoutLine = if ($gotOut) { $outTask.Result } else { '' }
$stderrText = if ($gotErr) { $errTask.Result } else { '' }
if (-not $mcpProc.HasExited) {
    try { $mcpProc.Kill() } catch { }
    try { Stop-Process -Id $mcpProc.Id -Force -ErrorAction SilentlyContinue } catch { }
}
try { [void]$mcpProc.WaitForExit(3000) } catch { }
$mcpExit = if ($mcpProc.HasExited) { $mcpProc.ExitCode } else { $null }
$stdoutLine | Set-Content -LiteralPath $mcpOutLog -Encoding utf8
$stderrText | Set-Content -LiteralPath $mcpErr -Encoding utf8
$bannerOk = $stderrText -match '\[mcp\]\s+ikemenlab-mcp'
$initOk = $stdoutLine -match '"name"\s*:\s*"ikemenlab-xray"' -or $stdoutLine -match 'ikemenlab-xray'
$mcpSmokeOk = $mcpAlive -and $initOk
Write-Host ("SMOKE_MCP started_alive={0} banner={1} init_ok={2} exit={3}" -f $mcpAlive, $bannerOk, $initOk, $mcpExit)
Write-Host ("SMOKE_MCP stderr={0}" -f $stderrText)
Write-Host ("SMOKE_MCP stdout_prefix={0}" -f ($(if ($stdoutLine.Length -gt 160) { $stdoutLine.Substring(0, 160) + '...' } else { $stdoutLine })))

$smoke = @{
    app = @{
        path = $appExe
        pid = $appProc.Id
        alive_after_5s = $appAlive
        exit_code = $appExit
    }
    mcp = @{
        path = $mcpExe
        pid = $mcpProc.Id
        started_alive = $mcpAlive
        banner_ok = [bool]$bannerOk
        initialize_ok = [bool]$initOk
        exit_code = $mcpExit
        stderr_log = $mcpErr
        stdout_log = $mcpOutLog
    }
}
$smoke | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $win 'publish\phase4-smoke-launch.log') -Encoding utf8

$reportPath = Join-Path $win 'publish\phase4-PUBLISH_REPORT.txt'
@(
    'IKEMEN-LAB Phase 4 win-x64 + MCP publish report'
    "commit=$commit"
    "subject=$subject"
    "commit_date=$commitDate"
    "branch=$branch"
    "publish_app=$pubApp"
    "publish_mcp=$pubMcp"
    "previous_backup_app=$backupApp"
    "previous_backup_mcp=$backupMcp"
    ''
    'HASHES'
    $hashReport
    ''
    'PHASE_VERIFY'
    $verify
    ''
    'SMOKE'
    "app_alive_after_5s=$appAlive"
    "app_pid=$($appProc.Id)"
    "app_exit_code=$appExit"
    "mcp_started_alive=$mcpAlive"
    "mcp_banner_ok=$bannerOk"
    "mcp_initialize_ok=$initOk"
    "mcp_exit_code=$mcpExit"
) | Set-Content $reportPath -Encoding utf8

Write-Host "REPORT=$reportPath"
$failCount = ($verify | Where-Object { $_ -like 'FAIL*' }).Count
if ($failCount -gt 0) { exit 2 }
if (-not $appAlive -and $appExit -ne 0) { exit 3 }
if (-not $mcpSmokeOk) { exit 4 }
exit 0
