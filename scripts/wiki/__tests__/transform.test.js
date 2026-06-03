/**
 * Test suite for wiki transformation functions
 */

const {
  isExternalLink,
  docsPathToWikiPage,
  findMarkdownLinks,
  CodeBlockTracker,
  transformLine,
  transformFile
} = require("../transform-docs-to-wiki.js");

describe("isExternalLink", () => {
  test("identifies http links", () => {
    expect(isExternalLink("http://example.com")).toBe(true);
  });

  test("identifies https links", () => {
    expect(isExternalLink("https://example.com/path")).toBe(true);
  });

  test("identifies mailto links", () => {
    expect(isExternalLink("mailto:test@example.com")).toBe(true);
  });

  test("identifies tel links", () => {
    expect(isExternalLink("tel:+1234567890")).toBe(true);
  });

  test("identifies ftp links", () => {
    expect(isExternalLink("ftp://files.example.com")).toBe(true);
  });

  test("identifies relative paths as internal", () => {
    expect(isExternalLink("./file.md")).toBe(false);
    expect(isExternalLink("../file.md")).toBe(false);
    expect(isExternalLink("file.md")).toBe(false);
  });

  test("identifies absolute paths as internal", () => {
    expect(isExternalLink("/docs/file.md")).toBe(false);
  });
});

describe("docsPathToWikiPage", () => {
  test("converts simple path", () => {
    expect(docsPathToWikiPage("concepts/message-types.md")).toBe("Concepts-Message-Types");
  });

  test("converts nested path", () => {
    expect(docsPathToWikiPage("getting-started/overview.md")).toBe("Getting-Started-Overview");
  });

  test("converts index.md to parent directory name", () => {
    expect(docsPathToWikiPage("concepts/index.md")).toBe("Concepts");
  });

  test("converts nested index.md to parent path", () => {
    expect(docsPathToWikiPage("advanced/topics/index.md")).toBe("Advanced-Topics");
  });

  test("converts root index.md to Home", () => {
    expect(docsPathToWikiPage("index.md")).toBe("Home");
  });

  test("converts README.md to Home", () => {
    expect(docsPathToWikiPage("README.md")).toBe("Home");
  });

  test("handles parent directory README reference", () => {
    expect(docsPathToWikiPage("../README.md")).toBe("Home");
  });

  test("capitalizes each path segment", () => {
    expect(docsPathToWikiPage("guides/testing.md")).toBe("Guides-Testing");
  });

  test("handles paths without .md extension", () => {
    expect(docsPathToWikiPage("concepts/types")).toBe("Concepts-Types");
  });
});

describe("CodeBlockTracker", () => {
  let tracker;

  beforeEach(() => {
    tracker = new CodeBlockTracker();
  });

  test("tracks triple backtick code blocks", () => {
    expect(tracker.processLine("```")).toBe(true);
    expect(tracker.processLine("code here")).toBe(true);
    expect(tracker.processLine("```")).toBe(false);
  });

  test("tracks code blocks with language", () => {
    expect(tracker.processLine("```javascript")).toBe(true);
    expect(tracker.processLine("const x = 1;")).toBe(true);
    expect(tracker.processLine("```")).toBe(false);
  });

  test("tracks code blocks with csharp language", () => {
    expect(tracker.processLine("```csharp")).toBe(true);
    expect(tracker.processLine("public class Test {}")).toBe(true);
    expect(tracker.processLine("```")).toBe(false);
  });

  test("tracks quadruple backtick code blocks", () => {
    expect(tracker.processLine("````")).toBe(true);
    expect(tracker.processLine("```")).toBe(true);
    expect(tracker.processLine("nested")).toBe(true);
    expect(tracker.processLine("```")).toBe(true);
    expect(tracker.processLine("````")).toBe(false);
  });

  test("tracks tilde code blocks", () => {
    expect(tracker.processLine("~~~")).toBe(true);
    expect(tracker.processLine("code")).toBe(true);
    expect(tracker.processLine("~~~")).toBe(false);
  });

  test("does not close backtick block with tildes", () => {
    expect(tracker.processLine("```")).toBe(true);
    expect(tracker.processLine("~~~")).toBe(true);
    expect(tracker.processLine("```")).toBe(false);
  });

  test("reset clears state", () => {
    tracker.processLine("```");
    expect(tracker.inCodeBlock).toBe(true);
    tracker.reset();
    expect(tracker.inCodeBlock).toBe(false);
  });

  test("handles indented code fences", () => {
    expect(tracker.processLine("    ```python")).toBe(true);
    expect(tracker.processLine('    print("hello")')).toBe(true);
    expect(tracker.processLine("    ```")).toBe(false);
  });
});

describe("findMarkdownLinks", () => {
  test("finds simple link", () => {
    const links = findMarkdownLinks("See [guide](guide.md) for more.");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("guide");
    expect(links[0].href).toBe("guide.md");
    expect(links[0].isImage).toBe(false);
  });

  test("finds multiple links", () => {
    const links = findMarkdownLinks("[one](a.md) and [two](b.md)");
    expect(links).toHaveLength(2);
    expect(links[0].text).toBe("one");
    expect(links[0].href).toBe("a.md");
    expect(links[1].text).toBe("two");
    expect(links[1].href).toBe("b.md");
  });

  test("finds image links", () => {
    const links = findMarkdownLinks("![alt text](image.png)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("alt text");
    expect(links[0].href).toBe("image.png");
    expect(links[0].isImage).toBe(true);
  });

  test("finds links with anchors", () => {
    const links = findMarkdownLinks("[section](page.md#heading)");
    expect(links).toHaveLength(1);
    expect(links[0].href).toBe("page.md#heading");
  });

  test("ignores links inside inline code", () => {
    const links = findMarkdownLinks("Use `[link](file.md)` syntax");
    expect(links).toHaveLength(0);
  });

  test("ignores links inside double-backtick inline code", () => {
    const links = findMarkdownLinks("Use ``[link](file.md)`` syntax");
    expect(links).toHaveLength(0);
  });

  test("ignores links inside double-backtick with text before", () => {
    const links = findMarkdownLinks("Some text ``[link](file.md)`` more text");
    expect(links).toHaveLength(0);
  });

  test("finds real link after double-backtick inline code", () => {
    const links = findMarkdownLinks("Use ``code`` then [real](file.md)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("real");
    expect(links[0].href).toBe("file.md");
  });

  test("ignores links inside triple-backtick inline code", () => {
    const links = findMarkdownLinks("Use ```[link](file.md)``` syntax");
    expect(links).toHaveLength(0);
  });

  test("handles mixed single and double backtick code spans", () => {
    const links = findMarkdownLinks("`single` and ``double [link](file.md)`` and [real](page.md)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("real");
    expect(links[0].href).toBe("page.md");
  });

  test("handles unclosed multi-backtick delimiter", () => {
    const links = findMarkdownLinks("Text ``unclosed [link](file.md)");
    expect(links).toHaveLength(1);
    expect(links[0].href).toBe("file.md");
  });

  test("handles asymmetric backticks correctly", () => {
    // Double open, single close - should not match, link should be found
    const links = findMarkdownLinks("Text ``code` [link](file.md)");
    expect(links).toHaveLength(1);
    expect(links[0].href).toBe("file.md");
  });

  test("finds links with nested brackets in text", () => {
    const links = findMarkdownLinks("[[nested]](page.md)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("[nested]");
  });

  test("finds links with parentheses in href", () => {
    const links = findMarkdownLinks("[link](page_(version).md)");
    expect(links).toHaveLength(1);
    expect(links[0].href).toBe("page_(version).md");
  });

  test("returns empty array for no links", () => {
    const links = findMarkdownLinks("No links here");
    expect(links).toHaveLength(0);
  });

  test("handles escaped brackets", () => {
    const links = findMarkdownLinks("\\[not a link\\](file.md)");
    expect(links).toHaveLength(0);
  });

  test("handles empty code span before link", () => {
    const links = findMarkdownLinks("`` `` then [link](file.md)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("link");
    expect(links[0].href).toBe("file.md");
  });

  test("handles very long backtick sequences", () => {
    const links = findMarkdownLinks("``````````[link](file.md)``````````");
    expect(links).toHaveLength(0);
  });

  test("handles backticks at end of line with no close", () => {
    const links = findMarkdownLinks("text with unclosed ``");
    expect(links).toHaveLength(0);
  });

  test("handles backtick immediately before link", () => {
    const links = findMarkdownLinks("`[link](file.md)");
    expect(links).toHaveLength(1);
    expect(links[0].text).toBe("link");
    expect(links[0].href).toBe("file.md");
  });
});

describe("transformLine", () => {
  test("transforms internal markdown link to wiki link", () => {
    const result = transformLine("[Guide](guide.md)", "index.md");
    // When link text matches wiki page name, simple [[Page]] format is used
    expect(result).toBe("[[Guide]]");
  });

  test("preserves external links", () => {
    const result = transformLine("[GitHub](https://github.com)", "index.md");
    expect(result).toBe("[GitHub](https://github.com)");
  });

  test("preserves anchor-only links", () => {
    const result = transformLine("[Section](#section)", "index.md");
    expect(result).toBe("[Section](#section)");
  });

  test("transforms links with anchors", () => {
    const result = transformLine("[Topic](concepts/types.md#section)", "index.md");
    expect(result).toBe("[[Concepts-Types#section|Topic]]");
  });

  test("transforms relative paths correctly", () => {
    // Note: Path resolution happens at a different level, '../index.md' becomes '..' page
    const result = transformLine("[Back](../index.md)", "concepts/types.md");
    expect(result).toBe("[[..|Back]]");
  });

  test("handles multiple links in one line", () => {
    const result = transformLine("[One](a.md) and [Two](b.md)", "index.md");
    expect(result).toContain("[[A|One]]");
    expect(result).toContain("[[B|Two]]");
  });

  test("transforms image paths", () => {
    const result = transformLine("![Image](images/diagram.png)", "index.md");
    expect(result).toBe("![Image](wiki-images/diagram.png)");
  });

  test("preserves external image links", () => {
    const result = transformLine("![Badge](https://img.shields.io/badge.svg)", "index.md");
    expect(result).toBe("![Badge](https://img.shields.io/badge.svg)");
  });

  test("returns unchanged line with no links", () => {
    const result = transformLine("Just some text", "index.md");
    expect(result).toBe("Just some text");
  });
});

describe("transformFile", () => {
  test("preserves code blocks", () => {
    const content = `# Example

\`\`\`markdown
[This should not be transformed](link.md)
\`\`\`

[This should be transformed](link.md)`;

    const result = transformFile(content, "index.md");
    expect(result).toContain("[This should not be transformed](link.md)");
    expect(result).toContain("[[Link|This should be transformed]]");
  });

  test("handles multiple code blocks", () => {
    const content = `[before](a.md)

\`\`\`
[inside1](b.md)
\`\`\`

[between](c.md)

\`\`\`csharp
[inside2](d.md)
\`\`\`

[after](e.md)`;

    const result = transformFile(content, "index.md");
    expect(result).toContain("[[A|before]]");
    expect(result).toContain("[inside1](b.md)");
    expect(result).toContain("[[C|between]]");
    expect(result).toContain("[inside2](d.md)");
    expect(result).toContain("[[E|after]]");
  });

  test("preserves line structure", () => {
    const content = `Line 1
Line 2
Line 3`;

    const result = transformFile(content, "index.md");
    const lines = result.split("\n");
    expect(lines).toHaveLength(3);
  });

  test("handles lone CR line endings", () => {
    const content = "[Guide](guide.md)\r\r[Section](#keep-anchor)\r";

    const result = transformFile(content, "index.md");
    expect(result).toContain("[[Guide]]");
    expect(result).toContain("[Section](#keep-anchor)");
    expect(result).toContain("\n\n");
    expect(result).not.toContain("\r");
  });

  test("handles empty content", () => {
    const result = transformFile("", "index.md");
    expect(result).toBe("");
  });

  test("handles content with only code blocks", () => {
    const content = `\`\`\`
code only
\`\`\``;

    const result = transformFile(content, "index.md");
    expect(result).toBe(content);
  });
});
