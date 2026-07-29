## Overview

Continuation material extracted from `powershell-best-practices.md` to keep .llm files within the 300-line budget.

## Solution

## Regex Pattern Pitfalls

### Character Class Exclusion vs Non-Greedy Matching

Using `[^x]*` to match content that might legitimately contain character `x` causes failures.

#### Problem: Matching HTML/XML Comments

```powershell
# BROKEN: Fails when comment contains > character
$pattern = '<!--[^>]*-->'
$content = '<!-- This has > inside the comment -->'
$content -match $pattern  # Fails to match correctly!
```

The pattern `[^>]*` means "match any character except `>`". When the content itself contains `>`,
the match terminates prematurely or fails entirely.

#### Solution: Use Non-Greedy Quantifiers

```powershell
# CORRECT: Non-greedy .*? matches minimal content
$pattern = '<!--.*?-->'
$content = '<!-- This has > inside the comment -->'
$content -match $pattern  # Correctly matches the entire comment
```

The `.*?` pattern matches any characters (including `>`) but stops at the first occurrence of the
closing delimiter `-->`.

#### When to Use Each Pattern

| Pattern  | Use Case                              | Example                   |
| -------- | ------------------------------------- | ------------------------- |
| `[^x]*`  | Content guaranteed not to contain `x` | `[^"]*` for simple quotes |
| `.*?`    | Content may contain any characters    | XML/HTML comments         |
| `[^x]*?` | Rarely needed; non-greedy exclusion   | Edge cases only           |

#### When `[^>]*` IS Appropriate: XML Tag Attributes

While `[^>]*` fails for XML/HTML **comments** (where `>` is allowed), it is safe for matching
XML **tag attributes** in well-formed XML. The `>` character IS allowed unescaped in attribute
values per XML 1.0 section 2.3's `AttValue` grammar, but the closing `>` of a tag is always
outside the quoted attribute values (since quotes must be properly matched):

```powershell
# SAFE: Matching XML tag attributes in well-formed XML
# The closing '>' is always outside quoted attribute values due to grammar constraints
$pattern = '<rect[^>]*/>'  # Safe: closing '>' is outside any attribute quotes
$pattern = '<g[^>]*>'      # Safe: same reasoning (malformed XML would fail parsing anyway)

# UNSAFE: Matching XML comments (where '>' IS allowed)
$pattern = '<!--[^>]*-->'  # Broken: comments can contain '>'
$pattern = '<!--.*?-->'    # Correct: use non-greedy for comments
```

This distinction applies when:

1. **Matching XML/SVG/HTML tag attributes**: `[^>]*` is safe -- closing `>` is outside quotes
1. **The file is controlled/validated**: Project-maintained assets with known format
1. **Matching comments or CDATA**: Use `.*?` instead -- these sections allow `>`

### Structural Completeness in XML/SVG Replacements

When using regex to find-and-replace XML/SVG structures, both the pattern and replacement must
capture complete structural units. Partial matches create fragile code that breaks on formatting
changes.

#### Problem: Incomplete Structural Boundaries

```powershell
# BROKEN: Pattern matches up to </text> but leaves </g> outside
$pattern = '<g id="version-badge">.*?</text>'
$replacement = '<g id="version-badge"><text>New Content</text>'

# Input:  <g id="version-badge"><text>Old</text></g>
# Result: <g id="version-badge"><text>New Content</text></g>  <- Works by accident!

# But what if there's whitespace or nested elements?
# Input:  <g id="version-badge">
#           <text>Old</text>
#         </g>
# Result: <g id="version-badge"><text>New Content</text>
#         </g>  <- Broken indentation, fragile!
```

The pattern relies on `</g>` being immediately after `</text>`, which is an undocumented assumption.

#### Solution: Match Complete Structural Units

```powershell
# CORRECT: Match the entire structural unit including closing tags
$pattern = '<g id="version-badge">.*?</g>'
$replacement = '<g id="version-badge"><text>New Content</text></g>'

# Now the replacement is self-contained and doesn't depend on surrounding structure
```

#### Structural Completeness Checklist

When writing XML/SVG regex replacements:

- [ ] Pattern includes all opening AND closing tags for matched elements
- [ ] Replacement is a valid, complete XML fragment on its own
- [ ] Test with both minified and pretty-printed input
- [ ] Consider using `(?s)` flag (DOTALL) if content spans multiple lines
- [ ] Document which elements are expected in the matched structure

### Self-Referential Documentation Breaking Code

Comments that document regex patterns must not contain literal examples of matched content.

#### Problem: Meta-Pattern Collision

```powershell
# BROKEN: The comment itself matches the pattern being documented!
# Pattern matches: <!-- banner-version:X.Y.Z -->
$pattern = '<!--\s*banner-version:[\d.]+\s*-->'
```

If a script uses this pattern to find and replace version comments in files, and the script file
itself contains the comment showing the matched format, the script may inadvertently modify itself.

#### Solution: Abstract Documentation

```powershell
# CORRECT: Describe without literal examples
# Pattern matches HTML version comments (see docs for format details)
$pattern = '<!--\s*banner-version:[\d.]+\s*-->'
```

Or use placeholder notation:

```powershell
# Pattern matches: <!-- banner-version:{SEMVER} -->
# (where {SEMVER} represents a version number like 1.2.3)
$pattern = '<!--\s*banner-version:[\d.]+\s*-->'
```

## Here-String Quote Escaping

PowerShell here-strings have different escaping rules than regular strings.

### Double-Quote Here-Strings (@"..."@)

In `@"..."@` here-strings, double quotes do **NOT** need escaping:

```powershell
# BROKEN: Doubled quotes appear literally in output
$json = @"
{
    ""name"": ""value""
}
"@
# Output: {"name": "value"} <- Wrong!

# CORRECT: Use single quotes naturally
$json = @"
{
    "name": "value"
}
"@
# Output: {"name": "value"} <- Correct!
```

### Single-Quote Here-Strings (@'...'@)

In `@'...'@` here-strings, no escaping is possible -- everything is literal.

### Here-String Syntax Rules

| Type      | Variable Expansion | Quote Escaping Required | Use Case            |
| --------- | ------------------ | ----------------------- | ------------------- |
| `@"..."@` | Yes                | (use single `"`)        | Dynamic content     |
| `@'...'@` | No                 | escaping possible       | Literal/static text |

## See Also

- [PowerShell Scripting Best Practices](./powershell-best-practices.md)
