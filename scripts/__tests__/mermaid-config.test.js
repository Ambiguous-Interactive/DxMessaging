/**
 * @fileoverview Tests for mermaid-config.js init directive stripping logic.
 *
 * These tests validate the INIT_DIRECTIVE_PATTERN regex and stripInitDirectives function
 * used to remove per-diagram init directives that would override theme configuration.
 * The pattern must handle various edge cases including:
 * - Leading whitespace (spaces and tabs)
 * - Different line ending formats (CRLF vs LF)
 * - Directives at different positions in the file
 * - Multi-line directives
 */

"use strict";

/**
 * Pattern to match Mermaid init directives that should be stripped.
 *
 * SYNC: Keep pattern in sync with docs/javascripts/mermaid-config.js INIT_DIRECTIVE_PATTERN
 *
 * Regex flags:
 * - 'g' (global): Matches all occurrences
 * - 'i' (case-insensitive): %%{init:...}%% and %%{INIT:...}%% both match
 * - 'm' (multiline): ^ matches start of any line
 * - 's' (dotAll): . matches any character including newlines
 *
 * Uses [ \t]* instead of \s* around the directive to avoid consuming newlines,
 * which would concatenate adjacent diagram lines and break Mermaid syntax.
 */
const INIT_DIRECTIVE_PATTERN = /^[ \t]*%%\{init:.*?\}%%[ \t]*\r?\n?/gims;

/**
 * Strip per-diagram init directives that would override theme configuration.
 *
 * SYNC: Keep logic in sync with docs/javascripts/mermaid-config.js stripInitDirectives
 *
 * @param {string} source - The original Mermaid diagram source
 * @returns {string} The source with init directives removed
 */
function stripInitDirectives(source) {
  return source.replace(INIT_DIRECTIVE_PATTERN, "");
}

/**
 * Helper to test if a pattern matches an input.
 * Creates a fresh regex to avoid global state issues with lastIndex.
 *
 * Derives the pattern from INIT_DIRECTIVE_PATTERN.source to avoid duplicating
 * the regex literal, ensuring tests always validate the same pattern used by
 * stripInitDirectives().
 *
 * @param {string} input - The input string to test
 * @returns {boolean} Whether the pattern matches
 */
function testPattern(input) {
  // Create fresh regex from INIT_DIRECTIVE_PATTERN to avoid lastIndex state issues
  // and ensure we're testing the exact same pattern used by stripInitDirectives()
  const pattern = new RegExp(INIT_DIRECTIVE_PATTERN.source, INIT_DIRECTIVE_PATTERN.flags);
  return pattern.test(input);
}

describe("INIT_DIRECTIVE_PATTERN", () => {
  describe("basic directive matching", () => {
    test("should match simple init directive", () => {
      const input = '%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with empty config", () => {
      const input = "%%{init: {}}%%";
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with complex theme variables", () => {
      const input = '%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#ff0000"}}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should not match non-init directives", () => {
      const input = "%%{config: {}}%%";
      expect(testPattern(input)).toBe(false);
    });

    test("should not match regular Mermaid comments", () => {
      const input = "%% This is a comment";
      expect(testPattern(input)).toBe(false);
    });

    test("should not match diagram content without directives", () => {
      const input = "graph TD\n  A --> B";
      expect(testPattern(input)).toBe(false);
    });
  });

  describe("case insensitivity", () => {
    test("should match lowercase init", () => {
      const input = '%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match uppercase INIT", () => {
      const input = '%%{INIT: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match mixed case Init", () => {
      const input = '%%{Init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match iNiT with any case variation", () => {
      const input = '%%{iNiT: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });
  });

  describe("leading whitespace handling", () => {
    test("should match directive with leading spaces", () => {
      const input = '    %%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with leading tab", () => {
      const input = '\t%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with multiple leading tabs", () => {
      const input = '\t\t\t%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with mixed spaces and tabs", () => {
      const input = '  \t  \t%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with many leading spaces", () => {
      const input = '                %%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });
  });

  describe("line ending handling", () => {
    test("should match directive followed by LF", () => {
      const input = '%%{init: {"theme": "dark"}}%%\n';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive followed by CRLF", () => {
      const input = '%%{init: {"theme": "dark"}}%%\r\n';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with no trailing newline", () => {
      const input = '%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with trailing spaces before LF", () => {
      const input = '%%{init: {"theme": "dark"}}%%   \n';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with trailing spaces before CRLF", () => {
      const input = '%%{init: {"theme": "dark"}}%%   \r\n';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive with trailing tab before newline", () => {
      const input = '%%{init: {"theme": "dark"}}%%\t\n';
      expect(testPattern(input)).toBe(true);
    });
  });

  describe("position in content", () => {
    test("should match directive at start of file", () => {
      const input = '%%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive in middle of file with LF", () => {
      const input = 'graph TD\n%%{init: {"theme": "dark"}}%%\n  A --> B';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive at end of file", () => {
      const input = 'graph TD\n  A --> B\n%%{init: {"theme": "dark"}}%%';
      expect(testPattern(input)).toBe(true);
    });

    test("should match directive after empty line", () => {
      const input = 'graph TD\n\n%%{init: {"theme": "dark"}}%%\n  A --> B';
      expect(testPattern(input)).toBe(true);
    });
  });
});

describe("stripInitDirectives", () => {
  beforeEach(() => {
    // Reset the regex lastIndex before each test since it's global
    INIT_DIRECTIVE_PATTERN.lastIndex = 0;
  });

  describe("single directive removal", () => {
    test("should remove simple init directive", () => {
      const input = '%%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove directive with leading spaces", () => {
      const input = '    %%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove directive with leading tabs", () => {
      const input = '\t%%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove directive with CRLF line ending", () => {
      const input = '%%{init: {"theme": "dark"}}%%\r\ngraph TD\r\n  A --> B';
      const expected = "graph TD\r\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove directive with LF line ending", () => {
      const input = '%%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle directive with no trailing newline", () => {
      const input = '%%{init: {"theme": "dark"}}%%';
      const expected = "";
      expect(stripInitDirectives(input)).toBe(expected);
    });
  });

  describe("multiple directive removal", () => {
    test("should remove multiple init directives with LF", () => {
      const input =
        '%%{init: {"theme": "dark"}}%%\ngraph TD\n%%{init: {"theme": "light"}}%%\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove multiple init directives with CRLF", () => {
      const input =
        '%%{init: {"theme": "dark"}}%%\r\ngraph TD\r\n%%{init: {"theme": "light"}}%%\r\n  A --> B';
      const expected = "graph TD\r\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove three consecutive directives", () => {
      const input = '%%{init: {"a": 1}}%%\n%%{init: {"b": 2}}%%\n%%{init: {"c": 3}}%%\ngraph TD';
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should remove directives at various positions", () => {
      const input =
        '%%{init: {"start": true}}%%\nsequenceDiagram\n  %%{init: {"mid": true}}%%\n  Alice->>Bob: Hello\n%%{init: {"end": true}}%%';
      const expected = "sequenceDiagram\n  Alice->>Bob: Hello\n";
      expect(stripInitDirectives(input)).toBe(expected);
    });
  });

  describe("preserving surrounding content", () => {
    test("should preserve content before directive", () => {
      const input = 'graph TD\n%%{init: {"theme": "dark"}}%%\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should preserve content after directive", () => {
      const input = '%%{init: {"theme": "dark"}}%%\ngraph TD\n  A --> B\n  B --> C';
      const expected = "graph TD\n  A --> B\n  B --> C";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should preserve empty lines around directive", () => {
      const input = 'graph TD\n\n%%{init: {"theme": "dark"}}%%\n\n  A --> B';
      const expected = "graph TD\n\n\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should preserve regular Mermaid comments", () => {
      const input = '%%{init: {"theme": "dark"}}%%\ngraph TD\n  %% This is a comment\n  A --> B';
      const expected = "graph TD\n  %% This is a comment\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should preserve indentation of following content", () => {
      const input = '    %%{init: {"theme": "dark"}}%%\n    A --> B';
      const expected = "    A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should not consume extra newlines", () => {
      const input = "%%{init: {}}%%\n\ngraph TD";
      const expected = "\ngraph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });
  });

  describe("edge cases", () => {
    test("should handle empty input", () => {
      const input = "";
      const expected = "";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle input with only whitespace", () => {
      const input = "   \n\t\n   ";
      const expected = "   \n\t\n   ";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle input with no init directives", () => {
      const input = "graph TD\n  A --> B\n  B --> C";
      const expected = "graph TD\n  A --> B\n  B --> C";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle directive with very long config", () => {
      const longConfig = JSON.stringify({
        theme: "base",
        themeVariables: {
          primaryColor: "#ff0000",
          secondaryColor: "#00ff00",
          tertiaryColor: "#0000ff",
          fontFamily: "Arial, sans-serif",
          fontSize: "16px"
        }
      });
      const input = `%%{init: ${longConfig}}%%\ngraph TD`;
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle directive only (no other content)", () => {
      const input = '%%{init: {"theme": "dark"}}%%\n';
      const expected = "";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle mixed CRLF and LF line endings", () => {
      const input = '%%{init: {"a": 1}}%%\r\ngraph TD\n%%{init: {"b": 2}}%%\n  A --> B';
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle directive with special characters in config", () => {
      const input = '%%{init: {"note": "Hello\\nWorld"}}%%\ngraph TD';
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle consecutive spaces in config", () => {
      const input = '%%{init:    {    "theme"   :   "dark"   }    }%%\ngraph TD';
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });
  });

  describe("multi-line directive handling", () => {
    test("should handle directive split across lines", () => {
      const input = `%%{init: {
  "theme": "dark",
  "themeVariables": {
    "primaryColor": "#ff0000"
  }
}}%%
graph TD`;
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle directive with CRLF in middle", () => {
      const input = '%%{init: {\r\n  "theme": "dark"\r\n}}%%\r\ngraph TD';
      const expected = "graph TD";
      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle multiple multi-line directives", () => {
      const input = `%%{init: {
  "a": 1
}}%%
graph TD
%%{init: {
  "b": 2
}}%%
  A --> B`;
      const expected = "graph TD\n  A --> B";
      expect(stripInitDirectives(input)).toBe(expected);
    });
  });

  describe("real-world examples", () => {
    test("should handle typical sequence diagram with theme override", () => {
      const input = `%%{init: {"theme": "dark", "themeVariables": {"actorBkg": "#2d2d2d"}}}%%
sequenceDiagram
    participant A as Alice
    participant B as Bob
    A->>B: Hello Bob!
    B-->>A: Hi Alice!`;

      const expected = `sequenceDiagram
    participant A as Alice
    participant B as Bob
    A->>B: Hello Bob!
    B-->>A: Hi Alice!`;

      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle flowchart with init directive in code block context", () => {
      const input = `%%{init: {"flowchart": {"htmlLabels": true}}}%%
graph LR
    A[Start] --> B{Decision}
    B -->|Yes| C[End]
    B -->|No| D[Retry]`;

      const expected = `graph LR
    A[Start] --> B{Decision}
    B -->|Yes| C[End]
    B -->|No| D[Retry]`;

      expect(stripInitDirectives(input)).toBe(expected);
    });

    test("should handle class diagram with configuration", () => {
      const input = `%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#409EFF"}}}%%
classDiagram
    class Animal {
        +name: string
        +age: int
        +makeSound()
    }
    class Dog {
        +breed: string
        +bark()
    }
    Animal <|-- Dog`;

      const expected = `classDiagram
    class Animal {
        +name: string
        +age: int
        +makeSound()
    }
    class Dog {
        +breed: string
        +bark()
    }
    Animal <|-- Dog`;

      expect(stripInitDirectives(input)).toBe(expected);
    });
  });
});
