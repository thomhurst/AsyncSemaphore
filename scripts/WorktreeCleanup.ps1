# WorktreeCleanup.ps1
# Shared worktree-removal helper, dot-sourced by Merge-Pr.ps1 and
# Remove-MergedWorktrees.ps1. Not meant to be run directly.
#
# Removal policy (one place, both callers):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths.
#   - PRESERVE local commits not contained in the recorded PR head.
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - Long-path safe: git's own delete now works because core.longpaths=true is set
#     system-wide; the `\\?\` extended-length Remove-Item is kept as a fallback for
#     environments where that config is missing.

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
    'docs/build'
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

function Remove-WorktreeDirectoryFallback {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [string]$Label = ''
    )

    if (-not (Test-Path -LiteralPath $Worktree)) { return $true }

    # A website node_modules directory can be a junction into the
    # shared pnpm content-addressable store. Recursing into it would delete the
    # store's contents, so leave the worktree for explicit manual cleanup.
    $nm = Join-Path $Worktree 'website\node_modules'
    $isJunction = $false
    if (Test-Path -LiteralPath $nm) {
        $isJunction = ((Get-Item -LiteralPath $nm -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }
    if ($isJunction) {
        Write-Host "WARNING: worktree $Label requires manual removal -- detach the node_modules junction at $nm first, then re-run cleanup: $Worktree"
        return $false
    }

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

    # Primary path: let git remove it (force clears untracked artifacts; tracked is clean).
    git -C $Repo worktree remove --force $Worktree 2>$null

    # Fallback for long-path failures (only if core.longpaths is somehow off).
    if (Test-Path -LiteralPath $Worktree) {
        Remove-WorktreeDirectoryFallback -Worktree $Worktree -Label $Label | Out-Null
    }

    git -C $Repo worktree prune
    if (Test-Path -LiteralPath $Worktree) {
        Write-Host "WARNING: could not fully remove $Worktree"
    } else {
        Write-Host "Removed worktree $Label : $Worktree"
    }
}
