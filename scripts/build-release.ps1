param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $RepoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.Version
}
$Version = $Version.TrimStart('v')

$DistDir = Join-Path $RepoRoot "dist"
$AppPublishDir = Join-Path $DistDir "app-win-x64"
$InstallerStageDir = Join-Path $DistDir "installer-stage"
$PayloadDir = Join-Path $RepoRoot "CompressIt.Installer\Payload"
$PayloadZip = Join-Path $PayloadDir "CompressIt-Payload.zip"
$PortableZip = Join-Path $DistDir "CompressIt-win-x64-v$Version.zip"
$FinalSetupExe = Join-Path $DistDir "CompressIt-Setup-v$Version.exe"

Write-Host "Building CompressIt v$Version..."
if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $DistDir, $PayloadDir | Out-Null

# 1. Publish main CompressIt app (self-contained win-x64)
dotnet publish (Join-Path $RepoRoot "CompressIt\CompressIt.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $AppPublishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish CompressIt failed." }

# 2. Package portable zip & embedded installer payload
if (Test-Path $PayloadZip) { Remove-Item $PayloadZip -Force }
Compress-Archive -Path "$AppPublishDir\*" -DestinationPath $PayloadZip -CompressionLevel Optimal
Copy-Item $PayloadZip $PortableZip -Force

# 3. Publish single-file self-contained Windows Installer (CompressIt-Setup.exe)
dotnet publish (Join-Path $RepoRoot "CompressIt.Installer\CompressIt.Installer.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $InstallerStageDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish CompressIt.Installer failed." }

$builtSetup = Join-Path $InstallerStageDir "CompressIt-Setup.exe"
Copy-Item $builtSetup $FinalSetupExe -Force

# Clean up intermediate staging directories
Remove-Item $AppPublishDir, $InstallerStageDir -Recurse -Force

Write-Host ""
Write-Host "Release artifacts ready:"
Write-Host "  Installer : $FinalSetupExe"
Write-Host "  Portable  : $PortableZip"
