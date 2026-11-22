[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\Vape Meter 1.0')
)

$ErrorActionPreference = 'Stop'
$toolchain = Join-Path (Split-Path -Parent $PSScriptRoot) 'toolchain'
$dotnet = Join-Path $toolchain 'dotnet\dotnet.exe'
$project = Join-Path $PSScriptRoot 'VapeMeter.Windows\VapeMeter.Windows.csproj'
$env:DOTNET_CLI_HOME = Join-Path $toolchain '.state'
$env:NUGET_PACKAGES = Join-Path $toolchain '.nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet publish $project -c Release -r win-x64 --self-contained --disable-build-servers -o $OutputDirectory `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Vape Meter build failed ($LASTEXITCODE)."
}
