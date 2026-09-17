param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'DeskMonitor/DeskMonitor.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if (-not $SkipTests) {
    & dotnet run --project "$root/DeskMonitor.Tests" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
}
$output = Join-Path $root "artifacts/package/v$version/win-x64"
& dotnet publish $project -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath "$root/README.md" -Destination "$output/README.md"
# Explicit payload: no settings, credentials, QA files, or debug symbols.
$names = @('DeskMonitor.exe', 'DeskMonitor.dll', 'DeskMonitor.Core.dll', 'DeskMonitor.deps.json', 'DeskMonitor.runtimeconfig.json', 'README.md')
$files = @($names | ForEach-Object {
    $file = Join-Path $output $_
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release file: $_" }
    $file
})
$releaseDir = Join-Path $root 'artifacts/releases'
$null = New-Item -ItemType Directory -Force -Path $releaseDir
$archive = Join-Path $releaseDir "DeskMonitor-v$version-win-x64.zip"
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$releaseDir/SHA256SUMS.txt" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
Write-Output "Release: $archive"
Write-Output "SHA256: $hash"
