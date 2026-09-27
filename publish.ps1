$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputPath = Join-Path $projectRoot 'dist'
dotnet publish (Join-Path $projectRoot 'NXBuildCenter\NXBuildCenter.csproj') `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $outputPath

$pdbPath = Join-Path $outputPath "NXBuildCenter.pdb"
if (Test-Path -LiteralPath $pdbPath) {
  Remove-Item -LiteralPath $pdbPath -Force
}

Write-Host "Published to $outputPath"
