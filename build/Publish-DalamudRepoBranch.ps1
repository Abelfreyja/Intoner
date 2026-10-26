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
$worktreePath = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "intoner-repo-$safeBranchName-$([Guid]::NewGuid().ToString('N'))"))

function Assert-GitSuccess()
{
    if ($LASTEXITCODE -ne 0)
    {
        throw "git command failed with exit code $LASTEXITCODE"
    }
}

if (-not $worktreePath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "refusing to use worktree path outside temp directory: '$worktreePath'"
}

$worktreeAdded = $false
try
{
    git -C $repoRoot fetch origin "refs/heads/$Branch" --depth=1
    Assert-GitSuccess

    git -C $repoRoot worktree add --detach $worktreePath FETCH_HEAD
    Assert-GitSuccess
    $worktreeAdded = $true

    Copy-Item -LiteralPath $resolvedRepoJson -Destination (Join-Path $worktreePath "repo.json") -Force
    git -C $worktreePath add repo.json
    Assert-GitSuccess

    $status = git -C $worktreePath status --short
    Assert-GitSuccess
    if ([string]::IsNullOrWhiteSpace($status))
    {
        Write-Host "repo branch is already up to date"
        return
    }

    git -C $worktreePath commit -m $Message
    Assert-GitSuccess

    git -C $worktreePath push origin "HEAD:refs/heads/$Branch"
    Assert-GitSuccess
}
finally
{
    if ($worktreeAdded)
    {
        git -C $repoRoot worktree remove $worktreePath --force
        Assert-GitSuccess
    }
}
