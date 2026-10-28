# AsyncSemaphore

Read [AGENTS.md](../../../../AGENTS.md) for validation and performance evidence. Releaser copies must share at-most-once release state; never pool it. Preserve the `Semaphores` namespace and analyzer contracts. Select frameworks from the project files and use the documented TUnit commands.

No Aspire AppHost. The queue's Redis lock container requires Docker; its namespace is `asyncsemaphore`.

Draft policy: `AGENTS.md` applies the skill's draft review lifecycle to every agent-created PR, including work outside the loop. Before creating or promoting a PR from an existing worktree, re-read `AGENTS.md` and this skill from the shared checkout; a branch copy can predate the current policy.

Draft reviewers: Claude Code Review and Codex must both approve the current head before promotion.

- Claude: `.github/workflows/claude-code-review.yml` reviews every new head, drafts included, and posts the review as a `github-actions[bot]` issue comment. It counts for a head when the `claude-review` check on that head succeeded and the comment was posted after that head was pushed. The workflow runs on `pull_request_target`, so it always uses `main`'s version of itself; a PR that edits it is still reviewed by the old version. When no review exists for the current head, run `gh workflow run claude-code-review.yml --ref main -f pr_number=<N>` once. That run supplies a review, but its check is attached to `main`, not the PR head. To get a PR-head `claude-review` check on a draft without changing its head, close and immediately reopen the PR once.
- Codex (`chatgpt-codex-connector[bot]`): request draft reviews as in the lifecycle. Verdicts that count for the current head: a `codex-pull-request-review-summary` comment whose `codex-review-state:v1` payload has `code_review.commit_sha` equal to the full head SHA, `status: completed`, `has_any_findings: false`, and `has_unresolved_findings: false`; a review on that commit without findings; or a thumbs-up on this head's request comment whose author you verified as the Codex bot through the reactions API. A null or older `commit_sha`, findings, or a pending status is not approval. Re-read the summary on each visit, because Codex edits it in place.
- CodeRabbit also reviews this repository, but it skips drafts. Its review arrives after promotion and is handled as in the lifecycle's step 5.

Draft CI: `.github/workflows/dotnet.yml` skips every job on draft PR events before a runner is allocated. `ready_for_review` starts a run on the head, and `converted_to_draft` cancels a run that is still in progress. Pushes to `main` and manual dispatch behave as before. The required `modularpipeline` check reports skipped on a draft; that is not evidence for the ready head, so merge only after the run that `ready_for_review` started passes.
