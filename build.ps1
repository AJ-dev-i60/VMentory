# Build VMentory — single-file self-contained exe for Windows x64.
# Requires .NET 8 SDK and internet access for the first run (to download win-x64 runtime packages).
# Subsequent builds use the local NuGet cache.
# Pass -Version 1.2.3 to embed a version number (default: 1.0.0 from csproj).

param(
    [string]$Output  = ".\dist",
    [string]$Version = ""
)

$ErrorActionPreference = 'Stop'

Write-Host "Building VMentory..." -ForegroundColor Cyan

$versionFlag = if ($Version) { "-p:Version=$Version" } else { "" }

dotnet publish `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    $(if ($versionFlag) { $versionFlag }) `
    -o $Output

if ($LASTEXITCODE -eq 0) {
    $exe = Join-Path $Output "VMentory.exe"
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Success: $exe ($size MB)" -ForegroundColor Green
} else {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}
