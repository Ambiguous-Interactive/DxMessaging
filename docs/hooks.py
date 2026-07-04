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
from html import unescape
from pathlib import Path
from urllib.parse import quote

from markupsafe import Markup
from pymdownx.highlight import Highlight

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

HERO_EXAMPLE_MARKER_PATTERN = re.compile(r"data-dxm-hero-example\s*=")
HTML_TAG_PATTERN = re.compile(r"<[^>]+>")

# --- Domain-aware ("semantic") highlighting -----------------------------------
# Pygments' CSharpLexer emits [DxTargetedMessage] as a single Name.Attribute span
# (class="na", brackets included), identifier usages as class="n", and type
# declarations as class="nc". apply_dxm_semantics() post-processes that HTML and
# adds dxm-route-* classes so the docs can color DxMessaging's message taxonomy
# (untargeted/targeted/broadcast) with the design-system route colors.
#
# Post-processing was chosen over a custom Pygments lexer/filter on purpose:
# pymdownx.highlight.Highlight exposes no filter hook, and bypassing it would
# lose line spans, language classes, and titles. Do not "clean this up" into a
# lexer without rechecking that trade-off.

# "Untargeted" contains "Targeted" - order matters everywhere routes are matched.
DXM_ROUTE_ORDER = ("Untargeted", "Targeted", "Broadcast")

DXM_SOURCE_DECLARATION_PATTERN = re.compile(
    r"\[Dx(Untargeted|Targeted|Broadcast)Message\]|\bstruct\s+([A-Za-z_]\w*)"
)
# Register/Emit must be followed by an uppercase letter so identifiers such as
# "RegisteredUntargeted" (a message-bus count property) are not treated as routes.
DXM_METHOD_PREFIX_PATTERN = re.compile(r"(?:Register|Emit)[A-Z]")
DXM_LINE_COMMENT_PATTERN = re.compile(r"//[^\n]*")
DXM_STRING_LITERAL_PATTERN = re.compile(r'"(?:\\.|[^"\\])*"')
DXM_ATTRIBUTE_SPAN_PATTERN = re.compile(
    r'<span class="na">(\[Dx(?:Untargeted|Targeted|Broadcast)Message\])</span>'
)
DXM_KEYWORD_SPAN_PATTERN = re.compile(
    r'<span class="(k[dnr]?)">([A-Za-z_]\w*)</span>'
)
DXM_NAME_SPAN_PATTERN = re.compile(r'<span class="(n[cf]?)">([A-Za-z_]\w*)</span>')
DXM_CSHARP_BLOCK_PATTERN = re.compile(
    r'<div class="language-csharp highlight">.*?</div>', re.DOTALL
)
DXM_CSHARP_KEYWORD_CLASSES = {
    "public": "dxm-cs-access",
    "private": "dxm-cs-access",
    "protected": "dxm-cs-access",
    "internal": "dxm-cs-access",
    "readonly": "dxm-cs-readonly",
    "partial": "dxm-cs-partial",
    "struct": "dxm-cs-declaration",
    "class": "dxm-cs-declaration",
    "interface": "dxm-cs-declaration",
    "record": "dxm-cs-declaration",
}


def dxm_route_for_identifier(identifier):
    """Return the route ("untargeted"/"targeted"/"broadcast") named inside an identifier."""
    for route in DXM_ROUTE_ORDER:
        if route in identifier:
            return route.lower()
    return None


def dxm_message_types(source):
    """Map struct names to routes by pairing each [Dx*Message] attribute with the
    next struct declaration, so mixed-route code blocks color each type correctly."""
    # Strings first (they may contain //), then comments - either could otherwise
    # feed a stray attribute or "struct" word into the pairing below.
    source = DXM_STRING_LITERAL_PATTERN.sub('""', source)
    source = DXM_LINE_COMMENT_PATTERN.sub("", source)
    message_types = {}
    pending_route = None
    for match in DXM_SOURCE_DECLARATION_PATTERN.finditer(source):
        route, struct_name = match.group(1), match.group(2)
        if route is not None:
            pending_route = route.lower()
        elif pending_route is not None:
            message_types[struct_name] = pending_route
            pending_route = None
    return message_types


def apply_dxm_semantics(html, source, message_types=None):
    """Add dxm-* classes to Pygments-highlighted DxMessaging C# HTML."""
    if message_types is None:
        message_types = dxm_message_types(source)

    def rewrite_attribute(match):
        attribute = match.group(1)
        route = dxm_route_for_identifier(attribute)
        return f'<span class="na dxm-attr dxm-route-{route}">{attribute}</span>'

    def rewrite_keyword(match):
        token_class, keyword = match.group(1), match.group(2)
        semantic_class = DXM_CSHARP_KEYWORD_CLASSES.get(keyword)
        if semantic_class is None:
            return match.group(0)

        return f'<span class="{token_class} {semantic_class}">{keyword}</span>'

    def rewrite_name(match):
        token_class, identifier = match.group(1), match.group(2)
        if DXM_METHOD_PREFIX_PATTERN.match(identifier):
            route = dxm_route_for_identifier(identifier)
            if route is not None:
                extra = f"dxm-method dxm-route-{route}"
                return f'<span class="{token_class} {extra}">{identifier}</span>'

        route = message_types.get(identifier)
        if route is not None:
            extra = f"dxm-msgtype dxm-route-{route}"
            return f'<span class="{token_class} {extra}">{identifier}</span>'

        return match.group(0)

    html = DXM_ATTRIBUTE_SPAN_PATTERN.sub(rewrite_attribute, html)
    html = DXM_KEYWORD_SPAN_PATTERN.sub(rewrite_keyword, html)
    return DXM_NAME_SPAN_PATTERN.sub(rewrite_name, html)


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


def on_env(env, config, files):
    """
    Add a Jinja helper for build-time C# highlighting in template overrides.

    Markdown code fences are handled by pymdownx.highlight. Homepage snippets live
    in a Jinja template, so they need the same highlighter wired into the template
    environment instead of a runtime JavaScript highlighter.
    """
    highlighter = Highlight(**get_highlight_options(config))
    # pymdownx numbers markdown fences per page from 0; seed template highlights
    # far above that range so __span-N/__codelineno-N ids never collide with them.
    code_block_count = {"value": 100}

    sample_root = resolve_override_sample_root(config)

    def highlight_csharp(source, title=""):
        code_block_count["value"] += 1
        code = str(source).strip("\n")
        highlighted = highlighter.highlight(
            code,
            "csharp",
            title=title,
            code_block_count=code_block_count["value"],
        )
        return Markup(apply_dxm_semantics(highlighted, code))

    def highlight_csharp_file(name, title=""):
        sample_path = (sample_root / str(name)).resolve()
        try:
            sample_path.relative_to(sample_root)
        except ValueError as exc:
            raise ValueError(f"C# sample path escapes override sample root: {name}") from exc

        return highlight_csharp(sample_path.read_text(encoding="utf-8"), title=title)

    env.globals["highlight_csharp"] = highlight_csharp
    env.globals["highlight_csharp_file"] = highlight_csharp_file
    return env


def html_to_text(html):
    """Return generated HTML as decoded text so highlighted spans do not hide code."""
    return unescape(HTML_TAG_PATTERN.sub("", html))


def on_page_content(html, page, config, files):
    """Apply domain-aware highlighting to every rendered C# code block."""
    blocks = DXM_CSHARP_BLOCK_PATTERN.findall(html)
    if not blocks:
        return html

    # Accumulate message types across the whole page first, so a struct declared
    # in one block is still colored when later blocks on the same page use it.
    page_message_types = {}
    for block in blocks:
        page_message_types.update(dxm_message_types(html_to_text(block)))

    def rewrite_block(match):
        block = match.group(0)
        return apply_dxm_semantics(
            block, html_to_text(block), message_types=page_message_types
        )

    return DXM_CSHARP_BLOCK_PATTERN.sub(rewrite_block, html)


def on_post_build(config):
    """Fail the docs build if the generated homepage loses key regression guards."""
    site_dir = Path(config["site_dir"])
    index_path = site_dir / "index.html"
    if not index_path.exists():
        raise RuntimeError(f"Generated docs homepage is missing: {index_path}")

    html = index_path.read_text(encoding="utf-8")
    text = html_to_text(html)
    normalized_text = " ".join(text.split())
    failures = []

    if "new Heal(50).EmitGameObjectTargeted" in text:
        failures.append("homepage still emits Heal from a temporary")

    if "Lifecycle tokens, scoped buses" in text:
        failures.append("homepage still uses the old 'Scales into complex systems' copy")

    if "Measured dispatch" in text:
        failures.append("homepage still uses the old 'Measured dispatch' feature heading")

    if "Benchmarks run in CI" in text:
        failures.append("homepage performance card still mentions CI mechanics")

    if "removing it is deleting that line" not in text:
        failures.append("homepage is missing the decouple/add-remove feature copy")

    trust_markers = (
        "MIT License",
        "zero dependencies",
        "O(1) routing",
        "zero allocation",
        "~10ns / handler",
        "sources OpenUPM \u00b7 npm \u00b7 Git",
    )
    trust_position = -1
    for marker in trust_markers:
        next_position = normalized_text.find(marker, trust_position + 1)
        if next_position < 0:
            failures.append(f"homepage trust strip is missing or orders incorrectly: {marker}")
            break
        trust_position = next_position

    if "data-dxm-copy" not in html:
        failures.append("homepage install command has no copy button")

    if "dxm-hero-glow" not in html:
        failures.append("homepage hero is missing the code-window glow layer")

    if "prefers-reduced-motion" not in html:
        failures.append("homepage motion is not gated behind prefers-reduced-motion")

    hero_example_count = len(HERO_EXAMPLE_MARKER_PATTERN.findall(html))
    if hero_example_count < 3:
        failures.append(f"homepage has {hero_example_count} hero examples; expected at least 3")

    if "language-csharp highlight" not in html:
        failures.append("homepage has no Pygments-highlighted C# blocks")

    for semantic_class in (
        "dxm-cs-access",
        "dxm-cs-readonly",
        "dxm-cs-partial",
        "dxm-cs-declaration",
    ):
        if semantic_class not in html:
            failures.append(
                f"homepage C# examples are missing semantic keyword class: {semantic_class}"
            )

    for example_name in ("untargeted", "targeted", "broadcast"):
        marker_match = re.search(
            rf"data-dxm-hero-example\s*=\s*(['\"]?){example_name}\1", html
        )
        if marker_match is None:
            failures.append(f"homepage is missing the {example_name} hero example")
            continue

        marker_index = marker_match.start()
        next_marker_match = HERO_EXAMPLE_MARKER_PATTERN.search(html, marker_match.end())
        if next_marker_match is not None:
            segment_end = next_marker_match.start()
        else:
            # Last hero panel: stop at the trust strip so page-content code blocks
            # further down cannot satisfy these checks vacuously.
            trust_index = html.find("dxm-trust", marker_index)
            segment_end = trust_index if trust_index >= 0 else marker_index + 12000
        segment = html[marker_index:segment_end]
        segment_text = html_to_text(segment)
        if "language-csharp highlight" not in segment:
            failures.append(f"{example_name} hero example is not highlighted")

        route_class_match = re.search(
            rf'class="[^"]*dxm-route-{example_name}', segment
        )
        if route_class_match is None:
            failures.append(
                f"{example_name} hero example has no dxm-route-{example_name} semantic spans"
            )

        if "dxm-msgtype" not in segment:
            failures.append(
                f"{example_name} hero example does not color its message type name"
            )

        if example_name == "broadcast":
            for marker in (
                "DamageFeed",
                "EnemyHealth",
                "Token belongs to this listener",
                "not the object taking damage",
                "RegisterBroadcastWithoutSource",
                "EmitGameObjectBroadcast(gameObject)",
            ):
                if marker not in segment_text:
                    failures.append(
                        "broadcast hero example does not clearly separate "
                        f"listener token ownership from emission: missing {marker}"
                    )

    if "dxm-wordmark" not in html:
        failures.append("homepage hero is missing the two-tone dxm-wordmark lockup")

    lockup_match = re.search(
        r"dxm-brand-lockup.{0,400}?width=(['\"]?)96\1", html, re.DOTALL
    )
    if lockup_match is None:
        failures.append("homepage brand lockup mark is not rendered at 96px")

    for route_marker, description in (
        ("#3d8a55", "board-spec window-bar green dot"),
        ("rgba(127, 166, 216", "untargeted route tint"),
        ("rgba(236, 70, 97", "targeted route tint"),
        ("rgba(127, 184, 138", "broadcast route tint"),
    ):
        if route_marker not in html and route_marker.replace(", ", ",") not in html:
            failures.append(f"homepage hero is missing the {description} ({route_marker})")

    for route in ("untargeted", "targeted", "broadcast"):
        # Dark and light (Press paper) glyph variants are swapped per color scheme.
        for glyph_path in (f"images/glyph-{route}.svg", f"images/glyph-{route}-light.svg"):
            if glyph_path not in html:
                failures.append(f"homepage does not reference the {route} glyph: {glyph_path}")

            if not (site_dir / glyph_path).exists():
                failures.append(f"generated {route} glyph asset is missing: {glyph_path}")

        attribute = f"[Dx{route.capitalize()}Message]"
        chip_match = re.search(
            rf"dxm-card-chip[^>]*>.{{0,120}}?\[Dx{route.capitalize()}Message\]",
            html,
            re.DOTALL,
        )
        if chip_match is None:
            failures.append(f"homepage feature card is missing its {attribute} chip")

    quick_start_path = site_dir / "getting-started" / "quick-start" / "index.html"
    if not quick_start_path.exists():
        failures.append(f"expected generated page is missing: {quick_start_path}")
    elif "dxm-method" not in quick_start_path.read_text(encoding="utf-8"):
        failures.append(
            "site-wide semantic highlighting is not applied "
            "(quick-start has no dxm-method spans)"
        )

    extra_css_path = site_dir / "stylesheets" / "extra.css"
    extra_css = extra_css_path.read_text(encoding="utf-8") if extra_css_path.exists() else ""
    if not re.search(r"\.md-button(?![-:])[^{]*\{[^}]*[^-]color\s*:", extra_css):
        failures.append(
            "extra.css does not declare an explicit color for secondary .md-button "
            "(Material derives it from --md-primary-fg-color, which this theme sets "
            "to the page background - the button becomes invisible)"
        )

    theme = config.get("theme", {})
    for asset_key in ("logo", "favicon"):
        asset_path = get_mapping_value(theme, asset_key)
        if not asset_path:
            failures.append(f"theme.{asset_key} is not configured")
            continue

        if asset_path not in html:
            failures.append(f"homepage does not reference configured {asset_key}: {asset_path}")

        if not (site_dir / asset_path).exists():
            failures.append(f"generated configured {asset_key} asset is missing: {asset_path}")

    if failures:
        joined = "\n- ".join(failures)
        raise RuntimeError(f"Generated docs homepage regression checks failed:\n- {joined}")


def get_highlight_options(config):
    """Read pymdownx.highlight options from mkdocs.yml, with local defaults."""
    options = {
        "anchor_linenums": True,
        "auto_title": True,
        "line_spans": "__span",
        "pygments_lang_class": True,
    }

    for extension in config.get("markdown_extensions", []):
        if not isinstance(extension, dict):
            continue

        for name, extension_options in extension.items():
            if name != "pymdownx.highlight":
                continue

            if extension_options:
                options.update(extension_options)
            return options

    return options


def get_mapping_value(mapping, key, default=None):
    """Read from MkDocs config objects and ordinary dictionaries."""
    try:
        return mapping.get(key, default)
    except AttributeError:
        pass

    try:
        return mapping[key]
    except (KeyError, TypeError):
        return default


def resolve_override_sample_root(config):
    config_path = Path(config["config_file_path"]).resolve()
    theme = config.get("theme", {})
    custom_dir = get_mapping_value(theme, "custom_dir", "docs/overrides")
    custom_dir_path = Path(custom_dir)
    if not custom_dir_path.is_absolute():
        custom_dir_path = config_path.parent / custom_dir_path

    return (custom_dir_path / "snippets").resolve()
