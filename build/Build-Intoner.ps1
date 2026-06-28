param(
    [ValidateSet("fast", "verify", "deps")]
    [string] $Mode = "fast",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [string] $Version,

    [int] $MaxCpuCount = [Math]::Max(1, [Math]::Min(2, [Environment]::ProcessorCount - 2)),

    [switch] $Restore,

    [switch] $UpdateSubmodules
)

$ErrorActionPreference  = "Stop"
$repoRoot               = Split-Path -Parent $PSScriptRoot
$dotnetPath             = "C:\Program Files\dotnet\dotnet.exe"
$projectPath            = Join-Path $repoRoot "Intoner\Intoner.csproj"
$dependencyProjectPath  = Join-Path $repoRoot "Submodules\Penumbra.GameData\Penumbra.GameData.csproj"
$mutexName              = "Local\Intoner.Build"
$mutex                  = $null
$lockTaken              = $false

$dependencyOutputs = @(
    "Submodules\Luna\Luna\bin\$Configuration\Luna.dll"
    "Submodules\Penumbra.Api\bin\$Configuration\Penumbra.Api.dll"
    "Submodules\Penumbra.GameData\bin\$Configuration\Penumbra.GameData.dll"
    "Submodules\Penumbra.String\bin\$Configuration\Penumbra.String.dll"
) | ForEach-Object { Join-Path $repoRoot $_ }

function Assert-FileExists([string] $Path, [string] $Name)
{
    if (-not (Test-Path $Path))
    {
        throw "$Name was not found at '$Path'"
    }
}

function Assert-DependencyOutputs()
{
    $missing = @($dependencyOutputs | Where-Object { -not (Test-Path $_) })
    if ($missing.Count -eq 0)
    {
        return
    }

    $missingText = $missing -join [Environment]::NewLine
    throw "dependency outputs are missing. run '.\build\Build-Intoner.ps1 -Mode deps -Restore' first.$([Environment]::NewLine)$missingText"
}

function Invoke-GitSubmoduleUpdate()
{
    Write-Host "updating Intoner submodules" -ForegroundColor Cyan
    & git -C $repoRoot -c "url.https://github.com/.insteadOf=git@github.com:" submodule update --init --recursive
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

function Get-CommonDotNetArguments([string] $TargetPath)
{
    return @(
        $TargetPath
        "--configuration"
        $Configuration
        "--verbosity"
        "minimal"
        "-p:Platform=x64"
    )
}

function Get-BuildArguments(
    [string] $TargetPath,
    [bool] $BuildProjectReferences,
    [bool] $RunAnalyzers)
{
    $arguments = @(
        "build"
    ) + (Get-CommonDotNetArguments $TargetPath) + @(
        "-m:$MaxCpuCount"
        "-nodeReuse:false"
        "-p:BuildProjectReferences=$BuildProjectReferences"
        "-p:GeneratePackageOnBuild=false"
        "-p:RunAnalyzers=$RunAnalyzers"
        "-p:RunAnalyzersDuringBuild=$RunAnalyzers"
    )

    if (-not $Restore)
    {
        $arguments += "--no-restore"
    }

    if (-not [string]::IsNullOrWhiteSpace($Version))
    {
        $arguments += "-p:Version=$Version"
    }

    return $arguments
}

function Get-RestoreArguments([string] $TargetPath)
{
    return @(
        "restore"
        $TargetPath
        "--verbosity"
        "minimal"
        "-p:Platform=x64"
        "-p:Configuration=$Configuration"
    )
}

function Invoke-DotNet(
    [string] $Label,
    [string[]] $Arguments)
{
    Write-Host $Label -ForegroundColor Cyan
    & $dotnetPath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

function Invoke-DotNetRestore([string] $TargetPath)
{
    Invoke-DotNet "restoring Intoner project ($Configuration)" (Get-RestoreArguments $TargetPath)
}

function Invoke-DotNetBuild(
    [string] $Label,
    [string] $TargetPath,
    [bool] $BuildProjectReferences,
    [bool] $RunAnalyzers)
{
    $arguments = Get-BuildArguments $TargetPath $BuildProjectReferences $RunAnalyzers
    $process = [System.Diagnostics.Process]::GetCurrentProcess()
    $previousPriority = $process.PriorityClass
    $priorityChanged = $false

    try
    {
        try
        {
            $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
            $priorityChanged = $true
        }
        catch
        {
            Write-Verbose "could not lower build process priority: $($_.Exception.Message)"
        }

        Invoke-DotNet "building $Label ($Configuration, max cpu $MaxCpuCount)" $arguments
    }
    finally
    {
        if ($priorityChanged)
        {
            $process.PriorityClass = $previousPriority
        }
    }
}

Assert-FileExists $dotnetPath "dotnet.exe"
Assert-FileExists $projectPath "project file"

try
{
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    if (-not $mutex.WaitOne(0))
    {
        throw "another Intoner build is already running"
    }

    $lockTaken = $true
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    if ($UpdateSubmodules)
    {
        Invoke-GitSubmoduleUpdate
    }

    Assert-FileExists $dependencyProjectPath "dependency project file"

    switch ($Mode)
    {
        "deps"
        {
            if ($Restore)
            {
                Invoke-DotNetRestore $projectPath
            }

            Invoke-DotNetBuild "Intoner dependencies" $dependencyProjectPath $true $false
            break
        }
        "verify"
        {
            Assert-DependencyOutputs
            Invoke-DotNetBuild "Intoner verify" $projectPath $false $true
            break
        }
        default
        {
            Assert-DependencyOutputs
            Invoke-DotNetBuild "Intoner fast" $projectPath $false $false
            break
        }
    }
}
finally
{
    if ($lockTaken -and $null -ne $mutex)
    {
        $null = $mutex.ReleaseMutex()
    }

    if ($null -ne $mutex)
    {
        $mutex.Dispose()
    }
}
