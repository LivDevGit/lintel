# Builds the release files:
#   dist\Lintel-Setup-<version>.exe                installer (per-user, no admin)
#   dist\Lintel-<version>-portable-win-x64.zip     unzip and run Lintel.exe, no install needed
# Requires the .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# Both ship the app as a plain self-contained folder rather than a single-file exe: compressed
# single-file apps unpack into private memory at startup (roughly doubling committed RAM),
# while plain files are mapped straight from disk. The installer and the zip compress the
# download instead.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\Lintel\Lintel.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:DebugType=none")

Write-Host "Publishing Lintel $version for the installer (self-contained folder)..."
$folder = Join-Path $root "publish\app"
if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
dotnet publish $project @common -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (folder) failed" }

$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Packing the portable zip..."
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath (Join-Path $dist "Lintel-$version-portable-win-x64.zip") -CompressionLevel Optimal

Write-Host "Compiling installer..."
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "Lintel.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem $dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
