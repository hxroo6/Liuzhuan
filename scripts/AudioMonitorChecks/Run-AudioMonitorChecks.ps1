param([string]$DotnetPath = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnet) {
        $DotnetPath = $dotnet.Source
    } elseif ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } else {
        throw 'A .NET 10 SDK is required. Pass -DotnetPath or add dotnet to PATH.'
    }
}

& $DotnetPath build (Join-Path $PSScriptRoot 'RealApi/RealApi.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Audio monitor real NAudio API build failed with exit code $LASTEXITCODE." }

& $DotnetPath run --project (Join-Path $PSScriptRoot 'AudioMonitorChecks.csproj') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "Audio monitor checks failed with exit code $LASTEXITCODE." }
