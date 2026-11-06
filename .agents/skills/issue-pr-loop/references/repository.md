# AsyncSemaphore

Read [AGENTS.md](../../../../AGENTS.md) for validation and performance evidence. Releaser copies must share at-most-once release state; never pool it. Preserve the `Semaphores` namespace and analyzer contracts. Select frameworks from the project files and use the documented TUnit commands.

No Aspire AppHost. The queue's Redis lock container requires Docker; its namespace is `asyncsemaphore`.

The mechanics this loop defers to:

| Need | Owner |
|---|---|
| One PR's review cycle: fetch every surface, verify findings, fix, one push, reply, resolve and unresolve threads | [Review cycle](#review-cycle) below and `AGENTS.md` § Pull requests |
| Merge protocol | [Merge gate](#merge-gate) below |
| Out-of-scope filing bar and procedure | `AGENTS.md` § Autonomous self-improvement and § Writing issues |
| Native sub-issue and `blocked_by` writes | [Issue relationships](#issue-relationships) below |
| Worktree location, lock registration, evidence directory, cleanup | [Worktrees and evidence](#worktrees-and-evidence) below |
| PR review lifecycle | [draft-review.md](draft-review.md) and [Draft reviews](#draft-reviews) below |
| Validation | `AGENTS.md` § Build and test and § Performance validation, and [Implement and ship](#implement-and-ship) below |

Lock owner variable: `ASYNCSEMAPHORE_AGENT_LOCK_OWNER_ID` (read by `scripts/AgentLocks.ps1` after `-OwnerId`, before `CODEX_THREAD_ID`).

## Owner-reserved decisions

No issue is reserved for the owner beyond the `[human]` row in SKILL.md § Resolve, do not defer.

## Queue

Never wait on an item. Pending CI, reviews, benchmark runs, reviewer replies, and bot comments all mean the same thing: leave the item and take another issue or PR. Do not run `gh pr checks --watch` or `gh run watch`. The pending result is usually ready when a later survey reaches that item again. Report a benchmark you started in the PR description when a later iteration finds the run finished. After a fix push, allow a bot review/CI cycle before considering merge in a later iteration. Use commentary while active.

`scripts/Start-IssuePrLoop.ps1` ranks claimable issues by how many they block, then by fewest comments. It reads no `priority:*` or `ci` label, so the tier order in [pick-an-issue.md](pick-an-issue.md) applies only when you choose an issue yourself.

## Draft reviews

`AGENTS.md` applies the skill's draft review lifecycle to every agent-created PR, including work outside the loop. Before creating or promoting a PR from an existing worktree, re-read `AGENTS.md` and this skill from the shared checkout; a branch copy can predate the current policy.

Claude Code Review and Codex must both approve the current head before promotion.

- Claude: `.github/workflows/claude-code-review.yml` reviews every new head, drafts included, and posts the review as a `github-actions[bot]` issue comment. It counts for a head when the `claude-review` check on that head succeeded and the comment was posted after that head was pushed. The workflow runs on `pull_request_target`, so it always uses `main`'s version of itself; a PR that edits it is still reviewed by the old version. When no review exists for the current head, run `gh workflow run claude-code-review.yml --ref main -f pr_number=<N>` once. That run supplies a review, but its check is attached to `main`, not the PR head. To get a PR-head `claude-review` check on a draft without changing its head, close and immediately reopen the PR once. Marking a PR ready does not re-run the workflow, so its check on the head stands.
- Codex (`chatgpt-codex-connector[bot]`): request draft reviews as in the lifecycle. Verdicts that count for the current head: a `codex-pull-request-review-summary` comment whose `codex-review-state:v1` payload has `code_review.commit_sha` equal to the full head SHA, `status: completed`, `has_any_findings: false`, and `has_unresolved_findings: false`; a review on that commit without findings; or a thumbs-up on this head's request comment whose author you verified as the Codex bot through the reactions API. A null or older `commit_sha`, findings, or a pending status is not approval. Re-read the summary on each visit, because Codex edits it in place.
- CodeRabbit also reviews this repository, but it skips drafts. Its review arrives after promotion.

After promotion: reviewers that skip drafts may review once the PR is ready; handle their findings under [Review cycle](#review-cycle). When a finding or a CI failure on a ready PR needs a push, first return the PR to draft with `gh pr ready <N> --undo`, push the fix, and repeat the lifecycle for the new head. A push disarms auto-merge, so arm it again only after the new head is approved and ready.

Automated review rounds (rework budget): count the Claude workflow's comments: `gh api --paginate "repos/{owner}/{repo}/issues/<N>/comments" --jq '.[] | select(.user.login == "github-actions[bot]") | .id' | wc -l`.

Draft CI: `.github/workflows/dotnet.yml` skips every job on draft PR events before a runner is allocated. `ready_for_review` starts a run on the head, and `converted_to_draft` cancels a run that is still in progress. Pushes to `main` and manual dispatch behave as before.

## Merge gate

Ready PRs normally merge through native auto-merge, armed in step 5 of [draft-review.md](draft-review.md). `pwsh scripts/Enable-AutoMerge.ps1 -Pr <N>` captures the head, runs `Assert-PrGreen.ps1 -ForAutoMerge`, and arms a squash merge pinned to that head. That gate allows pending CI but denies a draft, a conflicting or `BEHIND` branch, a failed check, a `claude-review` check that has not passed on this head, and unresolved threads. A ready PR without armed auto-merge gets the same call once its current head is approved. Use `Merge-Pr.ps1` only to merge immediately when every check has already passed on the current head.

An immediate merge requires an open, ready-for-review PR promoted through the draft lifecycle; `MERGEABLE` and `CLEAN`; every applicable check present, terminal, and passing (`SUCCESS`, `SKIPPED`, or `NEUTRAL`, with successful legacy commit statuses); no unresolved threads, unaddressed comments, or outstanding review-body concerns; approval when required; and a bot review/CI cycle after the last fix push. Missing expected checks and nonterminal checks block merging. Reassess if the head changes.

`Merge-Pr.ps1` calls `Assert-PrGreen.ps1`, merges on a passing mechanical gate, and cleans up. Run it and `Enable-AutoMerge.ps1` from the shared checkout, never from inside the worktree. Both are necessary but not sufficient: judge review dispositions and approval yourself first. Never bypass them with direct `gh pr merge` or `gh pr merge --auto`.

Auto-merge: branch protection on `main` requires one check, `CI Passed`, the `ci-passed` aggregator that every `dotnet.yml` job feeds. Armed auto-merge waits only for that check. The aggregator's name is an expression, so a draft run reports it under the raw expression text as skipped and never as a skipped `CI Passed`; auto-merge armed right after `gh pr ready` therefore waits for the ready run. Keep every new CI job in the aggregator's `needs`, keep its name an expression, and never make a fixed-name job a required check, because a skipped job counts as passing. The `disarm-auto-merge` job in `claude-code-review.yml` turns auto-merge off on every push to a non-Renovate PR. Renovate arms auto-merge on its own PRs, which are never drafts. Protection also requires branches to be up to date with `main`, and auto-merge never updates a branch: an armed PR that falls `BEHIND` stalls. Treat it as a conflict: return it to draft, rebase, and repeat the lifecycle for the new head.

## Review cycle

Run it under the PR lock. `AGENTS.md` § Pull requests sets the baseline; this is the full procedure.

Fetch every surface, including pagination and replies: REST `pulls/<N>/reviews`, `pulls/<N>/comments`, and `issues/<N>/comments` under `repos/{owner}/{repo}`, and GraphQL `reviewThreads` with IDs, resolution state, bodies, authors, and timestamps. Never infer a reviewer's surface from its identity; check both every cycle. A conversation comment that carries findings or a verdict is a review with no thread to resolve: disposition it with one PR comment that quotes or links it.

A `COMMENTED` review body can block with zero unresolved threads. Address outstanding concerns, `CHANGES_REQUESTED`, current inline findings, and unresolved threads. Empty acknowledgments are non-blocking. Assess older findings against the current head and later discussion; uncertain concerns block merging.

Verify every finding against the code and classify it:

- **Blocking:** a correctness, concurrency, security, contract, or performance defect, or missing tests for changed behavior. Fix each valid one in this PR.
- **Non-blocking:** naming, style, wording, optional refactors, extra tests for unchanged behavior, and "consider" suggestions. Implement one only when it is small and you already push a blocking fix. Otherwise decline it in the disposition with a one-line reason and push nothing for it.

Decline a wrong finding with evidence from the code. Move genuinely out-of-scope architectural work to a filed follow-up issue, linked in the reply. "Not done" or "left as is" without a classification, reason, or linked issue is not a disposition. Reply once per review covering each finding, and reply on each thread. Discuss disagreement once; implement reaffirmed blocking feedback. A disposition-only reply on the current head needs no new push, so it starts no new review cycle.

Resolve every thread, whether a human or a bot opened it, as soon as it is dispositioned: the fix commit is in the remote PR head and the reply names it, or the reply pushes back with evidence. Use GraphQL `resolveReviewThread`, without waiting for another review or CI cycle; that cycle still gates merging. Leave a thread open only while it has no disposition: the fix is not pushed, the item waits on the user, or the reviewer replied after your disposition. On each later cycle re-fetch every thread, resolved ones included. A reviewer reply newer than your disposition reopens it: run GraphQL `unresolveReviewThread` and handle it as reaffirmed feedback. Reply to addressed review-body findings and request re-review when an outstanding changes-requested review needs updating. Never dismiss a review to bypass approval requirements. Afterwards re-fetch reviews, threads, checks, and head SHA.

## Issue relationships

Native relationships take the numeric REST issue `.id`, not the issue number or `gh issue view --json id` (a GraphQL ID). Pass it with `-F` so it is sent as an integer:

```powershell
$childId = gh api "repos/$repoSlug/issues/<child>" --jq .id
gh api "repos/$repoSlug/issues/<parent>/sub_issues" -X POST -F "sub_issue_id=$childId"

$blockerId = gh api "repos/$repoSlug/issues/<blocker>" --jq .id
gh api "repos/$repoSlug/issues/<blocked>/dependencies/blocked_by" -X POST -F "issue_id=$blockerId"
```

Verify each write with `gh api "repos/$repoSlug/issues/<parent>/sub_issues"` or `gh api "repos/$repoSlug/issues/<blocked>/dependencies/blocked_by"`.

## Worktrees and evidence

Capture these with the values in SKILL.md § Isolation and ownership:

```powershell
$repoName = ($repoSlug -split '/')[-1]
$worktreeRoot = Join-Path (Split-Path $repo -Parent) ($repoName + '-worktrees')
```

Verify the repository and intended base; examples use `origin/main`. Use a writable temporary `<repoName>-worktrees` root if needed. No sparse-checkout or special push refspec applies here. Shell directory changes do not persist, so set `workdir` on each call, and compare `git rev-parse --show-toplevel` with the worktree's resolved absolute path. Keep explicit lock names.

Write nothing loose beside the repository. Every file an agent produces outside a worktree has one home, so cleanup can find it:

- Evidence (build and test logs, reports, PR and comment body drafts, diffs, validation projects) goes in `<repo-parent>/AsyncSemaphore-evidence/<lock-name>/`, for example `C:/git/AsyncSemaphore-evidence/issue-123/build.log`. `scripts/Remove-MergedWorktrees.ps1` deletes an entry once its newest file is older than `-EvidenceRetentionDays` (default 7).
- Throwaway checkouts and baselines, such as a benchmark baseline revision, are worktrees under `$worktreeRoot`, never plain copies.
- Downloaded SDKs and CLI tools go in `<repo-parent>/toolchains/<name>-<version>/`, shared by every repository.
- Never write to `<repo-parent>` itself or to the shared checkout.

Use the repository scripts for merged-worktree cleanup; `[gone]` or ancestry alone is insufficient. Preserve dirty, locked, open-PR, harness-managed, and locally divergent worktrees. Inspect untracked source before cleanup, because the scripts may clear build artifacts. Remove an abandoned worktree by hand only after verifying its repository and path, its ownership, and that it holds no valuable unpublished work.

Stop only the services, processes, and containers started for this worktree. Never blanket-delete containers or volumes, and never stop the shared lock Redis. Lifecycle, preservation, and crash behavior: [WorktreeLifecycle.md](../../../../scripts/WorktreeLifecycle.md).

## Implement and ship

Follow the validation, resource limits, target frameworks, generated-code boundaries, and performance requirements in `AGENTS.md`. Use TDD for bugs and behavior changes where feasible, and explain exceptions. Run focused and relevant broader checks, including docs and frontend checks when affected. Review the full diff for correctness, reuse, and efficiency (`/simplify` when available, otherwise an equivalent review). Preserve performance and contracts, and batch related fixes into one push.
