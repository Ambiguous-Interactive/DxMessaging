#!/usr/bin/env python3
"""
Tests for check_markdown_links.py

Run with: python3 -m pytest test_check_markdown_links.py -v
Or: python3 -m unittest test_check_markdown_links -v
"""
import os
import tempfile
import unittest

from check_markdown_links import (
    should_check_target,
    is_link_text_problematic,
    remove_inline_code,
    check_code_fence,
    check_line_for_issues,
    check_file_content,
    get_repo_root,
    iter_markdown_files,
    iter_markdown_inputs,
    iter_tracked_markdown_inputs,
    path_matches_input,
    LINK_RE,
)


class TestShouldCheckTarget(unittest.TestCase):
    """Tests for the should_check_target function."""

    def test_external_http_links_should_not_be_checked(self):
        self.assertFalse(should_check_target("https://example.com/file.md"))
        self.assertFalse(should_check_target("http://example.com/README.md"))

    def test_anchor_links_should_not_be_checked(self):
        self.assertFalse(should_check_target("#section-heading"))
        self.assertFalse(should_check_target("#top"))

    def test_mailto_links_should_not_be_checked(self):
        self.assertFalse(should_check_target("mailto:test@example.com"))

    def test_tel_links_should_not_be_checked(self):
        self.assertFalse(should_check_target("tel:+1234567890"))

    def test_data_links_should_not_be_checked(self):
        self.assertFalse(should_check_target("data:text/plain;base64,SGVsbG8="))

    def test_markdown_files_should_be_checked(self):
        self.assertTrue(should_check_target("README.md"))
        self.assertTrue(should_check_target("./docs/guide.md"))
        self.assertTrue(should_check_target("../parent/file.md"))
        self.assertTrue(should_check_target("path/to/File.MD"))

    def test_markdown_files_with_anchors_should_be_checked(self):
        self.assertTrue(should_check_target("README.md#section"))
        self.assertTrue(should_check_target("docs/guide.md#heading"))

    def test_markdown_files_with_query_params_should_be_checked(self):
        self.assertTrue(should_check_target("README.md?ref=main"))

    def test_non_markdown_files_should_not_be_checked(self):
        self.assertFalse(should_check_target("script.py"))
        self.assertFalse(should_check_target("image.png"))
        self.assertFalse(should_check_target("docs/index.html"))

    def test_url_encoded_markdown_files_should_be_checked(self):
        self.assertTrue(should_check_target("My%20File.md"))
        self.assertTrue(should_check_target("path/to/Some%20Doc.md"))


class TestIsLinkTextProblematic(unittest.TestCase):
    """Tests for the is_link_text_problematic function."""

    def test_exact_filename_match_is_problematic(self):
        self.assertTrue(is_link_text_problematic("README.md", "README.md"))
        self.assertTrue(is_link_text_problematic("readme.md", "README.md"))
        self.assertTrue(is_link_text_problematic("Guide.md", "docs/Guide.md"))

    def test_path_like_text_is_problematic(self):
        self.assertTrue(is_link_text_problematic("docs/README.md", "docs/README.md"))
        self.assertTrue(is_link_text_problematic("./guide.md", "./guide.md"))
        self.assertTrue(is_link_text_problematic("../parent/file.md", "../parent/file.md"))
        self.assertTrue(is_link_text_problematic("path\\to\\file.md", "path/to/file.md"))

    def test_text_ending_with_md_is_problematic(self):
        self.assertTrue(is_link_text_problematic("SomeFile.md", "path/SomeFile.md"))
        self.assertTrue(is_link_text_problematic("AnotherDoc.MD", "AnotherDoc.md"))

    def test_human_readable_text_is_not_problematic(self):
        self.assertFalse(is_link_text_problematic("the README", "README.md"))
        self.assertFalse(is_link_text_problematic("Getting Started Guide", "GettingStarted.md"))
        self.assertFalse(is_link_text_problematic("click here", "docs/guide.md"))
        self.assertFalse(is_link_text_problematic("documentation", "README.md"))

    def test_descriptive_text_with_spaces_is_not_problematic(self):
        self.assertFalse(is_link_text_problematic("see the guide", "guide.md"))
        self.assertFalse(is_link_text_problematic("read more here", "details.md"))

    def test_path_with_spaces_is_not_problematic(self):
        # Paths with spaces are treated as human-readable since they look like phrases
        self.assertFalse(is_link_text_problematic("docs / guide", "guide.md"))


class TestDataDrivenMatrices(unittest.TestCase):
    """Data-driven matrix tests for core link classification behavior."""

    def test_should_check_target_matrix(self):
        cases = [
            ("README.md", True),
            ("docs/guide.md#intro", True),
            ("My%20Doc.md", True),
            ("https://example.com/readme.md", False),
            ("#top", False),
            ("mailto:test@example.com", False),
            ("guide.txt", False),
        ]

        for target, expected in cases:
            with self.subTest(target=target):
                self.assertEqual(should_check_target(target), expected)

    def test_is_link_text_problematic_matrix(self):
        cases = [
            ("README.md", "README.md", True),
            ("docs/README.md", "docs/README.md", True),
            ("My File.md", "My%20File.md", True),
            ("the README", "README.md", False),
            ("Setup Guide", "docs/setup.md", False),
            ("docs / setup", "docs/setup.md", False),
        ]

        for text, target, expected in cases:
            with self.subTest(text=text, target=target):
                self.assertEqual(is_link_text_problematic(text, target), expected)


class TestIterMarkdownFiles(unittest.TestCase):
    """Tests for deterministic markdown file iteration and directory exclusion."""

    def test_iter_markdown_files_sorts_and_excludes_known_directories(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            docs_dir = os.path.join(tmp_dir, "docs")
            node_modules_dir = os.path.join(tmp_dir, "node_modules")
            temp_dir = os.path.join(tmp_dir, "Temp")
            os.makedirs(docs_dir)
            os.makedirs(node_modules_dir)
            os.makedirs(temp_dir)

            for rel_path in [
                "b.md",
                "a.md",
                "docs/c.md",
                "node_modules/ignored.md",
                "Temp/ignored-temp.md",
                "docs/not-markdown.txt",
            ]:
                full_path = os.path.join(tmp_dir, rel_path)
                os.makedirs(os.path.dirname(full_path), exist_ok=True)
                with open(full_path, "w", encoding="utf-8") as handle:
                    handle.write("placeholder")

            discovered = [
                os.path.relpath(path, tmp_dir).replace("\\", "/")
                for path in iter_markdown_files(tmp_dir)
            ]

            self.assertEqual(discovered, ["a.md", "b.md", "docs/c.md"])

    def test_iter_markdown_files_accepts_explicit_file(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            path = os.path.join(tmp_dir, "one.md")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("placeholder")

            self.assertEqual(list(iter_markdown_files(path)), [path])

    def test_iter_markdown_files_accepts_explicit_file_under_excluded_path(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            temp_dir = os.path.join(tmp_dir, "Temp")
            os.makedirs(temp_dir)
            path = os.path.join(temp_dir, "one.md")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("placeholder")

            self.assertEqual(list(iter_markdown_files(path)), [path])

    def test_iter_markdown_inputs_deduplicates_file_and_directory_inputs(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            path = os.path.join(tmp_dir, "one.md")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("placeholder")

            self.assertEqual(list(iter_markdown_inputs([tmp_dir, path])), [path])


class TestPathMatchesInput(unittest.TestCase):
    """Tests for tracked-file filtering by explicit input roots."""

    def test_explicit_file_input_matches_only_that_file(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            one = os.path.join(tmp_dir, "one.md")
            two = os.path.join(tmp_dir, "two.md")
            for path in [one, two]:
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write("placeholder")

            self.assertTrue(path_matches_input(one, one))
            self.assertFalse(path_matches_input(two, one))

    def test_directory_input_matches_nested_file(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            nested = os.path.join(tmp_dir, "docs", "guide.md")
            os.makedirs(os.path.dirname(nested))
            with open(nested, "w", encoding="utf-8") as handle:
                handle.write("placeholder")

            self.assertTrue(path_matches_input(nested, tmp_dir))
            self.assertFalse(path_matches_input(os.path.dirname(tmp_dir), tmp_dir))

    def test_repo_relative_input_matches_when_cwd_is_subdirectory(self):
        repo_root = get_repo_root()
        rel_path = ".llm/skills/performance/dispatch-hot-path.md"
        expected = os.path.join(repo_root, rel_path)
        subdir = os.path.join(repo_root, ".github", "scripts")
        previous_cwd = os.getcwd()
        try:
            os.chdir(subdir)
            discovered = list(iter_tracked_markdown_inputs([rel_path]))
        finally:
            os.chdir(previous_cwd)

        self.assertEqual(discovered, [expected])

    def test_cwd_relative_input_matches_when_cwd_is_subdirectory(self):
        repo_root = get_repo_root()
        rel_path = ".llm/skills/performance/dispatch-hot-path.md"
        expected = os.path.join(repo_root, rel_path)
        subdir = os.path.join(repo_root, ".github", "scripts")
        previous_cwd = os.getcwd()
        try:
            os.chdir(subdir)
            discovered = list(iter_tracked_markdown_inputs([f"../../{rel_path}"]))
        finally:
            os.chdir(previous_cwd)

        self.assertEqual(discovered, [expected])


class TestRemoveInlineCode(unittest.TestCase):
    """Tests for the remove_inline_code function."""

    def test_removes_single_inline_code(self):
        self.assertEqual(remove_inline_code("Some `code` here"), "Some  here")

    def test_removes_multiple_inline_codes(self):
        line = "Use `foo` and `bar` functions"
        self.assertEqual(remove_inline_code(line), "Use  and  functions")

    def test_preserves_line_without_inline_code(self):
        line = "This is a normal line"
        self.assertEqual(remove_inline_code(line), line)

    def test_removes_inline_code_with_links_inside(self):
        line = "Check `[README.md](README.md)` for info"
        result = remove_inline_code(line)
        self.assertNotIn("[README.md]", result)

    def test_handles_empty_line(self):
        self.assertEqual(remove_inline_code(""), "")

    def test_handles_adjacent_inline_codes(self):
        line = "`first``second`"
        result = remove_inline_code(line)
        self.assertNotIn("first", result)
        self.assertNotIn("second", result)


class TestCheckCodeFence(unittest.TestCase):
    """Tests for the check_code_fence function."""

    def test_entering_triple_backtick_code_block(self):
        in_block, pattern, is_fence = check_code_fence("```python", False, None)
        self.assertTrue(in_block)
        self.assertEqual(pattern, "```")
        self.assertTrue(is_fence)

    def test_exiting_triple_backtick_code_block(self):
        in_block, pattern, is_fence = check_code_fence("```", True, "```")
        self.assertFalse(in_block)
        self.assertIsNone(pattern)
        self.assertTrue(is_fence)

    def test_entering_triple_tilde_code_block(self):
        in_block, pattern, is_fence = check_code_fence("~~~markdown", False, None)
        self.assertTrue(in_block)
        self.assertEqual(pattern, "~~~")
        self.assertTrue(is_fence)

    def test_exiting_triple_tilde_code_block(self):
        in_block, pattern, is_fence = check_code_fence("~~~", True, "~~~")
        self.assertFalse(in_block)
        self.assertIsNone(pattern)
        self.assertTrue(is_fence)

    def test_entering_quad_backtick_code_block(self):
        in_block, pattern, is_fence = check_code_fence("````markdown", False, None)
        self.assertTrue(in_block)
        self.assertEqual(pattern, "````")
        self.assertTrue(is_fence)

    def test_exiting_quad_backtick_code_block(self):
        in_block, pattern, is_fence = check_code_fence("````", True, "````")
        self.assertFalse(in_block)
        self.assertIsNone(pattern)
        self.assertTrue(is_fence)

    def test_triple_backticks_inside_quad_block_do_not_exit(self):
        # When inside a ```` block, ``` should not exit
        in_block, pattern, is_fence = check_code_fence("```", True, "````")
        self.assertTrue(in_block)
        self.assertEqual(pattern, "````")
        self.assertFalse(is_fence)

    def test_backticks_inside_tilde_block_do_not_exit(self):
        in_block, pattern, is_fence = check_code_fence("```", True, "~~~")
        self.assertTrue(in_block)
        self.assertEqual(pattern, "~~~")
        self.assertFalse(is_fence)

    def test_non_fence_line_does_not_change_state(self):
        in_block, pattern, is_fence = check_code_fence("normal line", False, None)
        self.assertFalse(in_block)
        self.assertIsNone(pattern)
        self.assertFalse(is_fence)

    def test_non_fence_line_inside_block_stays_in_block(self):
        in_block, pattern, is_fence = check_code_fence("some code", True, "```")
        self.assertTrue(in_block)
        self.assertEqual(pattern, "```")
        self.assertFalse(is_fence)

    def test_fence_with_extra_content_does_not_exit(self):
        # A closing fence should be exactly the fence pattern, nothing more
        in_block, pattern, is_fence = check_code_fence("``` extra", True, "```")
        self.assertTrue(in_block)
        self.assertEqual(pattern, "```")
        self.assertFalse(is_fence)


class TestCheckLineForIssues(unittest.TestCase):
    """Tests for the check_line_for_issues function."""

    def test_finds_problematic_filename_link(self):
        line = "See [README.md](README.md) for details"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], "README.md")
        self.assertEqual(issues[0][1], "README.md")

    def test_finds_problematic_path_link(self):
        line = "Check [docs/guide.md](docs/guide.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], "docs/guide.md")

    def test_ignores_human_readable_links(self):
        line = "See [the README](README.md) for details"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_skips_when_in_code_block(self):
        line = "See [README.md](README.md) for details"
        issues = check_line_for_issues(line, True)
        self.assertEqual(len(issues), 0)

    def test_skips_links_in_inline_code(self):
        line = "Example: `[README.md](README.md)` shows bad format"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_finds_multiple_issues_on_one_line(self):
        line = "[README.md](README.md) and [GUIDE.md](GUIDE.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 2)

    def test_ignores_external_links(self):
        line = "[README.md](https://github.com/org/repo/README.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_ignores_anchor_links(self):
        line = "[Section](#section)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_ignores_image_links(self):
        line = "![README.md](README.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_mixed_good_and_bad_links(self):
        line = "[the README](README.md) and [GUIDE.md](GUIDE.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], "GUIDE.md")


class TestCheckFileContent(unittest.TestCase):
    """Tests for the check_file_content function."""

    def test_finds_issue_in_simple_file(self):
        lines = [
            "# Header\n",
            "\n",
            "See [README.md](README.md) for details.\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], 3)  # Line number
        self.assertEqual(issues[0][1], "README.md")

    def test_skips_triple_backtick_code_blocks(self):
        lines = [
            "# Header\n",
            "```markdown\n",
            "[README.md](README.md)\n",
            "```\n",
            "Normal text\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_skips_quad_backtick_code_blocks(self):
        lines = [
            "# Header\n",
            "````\n",
            "[README.md](README.md)\n",
            "````\n",
            "Normal text\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_skips_triple_tilde_code_blocks(self):
        lines = [
            "# Header\n",
            "~~~markdown\n",
            "[README.md](README.md)\n",
            "~~~\n",
            "Normal text\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_handles_nested_code_blocks(self):
        # ``` inside ```` should not close the outer block
        lines = [
            "````markdown\n",
            "Here's an example:\n",
            "```\n",
            "[README.md](README.md)\n",
            "```\n",
            "End of example\n",
            "````\n",
            "[the guide](guide.md)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_skips_indented_code_blocks_in_lists(self):
        lines = [
            "1. Item one\n",
            "   ```\n",
            "   [README.md](README.md)\n",
            "   ```\n",
            "2. Item two\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_skips_inline_code_spans(self):
        lines = [
            "Use `[README.md](README.md)` as an example.\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_skips_multiple_inline_code_spans(self):
        lines = [
            "Bad: `[A.md](A.md)` and `[B.md](B.md)` examples.\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_detects_issue_after_code_block(self):
        lines = [
            "```\n",
            "[A.md](A.md)\n",
            "```\n",
            "[README.md](README.md)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], 4)

    def test_empty_file(self):
        lines = []
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_file_with_only_good_links(self):
        lines = [
            "# My Project\n",
            "\n",
            "See [the README](README.md) for more info.\n",
            "Check out [our guide](docs/guide.md) too.\n",
            "[Learn more](https://example.com)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)


class TestLinkRegex(unittest.TestCase):
    """Tests for the LINK_RE regex pattern."""

    def test_matches_simple_link(self):
        match = LINK_RE.search("[text](target)")
        self.assertIsNotNone(match)
        self.assertEqual(match.group("text"), "text")
        self.assertEqual(match.group("target"), "target")

    def test_matches_link_with_title(self):
        match = LINK_RE.search('[text](target "title")')
        self.assertIsNotNone(match)
        self.assertEqual(match.group("text"), "text")
        self.assertEqual(match.group("target"), "target")

    def test_does_not_match_image(self):
        match = LINK_RE.search("![alt](image.png)")
        self.assertIsNone(match)

    def test_matches_link_with_path(self):
        match = LINK_RE.search("[text](path/to/file.md)")
        self.assertIsNotNone(match)
        self.assertEqual(match.group("target"), "path/to/file.md")

    def test_matches_link_with_anchor(self):
        match = LINK_RE.search("[text](file.md#section)")
        self.assertIsNotNone(match)
        self.assertEqual(match.group("target"), "file.md#section")

    def test_finds_multiple_links(self):
        text = "[one](a.md) and [two](b.md)"
        matches = list(LINK_RE.finditer(text))
        self.assertEqual(len(matches), 2)


class TestIntegrationScenarios(unittest.TestCase):
    """Integration tests for realistic file scenarios."""

    def test_readme_style_file(self):
        lines = [
            "# My Project\n",
            "\n",
            "## Documentation\n",
            "\n",
            "- [Getting Started](docs/GettingStarted.md) - start here\n",
            "- [API Reference](docs/API.md) - full API docs\n",
            "\n",
            "## Bad Links (should be flagged)\n",
            "\n",
            "- [GettingStarted.md](docs/GettingStarted.md)\n",
            "- [docs/API.md](docs/API.md)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 2)
        self.assertEqual(issues[0][0], 10)
        self.assertEqual(issues[1][0], 11)

    def test_tutorial_with_code_examples(self):
        lines = [
            "# Tutorial\n",
            "\n",
            "Here's how to link in markdown:\n",
            "\n",
            "```markdown\n",
            "[BadLink.md](BadLink.md)\n",
            "```\n",
            "\n",
            "The correct way:\n",
            "\n",
            "[See the tutorial](tutorial.md)\n",
            "\n",
            "Inline example: `[File.md](File.md)` is wrong.\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_deeply_nested_code_blocks(self):
        # ```` opens at line 3, ``` at line 8, ``` at line 10, ```` closes at line 14
        # The FILE.md link on line 16 should be flagged (after the code block)
        lines = [
            "# Nested Example\n",       # 1
            "\n",                        # 2
            "````markdown\n",            # 3 - opens outer block
            "Outer block content\n",     # 4
            "\n",                        # 5
            "```\n",                     # 6 - nested, doesn't close outer
            "[README.md](README.md)\n",  # 7 - in code block
            "```\n",                     # 8 - nested close, doesn't close outer
            "\n",                        # 9
            "Still in outer block\n",    # 10
            "[GUIDE.md](GUIDE.md)\n",    # 11 - still in code block
            "````\n",                    # 12 - closes outer block
            "\n",                        # 13
            "[FILE.md](FILE.md)\n",      # 14 - should be flagged
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], 14)
        self.assertEqual(issues[0][1], "FILE.md")

    def test_complex_list_with_code_blocks(self):
        lines = [
            "1. First item\n",
            "   \n",
            "   ```\n",
            "   [Code.md](Code.md)\n",
            "   ```\n",
            "   \n",
            "   [After code](after.md)\n",
            "\n",
            "2. Second item [Item.md](item.md)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], 9)
        self.assertEqual(issues[0][1], "Item.md")


class TestEdgeCases(unittest.TestCase):
    """Tests for edge cases and boundary conditions."""

    def test_unclosed_code_block(self):
        # If code block is never closed, all remaining content is in code block
        lines = [
            "```\n",
            "[README.md](README.md)\n",
            "More content\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_empty_link_text(self):
        # Empty text shouldn't cause issues
        line = "[](empty.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 0)

    def test_link_text_with_special_characters(self):
        line = "[file-name_v2.md](file-name_v2.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)

    def test_case_insensitive_file_matching(self):
        line = "[readme.md](README.MD)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)

    def test_url_encoded_spaces_in_target(self):
        line = "[My File.md](My%20File.md)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)

    def test_link_with_query_and_anchor(self):
        line = "[README.md](README.md?v=1#section)"
        issues = check_line_for_issues(line, False)
        self.assertEqual(len(issues), 1)

    def test_consecutive_code_blocks(self):
        lines = [
            "```\n",
            "[A.md](A.md)\n",
            "```\n",
            "```\n",
            "[B.md](B.md)\n",
            "```\n",
            "[C.md](C.md)\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 1)
        self.assertEqual(issues[0][0], 7)

    def test_code_fence_with_language_specifier(self):
        lines = [
            "```python\n",
            "[README.md](README.md)\n",
            "```\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_five_backtick_fence(self):
        lines = [
            "`````\n",
            "[README.md](README.md)\n",
            "`````\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)

    def test_mismatched_fence_lengths(self):
        # Opening with ```` but closing with ``` should not close
        lines = [
            "````\n",
            "[README.md](README.md)\n",
            "```\n",
            "[GUIDE.md](GUIDE.md)\n",
            "````\n",
        ]
        issues = check_file_content(lines)
        self.assertEqual(len(issues), 0)


if __name__ == "__main__":
    unittest.main()
