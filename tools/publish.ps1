<#
.SYNOPSIS
    Publishes Gibi-o-matic as a self-contained build and packs it into artifacts/.

.DESCRIPTION
    The version comes from the latest "v*" git tag (MinVer). Run from any folder:
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime linux-x64
    Output: artifacts\GibiOMatic-<version>-<runtime>.zip (Windows) or .tar.gz (Linux).

.PARAMETER Runtime
    .NET runtime identifier: win-x64 (default), win-arm64, linux-x64, linux-arm64.

.PARAMETER SkipTests
    Skips the test run before publishing.
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GibiOMatic.App\GibiOMatic.App.csproj'
$artifacts = Join-Path $root 'artifacts'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed (exit code $LASTEXITCODE)." }
}

$dirty = git -C $root status --porcelain
if ($dirty) {
    Write-Warning 'There are uncommitted changes: the build will not match any commit.'
}

if (-not $SkipTests) {
    Invoke-Dotnet test --solution (Join-Path $root 'GibiOMatic.sln') --configuration Release
}

# Same version MinVer stamps into the assemblies (without the "+commit" metadata).
$version = (& dotnet msbuild $project -target:MinVer -getProperty:MinVerVersion -p:Configuration=Release | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Could not read the version (MinVer).' }

$name = "GibiOMatic-$version-$Runtime"
$output = Join-Path $artifacts "publish\$name"
if (Test-Path $output) { Remove-Item -Recurse -Force $output }

Invoke-Dotnet publish $project --configuration Release --runtime $Runtime --self-contained `
    --output $output -p:DebugType=none

if ($Runtime -like 'win-*') {
    $package = Join-Path $artifacts "$name.zip"
    if (Test-Path $package) { Remove-Item -Force $package }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $package
} else {
    # A package made on Windows has no executable bit: run "chmod +x GibiOMatic" after extracting.
    $package = Join-Path $artifacts "$name.tar.gz"
    tar -czf $package -C $output .
    if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
}

Write-Host ''
Write-Host "Version: $version"
Write-Host "Package: $package"
