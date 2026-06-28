(function () {
  "use strict";

  var MAX_LOOKAHEAD = 50;

  function isPascalCase(text) {
    return /^[A-Z][a-zA-Z0-9]*$/.test(text);
  }

  function isCamelCase(text) {
    return /^[a-z][a-zA-Z0-9]*$/.test(text);
  }

  function getClassList(element) {
    if (element && element.classList) {
      return Array.from(element.classList);
    }
    return [];
  }

  function getNextNonWhitespace(element) {
    return getSiblingNonWhitespace(element, "nextElementSibling");
  }

  function getPrevNonWhitespace(element) {
    return getSiblingNonWhitespace(element, "previousElementSibling");
  }

  function getSiblingNonWhitespace(element, property) {
    var sibling = element[property];
    while (sibling && sibling.classList && sibling.classList.contains("w")) {
      sibling = sibling[property];
    }
    return sibling;
  }

  function classifyNameToken(span) {
    var text = span.textContent.trim();
    if (!text) return null;

    var next = getNextNonWhitespace(span);
    var prev = getPrevNonWhitespace(span);
    var nextText = next ? next.textContent.trim() : "";
    var prevText = prev ? prev.textContent.trim() : "";
    var nextClass = getClassList(next);
    var prevClass = getClassList(prev);

    if (nextClass.includes("p") && nextText === "(") {
      return "n-method";
    }

    if (nextClass.includes("o") && nextText.startsWith("<")) {
      var lookahead = next;
      var depth = 0;
      var iterations = 0;

      while (lookahead && iterations < MAX_LOOKAHEAD) {
        iterations++;
        var t = lookahead.textContent.trim();

        if (t === "<") depth++;
        if (t === ">") depth--;

        if (depth === 0 && t === "(") {
          return "n-method";
        }

        if (depth < 0) break;

        lookahead = lookahead.nextElementSibling;

        var lookaheadClass = getClassList(lookahead);
        if (lookaheadClass.includes("c1") || lookaheadClass.includes("cm")) {
          break;
        }
      }
      return "n-type";
    }

    if (
      [",", ")", "="].includes(nextText) &&
      (prevClass.includes("n") || [">", "]", "?"].includes(prevText)) &&
      isCamelCase(text)
    ) {
      return "n-param";
    }

    if (
      prevClass.includes("k") &&
      (prevText === "out" || prevText === "ref" || prevText === "in") &&
      isCamelCase(text)
    ) {
      return "n-param";
    }

    if (isPascalCase(text)) {
      if (prevClass.includes("k") || prevClass.includes("kt")) {
        return "n-type";
      }

      if (!prev) {
        return "n-type";
      }

      if (prevText === "(" || prevText === "<" || prevText === ",") {
        return "n-type";
      }

      if (prevText === ".") {
        return "n-type";
      }

      return "n-type";
    }

    if (isCamelCase(text)) {
      return "n-var";
    }

    return null;
  }

  function enhanceCSharpHighlighting() {
    var codeBlocks = document.querySelectorAll(
      ".language-csharp.highlight code, .highlight.language-csharp code"
    );

    codeBlocks.forEach(function (codeBlock) {
      if (codeBlock.hasAttribute("data-semantic-enhanced")) {
        return;
      }
      codeBlock.setAttribute("data-semantic-enhanced", "true");

      var nameSpans = codeBlock.querySelectorAll("span.n");

      nameSpans.forEach(function (span) {
        var semanticClass = classifyNameToken(span);
        if (semanticClass) {
          span.classList.add(semanticClass);
        }
      });
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", enhanceCSharpHighlighting);
  } else {
    enhanceCSharpHighlighting();
  }

  if (typeof document$ !== "undefined") {
    document$.subscribe(function () {
      enhanceCSharpHighlighting();
    });
  }
})();
