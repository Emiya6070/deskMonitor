param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $SkipTests) {
    & dotnet run --project "$root/DeskMonitor.Tests" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
}
& dotnet publish "$root/DeskMonitor/DeskMonitor.csproj" -c Release --self-contained false -o "$root/artifacts/app"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Ready: $root/artifacts/app/DeskMonitor.exe"
