# Session 001: Issue 52 cancellation safety

## Objective

Prevent stale pull-request runs from consuming the shared Unity license and avoid
rerunning the full Unity matrix for generated performance-document commits.

## Changes

- Added exact, immutable current-head guards before setup and lock acquisition.
- Granted the guard read-only pull-request metadata access.
- Ignored only the two CI-generated performance artifacts on push while keeping
  pull requests path-unfiltered.
- Extended the workflow policy validator to enforce guard placement, immutable
  pins, and trigger boundaries.

## Validation

- Workflow policy validator passed.
- Actionlint, formatting, spelling, and diff checks passed.
- The aggregate test suite passed except for three pre-existing Windows-host
  portability failures (symlink privilege, local Bash encoding, ANSI rendering).
- Adversarial review found no production defect; its trigger-parser hardening was
  incorporated and revalidated.
