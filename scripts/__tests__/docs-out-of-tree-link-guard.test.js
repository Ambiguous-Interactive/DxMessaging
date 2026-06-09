/**
 * @fileoverview Tests for validate-docs-out-of-tree-links.js. The validator
 * guards against the failure mode that took down `Validate Documentation
 * Build / Build documentation (strict mode)`: relative links from docs/ that
 * climb above the docs/ tree (for example `../../.github/workflows/...`) are
 * invalid in mkdocs strict mode because mkdocs only resolves links inside
 * the docs/ tree. Such references must use the absolute `https://github.com/.../blob/master/...`
 * URL instead.
 *
 * The validator additionally checks the OTHER half of the linking contract:
 * every self-repo `.../blob/<ref>/<path>` URL must resolve to a real file or
 * directory in the working tree (validated offline -- lychee excludes these
 * URLs because a network check 404s for files added in the same PR before
 * they reach master). Fixtures tagged with `reasonMatch` exercise that path.
 */

"use strict";

const path = require("path");

const validator = require("../validate-docs-out-of-tree-links");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const FIXTURES = [
  {
    name: "in-tree sibling link",
    relativePath: "docs/guides/foo.md",
    content: "See [other](./other.md) for context.",
    expectedViolations: 0
  },
  {
    name: "in-tree parent-then-sibling link",
    relativePath: "docs/runbooks/foo.md",
    content: "See [reference](../reference/runtime-settings.md).",
    expectedViolations: 0
  },
  {
    name: "absolute https GitHub URL is OK",
    relativePath: "docs/runbooks/unity-runners-after-transfer.md",
    content:
      "Use [unstick-run.yml](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/unstick-run.yml) for manual recovery.",
    expectedViolations: 0
  },
  {
    name: "absolute https external URL is OK",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [GitHub Community Discussion #186811](https://github.com/orgs/community/discussions/186811).",
    expectedViolations: 0
  },
  {
    name: "mailto link is OK",
    relativePath: "docs/index.md",
    content: "Email [team](mailto:team@example.com)",
    expectedViolations: 0
  },
  {
    name: "anchor link is OK",
    relativePath: "docs/index.md",
    content: "Jump to [the top](#top)",
    expectedViolations: 0
  },
  {
    name: "FAILS for ../../.github/workflows/... escape",
    relativePath: "docs/runbooks/foo.md",
    content: "Recovery: [unstick-run.yml](../../.github/workflows/unstick-run.yml).",
    expectedViolations: 1
  },
  {
    name: "FAILS for ../../scripts/... escape",
    relativePath: "docs/guides/foo.md",
    content: "See [helper](../../scripts/doctor.js) for diagnostics.",
    expectedViolations: 1
  },
  {
    name: "FAILS for ../../scripts/... escape in .markdown docs file",
    relativePath: "docs/guides/foo.markdown",
    content: "See [helper](../../scripts/doctor.js) for diagnostics.",
    expectedViolations: 1
  },
  {
    name: "FAILS for ../../../Runtime/... escape from nested docs subdir",
    relativePath: "docs/reference/sub/foo.md",
    content: "Look at [code](../../../Runtime/Core/MessageBus.cs).",
    expectedViolations: 1
  },
  {
    name: "FAILS for ../README.md escape from top-level docs file",
    relativePath: "docs/index.md",
    content: "See [readme](../README.md) at repo root.",
    expectedViolations: 1
  },
  {
    name: "FAILS for multiple violations in one file",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "First: [a](../../.github/workflows/unstick-run.yml)",
      "Second: [b](../../scripts/doctor.js)",
      "Third (in-tree, OK): [c](./other.md)"
    ].join("\n"),
    expectedViolations: 2
  },
  {
    name: "fenced code blocks are not scanned",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "```text",
      "[fake](../../scripts/should-be-ignored.js)",
      "```",
      "",
      "Real OK link [other](./other.md)"
    ].join("\n"),
    expectedViolations: 0
  },
  {
    name: "FAILS for full reference-style link with out-of-tree definition",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "Recovery: [unstick-run.yml][unstick].",
      "",
      "[unstick]: ../../.github/workflows/unstick-run.yml"
    ].join("\n"),
    expectedViolations: 1
  },
  {
    name: "FAILS for collapsed reference link with out-of-tree definition",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "Recovery: [unstick][].",
      "",
      "[unstick]: ../../.github/workflows/unstick-run.yml"
    ].join("\n"),
    expectedViolations: 1
  },
  {
    name: "FAILS for shortcut reference link with out-of-tree definition",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "Recovery: [unstick] right here.",
      "",
      "[unstick]: ../../.github/workflows/unstick-run.yml"
    ].join("\n"),
    expectedViolations: 1
  },
  {
    name: "in-tree reference definitions do NOT fail",
    relativePath: "docs/runbooks/foo.md",
    content: ["Recovery: [sibling][].", "", "[sibling]: ./other.md"].join("\n"),
    expectedViolations: 0
  },
  {
    name: "reference definition wrapped in <...> URL form is scanned",
    relativePath: "docs/runbooks/foo.md",
    content: ["See [bad][].", "", "[bad]: <../../scripts/doctor.js>"].join("\n"),
    expectedViolations: 1
  },
  {
    name: "reference definition with title is scanned",
    relativePath: "docs/runbooks/foo.md",
    content: ["See [bad][].", "", '[bad]: ../../scripts/doctor.js "title"'].join("\n"),
    expectedViolations: 1
  },
  {
    name: "absolute https reference definition is OK",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "See [gh][].",
      "",
      "[gh]: https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/doctor.js"
    ].join("\n"),
    expectedViolations: 0
  },
  {
    name: "inline backtick code spans containing escape are not scanned",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Use ``[doc](../../scripts/should-be-ignored.js)`` in inline code is just a markdown sample.",
    expectedViolations: 0
  },
  {
    name: "single-tick inline code span is not scanned",
    relativePath: "docs/runbooks/foo.md",
    content: "The literal `[ref](../../scripts/doctor.js)` is an example -- ignore it.",
    expectedViolations: 0
  },
  // Round-3 NIT-D coverage: mixed-density / nested-density inline code.
  // The validator's `stripInlineCodeSpans` is intentionally scoped to
  // single-backtick spans on a single line (see the comment on
  // INLINE_CODE_SPAN_RE in scripts/validate-docs-out-of-tree-links.js).
  // Multi-backtick spans -- which CommonMark allows so authors can embed
  // single backticks in their inline code -- are out of scope by design.
  // These fixtures lock in the actual behavior so the trade-off is
  // explicit. If the validator is later extended to handle multi-tick
  // spans, update these fixtures (and the validator comment) together.
  {
    // Mixed-density nested case: outer wrapper `` (double-tick), inner
    // `` ` `` (single-tick) wrapping a link. The validator's single-tick
    // regex matches the spans `` `the inner ` `` and `` ` literal` ``,
    // leaving the LINK ITSELF unblanked between them. This is the
    // documented out-of-scope shape -- multi-backtick spans are not
    // detected (see the comment on INLINE_CODE_SPAN_RE in
    // scripts/validate-docs-out-of-tree-links.js). The link DOES
    // surface as a violation. If a future change extends the validator
    // to handle multi-tick spans, flip this fixture to 0 and update the
    // validator comment.
    name: "MIXED-DENSITY (out-of-scope): double-tick wrapper around single-tick + link surfaces a violation",
    relativePath: "docs/runbooks/foo.md",
    content: "See ``the inner `[ref](../../scripts/doctor.js)` literal`` for context.",
    expectedViolations: 1
  },
  {
    // Double-tick wrapper around a bare link, no inner ticks. The
    // single-tick regex matches the middle slice `..[ref](...)..` of
    // the outer double-tick wrapper (second `` ` `` of opener to first
    // `` ` `` of closer), which ENCLOSES the link. So this shape DOES
    // get suppressed -- it "happens to work" via the same mechanism as
    // the existing `[doc](../../scripts/should-be-ignored.js)` fixture
    // immediately above this block. Documented here so future authors
    // do not assume the validator has formal multi-tick support.
    name: "MIXED-DENSITY (out-of-scope): double-tick wrapper around a bare link is suppressed by regex coincidence",
    relativePath: "docs/runbooks/foo.md",
    content: "Sample: ``[ref](../../scripts/doctor.js)`` -- treat as inline code.",
    expectedViolations: 0
  },
  {
    // Mixed density where the link is OUTSIDE every backtick span. The
    // surrounding single-tick code spans should not affect detection.
    name: "MIXED-DENSITY: single-tick code on either side does not suppress a real out-of-tree link",
    relativePath: "docs/runbooks/foo.md",
    content: "Compare `foo()` to [bad](../../scripts/doctor.js) and `bar()`.",
    expectedViolations: 1
  },
  {
    name: "4-space-indented code block is not scanned",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "Before block:",
      "",
      "    [example](../../scripts/should-be-ignored.js)",
      "    more code",
      "",
      "After block: [other](./other.md)"
    ].join("\n"),
    expectedViolations: 0
  },
  {
    name: "tab-indented code block is not scanned",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "Tabbed:",
      "",
      "\t[example](../../scripts/should-be-ignored.js)",
      "",
      "Outside: [other](./other.md)"
    ].join("\n"),
    expectedViolations: 0
  },
  // ---------------------------------------------------------------------------
  // Self-repo blob-link OFFLINE existence checks (concern 2). These verify the
  // `.../blob/<ref>/<path>` target resolves against the working tree. The
  // `<path>` portions below are real repo files/dirs (or deliberately bogus).
  // ---------------------------------------------------------------------------
  {
    name: "BLOB: self-repo link to an EXISTING tree path is OK",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [watchdog](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml).",
    expectedViolations: 0
  },
  {
    name: "BLOB: self-repo link to a directory target is OK",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [scripts dir](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts).",
    expectedViolations: 0
  },
  {
    name: "BLOB FAILS: self-repo link to a MISSING tree path",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [ghost](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/does-not-exist.yml).",
    expectedViolations: 1,
    reasonMatch: /does not exist in the working tree: \.github\/workflows\/does-not-exist\.yml/
  },
  {
    name: "BLOB: %20 (space) is decoded before resolution (Mini Combat README)",
    relativePath: "docs/getting-started/index.md",
    content:
      "Try [Mini Combat](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md).",
    expectedViolations: 0
  },
  {
    name: "BLOB: %2B (plus) and %20 are decoded before resolution (UI Buttons + Inspector README)",
    relativePath: "docs/getting-started/index.md",
    content:
      "Try [UI Buttons + Inspector](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/UI%20Buttons%20%2B%20Inspector/README.md).",
    expectedViolations: 0
  },
  {
    name: "BLOB: a #anchor is stripped before resolution",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Jump [there](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml#L10).",
    expectedViolations: 0
  },
  {
    name: "BLOB: a ?query is stripped before resolution",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Raw [there](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml?raw=true).",
    expectedViolations: 0
  },
  {
    name: "BLOB: link inside a fenced code block is ignored even if target is missing",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "```text",
      "[ghost](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/no/such/path.txt)",
      "```",
      "",
      "Real OK link [other](./other.md)"
    ].join("\n"),
    expectedViolations: 0
  },
  {
    name: "BLOB: link inside an inline code span is ignored even if target is missing",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Sample `[ghost](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/no/such/path.txt)` -- ignore it.",
    expectedViolations: 0
  },
  {
    name: "BLOB: a ref segment other than master still resolves against the working tree",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [watchdog](https://github.com/Ambiguous-Interactive/DxMessaging/blob/dev%2Fbranch/.github/workflows/stuck-job-watchdog.yml).",
    expectedViolations: 0
  },
  // ---------------------------------------------------------------------------
  // FIX 1 regression: a MALFORMED percent-encoding in a self-repo blob URL must
  // NOT crash the validator (decodeURIComponent throws URIError on `%bar` /
  // truncated `%2`). The raw path is kept; it cannot exist on disk, so it is
  // reported as exactly one normal "missing target" violation.
  // ---------------------------------------------------------------------------
  {
    name: "BLOB: malformed percent-encoding does not crash and produces exactly 1 violation",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Broken [bad](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/foo%bar.md).",
    expectedViolations: 1,
    reasonMatch: /does not exist in the working tree: foo%bar\.md/
  },
  {
    name: "BLOB: truncated percent-encoding does not crash and produces exactly 1 violation",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Broken [bad](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/foo%2).",
    expectedViolations: 1,
    reasonMatch: /does not exist in the working tree: foo%2/
  },
  // ---------------------------------------------------------------------------
  // FIX 2: self-repo `tree/<ref>/<path>` DIRECTORY links are covered by the same
  // offline existence check as `blob/` links (fs.existsSync resolves dirs too).
  // ---------------------------------------------------------------------------
  {
    name: "TREE: self-repo directory link to an EXISTING dir is OK",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [scripts dir](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/scripts).",
    expectedViolations: 0
  },
  {
    name: "TREE FAILS: self-repo directory link to a MISSING dir",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [ghost dir](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/no/such/dir).",
    expectedViolations: 1,
    reasonMatch: /does not exist in the working tree: no\/such\/dir/
  },
  // ---------------------------------------------------------------------------
  // EPHEMERAL CI-RUN URL rejection. These URLs (.../actions/runs/<id>) are
  // per-run audit decoration; the run is purgeable, the link goes 404, and
  // lychee reports a hard failure. The class-wide guard rejects ANY such link
  // in docs/ markdown. The right shape is backticked plain text (no hyperlink).
  // ---------------------------------------------------------------------------
  {
    name: "ACTIONS-RUN FAILS: self-repo actions/runs/<id> hyperlink",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Identified in [`70874414898`](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898) as the load-bearing missing DLL.",
    expectedViolations: 1,
    reasonMatch: /ephemeral CI run URLs go stale/
  },
  {
    name: "ACTIONS-RUN FAILS: cross-org actions/runs/<id> hyperlink",
    relativePath: "docs/runbooks/foo.md",
    content: "See [run](https://github.com/SomeOrg/SomeRepo/actions/runs/123456789).",
    expectedViolations: 1,
    reasonMatch: /ephemeral CI run URLs go stale/
  },
  {
    name: "ACTIONS-RUN FAILS: a deep URL fragment under actions/runs/<id>",
    relativePath: "docs/runbooks/foo.md",
    content:
      "See [job](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898/job/19370238492).",
    expectedViolations: 1,
    reasonMatch: /ephemeral CI run URLs go stale/
  },
  {
    name: "ACTIONS-RUN OK: backticked plain run id (no hyperlink) -- the canonical form",
    relativePath: "docs/runbooks/foo.md",
    // Inline `..` makes it a single inline code span, which the validator
    // already excludes from link scanning.
    content: "Identified in `production run 70874414898` as the load-bearing missing DLL.",
    expectedViolations: 0
  },
  {
    name: "ACTIONS-RUN OK: linking to a workflow definition under .github/workflows/ is NOT ephemeral",
    relativePath: "docs/runbooks/foo.md",
    content:
      "Trigger [stuck-job-watchdog.yml](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml).",
    expectedViolations: 0
  },
  {
    name: "ACTIONS-RUN OK: linking to the actions WORKFLOWS landing page (no run id) is not rejected",
    relativePath: "docs/runbooks/foo.md",
    // The validator scopes the ephemeral check to `/actions/runs/<digit-id>`; the
    // generic /actions or /actions/workflows page is not run-specific decoration.
    content: "See [workflows](https://github.com/Ambiguous-Interactive/DxMessaging/actions).",
    expectedViolations: 0
  },
  {
    name: "ACTIONS-RUN inside a fenced code block is ignored",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "```text",
      "[run](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898)",
      "```",
      "",
      "After: [other](./other.md)"
    ].join("\n"),
    expectedViolations: 0
  },
  {
    name: "ACTIONS-RUN as a reference-style link is rejected",
    relativePath: "docs/runbooks/foo.md",
    content: [
      "See [run][r].",
      "",
      "[r]: https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898"
    ].join("\n"),
    expectedViolations: 1,
    reasonMatch: /ephemeral CI run URLs go stale/
  }
];

describe("validate-docs-out-of-tree-links scanContent", () => {
  let tempDir;
  beforeAll(() => {
    tempDir = makeTempDir("docs-out-of-tree");
  });
  afterAll(() => {
    cleanupDir(tempDir);
  });

  test.each(FIXTURES)("$name", ({ relativePath, content, expectedViolations, reasonMatch }) => {
    // Build a synthetic docs root in tempDir so escapesDocsTree compares
    // against THIS tree, not the repo's real docs/. The validator uses
    // `validator.DOCS_ROOT` as the boundary, so we point relative paths
    // at the real docs/ by faking the file path under <repo>/docs/.
    const realDocsRoot = validator.DOCS_ROOT;
    const fakeAbsPath = path.join(realDocsRoot, "..", relativePath.replace(/^docs\//, "docs/"));
    // Make sure the file we hand the scanner actually points into the
    // real docs/ subtree (we never write to it on disk; scanContent
    // only needs the path to resolve the link's destination). Self-repo
    // blob fixtures additionally resolve their target against the real
    // working tree (validator.REPO_ROOT), which is why the existing/bogus
    // paths above are chosen relative to the actual repo.
    const violations = validator.scanContent(fakeAbsPath, content);
    expect(violations).toHaveLength(expectedViolations);
    for (const v of violations) {
      // Self-repo blob existence violations carry a distinct reason; the
      // out-of-tree relative-link violations carry the original one.
      expect(v.reason).toMatch(reasonMatch || /full https:\/\/github\.com/);
    }
  });
});

describe("validate-docs-out-of-tree-links auto-fix", () => {
  test("outOfTreeRelativeLinkToBlobUrl rewrites docs-relative escapes to blob URLs", () => {
    const file = path.join(validator.DOCS_ROOT, "guides", "mcp-local-setup.md");
    const rewritten = validator.outOfTreeRelativeLinkToBlobUrl(file, "../../scripts/mcp/README.md");
    expect(rewritten).toBe(
      "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md"
    );
  });

  test("fixOutOfTreeRelativeLinks rewrites inline and reference URLs outside code blocks", () => {
    const file = path.join(validator.DOCS_ROOT, "runbooks", "fix-fixture.md");
    const source = [
      "# Fix fixture",
      "",
      "Inline [bad](../../scripts/doctor.js)",
      "",
      "[bad-ref]: ../../scripts/doctor.js",
      "",
      "```text",
      "[ignored](../../scripts/doctor.js)",
      "```",
      ""
    ].join("\n");

    const fixed = validator.fixOutOfTreeRelativeLinks(file, source);
    expect(fixed.changed).toBe(true);
    expect(fixed.fixes).toHaveLength(2);
    expect(fixed.content).toContain(
      "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/doctor.js"
    );
    expect(fixed.content).toContain("[ignored](../../scripts/doctor.js)");
  });

  test("parseArgs accepts --fix and leaves file args intact", () => {
    expect(validator.parseArgs(["--fix", "docs/index.md"])).toEqual({
      help: false,
      fix: true,
      files: ["docs/index.md"]
    });
    expect(validator.parseArgs(["--help"])).toEqual({
      help: true,
      fix: false,
      files: []
    });
  });
});

describe("validate-docs-out-of-tree-links isDocsMarkdown", () => {
  test("docs markdown is recognized", () => {
    expect(validator.isDocsMarkdown(path.join(validator.DOCS_ROOT, "runbooks", "x.md"))).toBe(true);
  });
  test("docs .markdown is recognized", () => {
    expect(validator.isDocsMarkdown(path.join(validator.DOCS_ROOT, "runbooks", "x.markdown"))).toBe(
      true
    );
  });
  test("non-docs markdown is rejected", () => {
    expect(validator.isDocsMarkdown(path.join(validator.REPO_ROOT, "README.md"))).toBe(false);
  });
  test("docs non-markdown is rejected", () => {
    expect(validator.isDocsMarkdown(path.join(validator.DOCS_ROOT, "runbooks", "x.txt"))).toBe(
      false
    );
  });
});

describe("validate-docs-out-of-tree-links selfRepoBlobTarget (unit)", () => {
  test("returns null for an empty path (blob/<ref>/ with nothing after)", () => {
    expect(
      validator.selfRepoBlobTarget(
        "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/"
      )
    ).toBeNull();
  });

  test("returns null for a non-self-repo github URL", () => {
    expect(
      validator.selfRepoBlobTarget("https://github.com/orgs/community/discussions/186811")
    ).toBeNull();
  });

  test("decodes %20 (space) in the captured path", () => {
    expect(
      validator.selfRepoBlobTarget(
        "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md"
      )
    ).toBe("Samples~/Mini Combat/README.md");
  });

  test("decodes %2B (plus) and %20 in the captured path", () => {
    expect(
      validator.selfRepoBlobTarget(
        "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/UI%20Buttons%20%2B%20Inspector/README.md"
      )
    ).toBe("Samples~/UI Buttons + Inspector/README.md");
  });

  test("does NOT throw on malformed percent-encoding; falls back to the raw path (FIX 1)", () => {
    let result;
    expect(() => {
      result = validator.selfRepoBlobTarget(
        "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/foo%bar.md"
      );
    }).not.toThrow();
    expect(result).toBe("foo%bar.md");
  });

  test("recognizes tree/ directory links and returns the repo-relative path", () => {
    expect(
      validator.selfRepoBlobTarget(
        "https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/scripts"
      )
    ).toBe("scripts");
  });
});

describe("validate-docs-out-of-tree-links selfRepoBlobTargetExists (unit)", () => {
  test("resolves an existing real path with a trailing prose '.' via the trim", () => {
    // `scripts` exists; the trailing `.` is conservatively trimmed.
    expect(validator.selfRepoBlobTargetExists("scripts.")).toBe(true);
  });

  test("resolves an existing real path with a trailing prose ')' via the trim", () => {
    expect(validator.selfRepoBlobTargetExists("scripts)")).toBe(true);
  });

  test("returns false for a genuinely missing path (trim does not mask it)", () => {
    expect(validator.selfRepoBlobTargetExists("scripts/genuinely-missing-xyz.js")).toBe(false);
  });

  test("resolves an existing directory (tree-link target)", () => {
    expect(validator.selfRepoBlobTargetExists("scripts")).toBe(true);
  });
});

describe("validate-docs-out-of-tree-links EPHEMERAL_CI_RUN_RE (unit)", () => {
  test("matches a self-repo actions/runs/<id> URL", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898"
      )
    ).toBe(true);
  });

  test("matches a cross-org actions/runs/<id> URL", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/SomeOrg/SomeRepo/actions/runs/123456789"
      )
    ).toBe(true);
  });

  test("matches a deep job/<id> URL under actions/runs/<id>", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/70874414898/job/19370238492"
      )
    ).toBe(true);
  });

  test("does NOT match the generic /actions page (no run id)", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/Ambiguous-Interactive/DxMessaging/actions"
      )
    ).toBe(false);
  });

  test("does NOT match a workflow-definition blob link under .github/workflows/", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml"
      )
    ).toBe(false);
  });

  test("does NOT match the actions/workflows landing page", () => {
    expect(
      validator.EPHEMERAL_CI_RUN_RE.test(
        "https://github.com/Ambiguous-Interactive/DxMessaging/actions/workflows/ci.yml"
      )
    ).toBe(false);
  });
});

describe("validate-docs-out-of-tree-links real docs tree", () => {
  test("the live docs/ tree has no out-of-tree relative links", () => {
    const files = validator.listAllDocsFiles();
    expect(files.length).toBeGreaterThan(0);
    const violations = [];
    for (const file of files) {
      violations.push(...validator.scanFile(file));
    }
    if (violations.length > 0) {
      const lines = violations.map(
        (v) => `${path.relative(validator.REPO_ROOT, v.file)}:${v.line} -> ${v.url}`
      );
      throw new Error(`Found ${violations.length} out-of-tree link(s):\n${lines.join("\n")}`);
    }
    expect(violations).toHaveLength(0);
  });
});
