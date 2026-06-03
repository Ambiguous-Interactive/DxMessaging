/**
 * @fileoverview Fast regression guard for MessageExtensions dispatch helpers.
 *
 * Struct messages cannot be the message interface type itself, so a generic
 * `where TMessage : struct, IFooMessage` overload must not carry an interface
 * equality branch such as `typeof(TMessage) == typeof(IFooMessage)`. That branch
 * is dead code and made an earlier broadcast helper typo easy to miss.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const MESSAGE_EXTENSIONS = path.join(
  REPO_ROOT,
  "Runtime",
  "Core",
  "Extensions",
  "MessageExtensions.cs"
);

function readMessageExtensions() {
  return fs.readFileSync(MESSAGE_EXTENSIONS, "utf8");
}

function findGenericEmitMethods(source) {
  const lines = source.split(/\r\n|\r|\n/);
  const methods = [];

  for (let i = 0; i < lines.length; i++) {
    const declaration = /^\s*public static void\s+(\w+)<TMessage>\s*\(/.exec(lines[i]);
    if (!declaration) {
      continue;
    }

    const startLine = i + 1;
    const headerLines = [];
    let cursor = i;
    for (; cursor < lines.length; cursor++) {
      headerLines.push(lines[cursor]);
      if (/^\s*\{\s*$/.test(lines[cursor])) {
        break;
      }
    }

    let depth = 0;
    const bodyLines = [];
    for (; cursor < lines.length; cursor++) {
      const line = lines[cursor];
      bodyLines.push(line);
      for (const char of line) {
        if (char === "{") {
          depth++;
        } else if (char === "}") {
          depth--;
        }
      }
      if (depth === 0) {
        break;
      }
    }

    const header = headerLines.join("\n");
    const constraint = /where TMessage : (class|struct),\s*(I\w+Message)/.exec(header);
    if (constraint) {
      methods.push({
        name: declaration[1],
        startLine,
        kind: constraint[1],
        constraint: constraint[2],
        body: bodyLines.join("\n")
      });
    }
  }

  return methods;
}

describe("MessageExtensions generic dispatch policy", () => {
  const methods = findGenericEmitMethods(readMessageExtensions());
  const structMethods = methods.filter((method) => method.kind === "struct");
  const broadcastMethods = methods.filter((method) => method.constraint === "IBroadcastMessage");

  test("the scanner finds the expected struct emit overloads", () => {
    expect(structMethods.map((method) => method.name)).toEqual(
      expect.arrayContaining([
        "EmitTargeted",
        "Emit",
        "EmitFrom",
        "EmitBroadcast"
      ])
    );
    expect(structMethods.length).toBeGreaterThanOrEqual(6);
  });

  test.each(structMethods)(
    "$name at line $startLine dispatches struct messages through the typed by-ref path",
    (method) => {
      expect(method.body).not.toMatch(/typeof\(TMessage\)\s*==\s*typeof\(I\w+Message\)/);
    }
  );

  test.each(broadcastMethods)(
    "$name at line $startLine never checks broadcast messages against the targeted interface",
    (method) => {
      expect(method.body).not.toContain("typeof(TMessage) == typeof(ITargetedMessage)");
    }
  );
});
