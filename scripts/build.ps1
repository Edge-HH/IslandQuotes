[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$CreateCipx,
    [switch]$SkipRestore,
    [string]$DotnetPath = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'IslandQuotes.csproj'
$targetFramework = 'net8.0-windows'

function Fail([string]$Message) {
    Write-Host ''
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Get-DotnetExecutable {
    param([string]$ExplicitPath, [string]$Root)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            Fail "Requested dotnet executable was not found: $resolved"
        }
        return $resolved
    }

    $local = Join-Path $Root '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local -PathType Leaf) {
        return $local
    }

    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        Fail "No .NET executable found. Install the .NET 10 SDK into '$Root\.tools\dotnet' or put 'dotnet' on PATH."
    }
    return $command.Source
}

Write-Host 'IslandQuotes build helper' -ForegroundColor Cyan
Write-Host "Repository : $repoRoot"
Write-Host "Project    : $projectPath"
Write-Host "Config     : $Configuration"
Write-Host "CIPX       : $([bool]$CreateCipx)"

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    Fail "IslandQuotes.csproj was not found at '$projectPath'."
}

$dotnet = Get-DotnetExecutable -ExplicitPath $DotnetPath -Root $repoRoot
Write-Host "dotnet     : $dotnet"

$versionLines = @(& $dotnet --version 2>&1)
$versionExitCode = $LASTEXITCODE
if ($versionExitCode -ne 0) {
    Fail "Unable to query the .NET SDK version (exit $versionExitCode):`n$($versionLines -join [Environment]::NewLine)"
}
$sdkVersion = ($versionLines | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Last 1).ToString().Trim()
if ($sdkVersion -notmatch '^(?<major>\d+)\.') {
    Fail "Could not parse the .NET SDK version from '$sdkVersion'."
}
$sdkMajor = [int]$Matches['major']
Write-Host "SDK        : $sdkVersion"
if ($sdkMajor -lt 10) {
    Fail ".NET SDK 10 or newer is required, but '$dotnet' reports $sdkVersion. Expected .tools\dotnet\dotnet.exe (installed SDK) or a PATH .NET 10 SDK."
}

# ClassIsland's CreateCipx target invokes `pwsh` for checksum generation.
# Windows can be missing PowerShell 7 even when the .NET 10 SDK is present.
# A command-line property is a global MSBuild property, so it overrides the
# hard-coded default in ClassIsland.PluginSdk.targets.
$powershellBinaryOverride = ''
if ($CreateCipx) {
    $pwshCommand = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $pwshCommand) {
        $windowsPowerShell = Get-Command powershell.exe -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $windowsPowerShell) {
            $powershellBinaryOverride = 'powershell.exe'
            Write-Host 'pwsh was not found; CIPX checksum generation will use powershell.exe.'
        }
        else {
            Write-Host 'WARNING: neither pwsh nor powershell.exe was found; CIPX checksum generation may fail.' -ForegroundColor Yellow
        }
    }
}

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        Write-Host ''
        Write-Host '==> dotnet restore' -ForegroundColor Cyan
        & $dotnet restore $projectPath
        $restoreExitCode = $LASTEXITCODE
        if ($restoreExitCode -ne 0) {
            Fail "dotnet restore failed with exit code $restoreExitCode."
        }
    }
    else {
        Write-Host ''
        Write-Host '==> restore skipped (-SkipRestore)' -ForegroundColor Yellow
    }

    $buildArgs = @('build', $projectPath, '-c', $Configuration)
    if ($SkipRestore) {
        $buildArgs += '--no-restore'
    }
    if ($CreateCipx) {
        $buildArgs += '-p:CreateCipx=true'
        if (-not [string]::IsNullOrWhiteSpace($powershellBinaryOverride)) {
            $buildArgs += "-p:PowershellBinaryName=$powershellBinaryOverride"
        }
    }

    Write-Host ''
    Write-Host "==> dotnet $($buildArgs -join ' ')" -ForegroundColor Cyan
    & $dotnet @buildArgs
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        Fail "dotnet build failed with exit code $buildExitCode."
    }
}
finally {
    Pop-Location
}

$binDir = Join-Path $repoRoot "bin\$Configuration\$targetFramework"
$assemblyPath = Join-Path $binDir 'IslandQuotes.dll'
$manifestPath = Join-Path $binDir 'manifest.yml'
$iconPath = Join-Path $binDir 'icon.png'
$cipxPath = Join-Path $repoRoot 'cipx\IslandQuotes.cipx'

Write-Host ''
Write-Host 'Build outputs:' -ForegroundColor Green
$artifacts = @($assemblyPath, $manifestPath, $iconPath)
if ($CreateCipx) {
    $artifacts += $cipxPath
    $checksumPath = Join-Path $repoRoot 'cipx\checksums.md'
    if (Test-Path -LiteralPath $checksumPath -PathType Leaf) {
        $artifacts += $checksumPath
    }
}
$missingArtifacts = @()
foreach ($artifact in $artifacts) {
    if (Test-Path -LiteralPath $artifact -PathType Leaf) {
        $item = Get-Item -LiteralPath $artifact
        Write-Host ("  [OK]   {0} ({1:N0} bytes)" -f $item.FullName, $item.Length)
    }
    else {
        Write-Host "  [MISS] $artifact" -ForegroundColor Red
        $missingArtifacts += $artifact
    }
}

if ($missingArtifacts.Count -gt 0) {
    Fail "Build completed, but expected artifact(s) are missing:$([Environment]::NewLine) - $($missingArtifacts -join ([Environment]::NewLine + ' - '))"
}

Write-Host ''
Write-Host 'BUILD OK' -ForegroundColor Green
exit 0