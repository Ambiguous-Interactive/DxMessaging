---
# Template for new skills - copy this file and fill in all fields
# Required Fields
title: "Skill Title Here"
id: "skill-id-here" # Must match filename (without .md)
category: "category-name" # Must match parent folder name
version: "1.0.0"
created: "YYYY-MM-DD"
updated: "YYYY-MM-DD"

# Source Information
source:
  repository: "owner/repo-name"
  files:
    - path: "path/to/primary/file.cs"
      lines: "10-50" # Optional
      commit: "" # Optional
  url: "" # Optional

# Classification
tags:
  - "tag1"
  - "tag2"

complexity:
  level: "intermediate" # basic|intermediate|advanced|expert
  reasoning: ""

# Impact Assessment
impact:
  performance:
    rating: "medium" # none|low|medium|high|critical
    details: ""
  maintainability:
    rating: "medium"
    details: ""
  testability:
    rating: "medium"
    details: ""

# Dependencies and Prerequisites
prerequisites: []

dependencies:
  packages: []
  skills: []

# Applicability
applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

# Optional
aliases: []
related: []
status: "draft" # draft|review|stable|deprecated
---

# {title}

> **One-line summary**: Brief description of what this pattern accomplishes.

## Overview

Explain what this pattern is and why it matters.

## Problem Statement

Describe the specific problem this pattern addresses.

## Solution

### Core Concept

Explain the fundamental idea.

### Implementation

```csharp
// Primary code example
```

### Step-by-Step Breakdown

1. **Step 1**: Explanation
1. **Step 2**: Explanation

## Variations

### Variation A: {Name}

```csharp
// Variation code
```

## Usage Examples

### Example 1: {Scenario}

```csharp
// Complete example
```

## Anti-Patterns

### Don't Do This

```csharp
// Bad example
```

**Why it's wrong**: Explanation.

## Testing Considerations

- Testing notes

```csharp
// Test example
```

## Performance Notes

- Performance characteristics

## See Also

- Related Skill (link to a sibling skill, for example `../category/skill.md`)

## References

- [Source](https://example.com)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | YYYY-MM-DD | Initial version |
