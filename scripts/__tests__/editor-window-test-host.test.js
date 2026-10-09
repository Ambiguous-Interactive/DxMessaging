"use strict";

const { execFileSync } = require("node:child_process");
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const TEST_HOST_PATH = "Tests/Editor/EditorWindowTestUtility.cs";

function hasRawWindowClose(content) {
  const transports = new Set(
    [...content.matchAll(/\b(?:TcpClient|HttpListener)\??\s+(\w+)\b/g)].map((match) => match[1])
  );
  const contexts = new Set(
    [...content.matchAll(/\bHttpListenerContext\??\s+(\w+)\b/g)].map((match) => match[1])
  );
  return [...content.matchAll(/(\w+(?:\??\.\w+)*)?\??\.Close\(\);/g)].some((match) => {
    const receiver = (match[1] || "").split(/\??\./);
    return (
      !transports.has(receiver.at(-1)) &&
      !(receiver.at(-1) === "Response" && contexts.has(receiver.at(-2)))
    );
  });
}

function extractMethodBody(content, methodName) {
  const signatureIndex = content.indexOf(` ${methodName}(`);
  assert.notEqual(signatureIndex, -1, `${methodName} must exist`);

  const openBraceIndex = content.indexOf("{", signatureIndex);
  assert.notEqual(openBraceIndex, -1, `${methodName} must have a body`);

  let depth = 0;
  for (let index = openBraceIndex; index < content.length; index += 1) {
    if (content[index] === "{") {
      depth += 1;
    } else if (content[index] === "}") {
      depth -= 1;
      if (depth === 0) {
        return content.slice(openBraceIndex + 1, index);
      }
    }
  }

  assert.fail(`${methodName} body is not closed`);
}

test("editor tests use the stable test host for shown windows", () => {
  const output = execFileSync(
    "git",
    ["ls-files", "--cached", "--others", "--exclude-standard", "--", "Tests/Editor"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8"
    }
  );
  const violations = [];

  for (const relativePath of output.split("\n").filter(Boolean)) {
    if (relativePath === TEST_HOST_PATH || !relativePath.endsWith(".cs")) {
      continue;
    }

    const content = fs.readFileSync(path.join(REPO_ROOT, relativePath), "utf8");
    if (content.includes("ScriptableObject.CreateInstance<EditorWindow>()")) {
      violations.push(`${relativePath}: use EditorWindowTestUtility.CreateWindow()`);
    }
    if (/\.Show\(\);/.test(content)) {
      violations.push(`${relativePath}: use EditorWindowTestUtility.ShowWindow(window)`);
    }
    if (hasRawWindowClose(content)) {
      violations.push(`${relativePath}: use EditorWindowTestUtility.CloseWindow(window)`);
    }
  }

  assert.deepEqual(
    violations,
    [],
    "Shown editor tests must use DxMessagingTestHostWindow with HideAndDontSave so Unity layouts do not persist generic EditorWindow entries as Failed to Load tabs."
  );
});

test("closing guard distinguishes windows from typed network transports", () => {
  for (const content of [
    "EditorWindow window; window.Close();",
    "DxMessagingMonitorWindow? monitor; monitor?.Close();",
    "var window = CreateWindow(); window.Close();",
    "windows[0].Close();",
    "GetWindow().Close();",
    "owner.Window.Close();",
    "Response.Close();",
    "owner.Context.Response.Close();",
    "TcpClient Client; Client.Close(); var window = CreateWindow(); window.Close();"
  ])
    assert.equal(hasRawWindowClose(content), true, content);
  for (const content of [
    "TcpClient Client; owner.Client.Close(); HttpListener Listener; Listener.Close();",
    "HttpListenerContext Context; owner.Context.Response.Close();"
  ])
    assert.equal(hasRawWindowClose(content), false, content);
});

test("tracked editor-window cleanup avoids the global Resources leak sweep", () => {
  const content = fs.readFileSync(path.join(REPO_ROOT, TEST_HOST_PATH), "utf8");
  const createWindowBody = extractMethodBody(content, "CreateWindow");
  const closeWindowBody = extractMethodBody(content, "CloseWindow");
  const closeTrackedWindowsBody = extractMethodBody(content, "CloseTrackedWindows");

  assert.match(
    content,
    /private static readonly List<EditorWindow> CreatedWindows = new\(\);/,
    "Created test host windows must be tracked centrally so teardown can close them without scanning every Unity object."
  );
  assert.match(
    createWindowBody,
    /CreatedWindows\.Add\(window\);/,
    "CreateWindow must register each host window for deterministic teardown."
  );
  assert.match(
    closeWindowBody,
    /CreatedWindows\.Remove\(window\);/,
    "CloseWindow must unregister closed host windows."
  );
  assert.doesNotMatch(
    closeTrackedWindowsBody,
    /CloseLeakedEditorWindows\(/,
    "Normal tracked-window teardown must not run the global Resources sweep; Unity 6000 can emit invalid-GC-handle asserts while scanning stale editor objects."
  );
});
