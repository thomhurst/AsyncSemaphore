# WorktreeCleanup.ps1
# Shared worktree-removal helper, dot-sourced by Merge-Pr.ps1 and
# Remove-MergedWorktrees.ps1. Not meant to be run directly.
#
# Removal policy (one place, both callers):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths.
#   - PRESERVE local commits not contained in the recorded PR head.
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - DETACH directory junctions and symlinks before deleting, so no delete follows a link.
#   - Long-path safe: git's own delete now works because core.longpaths=true is set
#     system-wide; the `\\?\` extended-length Remove-Item is kept as a fallback for
#     environments where that config is missing.

# A named branch whose tip is already on main has no commits of its own: it was just
# cut from main, or never got work. Its tip is main's tip, so commit association and
# rebase-merged PR heads point at whichever PR produced main's tip. Only the branch
# name (tier 1) can prove such a branch merged. Unknown main tip fails closed.
function Test-HeadHasNoWorkBeyondMain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Head,
        [AllowNull()][string]$MainTip
    )

    if (-not $MainTip) { return $true }
    git -C $Repo merge-base --is-ancestor $Head $MainTip 2>$null
    return $LASTEXITCODE -eq 0
}

function New-OrdinalStringMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, bool]])]
    param()

    return [System.Collections.Generic.Dictionary[string, bool]]::new([System.StringComparer]::Ordinal)
}

$script:DisposableWorktreeGeneratedDirectories = New-OrdinalStringMap
foreach ($directory in @(
    '.artifacts',
    '.vs',
    '__pycache__',
    'ARM',
    'ARM64',
    'artifacts',
    'benchmark-results',
    'BenchmarkDotNet.Artifacts',
    'bin',
    'bld',
    'CodeCoverage',
    'Debug',
    'DebugPublic',
    'log',
    'logs',
    'node_modules',
    'obj',
    'Release',
    'Releases',
    'results',
    'StrykerOutput',
    'temptest',
    'TestResults',
    'Win32',
    'x64',
    'x86'
)) {
    $script:DisposableWorktreeGeneratedDirectories[$directory] = $true
}

$script:DisposableWorktreeScopedDirectories = @(
    'docs/.cache',
    'docs/.docusaurus',
    'docs/build',
    # Root-level SDK installs and agent scratch space.
    '.dotnet',
    '.tmp'
)

function Test-DisposableWorktreePath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    # Quoted porcelain paths require Git's escape decoding. Preserve them rather than
    # risk classifying an unusual source path as generated output.
    if ($Path.StartsWith('"', [System.StringComparison]::Ordinal)) { return $false }

    $normalizedPath = $Path -replace '\\', '/'
    # Keep these root-only patterns aligned with .gitignore. Ignore rules alone
    # cannot authorize deleting arbitrary files: ignored source and secrets survive.
    if (-not $normalizedPath.Contains('/') -and
        ($normalizedPath -cmatch '\.(log|nettrace)$' -or
         $normalizedPath -cmatch '(^|-)(pr-body|review-disposition|review-validation|rebase-validation|comment|issue)\.md$')) {
        return $true
    }
    foreach ($docsGeneratedDirectory in $script:DisposableWorktreeScopedDirectories) {
        if ($normalizedPath -ceq $docsGeneratedDirectory -or
            $normalizedPath.StartsWith("$docsGeneratedDirectory/", [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    foreach ($segment in ($normalizedPath -split '/')) {
        if ($script:DisposableWorktreeGeneratedDirectories.ContainsKey($segment)) { return $true }
    }

    return $false
}

function Get-NormalizedFilesystemPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    return [IO.Path]::TrimEndingDirectorySeparator(
        $(if ($resolved) { $resolved.Path } else { [IO.Path]::GetFullPath($Path) }))
}

function Test-SameFilesystemPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    $leftPath = Get-NormalizedFilesystemPath -Path $Left
    $rightPath = Get-NormalizedFilesystemPath -Path $Right
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    return [string]::Equals($leftPath, $rightPath, $comparison)
}

function Test-RegisteredWorktreeLocked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree
    )

    $isTarget = $false
    foreach ($line in (git -C $Repo worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *') {
            $isTarget = Test-SameFilesystemPath -Left ($line.Substring(9)) -Right $Worktree
        }
        elseif ($isTarget -and ($line -eq 'locked' -or $line -like 'locked *')) {
            return $true
        }
        elseif ($line -eq '') {
            $isTarget = $false
        }
    }

    return $false
}

function Get-GitHubRemoteName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [string]$GitHubRepo
    )

    if (-not $GitHubRepo) { return 'origin' }

    foreach ($remote in @(git -C $Repo remote 2>$null)) {
        $fetchUrls = @(git -C $Repo remote get-url --all $remote 2>$null)
        if ($LASTEXITCODE -ne 0 -or $fetchUrls.Count -eq 0) { continue }
        $pushUrls = @(git -C $Repo remote get-url --push --all $remote 2>$null)
        if ($LASTEXITCODE -ne 0 -or $pushUrls.Count -eq 0) { continue }

        $allUrlsMatch = @($fetchUrls + $pushUrls).Where({
            $_.Trim() -match 'github\.com[/:](?<repo>[^/]+/[^/]+?)(?:\.git)?$' -and
            [string]::Equals($Matches.repo, $GitHubRepo, [StringComparison]::OrdinalIgnoreCase)
        }).Count -eq ($fetchUrls.Count + $pushUrls.Count)
        if ($allUrlsMatch) {
            return $remote.Trim()
        }
    }

    return $null
}

function Test-CommitContainedInExpectedHead {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$LocalHead,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [string]$Remote = 'origin'
    )

    git -C $Repo cat-file -e "$ExpectedHead^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        # The PR may have been completed by another checkout, so its final head
        # object is not guaranteed to exist locally. Fetch the authoritative OID
        # before deciding a clean worktree contains divergent commits.
        git -C $Repo fetch --no-tags $Remote $ExpectedHead 2>$null
        git -C $Repo cat-file -e "$ExpectedHead^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            # Some servers refuse direct object wants even when the commit is
            # reachable from a branch. Fetch branch refs, then check again.
            git -C $Repo fetch --no-tags $Remote "+refs/heads/*:refs/remotes/$Remote/*" 2>$null
            git -C $Repo cat-file -e "$ExpectedHead^{commit}" 2>$null
        }
        if ($LASTEXITCODE -ne 0) {
            return $false
        }
    }

    git -C $Repo merge-base --is-ancestor $LocalHead $ExpectedHead 2>$null
    return $LASTEXITCODE -eq 0
}

function Remove-RemoteBranchAtExpectedHead {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [string]$Remote = 'origin'
    )

    $remoteRef = "refs/heads/$Branch"
    $lease = "${remoteRef}:$ExpectedHead"
    $output = git -C $Repo push "--force-with-lease=$lease" $Remote --delete $remoteRef 2>&1
    if ($LASTEXITCODE -ne 0 -and $output) {
        Write-Host "Remote branch deletion failed: $($output -join [Environment]::NewLine)"
    }
    return $LASTEXITCODE -eq 0
}

function Remove-LocalBranchAtExpectedHead {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [string]$Remote = 'origin'
    )

    $localHead = git -C $Repo rev-parse --verify "refs/heads/$Branch" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $localHead) { return $true }
    $localHead = $localHead.Trim()
    if (-not (Test-CommitContainedInExpectedHead -Repo $Repo -LocalHead $localHead -ExpectedHead $ExpectedHead -Remote $Remote)) {
        return $false
    }

    git -C $Repo branch -D $Branch 2>$null
    return $LASTEXITCODE -eq 0
}

# Git for Windows follows a directory junction while deleting a worktree: it empties the
# target, then fails with "Directory not empty" on the link and leaves a remnant without
# its .git file. Package managers link node_modules packages with junctions, and a junction
# can also point into another checkout or a shared store. Delete each link itself, never its
# target, before any recursive delete. Returns whether no directory link remains.
function Remove-WorktreeDirectoryLinks {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [string]$Label = ''
    )

    if (-not $IsWindows) { return $true }

    # Get-ChildItem does not recurse through links, so every match is a link to remove.
    $find = { @(Get-ChildItem -LiteralPath $Worktree -Directory -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue) }
    foreach ($link in (& $find)) {
        # DirectoryInfo.Delete() without recursion removes the reparse point only.
        try { $link.Delete() } catch { Write-Host "Could not detach directory link $($link.FullName): $_" }
    }

    $remaining = & $find
    if ($remaining.Count -gt 0) {
        Write-Host "WARNING: worktree $Label kept: $($remaining.Count) directory link(s) could not be detached, first $($remaining[0].FullName): $Worktree"
        return $false
    }
    return $true
}

function Remove-WorktreeDirectoryFallback {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [string]$Label = ''
    )

    if (-not (Test-Path -LiteralPath $Worktree)) { return $true }
    if (-not (Remove-WorktreeDirectoryLinks -Worktree $Worktree -Label $Label)) { return $false }

    # \\?\ disables Win32 path normalization, so forward slashes from git are not
    # translated. Normalize them explicitly on Windows; use the ordinary path on Unix.
    $deletionPath = if ($IsWindows) { '\\?\' + ($Worktree -replace '/', '\') } else { $Worktree }
    Remove-Item -LiteralPath $deletionPath -Recurse -Force -ErrorAction SilentlyContinue
    return -not (Test-Path -LiteralPath $Worktree)
}

function Test-WorktreeSafeToRemove {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [string]$Remote = 'origin',
        [string]$Label = ''
    )

    if (-not (Test-Path -LiteralPath $Worktree)) { return $false }
    if (Test-SameFilesystemPath -Left $Repo -Right $Worktree) {
        Write-Host "Preserving main checkout $Label : $Worktree (main checkout is never removable)"
        return $false
    }
    if (Test-RegisteredWorktreeLocked -Repo $Repo -Worktree $Worktree) {
        Write-Host "Preserving locked worktree $Label : $Worktree (worktree lock is active)"
        return $false
    }

    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving worktree $Label : $Worktree (could not inspect worktree status)"
        return $false
    }
    $work = @($status | Where-Object {
        if ($_ -notmatch '^(\?\?|!!) ') { return $true }
        return -not (Test-DisposableWorktreePath -Path $_.Substring(3))
    })
    if ($work.Count -gt 0) {
        Write-Host "Preserving dirty worktree $Label : $Worktree (uncommitted work)"
        foreach ($entry in $work) { Write-Host "  $entry" }
        return $false
    }

    $localHead = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $localHead) {
        Write-Host "Preserving worktree $Label : $Worktree (could not resolve local branch tip)"
        return $false
    }
    $localHead = $localHead.Trim()
    if (-not (Test-CommitContainedInExpectedHead -Repo $Repo -LocalHead $localHead -ExpectedHead $ExpectedHead -Remote $Remote)) {
        Write-Host "Preserving worktree $Label : $Worktree (local commits are not contained in PR head $ExpectedHead)"
        return $false
    }

    return $true
}

function Remove-MergedWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,       # a checkout that is NOT the one being removed (main)
        [Parameter(Mandatory)][string]$Worktree,   # path to remove
        [Parameter(Mandatory)][string]$ExpectedHead, # authoritative head OID recorded by GitHub
        [string]$Remote = 'origin',
        [string]$Label = ''                        # e.g. "#1234" for log lines
    )

    if (-not (Test-Path -LiteralPath $Worktree)) {
        git -C $Repo worktree prune
        return
    }

    $safetyArgs = @{
        Repo = $Repo
        Worktree = $Worktree
        ExpectedHead = $ExpectedHead
        Remote = $Remote
        Label = $Label
    }
    if (-not (Test-WorktreeSafeToRemove @safetyArgs)) { return }

    $pending = Register-PendingWorktreeRemoval -Repo $Repo -Worktree $Worktree
    if (Remove-VettedWorktree -Repo $Repo -Worktree $Worktree -Label $Label) {
        if ($pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue }
        Write-Host "Removed worktree $Label : $Worktree"
    } elseif ($pending) {
        Write-Host "WARNING: could not fully remove $Worktree; Remove-MergedWorktrees.ps1 retries the pending removal"
    } else {
        Write-Host "WARNING: could not fully remove $Worktree"
    }
}

# Deletes a worktree whose content callers already vetted as disposable, then prunes
# its registration. Returns whether the directory is gone.
function Remove-VettedWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree,
        [string]$Label = ''
    )

    if (-not (Remove-WorktreeDirectoryLinks -Worktree $Worktree -Label $Label)) { return $false }

    # Primary path: let git remove it. Double force also deletes the remnant of an
    # earlier, partially failed removal.
    git -C $Repo -c core.longpaths=true worktree remove --force --force $Worktree 2>$null | Out-Null

    # Fallback for long-path failures and remnants that Git no longer recognizes.
    if (Test-Path -LiteralPath $Worktree) {
        Remove-WorktreeDirectoryFallback -Worktree $Worktree -Label $Label | Out-Null
    }

    git -C $Repo worktree prune 2>$null | Out-Null
    return -not (Test-Path -LiteralPath $Worktree)
}

# PENDING REMOVALS
# On Windows a directory cannot be deleted while any process has its current directory
# inside it, and build or test processes can hold files open briefly. Git then deletes
# part of the tree, including the .git file, and fails. The remnant no longer looks like
# a worktree, so neither release nor the merged-worktree sweep would recognize it again.
# Before deleting a vetted checkout, cleanup records it under the common Git directory.
# The record is removed after success, and Complete-PendingWorktreeRemovals retries the
# rest once the blocking process has exited.

# Agents keep logs, reports and PR-body drafts for a work item under
# <main checkout>-evidence/<lock name>/, beside the worktree root, so nothing lands
# loose in the parent directory. Evidence matters only while its item is active.
function Get-AgentEvidenceRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MainCheckout)

    return "$(($MainCheckout -replace '\\', '/').TrimEnd('/'))-evidence"
}

# Removes each top-level entry of the evidence root whose newest write is older than
# the retention window. Returns the number of entries removed (or that would be).
function Remove-StaleAgentEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][int]$RetentionDays,
        [switch]$WhatIf
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return 0 }
    $cutoff = [DateTime]::UtcNow.AddDays(-$RetentionDays)
    $removed = 0
    foreach ($entry in (Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Write-Host "sweep: skipping evidence entry that is a link: $($entry.FullName)"
            continue
        }
        $items = @($entry)
        if ($entry.PSIsContainer) {
            $items += @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue)
        }
        $newest = ($items | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
        if ($newest -ge $cutoff) { continue }
        if ($WhatIf) {
            Write-Host "sweep: WOULD remove stale evidence $($entry.FullName)"
            $removed++
            continue
        }
        if ($entry.PSIsContainer -and -not (Remove-WorktreeDirectoryLinks -Worktree $entry.FullName -Label '(evidence)')) { continue }
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $entry.FullName) {
            Write-Host "sweep: WARNING could not remove stale evidence $($entry.FullName)"
            continue
        }
        Write-Host "sweep: removed stale evidence $($entry.FullName)"
        $removed++
    }
    return $removed
}

function Get-PendingWorktreeRemovalDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repo)

    $common = git -C $Repo rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $common) { return $null }
    return Join-Path $common.Trim() 'agent-pending-removals'
}

function Get-WorktreeAgentLockNames {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Worktree, [string]$Branch = '')

    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $marker = git -C $Worktree config --worktree --get agent.lockName 2>$null
    $markerOwner = git -C $Worktree config --worktree --get agent.lockWorktree 2>$null
    # `git worktree add` copies the source's config.worktree; a copied marker names
    # another checkout's path and says nothing about this worktree's lock.
    if ($marker -and $markerOwner -and
        (Get-NormalizedFilesystemPath -Path $markerOwner.Trim()) -eq (Get-NormalizedFilesystemPath -Path $Worktree)) {
        [void]$names.Add($marker.Trim())
    }
    $leaf = Split-Path -Path $Worktree -Leaf
    if ($leaf -match '^(?<Lock>(?:pr|issue)-\d+)(?:-|$)') { [void]$names.Add($Matches.Lock.ToLowerInvariant()) }
    foreach ($match in [regex]::Matches($Branch, '(?:^|[-/])((?:pr|issue)-\d+)(?=$|[-/])', 'IgnoreCase')) {
        [void]$names.Add($match.Groups[1].Value.ToLowerInvariant())
    }
    return $names | Sort-Object -CaseSensitive
}

function Register-PendingWorktreeRemoval {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree,
        [string[]]$LockNames = @()
    )

    $directory = Get-PendingWorktreeRemovalDirectory -Repo $Repo
    if (-not $directory) { return $null }
    $path = Get-NormalizedFilesystemPath -Path $Worktree
    $gitDirectory = git -C $path rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory) { return $null }
    $branch = git -C $path symbolic-ref -q --short HEAD 2>$null
    $names = @(Get-WorktreeAgentLockNames -Worktree $path -Branch ([string]$branch)) + $LockNames

    $key = if ($IsWindows) { $path.ToLowerInvariant() } else { $path }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
    $record = Join-Path $directory ($hash.Substring(0, 16).ToLowerInvariant() + '.json')
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [ordered]@{
        path         = $path
        gitDirectory = Get-NormalizedFilesystemPath -Path $gitDirectory.Trim()
        lockNames    = @($names | Where-Object { $_ } | Sort-Object -CaseSensitive -Unique)
        recordedAt   = [DateTimeOffset]::UtcNow.ToString('o')
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $record -NoNewline
    return $record
}

function Test-HasWorkNewerThan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][DateTimeOffset]$Cutoff
    )

    foreach ($file in (Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue)) {
        $relative = [IO.Path]::GetRelativePath($Path, $file.FullName)
        if ($relative -eq '.git' -or (Test-DisposableWorktreePath -Path $relative)) { continue }
        if ($file.LastWriteTimeUtc -gt $Cutoff.UtcDateTime) { return $true }
    }
    return $false
}

function Invoke-PendingRemovalLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][ValidateSet('status', 'acquire', 'release')][string]$Verb,
        [Parameter(Mandatory)][string]$LockName,
        [Parameter(Mandatory)][string]$OwnerId
    )

    # Acquire prints a token. Never forward it.
    $output = @(& pwsh -NoProfile -File $ScriptPath $Verb -LockName $LockName -OwnerId $OwnerId 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Status = ($output -join "`n").Trim() }
}

function Remove-PendingWorktreeRemovalRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Record, [switch]$WhatIf)

    if (-not $WhatIf) { Remove-Item -LiteralPath $Record -Force -ErrorAction SilentlyContinue }
}

function Complete-PendingWorktreeRemovals {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [switch]$WhatIf
    )

    $directory = Get-PendingWorktreeRemovalDirectory -Repo $Repo
    if (-not $directory -or -not (Test-Path -LiteralPath $directory)) { return 0 }
    $agentLocks = Join-Path $Repo 'scripts/AgentLocks.ps1'
    $completed = 0
    foreach ($recordFile in (Get-ChildItem -LiteralPath $directory -File -Filter '*.json')) {
        $recordPath = $recordFile.FullName
        try {
            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $path = [string]$record.path
            $recordedAt = [DateTimeOffset]::Parse([string]$record.recordedAt, [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            Write-Host "sweep: WARNING unreadable pending removal record: $recordPath"
            continue
        }

        if (-not $path -or -not (Test-Path -LiteralPath $path)) {
            Remove-PendingWorktreeRemovalRecord -Record $recordPath -WhatIf:$WhatIf
            continue
        }

        # A new checkout at the same path has its own Git metadata, created after the record.
        $marker = Join-Path $path '.git'
        if (Test-Path -LiteralPath $marker -PathType Leaf) {
            $gitDirectory = ((Get-Content -LiteralPath $marker -TotalCount 1) -replace '^gitdir:\s*', '').Trim()
            if ($gitDirectory -and -not [IO.Path]::IsPathRooted($gitDirectory)) { $gitDirectory = Join-Path $path $gitDirectory }
            if ($gitDirectory -and (Test-Path -LiteralPath $gitDirectory) -and (
                    -not (Test-SameFilesystemPath -Left $gitDirectory -Right ([string]$record.gitDirectory)) -or
                    (Get-Item -LiteralPath $gitDirectory -Force).CreationTimeUtc -gt $recordedAt.UtcDateTime)) {
                Write-Host "sweep: dropping pending removal superseded by a new checkout: $path"
                Remove-PendingWorktreeRemovalRecord -Record $recordPath -WhatIf:$WhatIf
                continue
            }
        }
        elseif (Test-Path -LiteralPath $marker) {
            Write-Host "sweep: dropping pending removal for a standalone repository: $path"
            Remove-PendingWorktreeRemovalRecord -Record $recordPath -WhatIf:$WhatIf
            continue
        }

        # The checkout was vetted before deletion began. Anything written since is new work.
        if (Test-HasWorkNewerThan -Path $path -Cutoff $recordedAt) {
            Write-Host "sweep: WARNING preserving pending removal with files changed after removal began; inspect manually: $path"
            Remove-PendingWorktreeRemovalRecord -Record $recordPath -WhatIf:$WhatIf
            continue
        }

        # Reserve the item locks through deletion so a live owner keeps its checkout.
        $names = @($record.lockNames | Where-Object { $_ })
        if ($names.Count -gt 0 -and -not (Test-Path -LiteralPath $agentLocks -PathType Leaf)) {
            Write-Host "sweep: preserving pending removal; canonical lock script is unavailable: $path"
            continue
        }
        $owner = "worktree-cleanup-$([Guid]::NewGuid().ToString('N'))"
        $acquired = [Collections.Generic.List[string]]::new()
        try {
            $available = $true
            foreach ($name in $names) {
                $verb = if ($WhatIf) { 'status' } else { 'acquire' }
                $result = Invoke-PendingRemovalLock -ScriptPath $agentLocks -Verb $verb -LockName $name -OwnerId $owner
                if ($result.ExitCode -ne 0 -or ($WhatIf -and $result.Status -ne 'FREE')) {
                    Write-Host "sweep: preserving pending removal; lock $name is held or unavailable: $path"
                    $available = $false
                    break
                }
                if (-not $WhatIf) { $acquired.Add($name) }
            }
            if (-not $available) { continue }
            if ($WhatIf) {
                Write-Host "sweep: WOULD finish pending removal of $path"
                continue
            }
            if (Remove-VettedWorktree -Repo $Repo -Worktree $path -Label '(pending removal)') {
                Remove-PendingWorktreeRemovalRecord -Record $recordPath
                $completed++
                Write-Host "sweep: finished pending removal of $path"
            } else {
                Write-Host "sweep: WARNING pending removal is still blocked, retrying next sweep: $path"
            }
        }
        finally {
            for ($index = $acquired.Count - 1; $index -ge 0; $index--) {
                $released = Invoke-PendingRemovalLock -ScriptPath $agentLocks -Verb release -LockName $acquired[$index] -OwnerId $owner
                if ($released.ExitCode -ne 0) { Write-Host "WARNING: could not release cleanup reservation $($acquired[$index])." }
            }
        }
    }
    return $completed
}
