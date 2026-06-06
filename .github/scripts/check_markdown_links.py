#!/usr/bin/env python3
import os
import re
import subprocess
import sys
import urllib.parse


EXCLUDE_DIRS = {
    ".git",
    "node_modules",
    ".vs",
    ".venv",
    ".artifacts",
    "site",
    "Library",
    "Obj",
    "Temp",
    "Samples~",
}


LINK_RE = re.compile(r"(?<!\!)\[(?P<text>[^\]]+)\]\((?P<target>[^)\s]+)(?:\s+\"[^\"]*\")?\)")


def should_check_target(target: str) -> bool:
    """Check if a link target should be validated for human-readable text."""
    if re.match(r"^(#|https?://|mailto:|tel:|data:)", target):
        return False
    # only check links that end in .md (ignoring anchors/query)
    core = re.sub(r"[?#].*$", "", target)
    try:
        core = urllib.parse.unquote(core)
    except Exception:
        # Ignore malformed URL encoding - continue with the original string
        pass
    return core.lower().endswith(".md")


def is_link_text_problematic(text: str, target: str) -> bool:
    """
    Check if link text is problematic (not human-readable).

    Returns True if the text is:
    - An exact match to the file name
    - A path-like string (contains / or \\ without spaces)
    - Ends with .md
    """
    target_core = re.sub(r"[?#].*$", "", target)
    try:
        target_core = urllib.parse.unquote(target_core)
    except Exception:
        # Ignore malformed URL encoding - continue with the original string
        pass
    file_name = os.path.basename(target_core)

    is_exact_file_name = text.lower() == file_name.lower()
    looks_like_path = (("/" in text) or ("\\" in text)) and not re.search(r"\s", text)
    looks_like_markdown = text.strip().lower().endswith(".md")

    return is_exact_file_name or looks_like_path or looks_like_markdown


def remove_inline_code(line: str) -> str:
    """Remove inline code spans from a line."""
    return re.sub(r"`[^`]+`", "", line)


def check_code_fence(stripped_line: str, in_code_block: bool, code_fence_pattern: str):
    """
    Check if a line is a code fence marker and update code block state.

    Args:
        stripped_line: Line with leading whitespace stripped
        in_code_block: Whether we're currently inside a code block
        code_fence_pattern: The fence pattern that opened the current block

    Returns:
        Tuple of (new_in_code_block, new_code_fence_pattern, is_fence_line)
    """
    if not stripped_line:
        return in_code_block, code_fence_pattern, False

    fence_char = stripped_line[0]
    if fence_char not in ("`", "~"):
        return in_code_block, code_fence_pattern, False

    if not stripped_line.startswith(fence_char * 3):
        return in_code_block, code_fence_pattern, False

    # Count the fence characters at the start.
    fence_count = 0
    for ch in stripped_line:
        if ch == fence_char:
            fence_count += 1
        else:
            break
    fence = fence_char * fence_count

    if not in_code_block:
        # Entering a code block
        return True, fence, True
    elif stripped_line.startswith(code_fence_pattern) and stripped_line.strip() == code_fence_pattern:
        # Exiting the code block (must match the opening fence exactly)
        return False, None, True

    return in_code_block, code_fence_pattern, False


def check_line_for_issues(line: str, in_code_block: bool) -> list:
    """
    Check a single line for problematic markdown links.

    Args:
        line: The line to check
        in_code_block: Whether we're inside a code block

    Returns:
        List of tuples (text, target) for each problematic link found
    """
    if in_code_block:
        return []

    issues = []
    line_to_check = remove_inline_code(line)

    for m in LINK_RE.finditer(line_to_check):
        text = m.group("text").strip()
        target_raw = m.group("target").strip()

        if not should_check_target(target_raw):
            continue

        if is_link_text_problematic(text, target_raw):
            issues.append((text, target_raw))

    return issues


def check_file_content(lines: list) -> list:
    """
    Check file content for problematic markdown links.

    Args:
        lines: List of lines in the file

    Returns:
        List of tuples (line_number, text, target) for each issue found
    """
    issues = []
    in_code_block = False
    code_fence_pattern = None

    for idx, line in enumerate(lines, start=1):
        stripped = line.lstrip()

        in_code_block, code_fence_pattern, is_fence = check_code_fence(
            stripped, in_code_block, code_fence_pattern
        )
        if is_fence:
            continue

        line_issues = check_line_for_issues(line, in_code_block)
        for text, target in line_issues:
            issues.append((idx, text, target))

    return issues


def is_excluded_path(path: str) -> bool:
    """Return True when any path segment is an excluded directory name."""
    return any(part in EXCLUDE_DIRS for part in os.path.normpath(path).split(os.sep))


def is_explicit_markdown_input(path: str) -> bool:
    """Return True when an input names a markdown file directly."""
    return path.lower().endswith(".md")


def is_markdown_file(path: str) -> bool:
    """Return True for markdown files this checker owns."""
    return os.path.isfile(path) and path.lower().endswith(".md")


def iter_markdown_files(root: str):
    """Yield markdown files under a file or directory in deterministic order."""
    if is_markdown_file(root):
        yield root
        return

    if not os.path.isdir(root):
        return

    for dirpath, dirnames, filenames in os.walk(root):
        # Prune excluded directories and sort for deterministic output across platforms.
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDE_DIRS)
        for filename in sorted(filenames):
            if filename.lower().endswith(".md"):
                yield os.path.join(dirpath, filename)


def iter_markdown_inputs(paths: list):
    """Yield unique markdown files from one or more file/directory inputs."""
    seen = set()
    for root in paths:
        for path in iter_markdown_files(root):
            normalized = os.path.normcase(os.path.abspath(path))
            if normalized in seen:
                continue
            seen.add(normalized)
            yield path


def input_abs_candidates(root: str, repo_root: str = None) -> list:
    """Return candidate absolute paths for a CLI input."""
    if os.path.isabs(root):
        return [os.path.abspath(root)]

    candidates = [os.path.abspath(root)]
    if repo_root:
        repo_relative = os.path.abspath(os.path.join(repo_root, root))
        if os.path.normcase(repo_relative) not in {
            os.path.normcase(candidate) for candidate in candidates
        }:
            candidates.append(repo_relative)
    return candidates


def path_matches_input(path: str, root: str, repo_root: str = None) -> bool:
    """Return True when path is the explicit input file or inside the input dir."""
    path_abs = os.path.abspath(path)
    root_candidates = input_abs_candidates(root, repo_root)
    root_is_file = is_explicit_markdown_input(root) or any(
        os.path.isfile(candidate) for candidate in root_candidates
    )

    if root_is_file:
        return any(
            os.path.normcase(path_abs) == os.path.normcase(candidate)
            for candidate in root_candidates
        )

    for root_abs in root_candidates:
        try:
            if os.path.commonpath([path_abs, root_abs]) == root_abs:
                return True
        except ValueError:
            continue
    return False


def get_repo_root(start: str = ".") -> str:
    """Return the repository root for tracked-file scans."""
    result = subprocess.run(
        ["git", "-C", start, "rev-parse", "--show-toplevel"],
        check=False,
        encoding="utf-8",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode != 0:
        raise RuntimeError(
            "git rev-parse failed while locating the repository root: "
            + (result.stderr.strip() or f"exit {result.returncode}")
        )
    return os.path.abspath(result.stdout.strip())


def should_include_tracked_path(rel_path: str, abs_path: str, roots: list, repo_root: str) -> bool:
    """Return True when a tracked markdown path is selected by the inputs."""
    matching_roots = [
        root for root in roots if path_matches_input(abs_path, root, repo_root=repo_root)
    ]
    if not matching_roots:
        return False

    if not is_excluded_path(rel_path):
        return True

    return any(is_explicit_markdown_input(root) for root in matching_roots)


def iter_tracked_markdown_inputs(paths: list, repo_root: str = None):
    """Yield tracked markdown files matching the requested inputs."""
    repo_root = repo_root or get_repo_root()
    result = subprocess.run(
        ["git", "-C", repo_root, "ls-files", "--full-name", "--", "*.md"],
        check=False,
        encoding="utf-8",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode != 0:
        raise RuntimeError(
            "git ls-files failed while collecting tracked markdown files: "
            + (result.stderr.strip() or f"exit {result.returncode}")
        )

    roots = paths or ["."]
    for rel_path in sorted(line for line in result.stdout.splitlines() if line.strip()):
        abs_path = os.path.join(repo_root, rel_path)
        if not should_include_tracked_path(rel_path, abs_path, roots, repo_root):
            continue
        yield abs_path


def display_path(path: str, repo_root: str = None) -> str:
    """Return a stable diagnostic path."""
    if not repo_root:
        return path

    try:
        rel_path = os.path.relpath(path, repo_root)
    except ValueError:
        return path
    return rel_path if not rel_path.startswith("..") else path


def describe_inputs(paths: list) -> str:
    """Format scanned inputs for a concise diagnostic summary."""
    if len(paths) == 1:
        return f"'{paths[0]}'"
    return f"{len(paths)} input path(s)"


def main(paths: list, tracked_only: bool = False) -> int:
    issue_count = 0
    scanned_files = 0
    file_issue_counts = {}
    roots = paths or ["."]
    repo_root = get_repo_root() if tracked_only else None

    iterator = (
        iter_tracked_markdown_inputs(roots, repo_root=repo_root)
        if tracked_only
        else iter_markdown_inputs(roots)
    )
    for path in iterator:
        scanned_files += 1
        diagnostic_path = display_path(path, repo_root=repo_root)
        try:
            with open(path, "r", encoding="utf-8") as f:
                lines = f.readlines()
        except Exception:
            # Skip files that cannot be read (permission errors, encoding issues, etc.)
            continue

        file_issues = check_file_content(lines)
        if file_issues:
            file_issue_counts[diagnostic_path] = len(file_issues)

        for line_num, text, target in file_issues:
            issue_count += 1
            msg = f"{diagnostic_path}:{line_num}: Link text '{text}' should be human-readable, not a raw file name or path (target: {target})"
            print(msg)

    if issue_count:
        print(
            f"Scanned {scanned_files} markdown file(s) under {describe_inputs(roots)}.",
            file=sys.stderr,
        )
        print("Issue count by file:", file=sys.stderr)
        for path, count in sorted(file_issue_counts.items()):
            print(f"  - {path}: {count}", file=sys.stderr)
        print(
            f"Found {issue_count} documentation link(s) with non-human-readable text.",
            file=sys.stderr,
        )
        print(
            "Use a descriptive phrase instead of the raw file name.", file=sys.stderr
        )
        return 1

    print(
        f"Scanned {scanned_files} markdown file(s); all markdown-to-markdown links use human-readable text."
    )
    return 0


if __name__ == "__main__":
    tracked = False
    inputs = []
    for arg in sys.argv[1:]:
        if arg == "--tracked":
            tracked = True
        else:
            inputs.append(arg)
    sys.exit(main(inputs, tracked_only=tracked))
