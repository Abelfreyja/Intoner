param(
    [string] $ProjectPath = (Join-Path $PSScriptRoot "..\Intoner\Intoner.csproj"),

    [Parameter(Mandatory = $true)]
    [ValidateSet("release", "testing")]
    [string] $Channel,

    [ValidateSet("none", "major", "minor", "patch", "build")]
    [string] $Bump = "patch",

    [string] $Version,

    [string] $GitHubOutputPath
)

$ErrorActionPreference = "Stop"
$versionScript = Join-Path $PSScriptRoot "Set-IntonerVersion.ps1"

function Get-ProjectVersion([string] $Path)
{
    [xml] $project = Get-Content -LiteralPath $Path
    return [Version]::Parse($project.Project.PropertyGroup.Version)
}

function Get-TestingVersion([Version] $BaseVersion, [string] $ExplicitVersion)
{
    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion))
    {
        return [Version]::Parse($ExplicitVersion).ToString()
    }

    if ([string]::IsNullOrWhiteSpace($env:GITHUB_RUN_NUMBER))
    {
        throw "GITHUB_RUN_NUMBER is required when testing version is not set explicitly"
    }

    $revision = [int] $env:GITHUB_RUN_NUMBER
    if ($revision -gt 65534)
    {
        throw "GitHub run number '$revision' is too large for an assembly version component. Set an explicit testing version."
    }

    return "$($BaseVersion.Major).$($BaseVersion.Minor).$($BaseVersion.Build).$revision"
}

function Write-OutputValue([string] $Name, [string] $Value)
{
    if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath))
    {
        Add-Content -LiteralPath $GitHubOutputPath -Value "$Name=$Value"
    }
}

$resolvedProjectPath = Resolve-Path -LiteralPath $ProjectPath
$baseVersion = Get-ProjectVersion $resolvedProjectPath

if ($Channel -eq "release")
{
    if (-not [string]::IsNullOrWhiteSpace($Version))
    {
        $resolvedVersion = & $versionScript -ProjectPath $resolvedProjectPath -Version $Version
    }
    elseif ($Bump -ne "none")
    {
        $resolvedVersion = & $versionScript -ProjectPath $resolvedProjectPath -Bump $Bump
    }
    else
    {
        $resolvedVersion = $baseVersion.ToString()
    }

    $buildVersion = $resolvedVersion
}
else
{
    $buildVersion = Get-TestingVersion $baseVersion $Version
    $resolvedVersion = $buildVersion
}

Write-OutputValue "base_version" $baseVersion.ToString()
Write-OutputValue "build_version" $buildVersion
Write-OutputValue "version" $resolvedVersion

[pscustomobject]@{
    BaseVersion  = $baseVersion.ToString()
    BuildVersion = $buildVersion
    Version      = $resolvedVersion
}
