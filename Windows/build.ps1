[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64'),
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this packaging script on Windows with the .NET 8 SDK or newer.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK or newer, then open a new PowerShell window.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'BloomNative.Windows.csproj'
$outputRoot = Join-Path $repoRoot 'dist/windows'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

foreach ($rid in ($Runtime | Select-Object -Unique)) {
    # Outputs live outside Windows/ so future builds cannot glob generated files.
    $publishDir = Join-Path $outputRoot $rid
    $archivePath = Join-Path $outputRoot "BloomNative-Windows-$rid.zip"
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    & dotnet publish $project --configuration $Configuration --runtime $rid `
        --self-contained true --output $publishDir `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit $LASTEXITCODE)."
    }

    $app = Join-Path $publishDir 'BloomNative.Windows.exe'
    if (-not (Test-Path -LiteralPath $app)) {
        throw "Expected application was not produced: $app"
    }
    Copy-Item -LiteralPath $app -Destination (Join-Path $publishDir 'BloomNative.scr')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $publishDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Resources/BloomOriginal.source.json') -Destination $publishDir

    # Publishing must never package the separately downloaded creator artwork.
    $artwork = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
        Where-Object { $_.Extension -in @('.mp4', '.jpg', '.jpeg', '.png', '.part') }
    if ($artwork) {
        throw 'Refusing to package image/video artwork in the Windows archive.'
    }
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $archivePath
    Write-Host "Created $archivePath"
}
