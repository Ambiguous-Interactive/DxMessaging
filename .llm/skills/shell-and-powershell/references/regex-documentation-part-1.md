## Overview

Continuation material extracted from `regex-documentation.md` to keep .llm files within the 300-line budget.

## Solution

## Flag-Specific Documentation Rules

### Global Flag (`g`)

The global flag changes whether a pattern finds one or all matches. Comments must reflect this.

#### Wrong Comments

```javascript
// WRONG: Comment implies single match, but /g finds all
const pattern = /\d+/g;
// Matches a number in the string
```

```javascript
// WRONG: Singular language with global flag
const emailPattern = /\w+@\w+\.\w+/g;
// Matches an email address
```

#### Correct Comments

```javascript
// CORRECT: Plural language matches global behavior
const pattern = /\d+/g;
// Matches all numbers in the string

const emailPattern = /\w+@\w+\.\w+/g;
// Matches all email addresses in the string
```

### Multiline Flag (`m`)

The multiline flag changes the meaning of `^` and `$` anchors. This is one of the most commonly misdocumented behaviors.

#### Wrong Comments

```javascript
// WRONG: Says "at start" but /m means "at start of ANY line"
const pattern = /^function/m;
// Matches 'function' at start of string
```

```javascript
// WRONG: Omits multiline behavior entirely
const endPattern = /;$/m;
// Matches semicolon at end
```

#### Correct Comments

```javascript
// CORRECT: Explicitly mentions line-level matching
const pattern = /^function/m;
// Matches 'function' at start of any line (not just string start)

const endPattern = /;$/m;
// Matches semicolon at end of any line (not just string end)
```

#### Multiline Behavior Demonstration

```javascript
const text = `line1
function test() {
  return 42;
}`;

// Without /m flag: ^ only matches position 0
/^function/.test(text); // false - 'function' is not at string start

// With /m flag: ^ matches after every newline too
/^function/m.test(text); // true - 'function' is at start of line 2
```

### DotAll Flag (`s`)

The DotAll flag makes `.` match newline characters. Without it, `.` matches any character except newlines.

#### Wrong Comments

```javascript
// WRONG: Says "any character" but without /s, newlines are excluded
const pattern = /start.*end/;
// Matches 'start', then any characters, then 'end'
```

```javascript
// WRONG: Implies newlines are matched when they're not
const blockPattern = /<div>.*<\/div>/;
// Matches everything between div tags
```

#### Correct Comments

```javascript
// CORRECT: Explicitly notes newline exclusion
const pattern = /start.*end/;
// Matches 'start', any characters EXCEPT newlines, then 'end'

// CORRECT: With /s flag, mentions newline inclusion
const multilinePattern = /start.*end/s;
// Matches 'start', any characters INCLUDING newlines, then 'end'

// CORRECT: Alternative using explicit character class
const explicitPattern = /start[\s\S]*end/;
// Matches 'start', any characters including newlines, then 'end'
```

#### DotAll Behavior Demonstration

```javascript
const text = `start
middle
end`;

// Without /s: . doesn't match newlines
/start.*end/.test(text); // false - can't cross newlines

// With /s: . matches everything including newlines
/start.*end/s.test(text); // true - matches across lines

// Alternative: [\s\S] matches any character including newlines
/start[\s\S]*end/.test(text); // true - explicit any-character class
```

### Case-Insensitive Flag (`i`)

#### Wrong Comments

```javascript
// WRONG: Doesn't mention case-insensitivity
const pattern = /error/i;
// Matches 'error' in the string
```

#### Correct Comments

```javascript
// CORRECT: Explicitly mentions case behavior
const pattern = /error/i;
// Matches 'error' case-insensitively (ERROR, Error, error, etc.)
```

### Unicode Flag (`u`)

#### Wrong Comments

```javascript
// WRONG: Doesn't explain Unicode implications
const emojiPattern = /\p{Emoji}/u;
// Matches emoji
```

#### Correct Comments

```javascript
// CORRECT: Notes Unicode property escapes
const emojiPattern = /\p{Emoji}/u;
// Matches any Unicode emoji character (requires /u flag for \p{} syntax)

const letterPattern = /\p{Letter}+/gu;
// Matches all sequences of Unicode letters (any script, not just ASCII)
```

## See Also

- [Regex Pattern Documentation](./regex-documentation.md)
