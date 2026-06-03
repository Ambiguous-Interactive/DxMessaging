"""
MkDocs hooks for transforming source code links.

This module provides a hook that transforms markdown links pointing to source files
into full GitHub URLs during the MkDocs build process. This approach keeps markdown
files readable in VSCode preview while generating correct links for GitHub Pages.

Transformations:
- Links starting with Runtime/, Tests/, Editor/, or Samples~/ are transformed
- File links use /blob/master/
- Directory links use /tree/master/
- Spaces in paths are URL-encoded
- Links inside code blocks (fenced or inline) are NOT transformed
"""

import re
from urllib.parse import quote

# GitHub repository configuration
REPO_URL = "https://github.com/Ambiguous-Interactive/DxMessaging"
BRANCH = "master"

# Patterns that indicate source file references (not doc-relative links)
SOURCE_PREFIXES = ("Runtime/", "Tests/", "Editor/", "Samples~/")

# Regex to match markdown links: [text](url)
# Captures: group 1 = display text, group 2 = URL
MARKDOWN_LINK_PATTERN = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")

# Regex to match fenced code blocks (``` or ~~~ with optional language specifier)
# Regex flags used:
#   - DOTALL (s): Makes . match newline characters, allowing pattern to span multiple lines
# The re.sub() method with this pattern finds all occurrences (equivalent to 'g' global flag)
# Pattern is case-sensitive (no IGNORECASE flag needed - backticks are symbols)
# No MULTILINE flag needed - we don't use ^ or $ anchors
FENCED_CODE_BLOCK_PATTERN = re.compile(r"(```|~~~)[^\n]*\n.*?\1", re.DOTALL)

# Regex to match inline code (handles multiple backticks like `` or ```)
# Matches backtick(s), then content that doesn't contain that same sequence, then same backticks
# Regex flags used:
#   - DOTALL (s): Makes . match newline characters, allowing inline code to span lines
# The re.sub() method with this pattern finds all occurrences (equivalent to 'g' global flag)
# Pattern is case-sensitive (no IGNORECASE flag needed - backticks are symbols)
# No MULTILINE flag needed - we don't use ^ or $ anchors
INLINE_CODE_PATTERN = re.compile(r"(`+)(?!`)(.*?)(?<!`)\1(?!`)", re.DOTALL)


def is_source_link(url):
    """
    Check if a URL is a source file reference that should be transformed.

    Args:
        url: The URL from a markdown link.

    Returns:
        True if the URL should be transformed to a GitHub URL.
    """
    return url.startswith(SOURCE_PREFIXES)


def is_directory_link(path):
    """
    Determine if a path points to a directory rather than a file.

    Args:
        path: The path to check.

    Returns:
        True if the path appears to be a directory.
    """
    # Paths ending with / are directories
    if path.endswith("/"):
        return True

    # Paths without a file extension in the last component are likely directories
    # (but be careful with extensionless files)
    last_component = path.rstrip("/").split("/")[-1]

    # If no dot in the last component, it's likely a directory
    # Exception: files like Makefile, Dockerfile, etc. but those are rare in this codebase
    return "." not in last_component


def transform_to_github_url(path):
    """
    Transform a source path to a full GitHub URL.

    Args:
        path: The source path (e.g., 'Runtime/Core/MessageBus.cs').

    Returns:
        The full GitHub URL for the path.
    """
    # Determine if it's a file or directory
    link_type = "tree" if is_directory_link(path) else "blob"

    # URL-encode spaces and other special characters in the path
    # We need to encode each path component separately to preserve slashes
    path_components = path.split("/")
    encoded_components = [quote(component, safe="") for component in path_components]
    encoded_path = "/".join(encoded_components)

    return f"{REPO_URL}/{link_type}/{BRANCH}/{encoded_path}"


def transform_link(match):
    """
    Transform a single markdown link match.

    Args:
        match: A regex match object with groups for display text and URL.

    Returns:
        The transformed markdown link, or the original if no transformation needed.
    """
    display_text = match.group(1)
    url = match.group(2)

    # Only transform source file links
    if not is_source_link(url):
        return match.group(0)

    # Transform to GitHub URL
    github_url = transform_to_github_url(url)
    return f"[{display_text}]({github_url})"


def on_page_markdown(markdown, page, config, files):
    """
    MkDocs hook that runs on each page during build.

    Transforms markdown links pointing to source files into full GitHub URLs.
    Code blocks (fenced and inline) are protected from transformation.

    Args:
        markdown: The markdown content of the page.
        page: The MkDocs page object.
        config: The MkDocs configuration.
        files: The MkDocs files collection.

    Returns:
        The transformed markdown content.
    """
    # Store code blocks to protect them from transformation
    fenced_blocks = []
    inline_codes = []

    def mask_fenced_block(match):
        """Replace fenced code block with placeholder."""
        placeholder = f"__FENCED_CODE_BLOCK_{len(fenced_blocks)}__"
        fenced_blocks.append(match.group(0))
        return placeholder

    def mask_inline_code(match):
        """Replace inline code with placeholder."""
        placeholder = f"__INLINE_CODE_{len(inline_codes)}__"
        inline_codes.append(match.group(0))
        return placeholder

    # Step 1: Mask fenced code blocks first (they may contain inline code syntax)
    content = FENCED_CODE_BLOCK_PATTERN.sub(mask_fenced_block, markdown)

    # Step 2: Mask inline code
    content = INLINE_CODE_PATTERN.sub(mask_inline_code, content)

    # Step 3: Transform all markdown links in the remaining content
    content = MARKDOWN_LINK_PATTERN.sub(transform_link, content)

    # Step 4: Restore inline code (in reverse order of masking)
    for i, inline_code in enumerate(inline_codes):
        content = content.replace(f"__INLINE_CODE_{i}__", inline_code)

    # Step 5: Restore fenced code blocks
    for i, fenced_block in enumerate(fenced_blocks):
        content = content.replace(f"__FENCED_CODE_BLOCK_{i}__", fenced_block)

    return content
