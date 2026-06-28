param(
    [string] $ProjectPath = (Join-Path $PSScriptRoot "..\Intoner\Intoner.csproj"),

    [ValidateSet("none", "major", "minor", "patch", "build")]
    [string] $Bump = "none",

    [string] $Version
)

$ErrorActionPreference = "Stop"

function Get-ProjectVersion([xml] $ProjectXml)
{
    $value = $ProjectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($value))
    {
        throw "project does not define a Version property"
    }

    return [Version]::Parse($value)
}

function Assert-VersionComponent([int] $Value, [string] $Name)
{
    if ($Value -lt 0 -or $Value -gt 65534)
    {
        throw "version component '$Name' must be between 0 and 65534"
    }
}

function Assert-ValidVersion([Version] $Value)
{
    Assert-VersionComponent $Value.Major "major"
    Assert-VersionComponent $Value.Minor "minor"
    Assert-VersionComponent $Value.Build "build"

    if ($Value.Revision -ge 0)
    {
        Assert-VersionComponent $Value.Revision "revision"
    }
}

function New-BumpedVersion([Version] $Current, [string] $BumpKind)
{
    $revision = [Math]::Max(0, $Current.Revision)
    switch ($BumpKind)
    {
        "major" { return [Version]::new($Current.Major + 1, 0, 0, 0) }
        "minor" { return [Version]::new($Current.Major, $Current.Minor + 1, 0, 0) }
        "patch" { return [Version]::new($Current.Major, $Current.Minor, $Current.Build + 1, 0) }
        "build" { return [Version]::new($Current.Major, $Current.Minor, $Current.Build, $revision + 1) }
        default { return $Current }
    }
}

$resolvedProjectPath = Resolve-Path -LiteralPath $ProjectPath
[xml] $project = Get-Content -LiteralPath $resolvedProjectPath
$nextVersion = if (-not [string]::IsNullOrWhiteSpace($Version))
{
    [Version]::Parse($Version)
}
else
{
    New-BumpedVersion (Get-ProjectVersion $project) $Bump
}

Assert-ValidVersion $nextVersion
$project.Project.PropertyGroup.Version = $nextVersion.ToString()
$project.Save($resolvedProjectPath)
Write-Output $nextVersion.ToString()
