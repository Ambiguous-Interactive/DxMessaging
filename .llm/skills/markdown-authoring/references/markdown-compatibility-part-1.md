## Overview

Continuation material extracted from `markdown-compatibility.md` to keep .llm files within the 300-line budget.

## Solution

## Forbidden Syntax and Alternatives

### 1. Admonitions

Admonitions (callout boxes) are a common MkDocs extension that use `!!!` syntax.

#### No Forbidden: MkDocs Admonitions

```markdown
!!! note "Important"
This is a note admonition that only renders in MkDocs.

!!! warning "Caution"
This warning will appear as broken text on GitHub.

!!! danger "Critical"
This danger callout is MkDocs-specific.

!!! tip "Pro Tip"
This tip syntax is not standard markdown.

!!! info
Admonitions without titles are also forbidden.
```

#### Yes Correct: Blockquotes with Emoji

```markdown
> ℹ️ **Note**: This is a note that renders everywhere.

> **Caution**: This warning displays correctly on all platforms.

> 🚨 **Critical**: This danger callout works universally.

> 💡 **Tip**: This tip renders in any markdown viewer.

> 📝 **Info**: Use descriptive emoji to convey meaning.
```

**Emoji reference for common admonition types**:

| Type     | Emoji | Example                  |
| -------- | ----- | ------------------------ |
| Note     | ℹ️    | `> ℹ️ **Note**: ...`     |
| Warning  |       | `>  **Warning**: ...`    |
| Danger   | 🚨    | `> 🚨 **Danger**: ...`   |
| Tip      | 💡    | `> 💡 **Tip**: ...`      |
| Info     | 📝    | `> 📝 **Info**: ...`     |
| Success  | Yes   | `> Yes **Success**: ...` |
| Error    | No    | `> No **Error**: ...`    |
| Example  | 📌    | `> 📌 **Example**: ...`  |
| See Also | 🔗    | `> 🔗 **See Also**: ...` |

---

### 2. Collapsible Admonitions

MkDocs supports collapsible sections with `???` syntax.

#### No Forbidden: MkDocs Collapsibles

```markdown
??? note "Click to expand"
This content is hidden by default in MkDocs.
It appears as broken syntax elsewhere.

???+ warning "Expanded by default"
The plus makes it expanded initially.
```

#### Yes Correct: Use Details/Summary HTML

```markdown
<details>
<summary>Click to expand</summary>

This content is hidden by default and works in GitHub and VS Code.

Note: Leave a blank line after `<summary>` for proper markdown rendering inside.

</details>
```

Or simply use a regular section if collapsibility is not essential:

```markdown
### Additional Details

This content is always visible, which is often clearer for users.
```

---

### 3. Content Tabs

MkDocs Material provides tabbed content with `===` syntax.

#### No Forbidden: MkDocs Tabs

```markdown
=== "Python"
`python
    print("Hello")
    `

=== "JavaScript"
`javascript
    console.log("Hello");
    `

=== "C#"
`csharp
    Console.WriteLine("Hello");
    `
```

#### Yes Correct: Use Headers

````markdown
### Python

```python
print("Hello")
```

### JavaScript

```javascript
console.log("Hello");
```

### C#

```csharp
Console.WriteLine("Hello");
```
````

For installation instructions or platform-specific content, headers provide clear navigation and work universally.

---

### 4. Button Attributes

MkDocs Material supports styled buttons via attribute syntax.

#### No Forbidden: Button Attributes

```markdown
[Get Started](getting-started.md){ .md-button }

[Download](https://example.com/download){ .md-button .md-button--primary }

[:fontawesome-brands-github: View on GitHub](https://github.com/example){ .md-button }
```

#### Yes Correct: Standard Links

```markdown
[Get Started](getting-started.md)

[Download](https://example.com/download)

[View on GitHub](https://github.com/example)
```

If you need visual emphasis, use bold or place links prominently:

```markdown
**[Get Started ->](getting-started.md)**
```

---

## See Also

- [markdown compatibility part 2](./markdown-compatibility-part-2.md)
