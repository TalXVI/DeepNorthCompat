$ErrorActionPreference = 'Stop'
$compatibilityRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $compatibilityRoot '.dotnet-home'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
Push-Location -LiteralPath $compatibilityRoot
try {
    foreach ($project in @(
        'tests\DeepNorthCompat.Tests\DeepNorthCompat.Tests.csproj',
        'tests\DeepNorthCompat.MonoHost\DeepNorthCompat.MonoHost.csproj'
    )) {
        # Regenerate project assets after relocation; never build against old absolute paths.
        & $dotnet restore $project --ignore-failed-sources --nologo -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
        & $dotnet build $project --configuration Release --no-restore --nologo -p:UseSharedCompilation=false -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    & '.\tests\DeepNorthCompat.MonoHost\bin\Release\net48\DeepNorthCompat.MonoHost.exe' '.\tests\DeepNorthCompat.Tests\bin\Release\net48\DeepNorthCompat.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Offline compatibility tests failed.' }
} finally {
    Pop-Location
}
