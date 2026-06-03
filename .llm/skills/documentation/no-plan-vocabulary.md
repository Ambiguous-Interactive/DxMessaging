---
title: "No PLAN Vocabulary in Shipping Content"
id: "no-plan-vocabulary"
category: "documentation"
version: "1.0.0"
created: "2026-05-06"
updated: "2026-05-06"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/validate-no-plan-vocabulary.js"
    - path: "scripts/__tests__/validate-no-plan-vocabulary.test.js"
    - path: "Runtime/"
    - path: "Editor/"
    - path: "SourceGenerators/"
    - path: "Samples~/"
    - path: "docs/"
    - path: "llms.txt"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "vocabulary"
  - "policy"
  - "shipping-content"
  - "validation"

complexity:
  level: "basic"
  reasoning: "Mechanical phrase rejection with an allowlist for legitimate exceptions."

impact:
  performance:
    rating: "none"
    details: "Validator runs at pre-push only; no runtime impact."
  maintainability:
    rating: "medium"
    details: "Keeps internal planning vocabulary out of user-facing surfaces."
  testability:
    rating: "low"
    details: "validate-no-plan-vocabulary.js plus its Jest test cover the rule."

prerequisites:
  - "human-prose-policy"

dependencies:
  packages: []
  skills:
    - "changelog-management"
    - "human-prose-policy"

applies_to:
  languages:
    - "C#"
    - "Markdown"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "No PLAN vocabulary"
  - "PLAN.md vocabulary policy"

related:
  - "changelog-management"
  - "human-prose-policy"

status: "stable"
---

# No PLAN Vocabulary in Shipping Content

> **One-line summary**: Internal `PLAN.md` filenames and `T0.0` / `P0.0`-style
> milestone tags must not appear in shipping content.

## The rule

The `validate:no-plan-vocabulary` validator rejects three forbidden patterns
inside any path under `Runtime/`, `Editor/`, `SourceGenerators/`, `Samples~/`,
`docs/`, root markdown (`*.md` at the repository root), and `llms.txt`:

1. Internal plan-file filename references: `PLAN.md`, `PERF-PLAN.md`,
   `OLD-PLAN.md`, `GH-PAGES-PLAN.md`. The validator matches on the literal
   filename token, not on every occurrence of the substring `plan`.
1. Tier tag patterns shaped like `T<digit>.<digit>` and `P<digit>.<digit>`
   (for example `T0.0`, `T6.3`, `P1.2`). The validator matches on the dotted
   form with explicit digit groups so that bare `T1` / `P0` references are
   not affected.
1. Plan-section headings: lines starting with `# Phase P<n>` or
   `# Tier T<n>` (any heading depth). The validator scans markdown files
   only. User-facing 'Phase 0/1/2/3' headings without the `P` prefix are
   intentionally allowed.

The root files `PLAN.md`, `PERF-PLAN.md`, `OLD-PLAN.md`, and `GH-PAGES-PLAN.md`
themselves are explicitly outside the validator's scan set; they are internal
planning artifacts that never ship.

## What is intentionally allowed

- Bare `T1` through `T6` Mermaid node IDs inside diagrams.
- Bare `P0`, `P1`, `P2` tokens inside test method names and identifiers.
- Phase 0 through Phase 3 stages in `docs/guides/migration-guide.md`.
  Other 'Phase N' variants (without the `P` prefix) are also allowed by
  the validator regex but are not currently used in shipping docs.

If any of those bare forms gets flagged, that is a validator false positive
to fix in `scripts/validate-no-plan-vocabulary.js`, not a docs rewrite.

## How to add a legitimate exception

If a public reference to one of the forbidden tokens is genuinely required
(for example, a CHANGELOG entry that has to name a deleted file), edit the
validator's allowlist with a justification comment in the same change. Never
disable the validator; never add a per-file ignore that hides the rule from
review.

## Why this rule exists

The repository's internal `PLAN.md`, `PERF-PLAN.md`, `OLD-PLAN.md`, and
`GH-PAGES-PLAN.md` capture in-flight planning that never ships to users.
Mixing those filenames or tier tags into release docs, runtime XML doc
comments, or sample code confuses readers, dates the docs, and hands users
a vocabulary they have to mentally translate. Keeping the planning
vocabulary out of shipping content is cheap when enforced mechanically and
expensive to scrub once it leaks.

## Validation

Run from the repository root:

```bash
npm run validate:no-plan-vocabulary
```

The hook fires at pre-push because the validator walks the full shipping
tree. Treat any failure as a prose rewrite, not a hook bypass; see
[Git Hook Performance Budget](../performance/git-hook-performance.md) for
the underlying budget rationale.

## See also

- [Changelog Management](./changelog-management.md)
- [Human-Prose Documentation Policy](./human-prose-policy.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-06 | Initial version |
