#!/usr/bin/env python3
"""Pin merge and sweep worktree safety."""

import json
import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path


SCRIPT = Path("scripts/Merge-Pr.ps1")
WORKTREE_CLEANUP = Path("scripts/WorktreeCleanup.ps1")
SWEEP_SCRIPT = Path("scripts/Remove-MergedWorktrees.ps1")


def run(
    *args: str,
    cwd: Path,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        args,
        cwd=cwd,
        check=False,
        capture_output=True,
        text=True,
        env=env,
    )


def assert_worktree_cleanup_behavior(errors: list[str]) -> None:
    cleanup_script = WORKTREE_CLEANUP.resolve()
    with tempfile.TemporaryDirectory(prefix="fixture-merge-safety-") as temp:
        root = Path(temp)
        repo = root / "repo"
        repo.mkdir()
        commands = (
            ("git", "init"),
            ("git", "config", "user.email", "test@fixture.local"),
            ("git", "config", "user.name", "Fixture Test"),
            ("git", "commit", "--allow-empty", "-m", "base"),
        )
        for command in commands:
            result = run(*command, cwd=repo)
            if result.returncode:
                errors.append(f"Fixture command failed: {' '.join(command)}: {result.stderr}")
                return

        merged_worktree = root / "merged-worktree"
        result = run("git", "worktree", "add", "-b", "merged-branch", str(merged_worktree), cwd=repo)
        if result.returncode:
            errors.append(f"Could not create merged fixture worktree: {result.stderr}")
            return
        run("git", "commit", "--allow-empty", "-m", "merged head", cwd=merged_worktree)
        expected_head = run("git", "rev-parse", "HEAD", cwd=merged_worktree).stdout.strip()

        command = (
            f". '{cleanup_script}'; "
            f"Remove-MergedWorktree -Repo '{repo}' -Worktree '{merged_worktree}' "
            f"-ExpectedHead '{expected_head}' -Label fixture"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        if result.returncode or merged_worktree.exists():
            errors.append(
                "Worktree cleanup must remove a clean worktree whose head is contained in the PR head. "
                f"stdout={result.stdout!r} stderr={result.stderr!r}"
            )

        local_worktree = root / "local-worktree"
        result = run("git", "worktree", "add", "-b", "local-branch", str(local_worktree), expected_head, cwd=repo)
        if result.returncode:
            errors.append(f"Could not create local-only fixture worktree: {result.stderr}")
            return
        run("git", "commit", "--allow-empty", "-m", "local only", cwd=local_worktree)

        command = (
            f". '{cleanup_script}'; "
            f"Remove-MergedWorktree -Repo '{repo}' -Worktree '{local_worktree}' "
            f"-ExpectedHead '{expected_head}' -Label fixture"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        if result.returncode or not local_worktree.exists():
            errors.append(
                "Worktree cleanup must preserve a clean worktree with local-only commits beyond the PR head. "
                f"stdout={result.stdout!r} stderr={result.stderr!r}"
            )

        source_worktree = root / "untracked-source-worktree"
        result = run("git", "worktree", "add", "-b", "untracked-source", str(source_worktree), expected_head, cwd=repo)
        if result.returncode:
            errors.append(f"Could not create untracked-source fixture: {result.stderr}")
            return
        source_file = source_worktree / "NewFeature.cs"
        source_file.write_text("internal sealed class NewFeature;", encoding="utf-8")
        command = (
            f". '{cleanup_script}'; "
            f"Remove-MergedWorktree -Repo '{repo}' -Worktree '{source_worktree}' "
            f"-ExpectedHead '{expected_head}' -Label fixture"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        if result.returncode or not source_file.exists():
            errors.append("Cleanup must preserve untracked source files.")

        locked_worktree = root / "cleanup-locked-worktree"
        result = run(
            "git",
            "worktree",
            "add",
            "-b",
            "cleanup-locked-branch",
            str(locked_worktree),
            expected_head,
            cwd=repo,
        )
        if result.returncode:
            errors.append(f"Could not create locked cleanup fixture: {result.stderr}")
            return
        run("git", "worktree", "lock", "--reason", "fixture", str(locked_worktree), cwd=repo)
        command = (
            f". '{cleanup_script}'; "
            f"Remove-MergedWorktree -Repo '{repo}' -Worktree '{locked_worktree}' "
            f"-ExpectedHead '{expected_head}' -Label fixture"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        if result.returncode or not locked_worktree.exists():
            errors.append(
                "Shared cleanup must preserve a locked worktree instead of bypassing the lock via raw deletion. "
                f"stdout={result.stdout!r} stderr={result.stderr!r}"
            )
        if locked_worktree.exists():
            run("git", "worktree", "unlock", str(locked_worktree), cwd=repo)

        remote = root / "remote.git"
        run("git", "init", "--bare", str(remote), cwd=root)
        run("git", "remote", "add", "origin", str(remote), cwd=repo)
        run("git", "branch", "exact-branch", expected_head, cwd=repo)
        run("git", "push", "origin", "exact-branch", cwd=repo)
        command = (
            f". '{cleanup_script}'; "
            f"if (Remove-RemoteBranchAtExpectedHead -Repo '{repo}' -Branch exact-branch "
            f"-ExpectedHead '{expected_head}') {{ exit 0 }} else {{ exit 1 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        exact_remote = run("git", "ls-remote", "--heads", "origin", "exact-branch", cwd=repo)
        if result.returncode or exact_remote.stdout.strip():
            errors.append("Remote cleanup must delete a branch whose tip still equals the PR head.")

        run("git", "branch", "race-branch", expected_head, cwd=repo)
        advanced_worktree = root / "advanced-worktree"
        run("git", "worktree", "add", str(advanced_worktree), "race-branch", cwd=repo)
        run("git", "commit", "--allow-empty", "-m", "remote advanced", cwd=advanced_worktree)
        run("git", "push", "origin", "race-branch", cwd=advanced_worktree)
        command = (
            f". '{cleanup_script}'; "
            f"if (Remove-RemoteBranchAtExpectedHead -Repo '{repo}' -Branch race-branch "
            f"-ExpectedHead '{expected_head}') {{ exit 1 }} else {{ exit 0 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        advanced_remote = run("git", "ls-remote", "--heads", "origin", "race-branch", cwd=repo)
        if result.returncode or not advanced_remote.stdout.strip():
            errors.append("Remote cleanup must preserve a branch advanced beyond the recorded PR head.")

        command = (
            f". '{cleanup_script}'; "
            f"if (Remove-LocalBranchAtExpectedHead -Repo '{repo}' -Branch exact-branch "
            f"-ExpectedHead '{expected_head}') {{ exit 0 }} else {{ exit 1 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        exact_local = run("git", "show-ref", "--verify", "refs/heads/exact-branch", cwd=repo)
        if result.returncode or exact_local.returncode == 0:
            errors.append("Local cleanup must delete a branch contained in the recorded PR head.")

        command = (
            f". '{cleanup_script}'; "
            f"if (Remove-LocalBranchAtExpectedHead -Repo '{repo}' -Branch race-branch "
            f"-ExpectedHead '{expected_head}') {{ exit 1 }} else {{ exit 0 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        race_local = run("git", "show-ref", "--verify", "refs/heads/race-branch", cwd=repo)
        if result.returncode or race_local.returncode:
            errors.append("Local cleanup must preserve a branch advanced beyond the recorded PR head.")

        remote_match_repo = root / "remote-match"
        remote_match_repo.mkdir()
        run("git", "init", cwd=remote_match_repo)
        run("git", "remote", "add", "upstream", "https://github.com/example/Fixture.git", cwd=remote_match_repo)
        command = (
            f". '{cleanup_script}'; "
            f"if ((Get-GitHubRemoteName -Repo '{remote_match_repo}' -GitHubRepo 'example/Fixture') "
            f"-eq 'upstream') {{ exit 0 }} else {{ exit 1 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=remote_match_repo)
        if result.returncode:
            errors.append("Repo overrides must resolve the matching git remote instead of assuming origin.")

        run(
            "git",
            "remote",
            "set-url",
            "--push",
            "upstream",
            "https://github.com/fork-owner/Fixture.git",
            cwd=remote_match_repo,
        )
        command = (
            f". '{cleanup_script}'; "
            f"if ($null -eq (Get-GitHubRemoteName -Repo '{remote_match_repo}' "
            f"-GitHubRepo 'example/Fixture')) {{ exit 0 }} else {{ exit 1 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=remote_match_repo)
        if result.returncode:
            errors.append("Remote selection must reject a matching fetch URL whose push URL targets another repo.")

        main_repo = root / "main-checkout"
        main_repo.mkdir()
        for command in commands:
            result = run(*command, cwd=main_repo)
            if result.returncode:
                errors.append(f"Main-checkout fixture command failed: {' '.join(command)}: {result.stderr}")
                return
        main_head = run("git", "rev-parse", "HEAD", cwd=main_repo).stdout.strip()
        command = (
            f". '{cleanup_script}'; "
            f"Remove-MergedWorktree -Repo '{main_repo}' -Worktree '{main_repo}' "
            f"-ExpectedHead '{main_head}' -Label main-checkout"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=root)
        if result.returncode or not (main_repo / ".git").exists():
            errors.append(
                "Worktree cleanup must refuse to remove the main checkout. "
                f"stdout={result.stdout!r} stderr={result.stderr!r}"
            )

        stale_worktree = root / "stale-worktree"
        run("git", "worktree", "add", "-b", "stale-branch", str(stale_worktree), expected_head, cwd=repo)
        shutil.rmtree(stale_worktree)
        command = (
            f". '{cleanup_script}'; "
            f"if (Test-SameFilesystemPath -Left '{stale_worktree}' -Right '{repo}') {{ exit 1 }}; "
            f"Remove-MergedWorktree -Repo '{repo}' -Worktree '{stale_worktree}' "
            f"-ExpectedHead '{expected_head}' -Label fixture"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=repo)
        listed = run("git", "worktree", "list", "--porcelain", cwd=repo)
        if result.returncode or "stale-branch" in listed.stdout:
            errors.append(
                "Cleanup must tolerate and prune a registered worktree whose directory is missing. "
                f"stdout={result.stdout!r} stderr={result.stderr!r}"
            )

        fetch_source = root / "fetch-source"
        fetch_source.mkdir()
        for command in commands:
            result = run(*command, cwd=fetch_source)
            if result.returncode:
                errors.append(f"Fetch fixture command failed: {' '.join(command)}: {result.stderr}")
                return
        fetch_remote = root / "fetch-remote.git"
        run("git", "init", "--bare", str(fetch_remote), cwd=root)
        run("git", "remote", "add", "origin", str(fetch_remote), cwd=fetch_source)
        run("git", "branch", "base-branch", cwd=fetch_source)
        run("git", "push", "origin", "base-branch", cwd=fetch_source)
        run("git", "commit", "--allow-empty", "-m", "expected head", cwd=fetch_source)
        missing_expected_head = run("git", "rev-parse", "HEAD", cwd=fetch_source).stdout.strip()
        run("git", "push", "origin", "HEAD:pr-branch", cwd=fetch_source)

        fetch_consumer = root / "fetch-consumer"
        result = run(
            "git", "clone", "--single-branch", "--branch", "base-branch",
            fetch_remote.as_uri(), str(fetch_consumer), cwd=root
        )
        if result.returncode:
            errors.append(f"Could not create missing-head fixture clone: {result.stderr}")
            return
        local_base = run("git", "rev-parse", "HEAD", cwd=fetch_consumer).stdout.strip()
        missing_before = run("git", "cat-file", "-e", f"{missing_expected_head}^{{commit}}", cwd=fetch_consumer)
        command = (
            f". '{cleanup_script}'; "
            f"if (Test-CommitContainedInExpectedHead -Repo '{fetch_consumer}' "
            f"-LocalHead '{local_base}' -ExpectedHead '{missing_expected_head}') {{ exit 0 }} else {{ exit 1 }}"
        )
        result = run("pwsh", "-NoProfile", "-Command", command, cwd=fetch_consumer)
        if missing_before.returncode == 0 or result.returncode:
            missing_after = run("git", "cat-file", "-e", f"{missing_expected_head}^{{commit}}", cwd=fetch_consumer)
            ancestry = run("git", "merge-base", "--is-ancestor", local_base, missing_expected_head, cwd=fetch_consumer)
            remote_refs = run("git", "branch", "-r", cwd=fetch_consumer)
            errors.append(
                "Containment checks must fetch an authoritative PR head missing from the local object database. "
                f"stdout={result.stdout!r} stderr={result.stderr!r} "
                f"object_after={missing_after.returncode} ancestry={ancestry.returncode} "
                f"remote_refs={remote_refs.stdout!r}"
            )


def assert_merged_worktree_sweep_behavior(errors: list[str]) -> None:
    sweep_script = SWEEP_SCRIPT.resolve()
    with tempfile.TemporaryDirectory(prefix="fixture-worktree-sweep-") as temp:
        root = Path(temp)
        repo = root / "repo"
        repo.mkdir()
        commands = (
            ("git", "init"),
            ("git", "config", "user.email", "test@fixture.local"),
            ("git", "config", "user.name", "Fixture Test"),
            ("git", "commit", "--allow-empty", "-m", "base"),
            ("git", "branch", "-M", "main"),
        )
        for command in commands:
            result = run(*command, cwd=repo)
            if result.returncode:
                errors.append(f"Sweep fixture command failed: {' '.join(command)}: {result.stderr}")
                return

        remote = root / "origin.git"
        if run("git", "init", "--bare", str(remote), cwd=root).returncode:
            errors.append("Could not create sweep fixture remote.")
            return
        run("git", "remote", "add", "origin", str(remote), cwd=repo)
        result = run("git", "push", "-u", "origin", "main", cwd=repo)
        if result.returncode:
            errors.append(f"Could not push sweep fixture main branch: {result.stderr}")
            return

        worktree_root = Path(f"{repo}-worktrees")
        worktree_root.mkdir()

        def add_worktree(branch: str, directory: str, start: str = "main") -> Path:
            path = worktree_root / directory
            result = run("git", "worktree", "add", "-b", branch, str(path), start, cwd=repo)
            if result.returncode:
                raise RuntimeError(
                    f"Could not create sweep worktree {branch}: {result.stderr}"
                )
            return path

        def commit(
            worktree: Path,
            message: str,
            commit_env: dict[str, str] | None = None,
        ) -> str:
            result = run(
                "git",
                "commit",
                "--allow-empty",
                "-m",
                message,
                cwd=worktree,
                env=commit_env,
            )
            if result.returncode:
                raise RuntimeError(f"Could not commit {message}: {result.stderr}")
            return run("git", "rev-parse", "HEAD", cwd=worktree).stdout.strip()

        def push(
            worktree: Path,
            remote_branch: str,
            set_upstream: bool = False,
        ) -> None:
            arguments = ["git", "push"]
            if set_upstream:
                arguments.append("--set-upstream")
            arguments.extend(("origin", f"HEAD:{remote_branch}"))
            result = run(*arguments, cwd=worktree)
            if result.returncode:
                raise RuntimeError(
                    f"Could not push sweep fixture branch {remote_branch}: {result.stderr}"
                )

        try:
            harness_managed = repo / ".claude" / "worktrees" / "harness-managed"
            harness_managed.parent.mkdir(parents=True)
            result = run(
                "git",
                "worktree",
                "add",
                "-b",
                "harness-managed-branch",
                str(harness_managed),
                "main",
                cwd=repo,
            )
            if result.returncode:
                raise RuntimeError(
                    f"Could not create harness-managed sweep worktree: {result.stderr}"
                )
            harness_managed_head = commit(harness_managed, "harness-managed merged head")
            push(harness_managed, "harness-managed-branch")

            named = add_worktree("merged-name", "merged-name")
            named_head = commit(named, "named merged head")
            push(named, "merged-name")

            renamed = add_worktree("renamed-local", "renamed-local")
            renamed_head = commit(renamed, "renamed merged head")
            push(renamed, "original-renamed")

            associated = add_worktree("associated-local", "associated-local")
            associated_head = commit(associated, "associated merged head")
            push(associated, "association-original", set_upstream=True)

            closed = add_worktree("closed-unmerged", "closed-unmerged")
            closed_head = commit(closed, "closed unmerged head")
            push(closed, "closed-unmerged")

            closed_divergent = add_worktree(
                "closed-divergent", "closed-divergent"
            )
            closed_divergent_pr_head = commit(
                closed_divergent, "closed divergent recorded PR head"
            )
            push(closed_divergent, "closed-divergent")
            commit(closed_divergent, "local commit after closed PR")

            locked = add_worktree("locked-branch", "locked-branch")
            locked_head = commit(locked, "locked merged head")
            push(locked, "locked-branch")
            result = run("git", "worktree", "lock", "--reason", "fixture", str(locked), cwd=repo)
            if result.returncode:
                raise RuntimeError(f"Could not lock sweep fixture worktree: {result.stderr}")

            open_guard = add_worktree("reused-merged-name", "reused-merged-name")
            open_guard_head = commit(open_guard, "head reused by an open PR")
            push(open_guard, "reused-merged-name")

            open_association_guard = add_worktree(
                "open-associated-local", "open-associated-local"
            )
            old_open_commit_env = os.environ.copy()
            old_open_commit_env["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z"
            old_open_commit_env["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
            open_association_guard_head = commit(
                open_association_guard,
                "old head owned by an open associated PR",
                old_open_commit_env,
            )
            push(open_association_guard, "open-association-original", set_upstream=True)

            divergent = add_worktree("divergent-branch", "divergent-branch")
            divergent_pr_head = commit(divergent, "divergent recorded PR head")
            push(divergent, "divergent-branch")
            commit(divergent, "local commit beyond recorded PR head")

            unmatched = add_worktree("unmatched-branch", "unmatched-branch")

            stale = add_worktree("stale-scratch", "stale-scratch")
            old_commit_env = os.environ.copy()
            old_commit_env["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z"
            old_commit_env["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
            commit(stale, "old local scratch head", old_commit_env)

            detached = worktree_root / "detached-main"
            base_head = run("git", "rev-parse", "origin/main", cwd=repo).stdout.strip()
            result = run(
                "git", "worktree", "add", "--detach", str(detached), base_head, cwd=repo
            )
            if result.returncode:
                raise RuntimeError(f"Could not create detached sweep worktree: {result.stderr}")

            mixed_parent = root / "mixed-parent"
            external_worktree = mixed_parent / "registered-worktree"
            result = run(
                "git",
                "worktree",
                "add",
                "-b",
                "external-worktree",
                str(external_worktree),
                "main",
                cwd=repo,
            )
            if result.returncode:
                raise RuntimeError(
                    f"Could not create external sweep worktree: {result.stderr}"
                )

            case_variant_empty_project: Path | None = None
            if os.name != "nt":
                case_variant_root = worktree_root.with_name(
                    worktree_root.name.swapcase()
                )
                case_variant_worktree = case_variant_root / "registered-worktree"
                result = run(
                    "git",
                    "worktree",
                    "add",
                    "-b",
                    "case-variant-external-worktree",
                    str(case_variant_worktree),
                    "main",
                    cwd=repo,
                )
                if result.returncode:
                    raise RuntimeError(
                        "Could not create case-variant external sweep worktree: "
                        f"{result.stderr}"
                    )
                case_variant_empty_project = case_variant_root / "new-project"
                (case_variant_empty_project / "src").mkdir(parents=True)
        except RuntimeError as exc:
            errors.append(str(exc))
            return

        orphan = worktree_root / "orphaned-worktree"
        orphan.mkdir()
        orphan_gitdir = repo / ".git" / "worktrees" / "missing-registration"
        (orphan / ".git").write_text(f"gitdir: {orphan_gitdir}\n", encoding="utf-8")

        markerless_empty_orphan = worktree_root / "markerless-empty-orphan"
        (markerless_empty_orphan / "src" / "Fixture.React").mkdir(parents=True)
        old_markerless_timestamp = time.time() - 600
        for path in (
            markerless_empty_orphan / "src" / "Fixture.React",
            markerless_empty_orphan / "src",
            markerless_empty_orphan,
        ):
            os.utime(path, (old_markerless_timestamp, old_markerless_timestamp))

        markerless_recent_candidate = worktree_root / "markerless-recent-candidate"
        (markerless_recent_candidate / "src").mkdir(parents=True)

        unrelated_empty_project = mixed_parent / "new-project"
        (unrelated_empty_project / "src").mkdir(parents=True)

        artifact = worktree_root / "build-artifact"
        artifact.mkdir()
        (artifact / "keep.txt").write_text("not a worktree\n", encoding="utf-8")

        foreign = worktree_root / "foreign-worktree"
        foreign.mkdir()
        foreign_gitdir = root / "foreign-repo" / ".git" / "worktrees" / "entry"
        (foreign / ".git").write_text(f"gitdir: {foreign_gitdir}\n", encoding="utf-8")

        merged_prs = [
            {
                "headRefName": "merged-name",
                "headRefOid": named_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "original-renamed",
                "headRefOid": renamed_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "locked-branch",
                "headRefOid": locked_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "divergent-branch",
                "headRefOid": divergent_pr_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "reused-merged-name",
                "headRefOid": open_guard_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "harness-managed-branch",
                "headRefOid": harness_managed_head,
                "isCrossRepository": False,
            },
        ]
        open_prs = [
            {
                "headRefName": "active-name-for-reused-head",
                "headRefOid": open_guard_head,
            }
        ]
        closed_prs = [
            {
                "headRefName": "closed-unmerged",
                "headRefOid": closed_head,
                "isCrossRepository": False,
            },
            {
                "headRefName": "closed-divergent",
                "headRefOid": closed_divergent_pr_head,
                "isCrossRepository": False,
            },
        ]
        associations = {
            open_association_guard_head: [
                {
                    "state": "open",
                    "merged_at": None,
                    "head": {
                        "sha": open_association_guard_head,
                        "ref": "open-association-original",
                        "repo": {"full_name": "example/Fixture"},
                    },
                    "base": {"repo": {"full_name": "example/Fixture"}},
                }
            ],
            associated_head: [
                {
                    "state": "closed",
                    "merged_at": "2026-07-15T10:30:18Z",
                    "head": {
                        "sha": associated_head,
                        "ref": "association-original",
                        "repo": {"full_name": "example/Fixture"},
                    },
                    "base": {"repo": {"full_name": "example/Fixture"}},
                }
            ],
            base_head: [
                {
                    "state": "closed",
                    "merged_at": "2026-07-01T10:30:18Z",
                    "head": {
                        "sha": base_head,
                        "ref": "historic-unrelated-branch",
                        "repo": {"full_name": "example/Fixture"},
                    },
                    "base": {"repo": {"full_name": "example/Fixture"}},
                }
            ],
        }

        harness = root / "run-sweep-with-fake-gh.ps1"
        association_entries = [
            f"    '{sha}' = '{json.dumps(pull_requests)}'"
            for sha, pull_requests in associations.items()
        ]
        harness.write_text(
            "\n".join(
                [
                    "param(",
                    "    [Parameter(Mandatory)][string]$SweepScript,",
                    "    [switch]$Preview,",
                    "    [int]$StaleDays = 0",
                    ")",
                    f"$mergedJson = '{json.dumps(merged_prs)}'",
                    f"$openJson = '{json.dumps(open_prs)}'",
                    f"$closedJson = '{json.dumps(closed_prs)}'",
                    "$associationJsonBySha = @{",
                    *association_entries,
                    "}",
                    "function global:gh {",
                    "    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)",
                    "    if ($Arguments[0] -eq 'pr' -and $Arguments[1] -eq 'list') {",
                    "        $stateIndex = [Array]::IndexOf($Arguments, '--state')",
                    "        $json = switch ($Arguments[$stateIndex + 1]) {",
                    "            'merged' { $mergedJson }",
                    "            'open' { $openJson }",
                    "            'closed' { $closedJson }",
                    "            default { throw \"unexpected PR state: $($Arguments[$stateIndex + 1])\" }",
                    "        }",
                    "        $global:LASTEXITCODE = 0",
                    "        if ($Arguments -contains '--jq') {",
                    "            foreach ($item in ($json | ConvertFrom-Json)) { Write-Output $item.headRefName }",
                    "        } else { Write-Output $json }",
                    "        return",
                    "    }",
                    "    if ($Arguments[0] -eq 'repo' -and $Arguments[1] -eq 'view') {",
                    "        $global:LASTEXITCODE = 0",
                    "        Write-Output 'example/Fixture'",
                    "        return",
                    "    }",
                    "    if ($Arguments[0] -eq 'api') {",
                    "        $sha = $Arguments[1] -replace '^.*/commits/([^/]+)/pulls$', '$1'",
                    "        $global:LASTEXITCODE = 0",
                    "        if ($associationJsonBySha.ContainsKey($sha)) { Write-Output $associationJsonBySha[$sha] }",
                    "        else { Write-Output '[]' }",
                    "        return",
                    "    }",
                    "    $global:LASTEXITCODE = 2",
                    "    throw \"unexpected gh arguments: $Arguments\"",
                    "}",
                    "$sweepArgs = @{}",
                    "if ($Preview) { $sweepArgs.WhatIf = $true }",
                    "if ($StaleDays -gt 0) { $sweepArgs.StaleDays = $StaleDays }",
                    "& $SweepScript @sweepArgs",
                ]
            )
            + "\n",
            encoding="utf-8",
        )

        preview = run(
            "pwsh",
            "-NoProfile",
            "-File",
            str(harness),
            "-SweepScript",
            str(sweep_script),
            "-Preview",
            cwd=repo,
        )
        preview_output = f"{preview.stdout}\n{preview.stderr}"
        if preview.returncode:
            errors.append(f"Merged-worktree preview must remain best-effort: {preview_output}")
        preview_paths = (
            named,
            renamed,
            associated,
            closed,
            closed_divergent,
            detached,
            orphan,
            markerless_empty_orphan,
            markerless_recent_candidate,
            divergent,
            open_guard,
            open_association_guard,
            stale,
            harness_managed,
            unrelated_empty_project,
        )
        if case_variant_empty_project is not None:
            preview_paths += (case_variant_empty_project,)
        for path in preview_paths:
            if not path.exists():
                errors.append(f"-WhatIf must not remove {path}. Output: {preview_output}")
        if any(
            "WOULD remove" in line and "divergent-branch" in line
            for line in preview_output.splitlines()
        ):
            errors.append(
                "-WhatIf must honor local-commit containment before claiming a worktree is removable. "
                f"Output: {preview_output}"
            )
        if any(
            "WOULD remove" in line and "stale-scratch" in line
            for line in preview_output.splitlines()
        ):
            errors.append("Stale scratch cleanup must require explicit -StaleDays opt-in.")

        result = run(
            "pwsh",
            "-NoProfile",
            "-File",
            str(harness),
            "-SweepScript",
            str(sweep_script),
            "-StaleDays",
            "30",
            cwd=repo,
        )
        output = f"{result.stdout}\n{result.stderr}"
        if result.returncode:
            errors.append(f"Merged-worktree sweep must remain best-effort: {output}")

        removed_paths = {
            named: "merged PR branch-name match",
            renamed: "merged PR head-SHA match after local rename",
            associated: "commit-to-PR association",
            closed: "closed-unmerged PR head",
            detached: "detached HEAD reachable from origin/main",
            orphan: "dangling registered-worktree directory",
            markerless_empty_orphan: "empty markerless orphan directory shell",
        }
        for path, reason in removed_paths.items():
            if path.exists():
                errors.append(f"Sweep must remove {reason}: {path}. Output: {output}")

        preserved_paths = {
            locked: "locked worktree",
            open_guard: "worktree whose tip belongs to an open PR under another branch name",
            open_association_guard: "old worktree whose tip is protected only by open-PR association",
            divergent: "branch with commits beyond recorded PR head",
            closed_divergent: "closed PR branch with commits beyond recorded PR head",
            unmatched: "worktree with no merge evidence",
            harness_managed: "harness-managed worktree nested under the main checkout",
            artifact: "non-worktree artifact directory",
            foreign: "worktree marker owned by another repository",
            unrelated_empty_project: "empty directory beside an external worktree",
            markerless_recent_candidate: "recently created markerless worktree directory",
        }
        if case_variant_empty_project is not None:
            preserved_paths[case_variant_empty_project] = (
                "case-distinct directory beside an external worktree"
            )
        for path, reason in preserved_paths.items():
            if not path.exists():
                errors.append(f"Sweep must preserve {reason}: {path}. Output: {output}")

        normalized_scan_lines = [
            line.replace("\\", "/")
            for line in output.splitlines()
            if "scanning orphan root" in line
        ]
        expected_scans = [(worktree_root.as_posix(), "markerless=True")]
        if case_variant_empty_project is not None:
            expected_scans.append(
                (case_variant_empty_project.parent.as_posix(), "markerless=False")
            )
        for expected_root, expected_markerless in expected_scans:
            if not any(
                expected_root in line and expected_markerless in line
                for line in normalized_scan_lines
            ):
                errors.append(
                    "Sweep must scan each case-distinct root with the correct "
                    f"markerless policy ({expected_root}, {expected_markerless}). "
                    f"Scan lines: {normalized_scan_lines}. Output: {output}"
                )

        expected_output = (
            "merged PR head tip SHA",
            "merged PR via commit association",
            "closed-unmerged PR head branch",
            "detached HEAD reachable from origin/main",
            "skipping locked worktree",
            "skipping worktree whose HEAD belongs to an open PR association",
            "removed orphaned dir",
            "removed empty markerless orphaned dir",
            "skipping markerless orphan candidate outside dedicated worktree root",
            "skipping recently modified markerless orphan candidate",
            "skipping unproven markerless orphan candidate with content",
        )
        for marker in expected_output:
            if marker not in output:
                errors.append(f"Sweep output must explain '{marker}'. Output: {output}")

        removed_local_branches = ("merged-name", "renamed-local", "associated-local")
        for branch in removed_local_branches:
            ref = run("git", "show-ref", "--verify", f"refs/heads/{branch}", cwd=repo)
            if ref.returncode == 0:
                errors.append(f"Sweep must remove safe local branch {branch} after worktree cleanup.")

        removed_remote_branches = (
            "merged-name",
            "original-renamed",
            "association-original",
        )
        for branch in removed_remote_branches:
            ref = run("git", "ls-remote", "--exit-code", "--heads", "origin", branch, cwd=repo)
            if ref.returncode == 0:
                errors.append(f"Sweep must remove safe remote PR branch {branch} with a lease.")

        if stale.exists():
            errors.append(
                "Sweep must remove an old clean scratch worktree only after -StaleDays opts in. "
                f"Output: {output}"
            )
        stale_branch = run(
            "git", "show-ref", "--verify", "refs/heads/stale-scratch", cwd=repo
        )
        if stale_branch.returncode:
            errors.append("Stale scratch cleanup must preserve its local branch.")

        for ref_name in ("closed-unmerged", "closed-divergent"):
            local_ref = run(
                "git", "show-ref", "--verify", f"refs/heads/{ref_name}", cwd=repo
            )
            if local_ref.returncode:
                errors.append(
                    f"Closed-unmerged cleanup must preserve local branch {ref_name}."
                )
            remote_ref = run(
                "git",
                "ls-remote",
                "--exit-code",
                "--heads",
                "origin",
                ref_name,
                cwd=repo,
            )
            if remote_ref.returncode:
                errors.append(
                    f"Closed-unmerged cleanup must preserve remote branch {ref_name}."
                )

        run("git", "worktree", "unlock", str(locked), cwd=repo)


def main() -> int:
    source = SCRIPT.read_text(encoding="utf-8")
    cleanup_source = WORKTREE_CLEANUP.read_text(encoding="utf-8")
    sweep_source = SWEEP_SCRIPT.read_text(encoding="utf-8")
    merge_line = next(
        (line.strip() for line in source.splitlines() if line.strip().startswith("gh pr merge ")),
        None,
    )
    errors: list[str] = []

    if merge_line is None:
        errors.append("Merge-Pr must invoke gh pr merge.")
    elif "--delete-branch" in merge_line:
        errors.append(
            "Merge-Pr must not ask gh to delete a branch while its worktree is still attached."
        )

    required_markers = (
        "Remove-MergedWorktree",
        "Remove-RemoteBranchAtExpectedHead",
        "Remove-LocalBranchAtExpectedHead",
    )
    positions = {marker: source.find(marker) for marker in required_markers}
    for marker, position in positions.items():
        if position < 0:
            errors.append(f"Merge-Pr is missing required cleanup marker: {marker}")

    if all(position >= 0 for position in positions.values()):
        if not (
            positions["Remove-MergedWorktree"]
            < positions["Remove-RemoteBranchAtExpectedHead"]
            < positions["Remove-LocalBranchAtExpectedHead"]
        ):
            errors.append(
                "Merge-Pr must remove the worktree before deleting remote and local branches."
            )

    if "--json state" not in source or '"MERGED"' not in source:
        errors.append("Merge-Pr must reconcile a non-zero gh exit against authoritative PR state.")

    if "ExpectedHead" not in cleanup_source or "merge-base --is-ancestor" not in cleanup_source:
        errors.append(
            "Worktree cleanup must preserve a clean branch whose local tip is not contained in the PR head."
        )
    if "-ExpectedHead $headRefOid" not in source:
        errors.append("Merge-Pr must pass the authoritative PR head to worktree cleanup.")
    if (
        "headRefOid" not in sweep_source
        or "ExpectedHead = $evidence.ExpectedHead" not in sweep_source
        or "Remove-MergedWorktree @cleanupArgs" not in sweep_source
    ):
        errors.append("Merged-worktree sweep must preserve commits beyond the merged PR head.")
    if "Remove-LocalBranchAtExpectedHead" not in source:
        errors.append("Merge-Pr must guard local branch deletion even when no worktree exists.")
    if cleanup_source.count("Test-CommitContainedInExpectedHead") < 2:
        errors.append("WorktreeCleanup must own and use the shared commit-containment policy.")
    if "Remove-RemoteBranchAtExpectedHead" not in source or "--force-with-lease=$lease" not in cleanup_source:
        errors.append("Remote PR branch deletion must be leased to the PR head commit.")
    if "Remove-LocalBranchAtExpectedHead" not in source or "Remove-LocalBranchAtExpectedHead" not in cleanup_source:
        errors.append("Local PR branch deletion must use the shared containment helper.")
    if "Get-GitHubRemoteName" not in source or "-Remote $gitRemote" not in source:
        errors.append("Merge-Pr must target the git remote matching the -Repo override.")
    if "remote get-url --push" not in cleanup_source:
        errors.append("GitHub remote selection must validate the push URL used for branch deletion.")
    if "isCrossRepository" not in sweep_source or "Remove-RemoteBranchAtExpectedHead" not in sweep_source:
        errors.append("The merged-worktree sweep must clean eligible remote branches after removal.")
    if "Remove-LocalBranchAtExpectedHead" not in sweep_source:
        errors.append("The merged-worktree sweep must clean eligible local branches after removal.")
    if "isCrossRepository" not in source or "if ($head.isCrossRepository)" not in source:
        errors.append("Merge-Pr must not delete an origin branch for a cross-repository PR.")
    if "Test-SameFilesystemPath -Left $cur -Right $mainRepo" not in source:
        errors.append("Merge-Pr worktree discovery must exclude the main checkout.")
    if "Test-SameFilesystemPath -Left $Repo -Right $Worktree" not in cleanup_source:
        errors.append("Worktree cleanup must refuse the main checkout before any removal attempt.")
    if "-replace '/', '\\'" not in cleanup_source:
        errors.append("Windows extended-length cleanup paths must normalize git's forward slashes.")

    assert_worktree_cleanup_behavior(errors)
    assert_merged_worktree_sweep_behavior(errors)

    for error in errors:
        print(f"::error::{error}")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
