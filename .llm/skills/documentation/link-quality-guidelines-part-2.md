---
title: "Link Quality and External URL Management Part 2"
id: "link-quality-guidelines-part-2"
category: "documentation"
version: "1.4.0"
created: "2026-01-22"
updated: "2026-03-16"
status: "stable"
tags:
  - migration
  - split
complexity:
  level: "intermediate"
impact:
  performance:
    rating: "low"
---

## Overview

Continuation material extracted from `link-quality-guidelines.md` to keep .llm files within the 300-line budget.

## Solution

## Validation Checklist

Before committing documentation or skill files:

- [ ] All link text is human-readable (no raw file names)
- [ ] Repository URLs in frontmatter match `git remote get-url origin`
- [ ] External URLs have been visited and return 200
- [ ] External URL fragments have been verified to scroll to the correct section
- [ ] GitHub Action versions are consistent across all workflows
- [ ] No `http://` links (use `https://` instead)
- [ ] No URL shorteners or tracking parameters

## See Also

- [Documentation Updates](documentation-updates.md)
- [Changelog Management](changelog-management.md)
- [Documentation Style Guide](documentation-style-guide.md)
- [External URL Fragment Validation](external-url-fragment-validation.md)
- [GitHub Actions Version Consistency](github-actions-version-consistency.md)

## References

- [Markdown Guide - Basic Syntax](https://www.markdownguide.org/basic-syntax/)
- [W3C WAI - Writing for Web Accessibility (Link Text)](https://www.w3.org/WAI/tips/writing/#make-link-text-meaningful)
- [GitHub Actions - Using Actions](https://docs.github.com/en/actions/using-workflows/workflow-syntax-for-github-actions#jobsjob_idstepsuses)

## Changelog

| Version | Date       | Changes                                                                        |
| ------- | ---------- | ------------------------------------------------------------------------------ |
| 1.4.0   | 2026-02-09 | Added guidance for handling link checker false positives and lychee exclusions |
| 1.3.0   | 2026-01-27 | Split fragment validation and GitHub Actions to separate skills                |
| 1.2.0   | 2026-01-23 | Added external URL fragment validation section based on CI failure analysis    |
| 1.1.0   | 2026-01-22 | Added documentation linting scripts section with testing guidance              |
| 1.0.0   | 2026-01-22 | Initial version covering link quality fundamentals                             |

## Related Links

- [Link Quality and External URL Management](./link-quality-guidelines.md)
