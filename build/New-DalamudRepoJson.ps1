param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("release", "testing")]
    [string] $Channel,

    [Parameter(Mandatory = $true)]
    [string] $PluginManifestPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [string] $ReleaseDownloadUrl,

    [Parameter(Mandatory = $true)]
    [string] $TestingDownloadUrl,

    [string] $ExistingRepoJsonPath,

    [string] $ReleaseAssemblyVersion,

    [string] $RepoUrl = "https://github.com/Abelfreyja/Intoner"
)

$ErrorActionPreference = "Stop"

function Read-JsonFile([string] $Path)
{
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path))
    {
        return $null
    }

    $content = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($content))
    {
        return $null
    }

    return $content | ConvertFrom-Json
}

function ConvertTo-ManifestObject($Value)
{
    if ($null -eq $Value)
    {
        return $null
    }

    if ($Value -is [array])
    {
        return $Value | Select-Object -First 1
    }

    return $Value
}

function Remove-Property($Object, [string] $Name)
{
    if ($Object.PSObject.Properties[$Name])
    {
        $Object.PSObject.Properties.Remove($Name)
    }
}

function Set-Property($Object, [string] $Name, $Value)
{
    if ($Object.PSObject.Properties[$Name])
    {
        $Object.$Name = $Value
        return
    }

    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
}

function Copy-TestingFields($Target, $Source)
{
    foreach ($propertyName in @("TestingAssemblyVersion", "TestingDalamudApiLevel", "IsTestingExclusive"))
    {
        if ($Source.PSObject.Properties[$propertyName])
        {
            Set-Property $Target $propertyName $Source.$propertyName
        }
    }
}

function Clear-TestingFields($Object)
{
    Remove-Property $Object "TestingAssemblyVersion"
    Remove-Property $Object "TestingDalamudApiLevel"
    Remove-Property $Object "IsTestingExclusive"
}

function Get-Version([string] $Value, [string] $Name)
{
    if ([string]::IsNullOrWhiteSpace($Value))
    {
        throw "$Name is required"
    }

    return [Version]::Parse($Value)
}

$pluginManifest = Get-Content -LiteralPath (Resolve-Path -LiteralPath $PluginManifestPath) -Raw | ConvertFrom-Json
$existingManifest = ConvertTo-ManifestObject (Read-JsonFile $ExistingRepoJsonPath)
$manifest = if ($Channel -eq "testing" -and $null -ne $existingManifest)
{
    $existingManifest
}
else
{
    $pluginManifest
}

Set-Property $manifest "RepoUrl" $RepoUrl
Set-Property $manifest "DownloadLinkInstall" $ReleaseDownloadUrl
Set-Property $manifest "DownloadLinkUpdate" $ReleaseDownloadUrl
Set-Property $manifest "DownloadLinkTesting" $TestingDownloadUrl
Set-Property $manifest "LastUpdate" ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

if ($Channel -eq "release")
{
    if ($null -ne $existingManifest)
    {
        Copy-TestingFields $manifest $existingManifest
    }

    if ($manifest.PSObject.Properties["TestingAssemblyVersion"])
    {
        $releaseVersion = Get-Version $manifest.AssemblyVersion "AssemblyVersion"
        $testingVersion = Get-Version $manifest.TestingAssemblyVersion "TestingAssemblyVersion"
        if ($testingVersion -le $releaseVersion)
        {
            Clear-TestingFields $manifest
        }
    }
}
else
{
    $testingVersion = Get-Version $pluginManifest.AssemblyVersion "testing AssemblyVersion"
    $releaseVersion = if (-not [string]::IsNullOrWhiteSpace($ReleaseAssemblyVersion))
    {
        Get-Version $ReleaseAssemblyVersion "ReleaseAssemblyVersion"
    }
    else
    {
        Get-Version $manifest.AssemblyVersion "AssemblyVersion"
    }

    if ($testingVersion -le $releaseVersion)
    {
        throw "testing version '$testingVersion' must be greater than release version '$releaseVersion'"
    }

    Set-Property $manifest "AssemblyVersion" $releaseVersion.ToString()
    Set-Property $manifest "TestingAssemblyVersion" $testingVersion.ToString()
    Set-Property $manifest "TestingDalamudApiLevel" $pluginManifest.DalamudApiLevel
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory))
{
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

ConvertTo-Json -InputObject @($manifest) -Depth 16 -Compress | Set-Content -LiteralPath $OutputPath -Encoding utf8
