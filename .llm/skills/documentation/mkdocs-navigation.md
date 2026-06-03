---
title: "MkDocs Navigation Management"
id: "mkdocs-navigation"
category: "documentation"
version: "1.0.0"
created: "2026-01-29"
updated: "2026-01-29"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "mkdocs.yml"
    - path: "docs/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "mkdocs"
  - "navigation"
  - "site-structure"
  - "yaml"

complexity:
  level: "basic"
  reasoning: "Simple YAML structure updates following established patterns"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Proper navigation ensures all documentation is discoverable"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Basic YAML syntax"
  - "Understanding of MkDocs site structure"

dependencies:
  packages: []
  skills:
    - "documentation-updates"
    - "documentation-update-workflow"
    - "markdown-compatibility"

applies_to:
  languages:
    - "YAML"
    - "Markdown"
  frameworks:
    - "MkDocs"
    - "Material for MkDocs"

aliases:
  - "MkDocs nav"
  - "Documentation navigation"
  - "Site structure"
  - "Nav synchronization"

related:
  - "documentation-updates"
  - "documentation-update-workflow"
  - "markdown-compatibility"

status: "stable"
---

# MkDocs Navigation Management

> **One-line summary**: Always update mkdocs.yml navigation when adding, renaming, or removing documentation pages.

## Overview

The `mkdocs.yml` file contains a `nav` section that defines the documentation site's navigation structure. When documentation files are added to the `docs/` directory but not added to the `nav` section, they become "orphaned" -- the files exist but are not discoverable through the sidebar navigation.

This skill ensures that navigation stays synchronized with the actual documentation files.

## Problem Statement

Orphaned documentation pages occur when:

- New markdown files are added to `docs/` without updating `mkdocs.yml`
- Subdirectories get new files but only some are added to nav
- Files are renamed in `docs/` but nav references the old names
- Files are deleted from `docs/` but remain in nav (causing broken links)

Users cannot find orphaned pages through normal navigation. They appear in search results but seem disconnected from the documentation structure.

## Solution

### Core Rule

#### Every documentation file in `docs/` must have a corresponding entry in `mkdocs.yml` nav

When you add, rename, or remove any `.md` file in `docs/`, you must make a corresponding update to the `nav` section in `mkdocs.yml`.

### Navigation Structure Patterns

The nav section uses a hierarchical YAML structure:

```yaml
nav:
  - Home: index.md
  - Section Name:
      - section/index.md # Clickable section header
      - Page Title: section/page.md # Named page
      - Another Page: section/another.md
```

### Index Pages for Clickable Section Headers

When a section has an `index.md` file, list it **without a title** to make the section header itself clickable:

```yaml
#  CORRECT: Section header is clickable, links to index.md
nav:
  - Getting Started:
      - getting-started/index.md # No title = clickable header
      - Installation: getting-started/install.md
      - Quick Start: getting-started/quick-start.md
```

```yaml
#  WRONG: Section header is not clickable, index appears as separate item
nav:
  - Getting Started:
      - Overview: getting-started/index.md # Title makes it a separate item
      - Installation: getting-started/install.md
```

### Logical Learning Progression

Order pages within sections to follow a natural learning progression:

1. **Overview/Introduction** (index.md) - What is this section about?
1. **Core concepts** - Fundamental ideas needed first
1. **Practical guides** - How to do common tasks
1. **Advanced topics** - Complex scenarios
1. **Reference material** - Lookup information

Example progression for a Concepts section:

```yaml
nav:
  - Concepts:
      - concepts/index.md # What are the core concepts?
      - Mental Model: concepts/mental-model.md # High-level understanding
      - Message Types: concepts/message-types.md # Basic building blocks
      - Listening Patterns: concepts/listening-patterns.md # How to receive
      - Targeting & Context: concepts/targeting-and-context.md # Where messages go
      - Interceptors: concepts/interceptors.md # Advanced modification
```

## Verification Checklist

When adding documentation:

- [ ] New `.md` file created in appropriate `docs/` subdirectory
- [ ] Corresponding entry added to `mkdocs.yml` nav section
- [ ] If section index, listed without title for clickable header
- [ ] Placed in logical learning order within section
- [ ] All sibling files in same directory are also in nav

When renaming documentation:

- [ ] File renamed in `docs/`
- [ ] Path updated in `mkdocs.yml` nav
- [ ] Internal links updated in other documentation files

When removing documentation:

- [ ] File deleted from `docs/`
- [ ] Entry removed from `mkdocs.yml` nav
- [ ] Links from other pages removed or redirected

## Synchronization Verification

After making changes, verify all files in a docs subdirectory are in navigation:

```bash
# List all markdown files in a subdirectory
ls docs/concepts/*.md

# Compare against nav section in mkdocs.yml
grep -A 20 "Concepts:" mkdocs.yml
```

Every file from the `ls` command should appear in the nav output.

### Complete Subdirectory Audit Script

```bash
# Check all docs subdirectories for orphaned files
for dir in docs/*/; do
    echo "=== Checking $dir ==="
    for file in "$dir"*.md; do
        if [ -f "$file" ]; then
            # Extract just the relative path from docs/
            relpath="${file#docs/}"
            if ! grep -q "$relpath" mkdocs.yml; then
                echo "ORPHANED: $relpath not found in mkdocs.yml nav"
            fi
        fi
    done
done
```

## Anti-Patterns

### Adding Files Without Nav Update

```bash
# Create new documentation
echo "# New Feature" > docs/guides/new-feature.md
git add docs/guides/new-feature.md
git commit -m "Add new feature docs"  # WRONG: mkdocs.yml not updated
```

**Why it's wrong**: The page exists but users cannot navigate to it.

### Incorrect Index Page Format

```yaml
# WRONG: Index has a title, section header not clickable
nav:
  - Concepts:
      - Concepts Overview: concepts/index.md
      - Message Types: concepts/message-types.md
```

**Why it's wrong**: Users must click "Concepts Overview" to see the overview instead of clicking "Concepts" directly.

### Random Page Order

```yaml
# WRONG: Advanced topic before basics
nav:
  - Concepts:
      - concepts/index.md
      - Interceptors: concepts/interceptors.md # Advanced!
      - Mental Model: concepts/mental-model.md # Basic - should be first
```

**Why it's wrong**: Users encounter advanced topics before understanding fundamentals.

## See Also

- [mkdocs navigation part 1](./mkdocs-navigation-part-1.md)
