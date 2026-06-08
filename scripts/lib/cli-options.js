"use strict";

/**
 * @fileoverview Declarative command-line option parser shared by repo scripts.
 *
 * Most scripts hand-rolled the same `parseArgs(argv)` shape: a loop over argv
 * matching `--flag` booleans, `--option value` / `--option=value` strings,
 * repeated options accumulated into arrays, and bare positionals, with a
 * `requireValue` helper that errored when a value was missing. This module
 * replaces that pattern with one tested parser driven by a small spec, so
 * each script declares its options instead of re-implementing the loop.
 *
 * The parser is intentionally non-throwing: it collects human-readable
 * problems into `result.errors` and lets the caller decide whether to throw,
 * print help, or `process.exit`. A `--` token ends option parsing; everything
 * after it is positional. Scripts whose CLI semantics genuinely differ from
 * this model keep their bespoke parser rather than contorting the spec.
 *
 * @example
 *   const { parseArgs } = require("./lib/cli-options");
 *   const { values, positionals, errors } = parseArgs(process.argv.slice(2), {
 *     options: {
 *       check: { type: "boolean", aliases: ["--check"] },
 *       input: { type: "string", aliases: ["--input"], multiple: true },
 *       tolerance: { type: "number", aliases: ["--tolerance"], default: 0.05 },
 *       help: { type: "boolean", aliases: ["--help", "-h"] }
 *     },
 *     unknownOption: "error"
 *   });
 *   if (errors.length > 0) {
 *     throw new Error(errors[0]);
 *   }
 */

/**
 * Looks like an option token (`--name`, `-h`) rather than a value/positional.
 * A lone `-` is treated as a positional (the conventional stdin placeholder),
 * and a non-string (e.g. a hole in a sparse argv) is not an option.
 *
 * This also defines the value boundary: a token that looks like an option is
 * never consumed as the value of a preceding option. A value that legitimately
 * begins with `-` (a negative number, a `-`-prefixed string) must therefore be
 * supplied with the `--opt=value` form, e.g. `--tolerance=-0.5`.
 *
 * @param {unknown} token
 * @returns {boolean}
 */
function looksLikeOption(token) {
  return typeof token === "string" && token.length > 1 && token.startsWith("-");
}

/**
 * Coerce and transform a raw string value per an option's spec.
 *
 * `type: "number"` uses `Number()` (so hex `0x10` and scientific `1e3` parse,
 * and an empty or whitespace-only value is rejected rather than silently
 * becoming 0). A consumer that needs base-10 integer semantics should declare
 * `type: "string"` with `transform: (v) => Number.parseInt(v, 10)` instead. A
 * `transform` that throws is caught and reported as an error so the parser
 * keeps its non-throwing contract.
 *
 * @param {string} raw
 * @param {{ type?: string, transform?: (value: any) => any }} optionSpec
 * @param {string} alias The alias the user typed, for error messages.
 * @param {string[]} errors Collector for human-readable problems.
 * @returns {{ ok: boolean, value: any }}
 */
function coerceValue(raw, optionSpec, alias, errors) {
  let value = raw;
  if (optionSpec.type === "number") {
    if (raw.trim() === "") {
      errors.push(`${alias} expects a number but received an empty value`);
      return { ok: false, value: undefined };
    }
    const parsed = Number(raw);
    if (!Number.isFinite(parsed)) {
      errors.push(`${alias} expects a number but received "${raw}"`);
      return { ok: false, value: undefined };
    }
    value = parsed;
  }
  if (typeof optionSpec.transform === "function") {
    try {
      value = optionSpec.transform(value);
    } catch (error) {
      errors.push(`${alias}: ${error.message}`);
      return { ok: false, value: undefined };
    }
  }
  return { ok: true, value };
}

/**
 * Parse `argv` against a declarative spec.
 *
 * @param {string[]} argv Arguments to parse (typically `process.argv.slice(2)`
 *   or `process.argv` together with `startIndex: 2`).
 * @param {{
 *   options?: Record<string, {
 *     aliases: string[],
 *     type?: "boolean" | "string" | "number",
 *     multiple?: boolean,
 *     default?: any,
 *     transform?: (value: any) => any
 *   }>,
 *   allowEquals?: boolean,
 *   unknownOption?: "error" | "collect" | "ignore",
 *   startIndex?: number,
 *   endOfOptions?: boolean
 * }} [spec]
 *   `allowEquals` (default true) accepts `--opt=value`. `unknownOption`
 *   (default "error") chooses whether an unrecognized `--opt` is recorded as
 *   an error, collected as a positional, or ignored. `startIndex` (default 0)
 *   skips leading entries. `endOfOptions` (default true) treats a bare `--` as
 *   the end-of-options separator; set it false to make `--` an ordinary token
 *   (so a legacy parser that reported `--` as unknown can migrate faithfully).
 * @returns {{ values: Record<string, any>, positionals: string[], errors: string[] }}
 */
function parseArgs(argv, spec = {}) {
  const {
    options = {},
    allowEquals = true,
    unknownOption = "error",
    startIndex = 0,
    endOfOptions = true
  } = spec;

  /** @type {Record<string, string>} alias -> option key */
  const aliasToKey = {};
  for (const [key, optionSpec] of Object.entries(options)) {
    if (!optionSpec || !Array.isArray(optionSpec.aliases) || optionSpec.aliases.length === 0) {
      throw new Error(`cli-options: option "${key}" must declare a non-empty aliases array`);
    }
    if (optionSpec.type === "boolean" && optionSpec.multiple) {
      throw new Error(
        `cli-options: option "${key}" cannot be both type:"boolean" and multiple:true`
      );
    }
    for (const alias of optionSpec.aliases) {
      if (Object.prototype.hasOwnProperty.call(aliasToKey, alias)) {
        throw new Error(`cli-options: alias "${alias}" is declared by more than one option`);
      }
      aliasToKey[alias] = key;
    }
  }

  const values = {};
  for (const [key, optionSpec] of Object.entries(options)) {
    if (Object.prototype.hasOwnProperty.call(optionSpec, "default")) {
      values[key] = optionSpec.default;
    } else if (optionSpec.multiple) {
      values[key] = [];
    } else if (optionSpec.type === "boolean") {
      values[key] = false;
    } else {
      values[key] = undefined;
    }
  }

  const positionals = [];
  const errors = [];
  let sawSeparator = false;

  for (let index = startIndex; index < argv.length; index++) {
    const token = argv[index];

    if (endOfOptions && !sawSeparator && token === "--") {
      sawSeparator = true;
      continue;
    }

    if (sawSeparator || !looksLikeOption(token)) {
      positionals.push(token);
      continue;
    }

    let alias = token;
    let inlineValue = null;
    const equalsAt = token.indexOf("=");
    if (allowEquals && equalsAt !== -1) {
      alias = token.slice(0, equalsAt);
      inlineValue = token.slice(equalsAt + 1);
    }

    const key = aliasToKey[alias];
    if (key === undefined) {
      if (unknownOption === "collect") {
        positionals.push(token);
      } else if (unknownOption === "ignore") {
        // Intentionally skip.
      } else {
        errors.push(`Unknown argument: ${token}`);
      }
      continue;
    }

    const optionSpec = options[key];

    if (optionSpec.type === "boolean") {
      if (inlineValue !== null) {
        errors.push(`${alias} is a flag and does not take a value`);
        continue;
      }
      values[key] = true;
      continue;
    }

    let raw = inlineValue;
    if (raw === null) {
      const next = argv[index + 1];
      if (next === undefined || looksLikeOption(next)) {
        errors.push(`${alias} requires a value`);
        continue;
      }
      raw = next;
      index++;
    }

    const coerced = coerceValue(raw, optionSpec, alias, errors);
    if (!coerced.ok) {
      continue;
    }
    if (optionSpec.multiple) {
      values[key].push(coerced.value);
    } else {
      values[key] = coerced.value;
    }
  }

  return { values, positionals, errors };
}

module.exports = {
  parseArgs,
  looksLikeOption
};
