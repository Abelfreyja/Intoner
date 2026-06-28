param(
    [Parameter(Mandatory = $true)]
    [string] $RepoJsonPath,

    [string] $Branch = "repo",

    [string] $Message = "Update Dalamud repository metadata"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedRepoJson = Resolve-Path -LiteralPath $RepoJsonPath
$safeBranchName = $Branch -replace "[^A-Za-z0-9._-]", "-"
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$worktreePath = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "intoner-repo-$safeBranchName"))

function Assert-GitSuccess()
{
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

if (-not $worktreePath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "refusing to use worktree path outside temp directory: '$worktreePath'"
}

if (Test-Path -LiteralPath $worktreePath)
{
    Remove-Item -LiteralPath $worktreePath -Recurse -Force
}

git -C $repoRoot fetch origin $Branch --depth=1 2>$null
if ($LASTEXITCODE -eq 0)
{
    git -C $repoRoot worktree add $worktreePath FETCH_HEAD
    Assert-GitSuccess
}
else
{
    git -C $repoRoot worktree add --detach $worktreePath HEAD
    Assert-GitSuccess

    git -C $worktreePath checkout --orphan $Branch
    Assert-GitSuccess

    git -C $worktreePath rm -r --cached . 2>$null
    Get-ChildItem -LiteralPath $worktreePath -Force |
        Where-Object { $_.Name -ne ".git" } |
        Remove-Item -Recurse -Force
}

try
{
    Copy-Item -LiteralPath $resolvedRepoJson -Destination (Join-Path $worktreePath "repo.json") -Force
    git -C $worktreePath add repo.json

    $status = git -C $worktreePath status --short
    if ([string]::IsNullOrWhiteSpace($status))
    {
        Write-Host "repo branch is already up to date"
        return
    }

    git -C $worktreePath commit -m $Message
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }

    git -C $worktreePath push origin "HEAD:$Branch"
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}
finally
{
    git -C $repoRoot worktree remove $worktreePath --force 2>$null
}
