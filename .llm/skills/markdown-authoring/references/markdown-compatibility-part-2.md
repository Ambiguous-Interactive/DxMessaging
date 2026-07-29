## Overview

Continuation material extracted from `markdown-compatibility.md` to keep .llm files within the 300-line budget.

## Solution

## Quick Reference

| Feature     | MkDocs Syntax               | Standard Alternative               |
| ----------- | --------------------------- | ---------------------------------- |
| Note        | `!!! note`                  | `> ℹ️ **Note**: ...`               |
| Warning     | `!!! warning`               | `>  **Warning**: ...`              |
| Danger      | `!!! danger`                | `> 🚨 **Danger**: ...`             |
| Tip         | `!!! tip`                   | `> 💡 **Tip**: ...`                |
| Collapsible | `??? note`                  | `<details><summary>...</summary>`  |
| Tabs        | `=== "Tab"`                 | `### Tab` headers                  |
| Button      | `[text](url){ .md-button }` | `[text](url)` or `**[text](url)**` |
| Emoji       | `:emoji:`                   | Unicode emoji: 🚀 Yes              |

---

## Mermaid Diagram Theming

> 🔗 **See [Mermaid Theming](./mermaid-theming.md)** for complete guidance on Mermaid diagram theming in MkDocs Material.

**Key rule**: Do not use `%%{init: {'theme': '...'}}%%` directives in `docs/` files. Material's built-in mermaid integration themes diagrams from the site palette automatically.

---

## Validation

Before committing documentation changes:

1. **Preview in VS Code**: Use `Ctrl+Shift+V` to open markdown preview
1. **Check on GitHub**: View the file in GitHub's web interface after pushing
1. **Search for forbidden patterns**: `grep -E '^(!!!|\?\?\?|===)' docs/`

### Automated Check (Optional)

Add to your review process:

```bash
# Find MkDocs-specific syntax in markdown files
grep -rn --include='*.md' -E '^(!!!|\?\?\?|===)' docs/
grep -rn --include='*.md' '{ *\.md-button' docs/
grep -rn --include='*.md' ':[a-z_]+:' docs/ | grep -v 'https://'
# Find per-diagram Mermaid theme directives (forbidden in docs/)
grep -rn --include='*.md' "%%{init.*theme" docs/
```

---

## Why This Matters

1. **GitHub is the primary documentation surface**: Most users first encounter docs in the repository, not the built site.

1. **Editor preview during development**: Contributors use VS Code preview while editing; broken syntax slows them down.

1. **Third-party integrations**: Tools like npm, documentation aggregators, and AI assistants read raw markdown.

1. **Graceful degradation**: Even if MkDocs adds features, the docs remain readable everywhere.

1. **Reduced maintenance burden**: No need to maintain two versions or worry about platform-specific rendering bugs.

---

### 5. Emoji Shortcodes

MkDocs uses `:emoji_name:` shortcode syntax from the `pymdownx.emoji` extension.

#### Forbidden: Emoji Shortcodes

```markdown
:warning: This is a warning.

:rocket: Fast performance!

:white_check_mark: Test passed.

:fontawesome-brands-github: GitHub integration.

:material-code-braces: Code example.
```

> **Note**: Material for MkDocs provides icon shortcodes that must also be avoided:
>
> - `:material-*:` patterns (e.g., `:material-code-braces:`, `:material-check:`)
> - `:octicons-*:` patterns (e.g., `:octicons-git-branch-16:`)
> - `:fontawesome-*:` patterns (e.g., `:fontawesome-brands-github:`)
> - `:simple-*:` patterns (e.g., `:simple-python:`)
>
> These render as literal text in standard markdown viewers.

#### Correct: Unicode Emoji Directly

```markdown
This is a warning.

🚀 Fast performance!

Test passed.

GitHub integration (use text, not icon).

Code example (describe with words).
```

**Common emoji substitutions**:

| Shortcode              | Unicode | Copy-paste |
| ---------------------- | ------- | ---------- |
| `:warning:`            |         |            |
| `:rocket:`             | 🚀      | 🚀         |
| `:white_check_mark:`   | Yes     | Yes        |
| `:x:`                  | No      | No         |
| `:bulb:`               | 💡      | 💡         |
| `:information_source:` | ℹ️      | ℹ️         |
| `:fire:`               | 🔥      | 🔥         |
| `:star:`               | ⭐      | ⭐         |
| `:book:`               | 📖      | 📖         |
| `:wrench:`             | 🔧      | 🔧         |

---

### 6. Other MkDocs-Specific Syntax

#### Forbidden: Annotations

```markdown
Some code (1)
{ .annotate }

1.  This is an annotation that appears on hover.
```

#### Forbidden: Keys Extension

```markdown
Press ++ctrl+alt+del++ to restart.
```

#### Forbidden: Critic Markup

```markdown
{--deleted text--}
{++inserted text++}
{~~old~>new~~}
```

#### Correct: Use Plain Descriptions

```markdown
Some code <!-- Explanation in a comment or below -->

Press `Ctrl+Alt+Del` to restart.

Changed "old text" to "new text".
```

---

## See Also

- [Markdown Compatibility Guidelines](./markdown-compatibility-part-1.md)
- [Markdown Compatibility Guidelines](./markdown-compatibility.md)

## Related Links

- [Documentation Style Guide](../../documentation-style/references/documentation-style-guide.md)
- [Documentation Updates](../../documentation-style/references/documentation-updates.md)
- [Mermaid Diagram Theming](./mermaid-theming.md)
- [MkDocs Navigation](./mkdocs-navigation.md)

## References

- [CommonMark Specification](https://spec.commonmark.org/)
- [GitHub Flavored Markdown](https://github.github.com/gfm/)
- [MkDocs Material Extensions](https://squidfunk.github.io/mkdocs-material/reference/)
- [Mermaid Theming](https://mermaid.js.org/config/theming.html)

## Changelog

| Version | Date       | Changes                                             |
| ------- | ---------- | --------------------------------------------------- |
| 1.2.0   | 2026-01-29 | Added nested fenced code blocks section             |
| 1.1.0   | 2026-01-29 | Added Mermaid diagram theming section               |
| 1.0.0   | 2026-01-29 | Initial version with forbidden syntax documentation |

## Related Links 2

- [Markdown Compatibility Guidelines](./markdown-compatibility.md)
