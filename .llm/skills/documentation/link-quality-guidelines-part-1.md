---
title: "Link Quality and External URL Management Part 1"
id: "link-quality-guidelines-part-1"
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

### Human-Readable Link Text

**Never use raw file names as link text.** Link text should describe what the user will find, not the file name.

#### Anti-patterns

```text
<!-- BAD: Raw file names as link text -->

See [README.md](../README.md) for installation instructions.
Check [CHANGELOG.md](../CHANGELOG.md) for version history.
Read [context.md](.llm/context.md) for guidelines.
```

#### Correct Patterns

```text
<!-- GOOD: Descriptive link text -->

See [the README](../README.md) for installation instructions.
Check [the changelog](../CHANGELOG.md) for version history.
Read [the AI Agent Guidelines](.llm/context.md) for guidelines.
```

#### Link Text Guidelines

| Scenario               | Bad Example                                   | Good Example                                          |
| ---------------------- | --------------------------------------------- | ----------------------------------------------------- |
| File reference         | `[package.json](package.json)`                | `[the package manifest](package.json)`                |
| Section reference      | `\[reference/faq.md\](docs/reference/faq.md)` | `[frequently asked questions](docs/reference/faq.md)` |
| Code location          | `[Tests/](Tests/)`                            | `[the test suite](Tests/)`                            |
| External documentation | `[docs.unity3d.com](url)`                     | `[Unity documentation](url)`                          |
| GitHub repository      | `[repo](url)`                                 | `[the DxMessaging repository](url)`                   |

#### Accessibility Considerations

Screen readers announce link text. Users should understand the destination without additional context:

```markdown
<!-- BAD: "Click here" pattern -->

For more information, click [here](../docs/guides/advanced.md).

<!-- GOOD: Descriptive and accessible -->

For more information, see [advanced usage patterns](../docs/guides/advanced.md).
```

### Repository URL Consistency

Skill files include repository metadata in the YAML frontmatter. These URLs must match the actual repository.

#### Frontmatter URL Fields

```yaml
source:
  repository: "Ambiguous-Interactive/DxMessaging" # Format: "owner/repo"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging" # Full HTTPS URL
```

#### Common Mistakes

| Mistake               | Incorrect Value                                         | Correct Value                                          |
| --------------------- | ------------------------------------------------------- | ------------------------------------------------------ |
| Wrong organization    | `wallstop-studios/DxMessaging`                          | `Ambiguous-Interactive/DxMessaging`                    |
| Wrong repository name | `wallstop/com.wallstop-studios.dxmessaging`             | `Ambiguous-Interactive/DxMessaging`                    |
| Missing `https://`    | `github.com/Ambiguous-Interactive/DxMessaging`          | `https://github.com/Ambiguous-Interactive/DxMessaging` |
| Trailing slash        | `https://github.com/Ambiguous-Interactive/DxMessaging/` | `https://github.com/Ambiguous-Interactive/DxMessaging` |
| SSH URL format        | `git@github.com:Ambiguous-Interactive/DxMessaging.git`  | `https://github.com/Ambiguous-Interactive/DxMessaging` |

#### Verification Steps

1. **Check the remote URL**: Run `git remote get-url origin` to confirm the actual repository
1. **Visit the URL**: Before committing, open the URL in a browser to verify it resolves
1. **Compare with existing files**: Check other skill files for consistent formatting

### External Link Validation

External URLs can break without warning. Follow these practices to minimize broken link risk.

#### Before Adding External Links

1. **Verify the URL loads**: Open in a browser, check for redirects
1. **Prefer stable URLs**: Use versioned documentation when available
1. **Check for canonical URLs**: Some sites redirect to preferred formats

#### Unity Documentation URLs

Unity documentation frequently reorganizes. Use current URL patterns:

```markdown
<!-- Current Unity documentation format -->

https://docs.unity3d.com/Manual/PageName.html
https://docs.unity3d.com/ScriptReference/ClassName.html

<!-- Versioned documentation (more stable) -->

https://docs.unity3d.com/2021.3/Documentation/Manual/PageName.html
```

#### External Link Checklist

| Check                       | Action                                               |
| --------------------------- | ---------------------------------------------------- |
| URL returns 200             | Open in browser, verify no 404/redirect chain        |
| Content matches expectation | Confirm the page contains the referenced information |
| URL is HTTPS                | Avoid HTTP links; use HTTPS for security             |
| URL shorteners              | Use full URLs, not bit.ly or similar                 |
| Versioned when possible     | Prefer `/v1.2.3/` over `/latest/` for stability      |

#### High-Risk External Domains

Some domains change URL structures frequently. Extra verification is needed:

- Unity documentation (`docs.unity3d.com`) - Check version-specific URLs
- Microsoft documentation (`docs.microsoft.com`, `learn.microsoft.com`) - Reorganized in 2023
- Stack Overflow - Answers can be deleted; quote key information

For detailed guidance on URL fragment validation (`#section-name` links), see [External URL Fragment Validation](external-url-fragment-validation.md).

### Handling Link Checker False Positives

Automated link checkers (like lychee) can fail on valid URLs when websites block automated requests. This does not mean the link is broken.

#### Common Blocking Status Codes

| Status Code | Meaning                | Example Cause                               |
| ----------- | ---------------------- | ------------------------------------------- |
| 403         | Forbidden              | User-agent blocking, geographic restriction |
| 415         | Unsupported Media Type | Server rejects non-browser Accept headers   |
| 429         | Too Many Requests      | Rate limiting                               |
| 503         | Service Unavailable    | Bot protection, Cloudflare challenge        |

#### Investigating Link Checker Failures

When a link checker reports an error:

1. **Manually verify the link**: Open the URL in a browser to confirm it works
1. **Check the status code**: Some codes (403, 415, 429) often indicate bot blocking, not broken links
1. **Try a different user-agent**: The link may work with browser-like headers
1. **Check if the site has bot protection**: Cloudflare, Akamai, or custom protection may block automated clients

#### Adding Exclusions to `.lychee.toml`

When a link is valid but the site blocks automated checkers, add an exclusion pattern:

```toml
exclude = [
  # NPM package page serves a Cloudflare JS challenge to non-browser clients
  "^https://www\\.npmjs\\.com/package/com\\.wallstop-studios\\.dxmessaging$",
  # Game Programming Patterns site returns 415 to automated clients despite valid content
  "^https://gameprogrammingpatterns\\.com/"
]
```

#### Exclusion Pattern Guidelines

- **Use regex anchors**: Start patterns with `^` to match from the beginning of the URL
- **Escape special characters**: Dots in domain names need `\\.` escaping
- **Document the reason**: Add a comment explaining why the exclusion exists
- **Be specific**: Prefer specific URL patterns over broad domain exclusions when possible
- **Verify first**: Always manually verify the link is actually valid before adding an exclusion

### GitHub Actions Version Consistency

Workflow files should use consistent action versions across all workflows. For detailed guidance including version update processes and common actions to monitor, see [GitHub Actions Version Consistency](github-actions-version-consistency.md).

## See Also

- [Link Quality and External URL Management](./link-quality-guidelines.md)
