<#
.SYNOPSIS
    Builds the Gibi-o-matic installer (Velopack) into artifacts/releases/<runtime>.

.DESCRIPTION
    The version comes from the latest "v*" git tag (MinVer). Run from any folder:
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime linux-x64
    Windows output: Gibi-o-matic-win-Setup.exe (installs per user, no administrator),
    Gibi-o-matic-win-Portable.zip and the update packages. Linux output: Gibi-o-matic.AppImage and
    its update packages. Either can be built on Windows or on Linux.

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
$project = Join-Path $root 'src/GibiOMatic.App/GibiOMatic.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$forWindows = $Runtime -like 'win-*'

# The installer's id is also the install folder (%LocalAppData%\Gibi-o-matic): it must not be
# "GibiOMatic", the app's data folder, or uninstalling would delete the index and the logs.
$packId = 'Gibi-o-matic'

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

$publish = Join-Path $artifacts "publish/$Runtime"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
Invoke-Dotnet publish $project --configuration Release --runtime $Runtime --self-contained `
    --output $publish -p:DebugType=none

# The release left in the folder by the previous run becomes a delta update.
# "[win]" / "[linux]" lets vpk package for the other system too.
$releases = Join-Path $artifacts "releases/$Runtime"
Invoke-Dotnet tool restore
Invoke-Dotnet vpk $(if ($forWindows) { '[win]' } else { '[linux]' }) pack --packId $packId --packVersion $version --packDir $publish `
    --mainExe $(if ($forWindows) { 'GibiOMatic.exe' } else { 'GibiOMatic' }) `
    --packTitle 'Gibi-o-matic' --packAuthors 'Gustavo Ayres' `
    --icon (Join-Path $root 'src/GibiOMatic.App/Assets/gibiomatic.ico') `
    --channel $(if ($forWindows) { 'win' } else { 'linux' }) `
    --outputDir $releases

# Only the newest release is needed: its full package makes the next delta, and installed apps
# update from GitHub. Older packages would pile up (about 60 MB each).
Get-ChildItem $releases -Filter "$packId-*.nupkg" |
    Where-Object { $_.Name -notlike "$packId-$version-*" } |
    Remove-Item -Force

Write-Host ''
Write-Host "Version: $version"
Write-Host "Output:  $releases"
