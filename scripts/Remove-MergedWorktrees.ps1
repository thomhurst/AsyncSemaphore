# Remove-MergedWorktrees.ps1
# Safety-net sweep for the issue-pr-loop skill: removes worktrees whose branch has
# merged or whose matching PR closed unmerged, regardless of who completed the PR.
#
# SQUASH-SAFE DETECTION — this is the whole point:
#   A squash (or rebase) merge rewrites history, so the branch tip is NOT an ancestor
#   of main. Ancestry is never used as a merge signal for named branches. GitHub's own
#   records are used in four tiers:
#     1. merged-PR head branch name (one bulk `gh pr list` call)
#     2. merged-PR head tip SHA (same call; catches detached or renamed checkouts)
#     3. detached HEAD reachable from the GitHub remote's main branch
#     4. commit-to-PR association whose head branch matches the local name/upstream
#        (survives branch deletion and the 1000-PR list window without false matches)
#
#   DELIBERATELY NOT a signal: a [gone] upstream branch. [gone] only means the remote
#   ref was deleted — which also happens when a PR is CLOSED UNMERGED and its branch
#   pruned. Treating [gone] as "merged" wrongly reaps unmerged work.
#   A closed-unmerged PR is removal evidence only when both its head branch and exact
#   head SHA match the worktree. Its local and remote branches are always preserved.
#
# ORPHANED DIRECTORIES:
#   A failed `worktree remove` followed by `worktree prune` can leave a directory whose
#   .git file points at a deleted gitdir. Such directories are invisible to
#   `git worktree list`, so known worktree roots are scanned for markers proving that a
#   directory belonged to this repo. Markerless directory shells are removed only from
#   the dedicated sibling worktree root, after a five-minute creation grace period,
#   and only with non-recursive directory deletes that cannot erase concurrent writes.
#
# Guards (never delete work):
#   - skip the main checkout and harness-managed worktrees inside it
#   - skip locked worktrees
#   - skip a branch/tip associated with an OPEN PR
#   - PRESERVE uncommitted tracked changes or commits beyond the recorded PR head
#   - preserve worktrees with no merge evidence unless -StaleDays explicitly opts in
#
# Usage:  pwsh scripts/Remove-MergedWorktrees.ps1 [-Repo owner/name] [-WhatIf] [-StaleDays n]
# Exit:   0 always (a sweep failure must not break the loop; problems are logged)

[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$WhatIf,
    [ValidateRange(0, 2147483647)]
    [int]$StaleDays = 0
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
$repoArgs = @(); if ($Repo) { $repoArgs = @('--repo', $Repo) }

function Warn([string]$m) { [Console]::Error.WriteLine("sweep: $m") }

function Test-WorktreePathRegistered {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Path
    )

    $worktreeList = @(git -C $Repo worktree list --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0) { return $true }
    foreach ($line in $worktreeList) {
        if ($line -like 'worktree *' -and
            (Test-SameFilesystemPath -Left $line.Substring(9) -Right $Path)) {
            return $true
        }
    }
    return $false
}

function Remove-EmptyDirectoryShell {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    try {
        $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Recurse -Force -ErrorAction Stop) |
            Sort-Object { $_.FullName.Length } -Descending
        foreach ($directory in $directories) {
            [IO.Directory]::Delete($directory.FullName, $false)
        }
        [IO.Directory]::Delete($Path, $false)
        return $true
    }
    catch {
        return $false
    }
}

function Test-PullRequestMatchesWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Worktree,
        [Parameter(Mandatory)]$PullRequest,
        [Parameter(Mandatory)][string]$GitRemote
    )

    if (-not $PullRequest.head.ref) { return $false }
    if ($Worktree.Detached) { return $true }
    if ($Worktree.Branch -and [string]::Equals(
        $Worktree.Branch,
        $PullRequest.head.ref,
        [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if (-not $Worktree.Branch) { return $false }

    # A renamed local PR branch retains its original upstream configuration even after
    # that remote ref is deleted. This ties association evidence to branch identity and
    # prevents a fresh branch at an old PR commit from being mistaken for merged work.
    $upstreamRemote = git -C $Worktree.Path config --get "branch.$($Worktree.Branch).remote" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $upstreamRemote) { return $false }
    $upstreamMerge = git -C $Worktree.Path config --get "branch.$($Worktree.Branch).merge" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $upstreamMerge) { return $false }

    return [string]::Equals($upstreamRemote.Trim(), $GitRemote, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($upstreamMerge.Trim(), "refs/heads/$($PullRequest.head.ref)", [StringComparison]::Ordinal)
}

# "Exit 0 always" is load-bearing: a sweep failure must never kill an otherwise-healthy
# loop iteration. $ErrorActionPreference is 'Stop', so any unguarded throw is caught,
# logged, and swallowed.
try {
    $mergedByName = @{}
    $mergedByOid = @{}
    $closedByIdentity = @{}
    $openNames = @{}
    $openOids = @{}

    $rawMerged = gh pr list @repoArgs --state merged --limit 1000 --json headRefName,headRefOid,isCrossRepository 2>$null
    if ($LASTEXITCODE -ne 0) {
        Warn "could not list merged PRs (exit $LASTEXITCODE) -- skipping sweep this round"
        exit 0
    }
    foreach ($pr in @(($rawMerged -join "`n") | ConvertFrom-Json)) {
        $name = if ($pr.headRefName) { $pr.headRefName.Trim() } else { $null }
        $oid = if ($pr.headRefOid) { $pr.headRefOid.Trim() } else { $null }
        if ($name -and $oid -and -not $mergedByName.ContainsKey($name)) { $mergedByName[$name] = $pr }
        if ($oid -and -not $mergedByOid.ContainsKey($oid)) { $mergedByOid[$oid] = $pr }
    }

    # Closed-unmerged PRs identify abandoned worktrees only by exact branch+head.
    # Failure disables this optional cleanup tier without weakening merged-PR guards.
    $rawClosed = gh pr list @repoArgs --state closed --limit 1000 --json headRefName,headRefOid,isCrossRepository 2>$null
    if ($LASTEXITCODE -ne 0) {
        Warn "could not list closed PRs (exit $LASTEXITCODE) -- dead worktrees will be preserved this round"
    }
    else {
        foreach ($pr in @(($rawClosed -join "`n") | ConvertFrom-Json)) {
            $name = if ($pr.headRefName) { $pr.headRefName.Trim() } else { $null }
            $oid = if ($pr.headRefOid) { $pr.headRefOid.Trim() } else { $null }
            if ($name -and $oid) { $closedByIdentity["$name`n$oid"] = $pr }
        }
    }

    # Open-PR lookup is a deletion guard, so failure is fail-closed.
    $rawOpen = gh pr list @repoArgs --state open --limit 1000 --json headRefName,headRefOid 2>$null
    if ($LASTEXITCODE -ne 0) {
        Warn "could not list open PRs (exit $LASTEXITCODE) -- skipping sweep this round"
        exit 0
    }
    foreach ($pr in @(($rawOpen -join "`n") | ConvertFrom-Json)) {
        if ($pr.headRefName) { $openNames[$pr.headRefName.Trim()] = $true }
        if ($pr.headRefOid) { $openOids[$pr.headRefOid.Trim()] = $true }
    }

    $mainRepo = ((git worktree list --porcelain) | Where-Object { $_ -like 'worktree *' } |
        Select-Object -First 1) -replace '^worktree ', ''
    $mainNormalized = ($mainRepo -replace '\\', '/').TrimEnd('/')
    $gitRemote = Get-GitHubRemoteName -Repo $mainRepo -GitHubRepo $Repo
    if (-not $gitRemote) {
        Warn "no git remote matches GitHub repository '$Repo' -- skipping sweep this round"
        exit 0
    }

    # `gh api` has no --repo flag, so resolve the repository slug separately.
    $slug = $Repo
    if (-not $slug) {
        $slug = gh repo view --json nameWithOwner --jq .nameWithOwner 2>$null
        if ($LASTEXITCODE -ne 0) { $slug = $null }
        elseif ($slug) { $slug = $slug.Trim() }
    }

    # Best effort: a failed refresh only disables detached-main detection. It cannot
    # make the sweep delete more work.
    git -C $mainRepo fetch --no-tags $gitRemote "+refs/heads/main:refs/remotes/$gitRemote/main" --quiet 2>$null
    $mainTip = git -C $mainRepo rev-parse --verify --quiet "refs/remotes/$gitRemote/main" 2>$null
    if ($LASTEXITCODE -ne 0) { $mainTip = $null }
    elseif ($mainTip) { $mainTip = $mainTip.Trim() }

    # Parse worktree / branch|detached / locked records.
    $worktrees = @()
    $currentPath = $null
    $branch = $null
    $detached = $false
    $locked = $false
    foreach ($line in (git -C $mainRepo worktree list --porcelain)) {
        if ($line -like 'worktree *') {
            $currentPath = $line.Substring(9)
            $branch = $null
            $detached = $false
            $locked = $false
        }
        elseif ($line -like 'branch *') { $branch = $line.Substring(7) -replace '^refs/heads/', '' }
        elseif ($line -eq 'detached') { $detached = $true }
        elseif ($line -eq 'locked' -or $line -like 'locked *') { $locked = $true }
        elseif ($line -eq '') {
            if ($currentPath) {
                $worktrees += [pscustomobject]@{
                    Path = $currentPath
                    Branch = $branch
                    Detached = $detached
                    Locked = $locked
                }
            }
            $currentPath = $null
        }
    }
    if ($currentPath) {
        $worktrees += [pscustomobject]@{
            Path = $currentPath
            Branch = $branch
            Detached = $detached
            Locked = $locked
        }
    }

    $removed = 0
    $unmatched = @()
    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($worktree in $worktrees) {
        $worktreeNormalized = ($worktree.Path -replace '\\', '/').TrimEnd('/')
        if ((Test-SameFilesystemPath -Left $worktree.Path -Right $mainRepo) -or
            $worktreeNormalized.StartsWith("$mainNormalized/", [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($worktree.Locked) {
            Write-Host "sweep: skipping locked worktree (session may own it): $($worktree.Path)"
            continue
        }
        if ($worktree.Branch -and $openNames.ContainsKey($worktree.Branch)) { continue }

        $sha = git -C $worktree.Path rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $sha) {
            $unmatched += $worktree
            continue
        }
        $sha = $sha.Trim()
        if ($openOids.ContainsKey($sha)) { continue }

        $evidence = $null
        if ($worktree.Branch -and $mergedByName.ContainsKey($worktree.Branch)) {
            $pr = $mergedByName[$worktree.Branch]
            $evidence = [pscustomobject]@{
                Reason = "merged PR head branch '$($worktree.Branch)'"
                ExpectedHead = $pr.headRefOid.Trim()
                HeadBranch = $pr.headRefName.Trim()
                IsCrossRepository = [bool]$pr.isCrossRepository
                IsMergedPr = $true
            }
        }

        if (-not $evidence -and $mergedByOid.ContainsKey($sha)) {
            $pr = $mergedByOid[$sha]
            $evidence = [pscustomobject]@{
                Reason = 'merged PR head tip SHA'
                ExpectedHead = $pr.headRefOid.Trim()
                HeadBranch = $pr.headRefName.Trim()
                IsCrossRepository = [bool]$pr.isCrossRepository
                IsMergedPr = $true
            }
        }

        $closedIdentity = if ($worktree.Branch) { "$($worktree.Branch)`n$sha" } else { $null }
        if (-not $evidence -and $closedIdentity -and $closedByIdentity.ContainsKey($closedIdentity)) {
            $pr = $closedByIdentity[$closedIdentity]
            $evidence = [pscustomobject]@{
                Reason = "closed-unmerged PR head branch '$($worktree.Branch)'"
                ExpectedHead = $pr.headRefOid.Trim()
                HeadBranch = $pr.headRefName.Trim()
                IsCrossRepository = [bool]$pr.isCrossRepository
                IsMergedPr = $false
            }
        }

        # Ancestry is safe only for a detached checkout: a named branch represents
        # intent and requires positive GitHub merge evidence.
        if (-not $evidence -and $sha -and $worktree.Detached -and $mainTip) {
            git -C $mainRepo merge-base --is-ancestor $sha $mainTip 2>$null
            if ($LASTEXITCODE -eq 0) {
                $evidence = [pscustomobject]@{
                    Reason = "detached HEAD reachable from $gitRemote/main"
                    ExpectedHead = $sha
                    HeadBranch = $null
                    IsCrossRepository = $true
                    IsMergedPr = $false
                }
            }
        }

        # Association survives squash merges, deleted/renamed branches, and the bulk
        # merged-PR query's 1000-result window. Branch identity must also match.
        if (-not $evidence -and $sha -and $slug) {
            $associationRaw = gh api "repos/$slug/commits/$sha/pulls" 2>$null
            if ($LASTEXITCODE -eq 0 -and $associationRaw) {
                $associations = @(($associationRaw -join "`n") | ConvertFrom-Json)
                if (@($associations | Where-Object { $_.state -eq 'open' }).Count -gt 0) {
                    Write-Host "sweep: skipping worktree whose HEAD belongs to an open PR association: $($worktree.Path)"
                    continue
                }
                $mergedAssociation = $null
                foreach ($candidate in @($associations | Where-Object { $_.merged_at })) {
                    if (Test-PullRequestMatchesWorktree -Worktree $worktree -PullRequest $candidate -GitRemote $gitRemote) {
                        $mergedAssociation = $candidate
                        break
                    }
                }
                if ($mergedAssociation -and $mergedAssociation.head.sha) {
                    $headRepo = $mergedAssociation.head.repo.full_name
                    $baseRepo = $mergedAssociation.base.repo.full_name
                    $isCrossRepository = -not ($headRepo -and $baseRepo -and
                        [string]::Equals($headRepo, $baseRepo, [StringComparison]::OrdinalIgnoreCase))
                    $evidence = [pscustomobject]@{
                        Reason = 'merged PR via commit association'
                        ExpectedHead = $mergedAssociation.head.sha.Trim()
                        HeadBranch = if ($mergedAssociation.head.ref) { $mergedAssociation.head.ref.Trim() } else { $null }
                        IsCrossRepository = $isCrossRepository
                        IsMergedPr = $true
                    }
                }
            }
        }

        # Explicit stale cleanup handles never-pushed scratch branches. The local branch
        # remains, and the shared helper still preserves uncommitted tracked changes.
        if (-not $evidence -and $StaleDays -gt 0 -and $sha) {
            $commitEpoch = git -C $worktree.Path log -1 --format=%ct 2>$null
            if ($LASTEXITCODE -eq 0 -and $commitEpoch -match '^\d+$' -and
                ($nowEpoch - [long]$commitEpoch) -gt ($StaleDays * 86400L)) {
                $evidence = [pscustomobject]@{
                    Reason = "no PR evidence, HEAD commit older than $StaleDays day(s)"
                    ExpectedHead = $sha
                    HeadBranch = $null
                    IsCrossRepository = $true
                    IsMergedPr = $false
                }
            }
        }

        if (-not $evidence) {
            $unmatched += $worktree
            continue
        }

        $cleanupArgs = @{
            Repo = $mainRepo
            Worktree = $worktree.Path
            ExpectedHead = $evidence.ExpectedHead
            Remote = $gitRemote
            Label = "($($evidence.Reason))"
        }
        if ($WhatIf) {
            if (Test-WorktreeSafeToRemove @cleanupArgs) {
                Write-Host "sweep: WOULD remove $($worktree.Path) -- $($evidence.Reason)"
            }
            continue
        }

        Remove-MergedWorktree @cleanupArgs
        if (Test-Path -LiteralPath $worktree.Path) { continue }

        $removed++
        if (-not $evidence.IsMergedPr) { continue }

        if (-not $evidence.IsCrossRepository -and $evidence.HeadBranch) {
            $remoteCleanupArgs = @{
                Repo = $mainRepo
                Branch = $evidence.HeadBranch
                ExpectedHead = $evidence.ExpectedHead
                Remote = $gitRemote
            }
            if (-not (Remove-RemoteBranchAtExpectedHead @remoteCleanupArgs)) {
                Write-Host "sweep: remote branch '$($evidence.HeadBranch)' was absent, advanced, or could not be deleted."
            }
        }
        if ($worktree.Branch) {
            $localCleanupArgs = @{
                Repo = $mainRepo
                Branch = $worktree.Branch
                ExpectedHead = $evidence.ExpectedHead
                Remote = $gitRemote
            }
            if (-not (Remove-LocalBranchAtExpectedHead @localCleanupArgs)) {
                Write-Host "sweep: preserving local branch '$($worktree.Branch)' because it contains commits outside the merged PR head or could not be deleted."
            }
        }
    }

    if ($unmatched.Count -gt 0) {
        Write-Host "sweep: keeping $($unmatched.Count) worktree(s) with no merge evidence:"
        foreach ($worktree in $unmatched) {
            $label = if ($worktree.Branch) { "[$($worktree.Branch)]" } else { '(detached)' }
            Write-Host "sweep:   $($worktree.Path) $label"
        }
        if ($StaleDays -eq 0) {
            Write-Host 'sweep: re-run with -StaleDays <n> to also remove clean ones older than n days.'
        }
    }

    # Re-read registrations after removals. A failed remove followed by prune may have
    # turned a formerly registered path into an orphan during this same sweep.
    $filesystemPathComparer = if ($IsWindows) {
        [StringComparer]::OrdinalIgnoreCase
    } else {
        [StringComparer]::Ordinal
    }
    $registered = [Collections.Generic.HashSet[string]]::new($filesystemPathComparer)
    foreach ($line in (git -C $mainRepo worktree list --porcelain)) {
        if ($line -like 'worktree *') {
            $normalized = Get-NormalizedFilesystemPath -Path $line.Substring(9)
            [void]$registered.Add($normalized)
        }
    }

    $dedicatedWorktreeRoot = Get-NormalizedFilesystemPath -Path "$mainNormalized-worktrees"
    $roots = [Collections.Generic.Dictionary[string, string]]::new($filesystemPathComparer)
    $roots[$dedicatedWorktreeRoot] = $dedicatedWorktreeRoot
    foreach ($worktree in $worktrees) {
        $path = ($worktree.Path -replace '\\', '/').TrimEnd('/')
        if ($path -eq $mainNormalized -or
            $path.StartsWith("$mainNormalized/", [StringComparison]::OrdinalIgnoreCase)) { continue }
        $parent = (Split-Path -Path $path -Parent) -replace '\\', '/'
        if ($parent) {
            $normalizedParent = Get-NormalizedFilesystemPath -Path $parent
            $roots[$normalizedParent] = $parent
        }
    }

    $markerlessCreationGrace = [TimeSpan]::FromMinutes(5)
    $orphansRemoved = 0
    foreach ($root in $roots.Values) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $isDedicatedWorktreeRoot = Test-SameFilesystemPath `
            -Left $root `
            -Right $dedicatedWorktreeRoot
        Write-Host "sweep: scanning orphan root $root (markerless=$isDedicatedWorktreeRoot)"
        foreach ($directory in (Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue)) {
            $normalized = Get-NormalizedFilesystemPath -Path $directory.FullName
            if ($registered.Contains($normalized)) { continue }
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Host "sweep: skipping orphan candidate that is itself a reparse point: $($directory.FullName)"
                continue
            }

            $marker = Join-Path $directory.FullName '.git'
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                if (-not $isDedicatedWorktreeRoot) {
                    Write-Host "sweep: skipping markerless orphan candidate outside dedicated worktree root: $($directory.FullName)"
                    continue
                }

                try {
                    $entries = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force -ErrorAction Stop)
                    $data = $entries | Where-Object {
                            -not $_.PSIsContainer -or
                            (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
                        } |
                        Select-Object -First 1
                }
                catch {
                    Write-Host "sweep: skipping unreadable markerless orphan candidate: $($directory.FullName)"
                    continue
                }

                if ($null -ne $data) {
                    Write-Host "sweep: skipping unproven markerless orphan candidate with content: $($directory.FullName)"
                    continue
                }

                $latestWrite = @($directory) + $entries |
                    Sort-Object LastWriteTimeUtc -Descending |
                    Select-Object -First 1
                if (([DateTime]::UtcNow - $latestWrite.LastWriteTimeUtc) -lt $markerlessCreationGrace) {
                    Write-Host "sweep: skipping recently modified markerless orphan candidate: $($directory.FullName)"
                    continue
                }
                if (Test-WorktreePathRegistered -Repo $mainRepo -Path $directory.FullName) {
                    Write-Host "sweep: skipping markerless orphan candidate registered during scan: $($directory.FullName)"
                    continue
                }
                if ($WhatIf) {
                    Write-Host "sweep: WOULD remove empty markerless orphaned dir $($directory.FullName)"
                    continue
                }

                if (Remove-EmptyDirectoryShell -Path $directory.FullName) {
                    Write-Host "sweep: removed empty markerless orphaned dir $($directory.FullName)"
                    $orphansRemoved++
                }
                else {
                    Write-Host "sweep: WARNING could not safely remove empty markerless orphaned dir $($directory.FullName)"
                }
                continue
            }
            $firstLine = Get-Content -LiteralPath $marker -TotalCount 1 -ErrorAction SilentlyContinue
            if (-not $firstLine -or $firstLine -notmatch '^gitdir:\s*(?<gitdir>.+?)\s*$') { continue }
            $gitdir = $Matches.gitdir -replace '\\', '/'

            # Only reap a directory proven to be an ex-worktree of this repository, and
            # only after its registration directory has disappeared.
            if ($gitdir -notlike "$mainNormalized/.git/worktrees/*") { continue }
            if (Test-Path -LiteralPath $gitdir) { continue }
            if ($WhatIf) {
                Write-Host "sweep: WOULD remove orphaned dir $($directory.FullName) (dangling gitdir: $gitdir)"
                continue
            }

            if (Remove-WorktreeDirectoryFallback -Worktree $directory.FullName -Label '(orphaned directory)') {
                Write-Host "sweep: removed orphaned dir $($directory.FullName)"
                $orphansRemoved++
            }
            else {
                Write-Host "sweep: WARNING could not safely remove orphaned dir $($directory.FullName)"
            }
        }
    }

    git -C $mainRepo worktree prune
    Write-Host "sweep: removed $removed registered worktree(s), $orphansRemoved orphaned dir(s)."
}
catch {
    Warn "unexpected sweep error (ignored, loop continues): $_"
}
exit 0
