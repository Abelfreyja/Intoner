param(
    [string] $OutputPath = (Join-Path $env:AppData "XIVLauncher\addon\Hooks\dev"),

    [string] $PackageUrl = "https://goatcorp.github.io/dalamud-distrib/latest.zip"
)

$ErrorActionPreference = "Stop"
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) "intoner-dalamud-latest-$PID.zip"

try
{
    Write-Host "downloading Dalamud" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $PackageUrl -OutFile $archivePath

    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

    Write-Host "installing Dalamud to $OutputPath" -ForegroundColor Cyan
    Expand-Archive -Force $archivePath $OutputPath
}
finally
{
    if (Test-Path -LiteralPath $archivePath)
    {
        Remove-Item -LiteralPath $archivePath -Force
    }
}
