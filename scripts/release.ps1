[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [Parameter()]
    [string]$ProjectFile = "Abel/Abel.csproj",

    [Parameter()]
    [string]$Remote = "origin",

    [Parameter()]
    [switch]$Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

function Assert-CleanWorktree {
    $status = git status --porcelain
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "Working tree is not clean. Commit or stash changes before releasing."
    }
}

function Assert-SemVer {
    param([string]$InputVersion)
    $pattern = '^\d+\.\d+\.\d+(-[0-9A-Za-z\.-]+)?$'
    if ($InputVersion -notmatch $pattern) {
        throw "Version '$InputVersion' is invalid. Use semver format like 0.1.6 or 0.1.6-beta.1."
    }
}

function Get-CurrentVersion {
    param([string]$CsprojPath)
    $content = Get-Content -Path $CsprojPath -Raw
    $match = [regex]::Match($content, '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        throw "Could not find <Version>...</Version> in '$CsprojPath'."
    }
    return $match.Groups[1].Value
}

function Set-Version {
    param(
        [string]$CsprojPath,
        [string]$NewVersion
    )

    $content = Get-Content -Path $CsprojPath -Raw
    $updated = [regex]::Replace($content, '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>", 1)
    Set-Content -Path $CsprojPath -Value $updated -NoNewline
}

function Assert-NoExistingTag {
    param(
        [string]$TagName,
        [string]$RemoteName
    )

    $localTag = git tag --list $TagName
    if (-not [string]::IsNullOrWhiteSpace($localTag)) {
        throw "Tag '$TagName' already exists locally."
    }

    $remoteTag = git ls-remote --tags $RemoteName "refs/tags/$TagName"
    if (-not [string]::IsNullOrWhiteSpace($remoteTag)) {
        throw "Tag '$TagName' already exists on remote '$RemoteName'."
    }
}

Require-Command "git"
Assert-SemVer -InputVersion $Version

$repoRoot = (Resolve-Path -Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$projectPath = Join-Path $repoRoot $ProjectFile
if (-not (Test-Path -Path $projectPath -PathType Leaf)) {
    throw "Project file not found: '$projectPath'."
}

Assert-CleanWorktree

$currentVersion = Get-CurrentVersion -CsprojPath $projectPath
if ($currentVersion -eq $Version) {
    throw "Version is already '$Version'."
}

$tag = "v$Version"
Assert-NoExistingTag -TagName $tag -RemoteName $Remote

Set-Version -CsprojPath $projectPath -NewVersion $Version
git add -- $ProjectFile
git commit -m "release: $tag"
git tag -a $tag -m "Release $tag"

Write-Host "Created release commit and tag:"
Write-Host "  version: $Version"
Write-Host "  tag:     $tag"

if ($Push) {
    git push $Remote HEAD
    git push $Remote $tag
    Write-Host "Pushed commit and tag to '$Remote'."
    Write-Host "GitHub Actions will publish after CI and release workflow checks pass."
}
else {
    Write-Host "Next steps:"
    Write-Host "  git push $Remote HEAD"
    Write-Host "  git push $Remote $tag"
}
