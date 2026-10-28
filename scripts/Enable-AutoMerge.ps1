# Enable-AutoMerge.ps1
# Arms GitHub native auto-merge (squash) for PR #<N>, pinned to the reviewed head.
#
#   1. captures the PR head
#   2. runs  Assert-PrGreen.ps1 -ForAutoMerge  (ready, MERGEABLE, no failed check,
#      `claude-review` complete on this head, no unresolved threads) and stops on any denial
#   3. arms  gh pr merge --auto --squash --match-head-commit <head>
#
# CI may still be running: GitHub merges once branch protection's required check
# (`CI Passed`, the aggregator in dotnet.yml) passes. A later push to the PR disarms
# auto-merge (the `disarm-auto-merge` job in claude-code-review.yml), so each new head
# needs its own review cycle and a new call to this script.
#
# The caller must read and address every review body, inline finding, and issue comment
# BEFORE running this; the gate cannot judge review-body content.
#
# Auto-merge leaves the PR's worktree in place; scripts/Remove-MergedWorktrees.ps1
# cleans it up after the merge.
#
# Usage: pwsh scripts/Enable-AutoMerge.ps1 -Pr 1234 [-Repo owner/name]
# Exit:  0 = armed; 1 = not armed (reason printed to stderr).

[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [string]$Repo
)

$ErrorActionPreference = 'Stop'

function Fail([string]$reason) {
    [Console]::Error.WriteLine("NOT ARMED #${Pr} -- $reason")
    exit 1
}

$repoArgs = @()
if ($Repo) { $repoArgs = @('--repo', $Repo) }

# Capture the head before the gate so a push racing the gate cannot be armed unreviewed:
# --match-head-commit makes GitHub refuse if the head moved since.
$head = gh pr view $Pr @repoArgs --json headRefOid --jq '.headRefOid' 2>$null
if ($LASTEXITCODE -ne 0 -or -not $head) { Fail "could not read PR head (exit $LASTEXITCODE)" }
$head = $head.Trim()

$gateArgs = @('-Pr', $Pr, '-ForAutoMerge')
if ($Repo) { $gateArgs += @('-Repo', $Repo) }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Assert-PrGreen.ps1') @gateArgs
if ($LASTEXITCODE -ne 0) { Fail 'auto-merge gate denied (see above)' }

gh pr merge $Pr @repoArgs --auto --squash --match-head-commit $head
if ($LASTEXITCODE -ne 0) { Fail "gh pr merge --auto failed (exit $LASTEXITCODE)" }

Write-Host "Armed auto-merge for #${Pr} at $head."
exit 0
