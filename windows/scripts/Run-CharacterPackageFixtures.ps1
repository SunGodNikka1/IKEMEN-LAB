# Runtime fixture check for Character Package Identity / Multiple DEF installs.
# Uses disposable temp roots only (never a real IKEMEN install).
# Exit 0 when CharacterPackageInstall + PrimaryDef + LibraryPrimary suites pass.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $root "IKEMENLab.Tests"))) {
    $root = Join-Path (Split-Path $PSScriptRoot -Parent) "windows"
}
Set-Location $root

Write-Host "=== Character package identity runtime fixtures (xUnit disposable roots) ==="
Write-Host "Scenarios covered by CharacterPackageInstallTests:"
Write-Host "  - flat archive named after zip"
Write-Host "  - existing character folder kept exactly"
Write-Host "  - wrapper folders dropped"
Write-Host "  - multiple character DEFs / AI-noAI / KFM placeholder"
Write-Host "  - ambiguous DEF requires choice (folder not renamed)"
Write-Host "  - collision rename / replace / skip"
Write-Host ""

dotnet test IKEMENLab.Tests\IKEMENLab.Tests.csproj --filter "FullyQualifiedName~CharacterPackageInstall|FullyQualifiedName~PrimaryDefResolver|FullyQualifiedName~LibraryPrimaryDef|FullyQualifiedName~PreferredCharacterRosterNameHelpers" --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "PACKAGE IDENTITY FIXTURES: PASS"
exit 0
