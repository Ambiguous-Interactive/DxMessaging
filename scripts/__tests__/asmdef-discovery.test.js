"use strict";

const { test, after } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {
  defaultIncludeAssemblies,
  resolveTestAssemblySelection
} = require("../unity/lib/asmdef-discovery");

const root = path.resolve(__dirname, "../..");
const prefix = "WallstopStudios.DxMessaging.Tests.";
const transport = prefix + "Editor.PipelineTransportResearch";
const integrations = ["Runtime.Reflex", "Runtime.VContainer", "Runtime.Zenject"];
const dependencyPath = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-pipeline-"));
fs.writeFileSync(
  path.join(dependencyPath, "package.json"),
  JSON.stringify({
    name: "com.unity.pipeline",
    version: "0.8.0-exp.1",
    repository: { revision: "dde7080264d32a1091171d4b358351279fa10ddb" }
  })
);
const admitted = { transportPackagePath: dependencyPath, unityVersion: "6000.4.6f1" };
after(() => fs.rmSync(dependencyPath, { recursive: true, force: true }));
for (const row of [
  { name: "default", options: {}, suffixes: ["Editor", "Runtime"], excluded: true },
  {
    name: "shipping with DI",
    options: { target: "editmode", includeIntegrations: true },
    suffixes: ["Editor", "Runtime", ...integrations],
    excluded: true
  },
  {
    name: "explicit transport",
    options: { target: "editmode", includeTransportControls: true, ...admitted },
    suffixes: ["Editor", "Editor.PipelineTransportResearch", "Runtime"],
    excluded: false
  },
  ...["playmode", "standalone"].map((target) => ({
    name: target,
    options: { target, includeIntegrations: true },
    suffixes: ["Runtime"],
    excluded: false
  })),
  {
    name: "legacy runtime-only",
    options: { runtimeOnly: true },
    suffixes: ["Runtime"],
    excluded: false
  }
]) {
  test("assembly discovery preserves the declared " + row.name + " scope", () => {
    const expected = row.suffixes.map((s) => prefix + s).sort();
    assert.deepEqual(defaultIncludeAssemblies(root, row.options), expected);
    const selection = resolveTestAssemblySelection(root, row.options);
    assert.deepEqual(selection.assemblies, expected);
    assert.deepEqual(selection.excludedTransportControls, row.excluded ? [transport] : []);
  });
}
for (const target of ["playmode", "standalone"]) {
  test("explicit transport refuses " + target, () => {
    assert.throws(
      () => defaultIncludeAssemblies(root, { target, includeTransportControls: true }),
      /Transport controls require the EditMode target/
    );
  });
}
test("explicit transport refuses an absent fixture", () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-transport-"));
  try {
    assert.throws(
      () => defaultIncludeAssemblies(empty, { includeTransportControls: true, ...admitted }),
      /Transport controls were requested but no compatible owned transport assembly exists/
    );
  } finally {
    fs.rmSync(empty, { recursive: true, force: true });
  }
});
test("transport selection refuses missing or mismatched host dependencies", () => {
  assert.throws(
    () => defaultIncludeAssemblies(root, { includeTransportControls: true }),
    /resolved Pipeline package path/
  );
  assert.throws(
    () =>
      defaultIncludeAssemblies(root, {
        includeTransportControls: true,
        ...admitted,
        unityVersion: "2021.3.45f1"
      }),
    /admitted Unity/
  );
  const manifestPath = path.join(dependencyPath, "package.json");
  const original = fs.readFileSync(manifestPath, "utf8");
  try {
    for (const override of [
      { name: "other.pipeline" },
      { version: "0.8.1" },
      { repository: { revision: "different-source" } }
    ]) {
      fs.writeFileSync(manifestPath, JSON.stringify({ ...JSON.parse(original), ...override }));
      assert.throws(
        () => defaultIncludeAssemblies(root, { includeTransportControls: true, ...admitted }),
        /admitted Unity/,
        JSON.stringify(override)
      );
    }
  } finally {
    fs.writeFileSync(manifestPath, original);
  }
});
