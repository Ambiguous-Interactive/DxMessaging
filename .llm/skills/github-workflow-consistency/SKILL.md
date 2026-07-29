---
name: github-workflow-consistency
description: "Structural and safety rules for .github/workflows: property order (name, on, concurrency, permissions, jobs), a concurrency group with cancel-in-progress, explicit minimal permissions, timeout-minutes on every job, persist-credentials: false on checkout, path filters that include .github/workflows/**, required gates that fail closed when the changes detector fails, the CI Success aggregate, per-extension git add --renormalize loops, .lychee.toml accept/exclude policy with the pinned install-pinned-lychee action, devcontainers/ci push and eventFilterForPush gotchas, and the release.yml notes/unitypackage invariants. Use when adding or editing a workflow, wiring a required check, debugging a GHCR push or a lychee failure, or touching the release pipeline."
metadata:
  category: "github-actions"
  tags: "github-actions, ci-cd, workflow, security, consistency, yaml"
---

# GitHub Workflow Consistency

Every workflow under `.github/workflows/` follows the same skeleton, declares the least
privilege it needs, and fails closed. This skill also covers the four subsystems that most
often break a workflow: git commands in CI, lychee link checking, devcontainer image
publishing, and the tag-triggered release.

## When to use

- Adding a workflow, or editing triggers, permissions, concurrency, or timeouts.
- Wiring a new required check or a `changes`-gated job.
- A GHCR `docker pull` fails with `manifest unknown`, or a push silently does not happen.
- A `Lint docs links` job fails, or `.lychee.toml` needs a new status code.
- Touching `release.yml`, release notes generation, or the `.unitypackage` export.

## Rules

### Workflow skeleton

- Top-level property order is exactly `name`, `on`, `concurrency`, `permissions`, `jobs`.
- Every workflow declares
  `concurrency: { group: ${{ github.workflow }}-${{ github.ref }}, cancel-in-progress: true }`
  and an explicit `permissions` block (default `contents: read`; never omit it).
- Every job declares `timeout-minutes`. Guides: lint 5, build 15-30, tests 30-60, deploy 10-15.
- Every `actions/checkout` declares `persist-credentials` explicitly and uses `false`. Push
  credentials are scoped to the one step that needs them: either
  `git -c http.https://github.com/.extraheader=...` on a specific command, or a guarded
  `git remote set-url` immediately before a `git-auto-commit-action` step followed immediately
  by a guarded cleanup that restores a plain `https://github.com/...` remote. Never leave a
  tokenized remote or a persistent `http.*.extraheader` config behind.
- For default-branch auto-commits authenticated by `actions/create-github-app-token`, prefer
  explicit shell steps: fetch the target ref with command-scoped credentials, verify the
  checked-out commit is still current, stage only the intended files, and push with a
  command-scoped extraheader. Regenerate or skip stale artifacts rather than letting a
  non-fast-forward race turn red. Use double quotes for strings and 2-space indentation.
- Self-referential workflows (anything that lints a file type) must include
  `.github/workflows/**` in their path filters, plus the tool config files (`.prettierrc*`,
  `.editorconfig`, `.markdownlint*`, `.csharpierrc*`, `package.json`, and so on). Keep the
  `push` and `pull_request` branch allow-lists consistent.

### Required checks and fail-closed gates

- Put new required static checks in `.github/workflows/ci.yml`, keep the job-level condition at
  `if: ${{ always() }}`, skip expensive steps internally when the shared `changes` job says the
  files are irrelevant, and add the job id to `ci-success.needs`. `CI Success` is the
  branch-protection API name; do not require individual static job names.
- GitHub treats a conditionally skipped job as successful, so a `changes`-gated required job
  must run unless `changes` SUCCEEDED and explicitly emitted `relevant=false`:
  `if: ${{ always() && (needs.changes.result != 'success' || needs.changes.outputs.relevant != 'false') }}`,
  with an early guard step that errors when `changes` failed or emitted anything other than
  `true`/`false`.

### Git in CI

- Guard commands that assume history: check `git rev-list --count HEAD` before `HEAD~1`, use
  `git fetch --depth=N` or `--unshallow` before history traversal, and handle detached HEAD
  (`git branch --show-current` returns empty) by falling back to `git rev-parse HEAD`.
- `grep` exits 1 with no matches and fails the step; use `|| true` or
  `$(grep -c ... || echo "0")`.
- `git add --renormalize` exits 128 when a pathspec matches zero files. ALWAYS use a
  per-extension loop with an existence check, never a single multi-pattern command:

  ```bash
  for ext in cs md json asmdef yml; do
    if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then
      git add --renormalize -- "*.$ext" "**/*.$ext"
    fi
  done
  ```

- `git ls-files` matches dotfiles but `git add` globs do not, so exclude any extension whose
  tracked files are all dotfiles (notably `yaml`; use `yml`). The same limitation applies to
  `file_pattern` in `git-auto-commit-action`. Keep renormalize patterns, `file_pattern`, and
  path triggers synchronized.
- `git add --renormalize` updates the INDEX only. To fix the working tree run
  `git add --renormalize . && git checkout -- .`. `.gitattributes` is the source of truth; the
  pre-commit `mixed-line-ending` hook is the enforcement.

### Lychee link checking

- Do not use `lycheeverse/lychee-action@v2`. Use `./.github/actions/install-pinned-lychee` with
  `version: v0.24.2`, then invoke the `lychee` CLI directly.
- `.lychee.toml` is shared by both jobs and uses the v0.24.2 field names: `verbose` (string
  enum, not `verbosity`), `max_retries` (not `retries`), `include_mail = false` (not
  `exclude_mail = true`). Lychee treats unknown fields as hard errors.
- `ci.yml` / `Lint docs links` is the blocking check: an offline pass
  (`--offline --include-fragments`) validating relative links and in-repo anchors with zero
  network, then a lenient external pass. `markdown-link-validity.yml` is a daily advisory scan
  that never fails and syncs one tracking issue from `./lychee/out.md`.
- Acceptance policy: never accept 404 or 410; always accept 403 and 429. When a site adopts a
  new blocking status, widen the shared `accept` list. Never add a per-domain `exclude` and
  never swap to a supposedly more stable domain. `exclude` is reserved for endpoints CI cannot
  reach at all: loopback hosts, the not-yet-deployed Pages site, and self-repo blob/tree links
  validated offline.
- `accept_timeouts` stays out of `.lychee.toml`; the blocking workflow passes
  `--accept-timeouts=true` on the command line so the advisory scan still reports slow hosts.

### Devcontainer image workflows

- `devcontainers/ci@v0.3` pushes from its POST action, which runs after normal steps. If the
  same job verifies GHCR, set `push: never` and run an explicit `docker push` (plus
  `docker manifest inspect`) before `docker pull`. This is what `devcontainer-prebuild.yml`
  does.
- When the action does publish, set `eventFilterForPush: ""` explicitly. The default `"push"`
  gates ALL push decisions, so `schedule` and `workflow_dispatch` runs silently skip the push.
  `devcontainer-test.yml` relies on `push: filter` plus `refFilterForPush` with an empty event
  filter.
- GHCR requires lowercase image names: derive `repository_lowercase` with `tr` in a `repo` step
  and use it in every `imageName` and `cacheFrom`. Prebuild jobs need `packages: write`.

### Release invariants

- The GitHub Release body MUST be the matching `## [version]` section of `CHANGELOG.md` plus an
  install footer, produced by the single extractor `scripts/release/release-notes.js` (backed
  by `scripts/release/changelog.js`). Never build notes inline with `printf`. Pass the bare
  version (`3.1.0`), not the tag. `changelog.js` reuses `CodeBlockTracker`, so a fenced
  `## [9.9.9]` inside an entry does not truncate the section.
- The ephemeral export project built by `export-unitypackage.ps1` MUST write the full built-in
  module set from `scripts/unity/unity-builtin-modules.json` into `Packages/manifest.json`.
  An empty `{"dependencies": {}}` enables no `com.unity.modules.*`, so `EditorGUIUtility`
  fails with `CS0012` and `ExportPackage` never runs. Gate optional samples behind
  `REFLEX_PRESENT` / `VCONTAINER_PRESENT` / `ZENJECT_PRESENT` instead of adding external
  packages to the export manifest.
- The `.unitypackage` is a release-blocking asset: `publish` uses plain `needs` gating with no
  `always()` escape hatch, a missing or empty staged file is a hard error, and a final step
  asserts all four assets (`.tgz` + `.sha256`, `.unitypackage` + `.sha256`) are attached.
  Guards live in `scripts/__tests__/changelog-section.test.js`,
  `export-unitypackage-stage.test.js`, and `asset-store-submission.test.js`.

## References

| Document                                                                                    | Purpose                                                                                                                                   |
| ------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| [cicd-devcontainer-workflows.md](./references/cicd-devcontainer-workflows.md)               | devcontainers/ci post-action push and eventFilterForPush gotchas, GHCR lowercase naming, and the pitfall table                            |
| [git-renormalize-patterns.md](./references/git-renormalize-patterns.md)                     | The per-extension renormalize loop, exit-128 behavior, and the dotfile glob difference between git ls-files and git add                   |
| [git-workflow-robustness-part-1.md](./references/git-workflow-robustness-part-1.md)         | CommonMark inline-code parsing rules, parser test coverage patterns, and grep exit-code handling in CI                                    |
| [git-workflow-robustness.md](./references/git-workflow-robustness.md)                       | Git commands that fail on initial commits, shallow clones, detached HEAD, and empty repos, plus index-versus-working-tree renormalization |
| [lychee-configuration-part-1.md](./references/lychee-configuration-part-1.md)               | Lychee config mistake table and the pre-change and post-upgrade checklists                                                                |
| [lychee-configuration.md](./references/lychee-configuration.md)                             | Field deprecation mappings, the pinned installer, the two-gate CI wiring, and the live .lychee.toml accept/exclude policy                 |
| [release-asset-and-notes-invariants.md](./references/release-asset-and-notes-invariants.md) | The three v3.1.0 release invariants: single notes extractor, full built-in module manifest, and the blocking .unitypackage asset          |
| [workflow-consistency-part-1.md](./references/workflow-consistency-part-1.md)               | Path-filter tables per workflow type, fail-closed required gates, formatting rules, and a complete workflow template                      |
| [workflow-consistency.md](./references/workflow-consistency.md)                             | Required property order and elements: concurrency, permissions, timeouts, secure checkout, and the CI Success static gate                 |
