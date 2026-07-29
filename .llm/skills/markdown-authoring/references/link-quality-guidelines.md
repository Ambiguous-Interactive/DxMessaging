# Link Quality and External URL Management

> **One-line summary**: Ensure all links use human-readable text, point to correct URLs, and remain valid over time.

## Overview

Links in documentation serve two purposes: navigation and context. Poor link quality -- whether through cryptic text, incorrect URLs, or broken references -- damages user trust and wastes developer time investigating CI failures.

This skill covers:

- Writing human-readable link text
- Ensuring repository URL consistency in skill files
- Validating external links before committing
- Keeping GitHub Action versions consistent

## Problem Statement

Link-related issues cause preventable CI/CD failures and documentation quality problems:

| Issue Type                     | Impact                                         | Example                                                     |
| ------------------------------ | ---------------------------------------------- | ----------------------------------------------------------- |
| Non-descriptive link text      | Poor accessibility, confusing navigation       | `[README.md](../README.md)` vs `[the README](../README.md)` |
| Incorrect repository URLs      | Broken skill file validation, wrong references | Using wrong org/repo in frontmatter                         |
| Broken external URLs           | 404 errors, outdated documentation references  | Linking to deprecated Unity docs pages                      |
| Workflow version inconsistency | Unpredictable CI behavior, security issues     | Mixing `actions/checkout@v3` and `actions/checkout@v4`      |

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [link quality guidelines part 1](./link-quality-guidelines-part-1.md)
- [link quality guidelines part 2](./link-quality-guidelines-part-2.md)
