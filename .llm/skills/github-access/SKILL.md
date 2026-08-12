---
name: github-access
description: Access GitHub repositories, issues, pull requests, reviews, checks, workflows, and releases through the repository's supported authentication paths. Use whenever an agent needs GitHub metadata or writes, needs to commit or push, is about to invoke gh, or sees gh authentication fail.
---

# GitHub Access

Use GitHub access in this order and stop at the first path that supports the operation:

1. Use the VS Code/Codex GitHub connector or GitHub extension.
1. Use local `git` for operations it supports.
1. Use `gh` only as the final fallback.

Never treat a failed `gh auth status` as a blocker before trying the connector, extension, and
`git` paths.

## Connector and extension first

- Prefer an available GitHub connector for repository, issue, pull-request, review, check,
  workflow, and release metadata or supported writes.
- If no connector tool covers the operation, use the VS Code credential helper exposed inside
  the devcontainer. The host has `github.vscode-pull-request-github` installed, and
  `git credential fill` can provide its current GitHub credential for direct API calls.
- Never print the output of `git credential fill`. Capture it and the password field in shell
  variables inside the same command that performs the API request, then unset them.
- Never write a GitHub token to a file, command output, progress record, or log.
- Reuse one captured credential for a related batch of API calls instead of requesting it for
  each call.

## Use git second

Use `git` for local history and remote transport operations it already supports, including:

- inspect, branch, stage, commit, fetch, rebase, and compare;
- push through the configured `origin` remote;
- query refs with `git ls-remote`.

The repository's `origin` uses SSH, so fetch and push can work even when `gh` has no valid token.
Do not replace a working remote merely to accommodate `gh`.

## Use gh last

- Invoke `gh` only when neither the connector/extension nor `git` supports the required action.
- Check `gh auth status` only after reaching this fallback. An unauthenticated or expired `gh`
  session does not invalidate the earlier access paths.
- Do not start a frequent `gh` polling loop. Prefer connector notifications, one blocking wait,
  or infrequent single checks measured in minutes.

## Mutation and verification

- Apply the normal authorization boundary before opening, editing, merging, closing, or deleting
  remote objects.
- After a write, verify the resulting object through the connector when possible, otherwise with
  the same successful access path.
- Push sparingly because every push starts the repository's full CI pipeline.
