# Local worktree lifecycle

`AgentLocks.ps1 release` removes its recorded checkout before releasing ownership. Register the path once after checkout with `renew -LockName <name> -Worktree <absolute-path>`. Use the shared checkout's current script, and release in `finally` after stopping owned processes and saving evidence outside the worktree.

Local branches remain available for `git worktree add <path> <branch>`. Detached commits are retained as `retained-worktrees/<SHA>`. Uncommitted source, unknown ignored files, Git-locked worktrees, the main checkout and foreign repositories are preserved. Known generated output is disposable under `WorktreeCleanup.ps1`. Release prints why a checkout was retained.

Run release with the shared checkout as the working directory. On Windows a process whose current directory is inside the worktree blocks deletion, and so can a build or test process that still holds a file. Git then deletes most of the tree, including its `.git` file, and fails. Cleanup therefore records each vetted checkout under `<git-common-dir>/agent-pending-removals` before deleting it, and removes the record on success. Each `Remove-MergedWorktrees.ps1` sweep retries recorded removals while reserving the recorded Redis item locks. It drops a record when a new checkout reuses the path, and preserves the directory when files outside known generated output changed after removal began.

A crashed process or an expired Redis TTL cannot run release. `Remove-MergedWorktrees.ps1` remains the conservative fallback for merged work. It also unlocks a `PR checkout setup` or `Issue checkout setup` Git lock that is older than the two-hour Redis TTL. No GitHub Actions job manages local disk.

Run regression tests locally with PowerShell 7, Git and Docker:

```powershell
pwsh scripts/Test-ReleasedWorktreeCleanup.ps1
```
