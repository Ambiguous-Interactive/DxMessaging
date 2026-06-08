/**
 * @fileoverview Unit tests for scripts/lib/cli-options.js.
 *
 * Pins the behavior of the shared declarative argument parser that replaces
 * the hand-rolled `parseArgs` loops across the repo's scripts: boolean flags,
 * string/number values (space- and equals-separated), repeated options,
 * defaults, positionals, the `--` separator, and unknown-option policy.
 */

"use strict";

const { parseArgs, looksLikeOption } = require("../cli-options");

describe("looksLikeOption", () => {
  test("treats --name and -h as options", () => {
    expect(looksLikeOption("--name")).toBe(true);
    expect(looksLikeOption("-h")).toBe(true);
  });

  test("treats a lone dash and bare words as non-options", () => {
    expect(looksLikeOption("-")).toBe(false);
    expect(looksLikeOption("file.txt")).toBe(false);
    expect(looksLikeOption("")).toBe(false);
  });

  test("treats non-string input (e.g. a sparse-argv hole) as a non-option", () => {
    expect(looksLikeOption(undefined)).toBe(false);
    expect(looksLikeOption(null)).toBe(false);
  });
});

describe("dash-prefixed value boundary", () => {
  const spec = {
    options: { tolerance: { type: "number", aliases: ["--tolerance"] } }
  };

  test("a space-separated value that begins with '-' is rejected (treated as an option)", () => {
    // Intentional, pinned behavior: values beginning with '-' must use the
    // equals form. The space-separated negative is reported as a missing value
    // and the dash token is then an unknown option.
    expect(parseArgs(["--tolerance", "-5"], spec).errors).toEqual([
      "--tolerance requires a value",
      "Unknown argument: -5"
    ]);
  });

  test("the equals form accepts a value that begins with '-'", () => {
    expect(parseArgs(["--tolerance=-0.5"], spec).values.tolerance).toBe(-0.5);
  });
});

describe("boolean flags", () => {
  const spec = {
    options: {
      check: { type: "boolean", aliases: ["--check"] },
      help: { type: "boolean", aliases: ["--help", "-h"] }
    }
  };

  test("default to false when absent", () => {
    const { values } = parseArgs([], spec);
    expect(values).toEqual({ check: false, help: false });
  });

  test("become true when present (any alias)", () => {
    expect(parseArgs(["--check"], spec).values.check).toBe(true);
    expect(parseArgs(["-h"], spec).values.help).toBe(true);
    expect(parseArgs(["--help"], spec).values.help).toBe(true);
  });

  test("reject an inline value", () => {
    const { errors } = parseArgs(["--check=yes"], spec);
    expect(errors).toEqual(["--check is a flag and does not take a value"]);
  });
});

describe("string options", () => {
  const spec = {
    options: {
      output: { type: "string", aliases: ["--output", "-o"] }
    }
  };

  test("consume the next token as the value", () => {
    expect(parseArgs(["--output", "dist"], spec).values.output).toBe("dist");
  });

  test("accept --opt=value form", () => {
    expect(parseArgs(["--output=dist"], spec).values.output).toBe("dist");
  });

  test("accept an empty --opt= value", () => {
    expect(parseArgs(["--output="], spec).values.output).toBe("");
  });

  test("default to undefined when absent and no default given", () => {
    expect(parseArgs([], spec).values.output).toBeUndefined();
  });

  test("error when the value is missing at end of argv", () => {
    expect(parseArgs(["--output"], spec).errors).toEqual(["--output requires a value"]);
  });

  test("error when the next token looks like another option", () => {
    // The missing-value token is not consumed, so the following option-like
    // token is then parsed on its own and reported as unknown. Callers that
    // throw on errors[0] still surface the missing-value problem first.
    expect(parseArgs(["--output", "--other"], spec).errors).toEqual([
      "--output requires a value",
      "Unknown argument: --other"
    ]);
  });
});

describe("number options", () => {
  const spec = {
    options: {
      tolerance: { type: "number", aliases: ["--tolerance"], default: 0.05 }
    }
  };

  test("parse a numeric value", () => {
    expect(parseArgs(["--tolerance", "0.2"], spec).values.tolerance).toBe(0.2);
  });

  test("keep the declared default when absent", () => {
    expect(parseArgs([], spec).values.tolerance).toBe(0.05);
  });

  test("error on a non-numeric value", () => {
    const { errors } = parseArgs(["--tolerance", "abc"], spec);
    expect(errors).toEqual(['--tolerance expects a number but received "abc"']);
  });

  test("error on an empty (--opt=) or whitespace-only value instead of silently yielding 0", () => {
    expect(parseArgs(["--tolerance="], spec).errors).toEqual([
      "--tolerance expects a number but received an empty value"
    ]);
    expect(parseArgs(["--tolerance", " "], spec).errors).toEqual([
      "--tolerance expects a number but received an empty value"
    ]);
  });

  test("uses Number() semantics (hex and scientific notation parse)", () => {
    // Pinned so a future reader knows this is intentional; integer/base-10
    // callers should use type:"string" + a parseInt transform instead.
    expect(parseArgs(["--tolerance=0x10"], spec).values.tolerance).toBe(16);
    expect(parseArgs(["--tolerance=1e3"], spec).values.tolerance).toBe(1000);
  });
});

describe("transform", () => {
  test("maps the coerced value", () => {
    const spec = {
      options: {
        name: { type: "string", aliases: ["--name"], transform: (v) => v.toUpperCase() }
      }
    };
    expect(parseArgs(["--name", "foo"], spec).values.name).toBe("FOO");
  });

  test("runs after numeric coercion", () => {
    const spec = {
      options: {
        count: { type: "number", aliases: ["--count"], transform: (v) => v * 2 }
      }
    };
    expect(parseArgs(["--count", "3"], spec).values.count).toBe(6);
  });

  test("a throwing transform is reported as an error, not propagated", () => {
    const spec = {
      options: {
        name: {
          type: "string",
          aliases: ["--name"],
          transform: () => {
            throw new Error("bad value");
          }
        }
      }
    };
    let result;
    expect(() => {
      result = parseArgs(["--name", "x"], spec);
    }).not.toThrow();
    expect(result.errors).toEqual(["--name: bad value"]);
    expect(result.values.name).toBeUndefined();
  });
});

describe("multiple (repeatable) options", () => {
  const spec = {
    options: {
      input: { type: "string", aliases: ["--input"], multiple: true }
    }
  };

  test("default to an empty array", () => {
    expect(parseArgs([], spec).values.input).toEqual([]);
  });

  test("accumulate repeated values in order", () => {
    expect(parseArgs(["--input", "a", "--input=b"], spec).values.input).toEqual(["a", "b"]);
  });
});

describe("positionals", () => {
  const spec = {
    options: { verbose: { type: "boolean", aliases: ["--verbose"] } }
  };

  test("collect bare arguments", () => {
    const { positionals, values } = parseArgs(["a.txt", "--verbose", "b.txt"], spec);
    expect(positionals).toEqual(["a.txt", "b.txt"]);
    expect(values.verbose).toBe(true);
  });

  test("a lone dash is positional", () => {
    expect(parseArgs(["-"], spec).positionals).toEqual(["-"]);
  });

  test("-- ends option parsing; later option-like tokens are positional", () => {
    const { positionals, values } = parseArgs(["--verbose", "--", "--not-a-flag", "x"], spec);
    expect(values.verbose).toBe(true);
    expect(positionals).toEqual(["--not-a-flag", "x"]);
  });

  test("endOfOptions:false makes -- an ordinary token (legacy faithfulness)", () => {
    // With the separator disabled, `--` flows through as an option-like token
    // and is reported by the unknown-option policy, letting a legacy parser
    // that treated `--` as an error/positional migrate without divergence.
    const errorResult = parseArgs(["--"], { ...spec, endOfOptions: false });
    expect(errorResult.errors).toEqual(["Unknown argument: --"]);
    expect(errorResult.positionals).toEqual([]);

    const collectResult = parseArgs(["--", "x"], {
      ...spec,
      endOfOptions: false,
      unknownOption: "collect"
    });
    expect(collectResult.positionals).toEqual(["--", "x"]);
    expect(collectResult.errors).toEqual([]);
  });
});

describe("unknownOption policy", () => {
  const spec = { options: { keep: { type: "boolean", aliases: ["--keep"] } } };

  test("error (default) records the unknown option", () => {
    expect(parseArgs(["--nope"], spec).errors).toEqual(["Unknown argument: --nope"]);
  });

  test("collect treats it as a positional", () => {
    const { positionals, errors } = parseArgs(["--nope"], { ...spec, unknownOption: "collect" });
    expect(positionals).toEqual(["--nope"]);
    expect(errors).toEqual([]);
  });

  test("ignore drops it silently", () => {
    const { positionals, errors } = parseArgs(["--nope"], { ...spec, unknownOption: "ignore" });
    expect(positionals).toEqual([]);
    expect(errors).toEqual([]);
  });
});

describe("allowEquals", () => {
  test("when false, --opt=value is an unknown option", () => {
    const spec = {
      options: { output: { type: "string", aliases: ["--output"] } },
      allowEquals: false
    };
    expect(parseArgs(["--output=x"], spec).errors).toEqual(["Unknown argument: --output=x"]);
  });
});

describe("startIndex", () => {
  test("skips leading entries (e.g. node + script path)", () => {
    const argv = ["/usr/bin/node", "script.js", "--keep"];
    const spec = { options: { keep: { type: "boolean", aliases: ["--keep"] } }, startIndex: 2 };
    expect(parseArgs(argv, spec).values.keep).toBe(true);
  });
});

describe("explicit defaults", () => {
  test("honor a declared default for a string option", () => {
    const spec = { options: { scope: { type: "string", aliases: ["--scope"], default: "all" } } };
    expect(parseArgs([], spec).values.scope).toBe("all");
    expect(parseArgs(["--scope", "one"], spec).values.scope).toBe("one");
  });
});

describe("spec validation", () => {
  test("throws when an option declares no aliases", () => {
    expect(() => parseArgs([], { options: { x: { type: "boolean" } } })).toThrow(
      /must declare a non-empty aliases array/
    );
  });

  test("throws when two options share an alias", () => {
    const spec = {
      options: {
        a: { type: "boolean", aliases: ["--shared"] },
        b: { type: "boolean", aliases: ["--shared"] }
      }
    };
    expect(() => parseArgs([], spec)).toThrow(/declared by more than one option/);
  });

  test("throws when an option is both boolean and multiple", () => {
    const spec = {
      options: { v: { type: "boolean", aliases: ["--v"], multiple: true } }
    };
    expect(() => parseArgs([], spec)).toThrow(/cannot be both type:"boolean" and multiple:true/);
  });
});

describe("realistic combined spec", () => {
  test("parses a mix of flags, values, repeats, and positionals", () => {
    const spec = {
      options: {
        check: { type: "boolean", aliases: ["--check"] },
        input: { type: "string", aliases: ["--input"], multiple: true },
        threshold: { type: "number", aliases: ["--threshold"], default: 1 },
        help: { type: "boolean", aliases: ["--help", "-h"] }
      }
    };
    const argv = ["--check", "--input", "a", "--input=b", "--threshold", "0.5", "file.cs"];
    const { values, positionals, errors } = parseArgs(argv, spec);
    expect(errors).toEqual([]);
    expect(values.check).toBe(true);
    expect(values.input).toEqual(["a", "b"]);
    expect(values.threshold).toBe(0.5);
    expect(values.help).toBe(false);
    expect(positionals).toEqual(["file.cs"]);
  });
});
