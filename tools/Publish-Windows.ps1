param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $studioRoot 'artifacts/publish/win-x64'
}
$studioProject = Join-Path $studioRoot 'src/DAQ.CalibrationStudio.App/DAQ.CalibrationStudio.App.csproj'

dotnet publish $studioProject -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output (Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'DAQ.CalibrationStudio.App.exe')
