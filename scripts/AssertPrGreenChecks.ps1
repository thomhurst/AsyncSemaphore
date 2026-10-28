# AssertPrGreenChecks.ps1
# PR state and status-check rules for Assert-PrGreen.ps1, kept free of GitHub calls so
# Test-AssertPrGreenChecks.ps1 can exercise them.
#
# Get-PrStateDenial returns the reason the PR must not be merged (or have auto-merge
# armed), or $null when its state and checks pass.
#
# Merge mode: the PR is ready, MERGEABLE and CLEAN, and every check is terminal and
# passing.
#
# Auto-merge mode (-ForAutoMerge): GitHub waits for the required checks itself, so CI
# may still be pending. The PR must still be ready and MERGEABLE, no check may have
# failed, and the `claude-review` check must have completed successfully on this head:
# auto-merge never waits for the review.

$script:ReviewCheckName = 'claude-review'

# Merge states in which armed auto-merge can still complete without another push.
# BEHIND cannot: the strict required-check setting makes the PR wait for an update
# that auto-merge never performs.
$script:AutoMergeStates = @('CLEAN', 'BLOCKED', 'UNSTABLE', 'HAS_HOOKS')

function Get-CheckName($Check) {
    if ($null -ne $Check.name) { return [string]$Check.name }
    return [string]$Check.context
}

# Returns 'PASSED', 'PENDING' or 'FAILED'. Unknown shapes and states fail closed.
function Get-CheckVerdict($Check) {
    if ($null -ne $Check.status) {
        # CheckRun.
        if ([string]$Check.status -ne 'COMPLETED') {
            if (@('QUEUED', 'IN_PROGRESS', 'PENDING', 'WAITING', 'REQUESTED') -contains [string]$Check.status) { return 'PENDING' }
            return 'FAILED'
        }
        if (@('SUCCESS', 'SKIPPED', 'NEUTRAL') -contains [string]$Check.conclusion) { return 'PASSED' }
        return 'FAILED'
    }
    if ($null -ne $Check.state) {
        # StatusContext.
        if ([string]$Check.state -eq 'SUCCESS') { return 'PASSED' }
        if (@('PENDING', 'EXPECTED') -contains [string]$Check.state) { return 'PENDING' }
        return 'FAILED'
    }
    return 'FAILED'
}

function Get-PrStateDenial {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$View,
        [switch]$ForAutoMerge
    )

    if ($View.state -ne 'OPEN') { return "state=$($View.state) (need OPEN)" }
    if ($View.isDraft -ne $false) { return 'PR is a draft; mark it ready only after its reviewers approve the current head' }
    if ($View.mergeable -ne 'MERGEABLE') { return "mergeable=$($View.mergeable) (need MERGEABLE)" }
    if ($ForAutoMerge) {
        if ($script:AutoMergeStates -notcontains $View.mergeStateStatus) {
            if ($View.mergeStateStatus -eq 'BEHIND') {
                return 'mergeStateStatus=BEHIND; rebase onto origin/main first, because auto-merge never updates the branch'
            }
            return "mergeStateStatus=$($View.mergeStateStatus) (auto-merge could not complete)"
        }
    }
    elseif ($View.mergeStateStatus -ne 'CLEAN') {
        return "mergeStateStatus=$($View.mergeStateStatus) (need CLEAN)"
    }

    $checks = @($View.statusCheckRollup | Where-Object { $null -ne $_ })
    if ($checks.Count -eq 0) {
        return 'statusCheckRollup is empty -- no checks registered yet; refusing to merge blind'
    }

    $bad = @()
    foreach ($check in $checks) {
        $name = Get-CheckName $check
        if ([string]::IsNullOrWhiteSpace($name)) {
            $bad += "unrecognized check entry: $($check | ConvertTo-Json -Compress)"
            continue
        }
        $verdict = Get-CheckVerdict $check
        if ($verdict -eq 'PASSED') { continue }
        # Auto-merge waits for pending CI itself, but never for the review.
        if ($verdict -eq 'PENDING' -and $ForAutoMerge -and $name -ne $script:ReviewCheckName) { continue }
        $detail = if ($null -ne $check.status -and $check.status -ne 'COMPLETED') { "status=$($check.status)" }
        elseif ($null -ne $check.status) { "conclusion=$($check.conclusion)" }
        else { "state=$($check.state)" }
        $bad += "${name}: $detail"
    }
    if ($bad.Count -gt 0) {
        return 'checks not green: ' + ($bad -join '; ')
    }

    if ($ForAutoMerge) {
        $review = @($checks | Where-Object { (Get-CheckName $_) -eq $script:ReviewCheckName -and (Get-CheckVerdict $_) -eq 'PASSED' -and $_.conclusion -eq 'SUCCESS' })
        if ($review.Count -eq 0) {
            return "no successful $($script:ReviewCheckName) check on the current head; wait for its review before arming auto-merge"
        }
    }

    return $null
}
