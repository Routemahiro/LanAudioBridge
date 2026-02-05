param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [switch] $SelfContained,

    [switch] $SingleFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'LanMicBridge\LanMicBridge.csproj'

if (-not (Test-Path $projectPath)) {
    throw "Project not found: $projectPath"
}

$dotnet = $null
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
} catch {
    $dotnet = $null
}
if (-not $dotnet) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
}
if (-not (Test-Path $dotnet)) {
    throw "dotnet.exe not found. Install .NET SDK or update PATH. Tried: $dotnet"
}

$mode = if ($SelfContained.IsPresent) { 'self-contained' } else { 'framework-dependent' }
$outputDir = Join-Path $repoRoot ("publish\LanMicBridge\{0}\{1}" -f $Runtime, $mode)
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$args = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', ($SelfContained.IsPresent.ToString().ToLowerInvariant()),
    '-o', $outputDir
)

if ($SingleFile.IsPresent) {
    $args += '-p:PublishSingleFile=true'
}

Write-Host "Publishing to: $outputDir"
Write-Host "Runtime: $Runtime  SelfContained: $($SelfContained.IsPresent)  SingleFile: $($SingleFile.IsPresent)"
& $dotnet @args

Write-Host ""
Write-Host "Done."
Write-Host ("Run: {0}" -f (Join-Path $outputDir 'LanMicBridge.exe'))
