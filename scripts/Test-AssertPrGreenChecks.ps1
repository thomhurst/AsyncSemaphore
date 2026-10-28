# Exercises the merge and auto-merge gate rules in AssertPrGreenChecks.ps1 without GitHub.
# Run locally with PowerShell 7: pwsh scripts/Test-AssertPrGreenChecks.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AssertPrGreenChecks.ps1')

function New-View([string]$MergeState = 'CLEAN', [object[]]$Checks, [bool]$Draft = $false) {
    [pscustomobject]@{
        state             = 'OPEN'
        isDraft           = $Draft
        mergeable         = 'MERGEABLE'
        mergeStateStatus  = $MergeState
        statusCheckRollup = $Checks
    }
}

function New-Check([string]$Name, [string]$Status = 'COMPLETED', [string]$Conclusion = 'SUCCESS') {
    [pscustomobject]@{ name = $Name; status = $Status; conclusion = $Conclusion }
}

$review = New-Check 'claude-review'
$ci = New-Check 'CI Passed'
# A job that a draft event skipped reports its unevaluated name expression.
$draftSkip = New-Check "needs.modularpipeline.result != '' && 'CI Passed' || 'CI Passed (not run)'" 'COMPLETED' 'SKIPPED'

$cases = @(
    @{ Name = 'merges a clean green PR'; View = New-View 'CLEAN' @($review, $ci); Auto = $false; Denied = $false }
    @{ Name = 'refuses to merge a draft'; View = New-View 'CLEAN' @($review, $ci) $true; Auto = $false; Denied = $true }
    @{ Name = 'refuses to merge while CI is pending'; View = New-View 'BLOCKED' @($review, (New-Check 'CI Passed' 'IN_PROGRESS' $null)); Auto = $false; Denied = $true }
    @{ Name = 'refuses to merge with a failed check'; View = New-View 'CLEAN' @($review, (New-Check 'CI Passed' 'COMPLETED' 'FAILURE')); Auto = $false; Denied = $true }
    @{ Name = 'refuses to merge without checks'; View = New-View 'CLEAN' @(); Auto = $false; Denied = $true }
    @{ Name = 'arms while CI is pending'; View = New-View 'BLOCKED' @($review, $draftSkip, (New-Check 'Tests (windows-latest)' 'QUEUED' $null)); Auto = $true; Denied = $false }
    @{ Name = 'arms with a pending status context'; View = New-View 'BLOCKED' @($review, [pscustomobject]@{ context = 'external'; state = 'PENDING' }); Auto = $true; Denied = $false }
    @{ Name = 'never arms a draft'; View = New-View 'DRAFT' @($review) $true; Auto = $true; Denied = $true }
    @{ Name = 'never arms with a failed check'; View = New-View 'BLOCKED' @($review, (New-Check 'Tests (macos-latest)' 'COMPLETED' 'FAILURE')); Auto = $true; Denied = $true }
    @{ Name = 'never arms with a cancelled check'; View = New-View 'BLOCKED' @($review, (New-Check 'CI Passed' 'COMPLETED' 'CANCELLED')); Auto = $true; Denied = $true }
    @{ Name = 'never arms while the review runs'; View = New-View 'BLOCKED' @((New-Check 'claude-review' 'IN_PROGRESS' $null)); Auto = $true; Denied = $true }
    @{ Name = 'never arms without a review check'; View = New-View 'BLOCKED' @((New-Check 'Tests (windows-latest)' 'QUEUED' $null)); Auto = $true; Denied = $true }
    @{ Name = 'never arms on a skipped review'; View = New-View 'BLOCKED' @((New-Check 'claude-review' 'COMPLETED' 'SKIPPED')); Auto = $true; Denied = $true }
    @{ Name = 'never arms a branch that is behind'; View = New-View 'BEHIND' @($review); Auto = $true; Denied = $true }
    @{ Name = 'never arms a conflicting PR'; View = New-View 'DIRTY' @($review); Auto = $true; Denied = $true }
    @{ Name = 'fails closed on an unknown check state'; View = New-View 'BLOCKED' @($review, (New-Check 'CI Passed' 'MYSTERY' $null)); Auto = $true; Denied = $true }
)

$failures = @()
foreach ($case in $cases) {
    $reason = Get-PrStateDenial -View $case.View -ForAutoMerge:$case.Auto
    if (($null -ne $reason) -ne $case.Denied) {
        $failures += "$($case.Name): expected denied=$($case.Denied), got '$reason'"
    }
}

$closed = New-View 'CLEAN' @($review, $ci)
$closed.state = 'MERGED'
if (-not (Get-PrStateDenial -View $closed -ForAutoMerge)) { $failures += 'never arms a PR that is not open' }

if ($failures.Count -gt 0) { throw ($failures -join "`n") }
Write-Host "Assert-PrGreen check gate tests passed ($($cases.Count + 1) cases)."
