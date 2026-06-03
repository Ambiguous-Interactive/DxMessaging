---
title: "External URL Fragment Validation"
id: "external-url-fragment-validation"
category: "documentation"
version: "1.0.0"
created: "2026-01-27"
updated: "2026-01-27"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: ".llm/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "links"
  - "urls"
  - "validation"
  - "fragments"
  - "ci-cd"

complexity:
  level: "basic"
  reasoning: "Fragment validation requires attention to detail but follows clear patterns"

impact:
  performance:
    rating: "none"
    details: "Documentation links do not affect runtime performance"
  maintainability:
    rating: "high"
    details: "Broken fragments degrade documentation quality and user experience"
  testability:
    rating: "low"
    details: "Fragment validation can be automated with tools like lychee"

prerequisites:
  - "Understanding of URL structure"
  - "Familiarity with HTML anchor IDs"

dependencies:
  packages: []
  skills:
    - "link-quality-guidelines"

applies_to:
  languages:
    - "Markdown"
  frameworks: []

aliases:
  - "URL fragment validation"
  - "Anchor link validation"
  - "Section link checking"

related:
  - "link-quality-guidelines"
  - "documentation-updates"

status: "stable"
---

# External URL Fragment Validation

> **One-line summary**: Verify that URL fragments (`#section-name`) point to valid, existing anchors on external pages.

## Overview

URL fragments (the `#section-name` portion after the main URL) are particularly fragile for external links. The target page's heading structure can change without notice, breaking fragment references.

## Problem Statement

Fragment links can break silently or cause CI failures:

| Issue                          | Example                                                   | Risk                                                        |
| ------------------------------ | --------------------------------------------------------- | ----------------------------------------------------------- |
| Missing ID on target heading   | `<h2>Links</h2>` with no `id` attribute                   | CI failure when lychee validates with `--include-fragments` |
| Incorrect fragment guessing    | `#links-1` instead of `#links`                            | CI failure when lychee validates with `--include-fragments` |
| Heading renumbered             | `#step-3-install` becomes `#step-4-install`               | Silent breakage, confusing users                            |
| Heading text changed           | `#getting-started` becomes `#quick-start`                 | 404-like behavior on the page                               |
| Auto-generated fragment suffix | `#links` vs `#links-1` (duplicate heading disambiguation) | Wrong section targeted                                      |

> **Note**: Some websites have broken internal links themselves. For example, markdownguide.org
> links to `#links` but their `<h2>Links</h2>` has no `id` attribute. When in doubt, omit the fragment.

## Solution

### Fragment Validation Process

1. **Navigate to the exact URL**: Open `https://example.com/page#fragment` in a browser
1. **Verify scroll position**: Confirm the page scrolls to the expected section
1. **Inspect the heading ID**: Right-click the heading -> Inspect -> Check the `id` attribute
1. **Test with link checker**: Run `lychee --include-fragments "URL"` locally

### Fragment ID Discovery

Different sites generate fragment IDs differently:

```bash
# GitHub generates IDs from heading text (lowercase, hyphens for spaces)
## Getting Started -> #getting-started

# Some sites use custom IDs
<h2 id="quick-start">Getting Started</h2> -> #quick-start

# Duplicate headings get suffixes
## Links -> #links
## Links -> #links-1  (second occurrence)
```

### Best Practices for Fragment URLs

- **Prefer linking without fragments** when the page is short enough to scan
- **Quote key information** rather than relying solely on the fragment link
- **Use versioned documentation** when fragments must remain stable
- **Add comments** near fragile external links explaining what they reference

```markdown
<!-- Good: Includes context in case fragment breaks -->

See the [Markdown links syntax](https://www.markdownguide.org/basic-syntax/)
section for details on inline and reference-style links.

<!-- Risky: Fragment-only reference with no context -->

See [here](https://www.markdownguide.org/basic-syntax/).
```

### Common Fragment Patterns by Site

| Site            | ID Generation Pattern                                     |
| --------------- | --------------------------------------------------------- |
| GitHub          | Lowercase, spaces -> hyphens, special chars removed       |
| Unity Docs      | Custom IDs, often different from heading text             |
| Microsoft Learn | Lowercase, spaces -> hyphens, may include section numbers |
| MDN Web Docs    | Lowercase, underscores for spaces                         |
| Stack Overflow  | Numeric IDs for answers, text for sections                |

### Automated Validation

Use `lychee` or similar tools to validate fragments:

```bash
# Check all links including fragments
lychee --include-fragments docs/

# Check a specific URL with fragment
lychee --include-fragments "https://example.com/page#section"
```

### When to Omit Fragments

Consider omitting fragments when:

- The target page is short (users can easily find the section)
- The fragment target is unstable (frequently reorganized documentation)
- You're quoting the key information anyway
- The site is known to have broken fragment implementations

## Validation Checklist

Before committing links with fragments:

- [ ] URL with fragment loads and scrolls to correct section
- [ ] Heading has an `id` attribute (inspect element to verify)
- [ ] Fragment format matches site's ID generation pattern
- [ ] Key information is quoted in case fragment breaks
- [ ] Versioned documentation used when available

## See Also

- [Link Quality Guidelines](link-quality-guidelines.md) - Main link quality skill
- [Documentation Updates](documentation-updates.md) - Keeping docs in sync

## Changelog

| Version | Date       | Changes                                              |
| ------- | ---------- | ---------------------------------------------------- |
| 1.0.0   | 2026-01-27 | Split from link-quality-guidelines for focused scope |
