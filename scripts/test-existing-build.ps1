$ErrorActionPreference = 'Stop'
$compatibilityRoot = Split-Path -Parent $PSScriptRoot
$hostExecutable = Join-Path $compatibilityRoot 'tests\DeepNorthCompat.MonoHost\bin\Release\net48\DeepNorthCompat.MonoHost.exe'
$testExecutable = Join-Path $compatibilityRoot 'tests\DeepNorthCompat.Tests\bin\Release\net48\DeepNorthCompat.Tests.exe'
& $hostExecutable $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Offline compatibility tests failed.' }
