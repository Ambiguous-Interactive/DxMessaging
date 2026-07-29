## Overview

Continuation material extracted from `regex-documentation.md` to keep .llm files within the 300-line budget.

## Solution

## Combined Flag Documentation

When patterns use multiple flags, document the combined effect.

### Wrong Comments

```javascript
// WRONG: Doesn't explain combined behavior
const pattern = /^todo:/gim;
// Matches TODO markers
```

### Correct Comments

```javascript
// CORRECT: Documents each flag's contribution
const pattern = /^todo:/gim;
// Matches all 'todo:' markers at start of any line, case-insensitively
// - /g: finds all occurrences
// - /i: matches TODO, Todo, todo, etc.
// - /m: ^ matches start of each line, not just string start
```

## Anti-Patterns to Avoid

### 1. Copy-Paste Comments

Never copy comments from a similar regex without verifying accuracy.

```javascript
// ANTI-PATTERN: Comment copied from non-global version
const pattern = /\bword\b/g;
// Matches 'word' as a whole word  // WRONG: should say "all occurrences"
```

### 2. Intent-Based Comments

Comments should describe behavior, not intent.

```javascript
// ANTI-PATTERN: Describes intent, not behavior
const pattern = /^#.*/m;
// For matching comment lines  // WRONG: doesn't explain what it actually does

// CORRECT: Describes actual matching behavior
const pattern = /^#.*/m;
// Matches lines starting with # (from # to end of line, on any line)
```

### 3. Incomplete Anchor Documentation

Always specify what anchors match in context.

```javascript
// ANTI-PATTERN: Vague anchor description
const pattern = /^start/;
// Matches 'start' at the beginning

// CORRECT: Specific anchor behavior
const pattern = /^start/;
// Matches 'start' at string start only (not line start; no /m flag)
```

### 4. Omitting Quantifier Behavior

Document what quantifiers actually capture.

```javascript
// ANTI-PATTERN: Doesn't explain greedy vs lazy
const pattern = /".+"/;
// Matches quoted strings

// CORRECT: Explains greedy matching implications
const pattern = /".+"/;
// Matches from first " to LAST " (greedy); may span multiple quoted sections
// Use /"[^"]+"/g or /".+?"/g for individual quoted strings
```

### 5. Ignoring Edge Cases

Document known limitations.

```javascript
// ANTI-PATTERN: No edge case documentation
const urlPattern = /https?:\/\/\S+/g;
// Matches URLs

// CORRECT: Documents limitations
const urlPattern = /https?:\/\/\S+/g;
// Matches all http/https URLs (simple pattern)
// Note: May include trailing punctuation; doesn't validate URL structure
```

## Validation Checklist

Before committing any regex pattern with comments, verify:

### Flag Verification

- [ ] **Global (`g`)**: Comments use plural forms ("all X" not "X")
- [ ] **Multiline (`m`)**: Comments say "any line" for `^` and `$` anchors
- [ ] **DotAll (`s`)**: Comments mention newline matching for `.`
- [ ] **Case-insensitive (`i`)**: Comments explicitly note case behavior
- [ ] **Unicode (`u`)**: Comments note Unicode-specific features if used

### Anchor Verification

- [ ] `^` comment specifies: string start, line start, or both (based on `/m`)
- [ ] `$` comment specifies: string end, line end, or both (based on `/m`)
- [ ] `\b` and `\B` behavior is correctly described

### Quantifier Verification

- [ ] Greedy vs lazy behavior is documented when it matters
- [ ] `*` vs `+` implications are clear (zero-or-more vs one-or-more)

### Behavioral Verification

- [ ] Comment describes actual behavior, not intended behavior
- [ ] Edge cases and limitations are documented
- [ ] Combined flag effects are explained

### Testing Verification

- [ ] Pattern has been tested with expected matches
- [ ] Pattern has been tested with expected non-matches
- [ ] Edge cases have been verified

## Language-Specific Notes

### JavaScript

- Use template literals for patterns with many escapes: ``new RegExp(`pattern`, 'flags')``
- Remember that string escaping doubles backslashes: `new RegExp('\\d+')` vs `/\d+/`

### Shell (grep, sed, awk)

- BRE (Basic Regular Expressions): `grep 'pattern'`
- ERE (Extended Regular Expressions): `grep -E 'pattern'`
- Different flag syntax: `-i` for case-insensitive, not `/i`
- See [Shell Best Practices](./shell-best-practices.md) for grep-specific guidance

### Python

- Use raw strings: `r'\d+'` to avoid double-escaping
- `re.MULTILINE` corresponds to `/m`, `re.DOTALL` to `/s`

## See Also

- [Shell Best Practices](./shell-best-practices.md) - grep pattern matching guidelines
- [Cross-Platform Compatibility](./cross-platform-compatibility.md) - regex differences across platforms

## Related Links

- [Regex Pattern Documentation](./regex-documentation.md)
