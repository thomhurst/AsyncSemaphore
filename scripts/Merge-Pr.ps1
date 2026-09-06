# Merge-Pr.ps1
# One-command, fail-closed merge for the issue-pr-loop skill:
#   1. runs the pure gate  Assert-PrGreen.ps1  (read-only; exits 0 only when green)
#   2. merges with  gh pr merge --squash  (only if the gate passed)
#   3. removes the PR's isolated worktree, then deletes its branches
#
# Cleanup is *part of* the merge command — an agent cannot merge and then forget
# to remove the worktree, because it is the same call. This is the durable fix for
# the worktree pile-up the prose-only rule could not guarantee.
#
# The gate stays a separate, pure predicate ON PURPOSE: Assert-PrGreen.ps1 must be
# safe to run as a check without side effects. Merge-Pr COMPOSES it; it does not
# fold merging into the assert.
#
# Worktree is resolved by matching the PR's head branch against `git worktree list`
# (robust to directory naming: pr-<N>-*, issue-<N>-*, etc.). A worktree with
# uncommitted TRACKED changes or clean local-only commits is PRESERVED, never
# force-discarded; untracked build artifacts (node_modules/bin/obj) are cleared.
#
# Usage:  pwsh scripts/Merge-Pr.ps1 -Pr 1234 [-Repo owner/name] [-Worktree <path>]
# Exit:   0 = merged (worktree removed, or preserved because dirty)
#         1 = NOT merged (gate denied, or merge failed) — nothing destroyed

[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [string]$Repo,
    [string]$Worktree
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
$repoArgs = @(); if ($Repo) { $repoArgs = @('--repo', $Repo) }

function Fail([string]$msg) { [Console]::Error.WriteLine("MERGE ABORTED #${Pr} -- $msg"); exit 1 }

# --- 1. Gate: the pure predicate decides. No merge unless it exits 0. -----------
& pwsh (Join-Path $PSScriptRoot 'Assert-PrGreen.ps1') -Pr $Pr @repoArgs
if ($LASTEXITCODE -ne 0) { Fail "Assert-PrGreen denied (exit $LASTEXITCODE). Not merging." }

# Resolve the head branch and commit before merging so cleanup remains independent
# of PR state changes and can preserve clean local-only commits.
$headJson = gh pr view $Pr @repoArgs --json headRefName,headRefOid,isCrossRepository 2>$null
if ($LASTEXITCODE -ne 0 -or -not $headJson) { Fail "could not resolve PR head (exit $LASTEXITCODE)" }
$head = $headJson | ConvertFrom-Json
$headRef = $head.headRefName
$headRefOid = $head.headRefOid
if (-not $headRef -or -not $headRefOid -or $null -eq $head.isCrossRepository) {
    Fail "PR head response omitted branch, commit, or repository identity"
}

# Main worktree path — git removals/prunes must run from a checkout that is NOT the
# one being removed; the first `worktree list` entry is always the main checkout.
$mainRepo = ((git worktree list --porcelain) | Where-Object { $_ -like 'worktree *' } |
    Select-Object -First 1) -replace '^worktree ', ''
$gitRemote = Get-GitHubRemoteName -Repo $mainRepo -GitHubRepo $Repo
if (-not $gitRemote) { Fail "no git remote matches GitHub repository '$Repo'" }

# --- 2. Merge. -----------------------------------------------------------------
gh pr merge $Pr @repoArgs --squash
$mergeExit = $LASTEXITCODE
if ($mergeExit -ne 0) {
    # gh can complete the remote merge and then fail a local follow-up. GitHub's PR
    # state is authoritative; never report an already-merged PR as an abort.
    $state = gh pr view $Pr @repoArgs --json state --jq '.state' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $state -or $state.Trim() -ne "MERGED") {
        Fail "gh pr merge failed (exit $mergeExit). Worktree untouched."
    }
    Write-Host "gh exited $mergeExit after GitHub merged #${Pr}; continuing cleanup."
}
Write-Host "Merged #${Pr} ($headRef)."

# --- 3. Remove the PR's worktree. ----------------------------------------------
if (-not $Worktree) {
    # Find the worktree whose checked-out branch matches the PR head branch.
    $wt = $null; $cur = $null
    foreach ($line in (git -C $mainRepo worktree list --porcelain)) {
        if ($line -like 'worktree *') { $cur = $line.Substring(9) }
        elseif ($line -eq "branch refs/heads/$headRef" -and
            -not (Test-SameFilesystemPath -Left $cur -Right $mainRepo)) {
            $wt = $cur
            break
        }
    }
    $Worktree = $wt
}
if (-not $Worktree) {
    Write-Host "No isolated worktree found for branch '$headRef'."
} else {
    Remove-MergedWorktree -Repo $mainRepo -Worktree $Worktree -ExpectedHead $headRefOid -Remote $gitRemote -Label "#${Pr}"
    if (Test-Path -LiteralPath $Worktree) {
        Write-Host "Preserving branches for '$headRef' because its worktree remains."
        exit 0
    }
}

# Branch deletion happens only after the worktree is detached. Asking gh to delete
# first makes a successful remote merge exit non-zero when the local branch is checked out.
if ($head.isCrossRepository) {
    Write-Host "Preserving remote branch '$headRef' because PR #${Pr} came from another repository."
} elseif (-not (Remove-RemoteBranchAtExpectedHead -Repo $mainRepo -Branch $headRef -ExpectedHead $headRefOid -Remote $gitRemote)) {
    Write-Host "Remote branch '$headRef' was absent, advanced beyond PR head $headRefOid, or could not be deleted."
}
if (-not (Remove-LocalBranchAtExpectedHead -Repo $mainRepo -Branch $headRef -ExpectedHead $headRefOid -Remote $gitRemote)) {
    Write-Host "Preserving local branch '$headRef' because it contains commits outside PR head $headRefOid or could not be deleted."
}
git -C $mainRepo worktree prune
exit 0
