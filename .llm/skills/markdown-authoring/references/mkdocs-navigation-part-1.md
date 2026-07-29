## Overview

Continuation extracted from `mkdocs-navigation.md` to keep files within the repository line-budget policy.

## Solution

## Integration with Documentation Workflow

Add nav verification to the documentation update checklist:

1. **Identify scope**: What documentation needs to change?
1. **Create/modify files**: Add or update `.md` files in `docs/`
1. **Update navigation**: Ensure all files appear in `mkdocs.yml` nav
1. **Verify order**: Check logical progression within sections
1. **Verify completeness**: Audit subdirectory against nav entries
1. **Test locally**: Run `mkdocs serve` and verify navigation

## Local Testing

Always test navigation changes locally before committing:

```bash
# Install dependencies if needed
pip install -r requirements-docs.txt

# Serve documentation locally
mkdocs serve

# Open browser to http://localhost:8000
# Verify new pages appear in sidebar navigation
```

## See Also

- [Documentation Updates](../../documentation-style/references/documentation-updates.md)
- [Documentation Update Workflow](../../documentation-style/references/documentation-update-workflow.md)
- [Markdown Compatibility](./markdown-compatibility.md)

## References

- [MkDocs Navigation Configuration](https://www.mkdocs.org/user-guide/configuration/#nav)
- [Material for MkDocs Navigation](https://squidfunk.github.io/mkdocs-material/setup/setting-up-navigation/)

## Changelog

| Version | Date       | Changes                                      |
| ------- | ---------- | -------------------------------------------- |
| 1.0.0   | 2026-01-29 | Initial version - prevent orphaned doc pages |

## Related Links

- [MkDocs Navigation Management](./mkdocs-navigation.md)
