---
name: markdown-authoring
description: "Markdown that renders the same in GitHub, VS Code, and MkDocs Material: no admonitions (!!!), collapsibles (???), tabs (===), md-button attributes, emoji shortcodes, keys, or critic markup; no per-diagram Mermaid %%{init theme}%% directives; nested fences sized by backtick count; mkdocs.yml nav kept in sync with docs/; descriptive link text; and the lychee link gates. Use when adding or editing a Markdown page, a Mermaid diagram, a docs/ nav entry, or any internal or external link, or when a lychee link check fails."
metadata:
  category: "documentation"
  tags: "documentation, markdown, compatibility, mkdocs, portability"
---

# Markdown Authoring

DxMessaging documentation is read on GitHub, in VS Code preview, and on the MkDocs Material
site. Use only syntax that renders in all three, keep `mkdocs.yml` navigation synchronized with
`docs/`, and keep links descriptive and resolvable.

## When to use

- Adding or editing any `.md` file in `docs/`, `README.md`, or elsewhere in the repo.
- Adding a Mermaid diagram or touching diagram theming.
- Adding, renaming, or deleting a page under `docs/`.
- Adding an internal or external link, or a `#fragment` anchor.
- Triaging a `Lint docs links` failure or a scheduled link-scan tracking issue.

## Rules

### Forbidden MkDocs-only syntax

These render as literal broken text outside MkDocs. None may appear in any `.md` file:

- Admonitions `!!! note` / `!!! warning` / `!!! danger` / `!!! tip`. Use a blockquote callout
  instead: `> **Note**: ...`, `> **Warning**: ...`.
- Collapsibles `??? note` / `???+ warning`. Use `<details><summary>...</summary>` with a blank
  line after `<summary>`, or a plain heading.
- Content tabs `=== "Python"`. Use `###` headings per language.
- Button attributes `[text](url){ .md-button }`. Use a plain link, optionally bolded.
- Emoji shortcodes `:warning:`, `:rocket:`, and the icon families `:material-*:`,
  `:octicons-*:`, `:fontawesome-*:`, `:simple-*:`. Use a Unicode emoji directly, and only in a
  blockquote callout position (see the ASCII policy in the `documentation-style` skill).
- Annotations `{ .annotate }`, the keys extension `++ctrl+alt+del++`, and critic markup
  `{--del--}` / `{++ins++}` / `{~~old~>new~~}`.

Grep for regressions:

```bash
grep -rn --include='*.md' -E '^(!!!|\?\?\?|===)' docs/
grep -rn --include='*.md' '{ *\.md-button' docs/
grep -rn --include='*.md' ':[a-z_]+:' docs/ | grep -v 'https://'
```

### Nested fenced code blocks

- The outer fence must have MORE backticks than any inner fence. Three inside means four
  outside; four inside means five outside. Opening and closing sequences must match exactly.
- Never place a real document heading (for example `## See Also`) inside a fenced example. If
  removing the fence would change the document structure, the heading belongs outside.

### Mermaid

- Never use `%%{init: {'theme': '...'}}%%` in ANY markdown file, including `README.md`. GitHub
  and VS Code follow `prefers-color-scheme`; MkDocs Material is driven by
  `docs/javascripts/mermaid-config.js`, which detects `data-md-color-scheme`, re-renders on
  theme change, and strips stray init directives as a safety net.
- Avoid inline `style ... fill:#hex` node directives. They cannot be stripped automatically and
  break contrast in the opposite theme. If unavoidable, verify contrast in both themes.
- Verify with `grep -rn --include='*.md' "%%{init.*theme" .` (no output means clean).

### MkDocs navigation

- Every `.md` file under `docs/` must have an entry in the `nav` section of `mkdocs.yml`.
  Adding, renaming, or removing a page is a two-file change.
- A section's `index.md` is listed WITHOUT a title so the section header itself is clickable;
  giving it a title turns it into a separate item.
- Order pages by learning progression: index, core concepts, practical guides, advanced
  topics, reference. Test with `pip install -r requirements-docs.txt` then `mkdocs serve`.

### Links

- Link text describes the destination, never the raw file name: `[the README](../README.md)`,
  not `[README.md](../README.md)`. No "click here".
- Repository URLs in frontmatter use the `https://github.com/Ambiguous-Interactive/DxMessaging`
  form: no trailing slash, no SSH form, no wrong org. Confirm with `git remote get-url origin`.
- Use HTTPS, full URLs (no shorteners or tracking parameters), and versioned documentation
  paths where fragment stability matters.
- In-repo `#anchor` fragments are the hard PR gate, validated offline by
  `lychee --offline --include-fragments`. A fragment that matches no heading fails the PR.
- External-page fragments are NOT fetched by the blocking gate; verify them in a browser.
  Deep external rot is reported by the scheduled advisory scan, which opens a tracking issue.
- Only 404, 410, and DNS/connection failures fail the external check. 401/403/405/406/408/415/
  429/451 and 5xx are bot-detection or throttling responses and are accepted. When a site
  adopts a new blocking status, widen `accept` in `.lychee.toml`; never add a per-domain
  `exclude` and never swap to a "more stable" domain.

## References

| Document                                                                                | Purpose                                                                                                    |
| --------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| [external-url-fragment-validation.md](./references/external-url-fragment-validation.md) | Offline-gated in-repo anchors versus author-verified external fragments, and per-site anchor ID generation |
| [link-quality-guidelines-part-1.md](./references/link-quality-guidelines-part-1.md)     | Link-text patterns, repository URL formats, and the widen-accept-not-exclude policy for lychee             |
| [link-quality-guidelines-part-2.md](./references/link-quality-guidelines-part-2.md)     | Pre-commit link validation checklist                                                                       |
| [link-quality-guidelines.md](./references/link-quality-guidelines.md)                   | Overview of link-quality failure classes and their CI impact                                               |
| [markdown-compatibility-part-1.md](./references/markdown-compatibility-part-1.md)       | Forbidden admonition, collapsible, tab, and button syntax with portable replacements                       |
| [markdown-compatibility-part-2.md](./references/markdown-compatibility-part-2.md)       | Quick-reference substitution table, emoji shortcode ban, and grep validation commands                      |
| [markdown-compatibility.md](./references/markdown-compatibility.md)                     | Why cross-renderer syntax matters, plus the nested-fence backtick rules                                    |
| [mermaid-theming-part-1.md](./references/mermaid-theming-part-1.md)                     | Inline style directives, the mermaid-config.js palette, and the init-directive stripping regex             |
| [mermaid-theming.md](./references/mermaid-theming.md)                                   | The no-hardcoded-theme rule and how global theme detection works                                           |
| [mkdocs-navigation-part-1.md](./references/mkdocs-navigation-part-1.md)                 | Nav verification inside the doc workflow and local `mkdocs serve` testing                                  |
| [mkdocs-navigation.md](./references/mkdocs-navigation.md)                               | Nav structure, clickable section indexes, ordering, and the orphan-page audit script                       |
