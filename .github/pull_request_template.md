<!--
Keep this short. Write in plain, simple English: short sentences, common words,
one idea per sentence. Aim for 200 words, stop at 400.
Agents: follow .llm/skills/pull-request-writing/SKILL.md.
-->

## Why

<!-- What went wrong for a user, or what they could not do. One short paragraph. -->

## What changed

<!-- What the code does now. A short list if there is more than one part. -->

## How we know

<!-- The test, the measurement, or the run that proves it. -->

## Related Issue

Closes #

## Type of Change

<!-- Check all that apply -->

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to change)
- [ ] Documentation update
- [ ] Refactor (code change that neither fixes a bug nor adds a feature)

## Checklist

<!-- Ensure all items are completed before requesting review -->

- [ ] All tests pass locally
- [ ] Code is properly formatted
- [ ] I have added tests that prove my fix is effective or my feature works
- [ ] I have updated the documentation accordingly
- [ ] I have updated the [CHANGELOG](../CHANGELOG.md)
- [ ] My changes do not introduce breaking changes, or breaking changes are documented

<!--
Dispatch-throughput numbers are owned entirely by CI: the Performance Numbers
workflow (.github/workflows/perf-numbers.yml) re-runs the benchmarks on each
eligible same-repository pull_request change and posts an exact commit- and
run-linked evidence comment on this PR. The comment includes current Standalone
IL2CPP throughput deltas against the current baseline and Standalone TargetMap
rows. In every delta, "+" means better and "-" means worse. Fork and Dependabot pull requests skip
licensed benchmarks because they cannot receive Unity credentials. After the PR
merges, CI commits the refreshed table directly to the default branch
(docs/architecture/performance.md). You do NOT need to paste before/after numbers
into this description.
-->
