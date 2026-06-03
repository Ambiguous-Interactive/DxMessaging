"use strict";

const { convertHeadingToHtmlFragment, processMarkdownContent } = require("../fix-md029-md051.js");

describe("convertHeadingToHtmlFragment", () => {
  test("matches markdownlint slug behavior for punctuation and slash", () => {
    const fragment = convertHeadingToHtmlFragment("DXMSG009: Implicit hide / missing modifier");

    expect(fragment).toBe("#dxmsg009-implicit-hide--missing-modifier");
  });
});

describe("processMarkdownContent", () => {
  test("normalizes ordered lists to one-style", () => {
    const input = ["# Header", "", "1. First", "2. Second", "3. Third", ""].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(true);
    expect(result.content).toContain("1. First");
    expect(result.content).toContain("1. Second");
    expect(result.content).toContain("1. Third");
  });

  test("fixes local fragment links using heading fragments", () => {
    const input = [
      "# Diagnostics",
      "",
      "## DXMSG009: Implicit hide / missing modifier",
      "",
      "See [DXMSG009](#dxmsg009-implicit-hide-missing-modifier).",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(true);
    expect(result.content).toContain("[DXMSG009](#dxmsg009-implicit-hide--missing-modifier)");
  });

  test("does not rewrite content inside fenced code blocks", () => {
    const input = [
      "# Header",
      "",
      "```markdown",
      "2. Keep this list number",
      "See [Link](#header)",
      "```",
      "",
      "2. Rewrite this one",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.content).toContain("2. Keep this list number");
    expect(result.content).toContain("See [Link](#header)");
    expect(result.content).toContain("1. Rewrite this one");
  });

  test("keeps GitHub line fragments untouched", () => {
    const input = ["# Header", "", "[Line link](#L20)", ""].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(false);
    expect(result.content).toContain("[Line link](#L20)");
  });

  test("leaves valid duplicate-heading fragments unchanged", () => {
    const input = [
      "## Configuration",
      "",
      "## Configuration",
      "",
      "See [First](#configuration) and [Second](#configuration-1).",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(false);
    expect(result.content).toContain("[First](#configuration)");
    expect(result.content).toContain("[Second](#configuration-1)");
  });

  test("supports custom heading anchors", () => {
    const input = [
      "## Inspector section {#editor-inspector-overlay}",
      "",
      "Jump to [section](#editor-inspector-overlay).",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(false);
    expect(result.content).toContain("[section](#editor-inspector-overlay)");
  });

  test("fixes definition-style fragment links", () => {
    const input = [
      "## DXMSG009: Implicit hide / missing modifier",
      "",
      "[dxmsg9]: #dxmsg009-implicit-hide-missing-modifier",
      "Use [DXMSG009][dxmsg9].",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.changed).toBe(true);
    expect(result.content).toContain("[dxmsg9]: #dxmsg009-implicit-hide--missing-modifier");
  });

  test("does not modify blockquote-wrapped fenced code blocks", () => {
    const input = [
      "# Header",
      "",
      "> ```markdown",
      "> 2. Keep this list number",
      "> [Link](#header)",
      "> ```",
      "",
      "2. Rewrite this one",
      ""
    ].join("\n");

    const result = processMarkdownContent(input);

    expect(result.content).toContain("> 2. Keep this list number");
    expect(result.content).toContain("> [Link](#header)");
    expect(result.content).toContain("1. Rewrite this one");
  });
});
