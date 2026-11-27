"""Release hold must fail closed and require wallstop's exact-candidate approval."""

import importlib.util
import json
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "verify_release_hold",
    Path(__file__).resolve().parents[2] / "scripts/release/verify_release_hold.py",
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

TREE = "a" * 40
OTHER_TREE = "b" * 40
PACKAGE = "c" * 64
OTHER_PACKAGE = "d" * 64
CLOSED_AT = "2026-11-28T12:00:00Z"
APPROVED_AT = "2026-11-28T11:00:00Z"


def marker(version="4.0.0", tree=TREE, package=PACKAGE, destinations=None):
    return (
        "Approved.\n\n<!-- dxm-release-approval: "
        + json.dumps(
            {
                "version": version,
                "tree": tree,
                "package_sha256": package,
                "destinations": destinations or ["npm", "github-release"],
            }
        )
        + " -->"
    )


def comment(body, login="wallstop", created_at=APPROVED_AT, updated_at=None):
    return {
        "user": {"login": login},
        "body": body,
        "created_at": created_at,
        "updated_at": updated_at or created_at,
    }


def closed_issue(state_reason="completed", closed_at=CLOSED_AT):
    return {
        "number": 575,
        "state": "closed",
        "state_reason": state_reason,
        "closed_at": closed_at,
    }


class PrepareTests(unittest.TestCase):
    def test_prepare_allows_the_readiness_issue_to_stay_open(self):
        errors = MODULE.evaluate(
            [{"number": 575}], None, [], phase="prepare", version="4.0.0", tree=TREE
        )
        self.assertEqual(errors, [])

    def test_other_versions_are_not_blocked_by_the_4_0_0_hold(self):
        errors = MODULE.evaluate(
            [{"number": 575}, {"number": 726}],
            None,
            [],
            phase="prepare",
            version="4.0.1",
            tree=TREE,
        )
        self.assertEqual(errors, [])

    def test_prepare_blocks_any_other_open_milestone_issue(self):
        errors = MODULE.evaluate(
            [{"number": 575}, {"number": 726}],
            None,
            [],
            phase="prepare",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("#726" in error for error in errors))


class TagTests(unittest.TestCase):
    def test_tag_requires_the_readiness_issue_to_be_closed(self):
        errors = MODULE.evaluate(
            [{"number": 575}],
            {"number": 575, "state": "open", "state_reason": None, "closed_at": None},
            [],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("still open" in error for error in errors))

    def test_tag_requires_completed_closure_not_not_planned(self):
        errors = MODULE.evaluate(
            [],
            closed_issue("not_planned"),
            [comment(marker())],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("not 'completed'" in error for error in errors))

    def test_tag_accepts_wallstops_exact_candidate_marker(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker())],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertEqual(errors, [])

    def test_tag_rejects_a_different_tree(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(tree=OTHER_TREE))],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("No human approval marker" in error for error in errors))

    def test_tag_ignores_markers_from_anyone_but_wallstop(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(), login="some-agent")],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("No human approval marker" in error for error in errors))

    def test_tag_rejects_approval_posted_after_the_issue_closed(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(), created_at="2026-11-28T13:00:00Z")],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("posted after" in error or "posted or edited" in error for error in errors))

    def test_tag_rejects_approval_edited_after_the_issue_closed(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(), updated_at="2026-11-28T13:00:00Z")],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("posted or edited" in error for error in errors))

    def test_tag_rejects_a_marker_timestamp_equal_to_closure(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(), updated_at=CLOSED_AT)],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("at or after" in error for error in errors))

    def test_tag_requires_npm_and_github_release_destinations(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(destinations=["asset-store"]))],
            phase="tag",
            version="4.0.0",
            tree=TREE,
        )
        self.assertTrue(any("destinations" in error for error in errors))

    def test_tag_verifies_the_package_when_provided(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(package=OTHER_PACKAGE))],
            phase="tag",
            version="4.0.0",
            tree=TREE,
            package_sha256=PACKAGE,
        )
        self.assertTrue(any("No human approval marker" in error for error in errors))


class PublishTests(unittest.TestCase):
    def test_publish_requires_the_approved_package_bytes(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker(package=OTHER_PACKAGE))],
            phase="publish",
            version="4.0.0",
            tree=TREE,
            package_sha256=PACKAGE,
        )
        self.assertTrue(any("No human approval marker" in error for error in errors))

    def test_publish_accepts_the_approved_package_bytes(self):
        errors = MODULE.evaluate(
            [],
            closed_issue(),
            [comment(marker())],
            phase="publish",
            version="4.0.0",
            tree=TREE,
            package_sha256=PACKAGE,
        )
        self.assertEqual(errors, [])


class IssueFilterTests(unittest.TestCase):
    def test_pull_requests_in_the_milestone_do_not_block(self):
        records = [
            {"number": 852, "pull_request": {"url": "https://example.invalid"}},
            {"number": 726},
        ]
        self.assertEqual(MODULE.issues_only(records), [{"number": 726}])

    def test_malformed_marker_is_ignored(self):
        self.assertEqual(MODULE.approval_markers([comment("<!-- dxm-release-approval: { -->")]), [])


if __name__ == "__main__":
    unittest.main()
