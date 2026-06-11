"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");

const {
  isExternalLink,
  docsPathToWikiPage,
  findMarkdownLinks,
  CodeBlockTracker,
  transformLine,
  transformFile
} = require("../transform-docs-to-wiki.js");

test("isExternalLink identifies external schemes", () => {
  assert.equal(isExternalLink("http://example.com"), true);
  assert.equal(isExternalLink("https://example.com/path"), true);
  assert.equal(isExternalLink("mailto:test@example.com"), true);
  assert.equal(isExternalLink("tel:+1234567890"), true);
  assert.equal(isExternalLink("ftp://files.example.com"), true);
});

test("isExternalLink identifies internal paths", () => {
  assert.equal(isExternalLink("./file.md"), false);
  assert.equal(isExternalLink("../file.md"), false);
  assert.equal(isExternalLink("file.md"), false);
  assert.equal(isExternalLink("/docs/file.md"), false);
});

test("docsPathToWikiPage converts paths to wiki page names", () => {
  assert.equal(docsPathToWikiPage("concepts/message-types.md"), "Concepts-Message-Types");
  assert.equal(docsPathToWikiPage("getting-started/overview.md"), "Getting-Started-Overview");
  assert.equal(docsPathToWikiPage("guides/testing.md"), "Guides-Testing");
  assert.equal(docsPathToWikiPage("concepts/types"), "Concepts-Types");
});

test("docsPathToWikiPage maps index and README pages", () => {
  assert.equal(docsPathToWikiPage("concepts/index.md"), "Concepts");
  assert.equal(docsPathToWikiPage("advanced/topics/index.md"), "Advanced-Topics");
  assert.equal(docsPathToWikiPage("index.md"), "Home");
  assert.equal(docsPathToWikiPage("README.md"), "Home");
  assert.equal(docsPathToWikiPage("../README.md"), "Home");
});

test("CodeBlockTracker tracks backtick code blocks", () => {
  const tracker = new CodeBlockTracker();
  assert.equal(tracker.processLine("```"), true);
  assert.equal(tracker.processLine("code here"), true);
  assert.equal(tracker.processLine("```"), false);

  assert.equal(tracker.processLine("```csharp"), true);
  assert.equal(tracker.processLine("public class Test {}"), true);
  assert.equal(tracker.processLine("```"), false);
});

test("CodeBlockTracker handles nested fences of different lengths", () => {
  const tracker = new CodeBlockTracker();
  assert.equal(tracker.processLine("````"), true);
  assert.equal(tracker.processLine("```"), true);
  assert.equal(tracker.processLine("nested"), true);
  assert.equal(tracker.processLine("```"), true);
  assert.equal(tracker.processLine("````"), false);
});

test("CodeBlockTracker handles tilde fences and mismatched markers", () => {
  const tracker = new CodeBlockTracker();
  assert.equal(tracker.processLine("~~~"), true);
  assert.equal(tracker.processLine("code"), true);
  assert.equal(tracker.processLine("~~~"), false);

  // A tilde line must not close a backtick block.
  assert.equal(tracker.processLine("```"), true);
  assert.equal(tracker.processLine("~~~"), true);
  assert.equal(tracker.processLine("```"), false);
});

test("CodeBlockTracker reset clears state and indented fences work", () => {
  const tracker = new CodeBlockTracker();
  tracker.processLine("```");
  assert.equal(tracker.inCodeBlock, true);
  tracker.reset();
  assert.equal(tracker.inCodeBlock, false);

  assert.equal(tracker.processLine("    ```python"), true);
  assert.equal(tracker.processLine('    print("hello")'), true);
  assert.equal(tracker.processLine("    ```"), false);
});

test("findMarkdownLinks finds simple, multiple, image, and anchored links", () => {
  let links = findMarkdownLinks("See [guide](guide.md) for more.");
  assert.equal(links.length, 1);
  assert.equal(links[0].text, "guide");
  assert.equal(links[0].href, "guide.md");
  assert.equal(links[0].isImage, false);

  links = findMarkdownLinks("[one](a.md) and [two](b.md)");
  assert.equal(links.length, 2);
  assert.equal(links[1].href, "b.md");

  links = findMarkdownLinks("![alt text](image.png)");
  assert.equal(links.length, 1);
  assert.equal(links[0].isImage, true);

  links = findMarkdownLinks("[section](page.md#heading)");
  assert.equal(links[0].href, "page.md#heading");
});

test("findMarkdownLinks ignores links inside inline code spans", () => {
  assert.equal(findMarkdownLinks("Use `[link](file.md)` syntax").length, 0);
  assert.equal(findMarkdownLinks("Use ``[link](file.md)`` syntax").length, 0);
  assert.equal(findMarkdownLinks("Some text ``[link](file.md)`` more text").length, 0);
  assert.equal(findMarkdownLinks("Use ```[link](file.md)``` syntax").length, 0);
  assert.equal(findMarkdownLinks("``````````[link](file.md)``````````").length, 0);
});

test("findMarkdownLinks finds real links around code spans", () => {
  let links = findMarkdownLinks("Use ``code`` then [real](file.md)");
  assert.equal(links.length, 1);
  assert.equal(links[0].text, "real");

  links = findMarkdownLinks("`single` and ``double [link](file.md)`` and [real](page.md)");
  assert.equal(links.length, 1);
  assert.equal(links[0].href, "page.md");

  links = findMarkdownLinks("`` `` then [link](file.md)");
  assert.equal(links.length, 1);
  assert.equal(links[0].text, "link");
});

test("findMarkdownLinks handles unclosed and asymmetric backticks", () => {
  let links = findMarkdownLinks("Text ``unclosed [link](file.md)");
  assert.equal(links.length, 1);
  assert.equal(links[0].href, "file.md");

  // Double open, single close: not a code span, link is found.
  links = findMarkdownLinks("Text ``code` [link](file.md)");
  assert.equal(links.length, 1);

  links = findMarkdownLinks("`[link](file.md)");
  assert.equal(links.length, 1);

  assert.equal(findMarkdownLinks("text with unclosed ``").length, 0);
});

test("findMarkdownLinks handles brackets, parens, escapes, and no links", () => {
  let links = findMarkdownLinks("[[nested]](page.md)");
  assert.equal(links.length, 1);
  assert.equal(links[0].text, "[nested]");

  links = findMarkdownLinks("[link](page_(version).md)");
  assert.equal(links[0].href, "page_(version).md");

  assert.equal(findMarkdownLinks("No links here").length, 0);
  assert.equal(findMarkdownLinks("\\[not a link\\](file.md)").length, 0);
});

test("transformLine converts internal links and preserves external/anchor links", () => {
  assert.equal(transformLine("[Guide](guide.md)", "index.md"), "[[Guide]]");
  assert.equal(
    transformLine("[GitHub](https://github.com)", "index.md"),
    "[GitHub](https://github.com)"
  );
  assert.equal(transformLine("[Section](#section)", "index.md"), "[Section](#section)");
  assert.equal(transformLine("Just some text", "index.md"), "Just some text");
});

test("transformLine handles anchors, relative paths, and multiple links", () => {
  assert.equal(
    transformLine("[Topic](concepts/types.md#section)", "index.md"),
    "[[Concepts-Types#section|Topic]]"
  );
  assert.equal(transformLine("[Back](../index.md)", "concepts/types.md"), "[[..|Back]]");

  const result = transformLine("[One](a.md) and [Two](b.md)", "index.md");
  assert.ok(result.includes("[[A|One]]"));
  assert.ok(result.includes("[[B|Two]]"));
});

test("transformLine rewrites image paths but keeps external images", () => {
  assert.equal(
    transformLine("![Image](images/diagram.png)", "index.md"),
    "![Image](wiki-images/diagram.png)"
  );
  assert.equal(
    transformLine("![Badge](https://img.shields.io/badge.svg)", "index.md"),
    "![Badge](https://img.shields.io/badge.svg)"
  );
});

test("transformFile leaves links inside code blocks untouched", () => {
  const content = [
    "[before](a.md)",
    "",
    "```",
    "[inside1](b.md)",
    "```",
    "",
    "[between](c.md)",
    "",
    "```csharp",
    "[inside2](d.md)",
    "```",
    "",
    "[after](e.md)"
  ].join("\n");

  const result = transformFile(content, "index.md");
  assert.ok(result.includes("[[A|before]]"));
  assert.ok(result.includes("[inside1](b.md)"));
  assert.ok(result.includes("[[C|between]]"));
  assert.ok(result.includes("[inside2](d.md)"));
  assert.ok(result.includes("[[E|after]]"));
});

test("transformFile preserves line structure and handles edge inputs", () => {
  assert.equal(transformFile("Line 1\nLine 2\nLine 3", "index.md").split("\n").length, 3);
  assert.equal(transformFile("", "index.md"), "");

  const codeOnly = "```\ncode only\n```";
  assert.equal(transformFile(codeOnly, "index.md"), codeOnly);
});

test("transformFile normalizes lone CR line endings", () => {
  const result = transformFile("[Guide](guide.md)\r\r[Section](#keep-anchor)\r", "index.md");
  assert.ok(result.includes("[[Guide]]"));
  assert.ok(result.includes("[Section](#keep-anchor)"));
  assert.ok(result.includes("\n\n"));
  assert.ok(!result.includes("\r"));
});
