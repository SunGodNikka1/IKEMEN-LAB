# Runtime fixture check for Refresh and Delete Character.
# Uses disposable temp roots only (never a real IKEMEN install).
# Exit 0 when the deletion, DeleteDirectory mutation and refresh suites pass. On Windows this also runs
# the open-file cases (a file held open inside the character folder must block the delete and leave
# select.def restored byte-for-byte), which other platforms cannot provoke.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $root "IKEMENLab.Tests"))) {
    $root = Join-Path (Split-Path $PSScriptRoot -Parent) "windows"
}
Set-Location $root

Write-Host "=== Refresh / Delete Character runtime fixtures (xUnit disposable roots) ==="
Write-Host "Scenarios covered:"
Write-Host "  - delete active middle / first / last roster character (next character moves up)"
Write-Host "  - delete disabled character, multi-DEF variants, nested package, unregistered character"
Write-Host "  - folder failure rolls select.def back; select.def failure deletes nothing"
Write-Host "  - Date Added / primary-DEF records retired only after success"
Write-Host "  - refresh picks up manual folder, select.def and config.ini changes"
Write-Host ""

dotnet test IKEMENLab.Tests\IKEMENLab.Tests.csproj --filter "FullyQualifiedName~CharacterDeletion|FullyQualifiedName~DeleteDirectoryMutation|FullyQualifiedName~LibraryRefresh" --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "REFRESH / DELETE FIXTURES: PASS"
exit 0
