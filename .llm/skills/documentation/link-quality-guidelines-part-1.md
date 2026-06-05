---
title: "Link Quality and External URL Management Part 1"
id: "link-quality-guidelines-part-1"
category: "documentation"
version: "1.5.0"
created: "2026-01-22"
updated: "2026-06-04"
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

External URLs can break without warning. Follow these practices to point at the right page. Note what CI does and does not gate: external link _liveness_ is non-deterministic (authoritative sites return 403/429/503 to CI bots via WAF or bot detection even when the page loads in a browser), so the blocking PR check accepts those responses and fails only on genuinely-dead links (404/410 or DNS/connection failure). Deep external rot is caught by the scheduled advisory scan, which opens a tracking issue instead of failing CI. See [Lychee Link Checker Configuration Management](../github-actions/lychee-configuration.md) for the accept-list policy.

#### Before Adding External Links

1. **Open the URL in a browser**: Confirm it resolves and check for redirects
1. **Use the correct canonical URL**: Some sites redirect to a preferred format; link to that one directly
1. **Use versioned documentation when it exists**: A `/2021.3/` path keeps a fragment stable; this is a content-accuracy choice, not a way to dodge a CI failure (swapping to a "more stable" domain does not help when the new domain also blocks bots)

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

| Check                       | Action                                                              |
| --------------------------- | ------------------------------------------------------------------- |
| URL resolves in a browser   | Open it; confirm it loads (a CI 403/429 does not mean it is broken) |
| Content matches expectation | Confirm the page contains the referenced information                |
| URL is HTTPS                | Avoid HTTP links; use HTTPS for security                            |
| URL shorteners              | Use full URLs, not bit.ly or similar                                |
| Versioned when possible     | Prefer `/v1.2.3/` over `/latest/` for fragment stability            |

#### High-Risk External Domains

Some domains change URL structures frequently. Extra verification is needed:

- Unity documentation (`docs.unity3d.com`) - Check version-specific URLs
- Microsoft documentation (`docs.microsoft.com`, `learn.microsoft.com`) - Reorganized in 2023
- Stack Overflow - Answers can be deleted; quote key information

For detailed guidance on URL fragment validation (`#section-name` links), see [External URL Fragment Validation](external-url-fragment-validation.md).

### Handling Link Checker False Positives

Automated link checkers (like lychee) can fail on valid URLs when websites block automated requests. This does not mean the link is broken.

#### Bot-Detection and Throttling Status Codes (accepted, never fail CI)

These codes mean the server answered and the page is real; the accept list in `.lychee.toml` treats them as pass.

| Status Code        | Meaning                        | Example Cause                                    |
| ------------------ | ------------------------------ | ------------------------------------------------ |
| 401, 403           | Unauthorized / Forbidden       | User-agent blocking, WAF, geographic restriction |
| 405, 406, 415, 451 | Method / content / legal block | Server rejects non-browser methods or headers    |
| 408, 429           | Request Timeout / Too Many     | Rate limiting, throttling                        |
| 500-599            | Server error                   | Transient outage, Cloudflare challenge           |

Only 404 (Not Found), 410 (Gone), and DNS/connection failures fail the check, because those are the responses that mean the link is genuinely dead.

#### Investigating Link Checker Failures

When the blocking PR check reports an external-link error:

1. **Confirm it is a dead-link failure**: The blocking check fails only on 404/410 or a DNS/connection error. A 403/429/503 is already accepted and never reds the PR.
1. **Open the URL in a browser**: If a 404/410 reproduces, the page moved or was removed; update the link to its new location.
1. **If the page loads fine but a NEW blocking status appears in CI**: the site adopted a status code not yet in `accept`. Widen `accept` (see below), do not add a per-domain exclude.

#### The Fix Is To Widen `accept`, Not To Exclude

External link liveness is non-deterministic, so the policy is to accept every "server answered but refused/throttled the bot" status in `.lychee.toml` rather than silence individual domains. If a real site starts returning a blocking status that is not yet accepted, add that status to the shared `accept` list:

```toml
accept = [
  "200..=299",
  "401",
  "403",
  "405",
  "406",
  "408",
  "415",
  "429",
  "451",
  "500..=599",
]
```

Do NOT add a per-domain `exclude` (for example a regex for the npm registry page or the Game Programming Patterns site) to silence a bot-detection response, and do NOT swap the link to a "more stable" domain. Both are the fragile patterns this policy retired: the prior `webaim.org` to `w3.org` swap failed identically because both block bots. The `exclude` list in `.lychee.toml` is reserved for endpoints CI cannot reach at all (localhost, the not-yet-deployed GitHub Pages site, and self-repo blob/tree links validated offline). The `validate-lychee-config.js` script enforces this: it FORBIDS accepting 404/410 and REQUIRES accepting 403/429.

#### `exclude` Pattern Guidelines (reserved cases only)

- **Use regex anchors**: Start patterns with `^` to match from the beginning of the URL
- **Escape special characters**: Dots in domain names need `\\.` escaping
- **Document the reason**: Add a comment explaining why CI cannot reach the endpoint
- **Reserve for true non-reachability**: Only loopback hosts, sites not yet deployed, and offline-validated self-repo links belong here -- never a site that merely blocks bots

### GitHub Actions Version Consistency

Workflow files should use consistent action versions across all workflows. For detailed guidance including version update processes and common actions to monitor, see [GitHub Actions Version Consistency](github-actions-version-consistency.md).

## See Also

- [Link Quality and External URL Management](./link-quality-guidelines.md)
