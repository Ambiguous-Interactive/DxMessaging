#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const { createRequire } = require("module");
const { pathToFileURL } = require("url");
const { resolveBundledNpxCliPath } = require("./lib/managed-prettier");
const { INTEGRITY_TARGETS, probeIntegrity } = require("./lib/node-modules-integrity");
const { isOutsideRelative } = require("./lib/path-classifier");
const {
  probeDependencyVersionParity,
  formatDriftLines
} = require("./lib/dependency-version-parity");

const REPO_ROOT = path.resolve(__dirname, "..");
const REPO_REQUIRE = createRequire(path.join(REPO_ROOT, "package.json"));
const SCRIPTS_DIR = path.join(REPO_ROOT, "scripts");

const UNSAFE_DIRECT_NPX_PATTERNS = [
  /\b(?:childProcess\.)?(?:spawnSync|execSync|execFileSync)\s*\(\s*(["'`])npx(?:\.cmd)?\1/,
  /\bspawnPlatformCommandSync\s*\(\s*(["'`])npx(?:\.cmd)?\1/,
  /\b\w*spawnSyncImpl\s*\(\s*(["'`])npx(?:\.cmd)?\1/
];

const UNSAFE_PROCESS_INVOKER_NAMES = [
  "spawnSync",
  "execSync",
  "execFileSync",
  "spawnPlatformCommandSync"
];

const NPX_COMMAND_ASSIGNMENT_RE =
  /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(["'`])npx(?:\.cmd)?\2/g;
const PROCESS_INVOKER_ALIAS_ASSIGNMENT_RE =
  /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:(?:childProcess\.)?(?:spawnSync|execSync|execFileSync|spawnPlatformCommandSync)|\w*spawnSyncImpl)\b/g;
const CHILD_PROCESS_DESTRUCTURE_RE = /\b(?:const|let|var)\s*\{([^}]+)\}\s*=\s*childProcess\b/g;

const MANAGED_NPX_POLICY_GUIDANCE =
  "Use scripts/lib/managed-prettier.js runBundledNpxCommand() for PATH-independent npm CLI invocation.";
const MANAGED_NPX_CLI_GUIDANCE =
  "Managed npx fallback requires npm's bundled npx-cli.js next to the active Node runtime.";

// TOOL_SPECS preserves the load-time loadability checks (require/import/
// resolve) that validate-node-tooling has always performed. The file
// existence / non-zero-size layer was previously duplicated here; it now
// lives in scripts/lib/node-modules-integrity.js#INTEGRITY_TARGETS and
// validateTooling iterates that list separately. `requiredFiles` is kept on
// TOOL_SPECS for backward compatibility with consumers (e.g. doctor.js) but
// reflects the same source-of-truth list as INTEGRITY_TARGETS via the
// helper below.
const TOOL_SPECS = [
  {
    name: "prettier",
    requiredFiles: ["node_modules/prettier/index.cjs", "node_modules/prettier/bin/prettier.cjs"],
    load: "require",
    entry: "node_modules/prettier/index.cjs"
  },
  {
    name: "markdownlint-cli2",
    requiredFiles: ["node_modules/markdownlint-cli2/markdownlint-cli2.mjs"],
    load: "import",
    entry: "node_modules/markdownlint-cli2/markdownlint-cli2.mjs"
  },
  {
    name: "cspell",
    requiredFiles: [
      "node_modules/cspell/bin.mjs",
      "node_modules/cspell/dist/esm/app.js",
      "node_modules/cspell-lib/dist/index.js"
    ]
  },
  {
    name: "jest",
    requiredFiles: ["node_modules/jest/bin/jest.js"]
  },
  {
    name: "jest-circus",
    requiredFiles: [],
    load: "resolve",
    entry: "jest-circus/runner"
  }
];

function toAbs(repoRelativePath) {
  return path.join(REPO_ROOT, ...repoRelativePath.split("/"));
}

function toRepoRelative(absPath) {
  const rel = path.relative(REPO_ROOT, absPath);
  // Cross-drive-safe (see scripts/lib/path-classifier.js): `isOutsideRelative`
  // also catches the absolute target `path.relative` returns on Windows when
  // `absPath` is on a different drive than REPO_ROOT.
  return isOutsideRelative(rel) ? absPath.split(path.sep).join("/") : rel.split(path.sep).join("/");
}

function formatInstallGuidance() {
  return [
    "Repair local Node tooling before running hooks:",
    "  npm install",
    "If package-lock.json is present and should be authoritative in this workspace, use:",
    "  npm ci",
    "Then re-validate the pre-push environment end-to-end:",
    "  npm run preflight:pre-push",
    "Skill reference: .llm/skills/scripting/jest-hook-robustness.md"
  ].join("\n");
}

async function importToolEntry(absPath) {
  return import(pathToFileURL(absPath).href);
}

function resolveRepoModule(
  moduleSpecifier,
  moduleResolver = REPO_REQUIRE.resolve.bind(REPO_REQUIRE)
) {
  try {
    return moduleResolver(moduleSpecifier);
  } catch {
    return null;
  }
}

function hasUnsafeDirectNpxUsage(content) {
  if (UNSAFE_DIRECT_NPX_PATTERNS.some((pattern) => pattern.test(content))) {
    return true;
  }

  const npxCommandVariables = collectNpxCommandVariableNames(content);
  if (npxCommandVariables.size === 0) {
    return false;
  }

  const processInvokerNames = collectUnsafeProcessInvokerNames(content);
  const invokerPattern = [...processInvokerNames].map(escapeRegex).join("|");
  const npxVarPattern = [...npxCommandVariables].map(escapeRegex).join("|");

  if (!invokerPattern || !npxVarPattern) {
    return false;
  }

  const variableInvocationPattern = new RegExp(
    `\\b(?:${invokerPattern})\\s*\\(\\s*(?:${npxVarPattern})\\b`
  );

  return variableInvocationPattern.test(content);
}

function collectNpxCommandVariableNames(content) {
  const names = new Set();
  NPX_COMMAND_ASSIGNMENT_RE.lastIndex = 0;

  let match;
  while ((match = NPX_COMMAND_ASSIGNMENT_RE.exec(content)) !== null) {
    names.add(match[1]);
  }

  return names;
}

function collectUnsafeProcessInvokerNames(content) {
  const names = new Set(UNSAFE_PROCESS_INVOKER_NAMES);

  PROCESS_INVOKER_ALIAS_ASSIGNMENT_RE.lastIndex = 0;
  let aliasMatch;
  while ((aliasMatch = PROCESS_INVOKER_ALIAS_ASSIGNMENT_RE.exec(content)) !== null) {
    names.add(aliasMatch[1]);
  }

  CHILD_PROCESS_DESTRUCTURE_RE.lastIndex = 0;
  let destructureMatch;
  while ((destructureMatch = CHILD_PROCESS_DESTRUCTURE_RE.exec(content)) !== null) {
    const bindings = destructureMatch[1].split(",");
    for (const rawBinding of bindings) {
      const binding = rawBinding.trim();
      if (!binding) {
        continue;
      }

      const destructured = /^([A-Za-z_$][\w$]*)(?:\s*:\s*([A-Za-z_$][\w$]*))?/.exec(binding);
      if (!destructured) {
        continue;
      }

      const imported = destructured[1];
      const localName = destructured[2] || imported;

      if (UNSAFE_PROCESS_INVOKER_NAMES.includes(imported) || imported.endsWith("spawnSyncImpl")) {
        names.add(localName);
      }
    }
  }

  return names;
}

function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function collectScriptPaths(
  scriptsDir = SCRIPTS_DIR,
  readdirSyncFn = fs.readdirSync,
  current = scriptsDir,
  files = []
) {
  const entries = readdirSyncFn(current, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(current, entry.name);

    if (entry.isDirectory()) {
      if (entry.name === "__tests__") {
        continue;
      }

      collectScriptPaths(scriptsDir, readdirSyncFn, fullPath, files);
      continue;
    }

    if (entry.isFile() && fullPath.endsWith(".js")) {
      files.push(fullPath);
    }
  }

  return files;
}

function validateManagedNpxPolicy(options = {}) {
  const {
    scriptSources,
    scriptsDir = SCRIPTS_DIR,
    readdirSyncFn = fs.readdirSync,
    readFileSyncFn = fs.readFileSync
  } = options;
  const violations = [];

  let sources = scriptSources;
  if (!Array.isArray(sources)) {
    sources = collectScriptPaths(scriptsDir, readdirSyncFn).map((filePath) => ({ filePath }));
  }

  for (const source of sources) {
    const filePath = source.filePath;
    if (typeof filePath !== "string" || filePath.length === 0) {
      continue;
    }

    let content = source.content;
    if (typeof content !== "string") {
      try {
        content = readFileSyncFn(filePath, "utf8");
      } catch (error) {
        violations.push(
          `managed-npx-policy: unable to read ${toRepoRelative(filePath)}: ${error.message}`
        );
        continue;
      }
    }

    if (hasUnsafeDirectNpxUsage(content)) {
      violations.push(
        `managed-npx-policy: ${toRepoRelative(filePath)} uses direct npx process spawning. ${MANAGED_NPX_POLICY_GUIDANCE}`
      );
    }
  }

  return violations;
}

function validateManagedNpxCliAvailability(options = {}) {
  const {
    execPath = process.execPath,
    resolveBundledNpxCliPathFn = resolveBundledNpxCliPath,
    existsSyncFn = fs.existsSync
  } = options;

  const resolvedNpxCliPath = resolveBundledNpxCliPathFn({
    execPath,
    existsSyncFn
  });

  if (!resolvedNpxCliPath) {
    return [
      `managed-npx-policy: unable to resolve npm bundled npx-cli.js for ${execPath}. ${MANAGED_NPX_CLI_GUIDANCE}`
    ];
  }

  if (!existsSyncFn(resolvedNpxCliPath)) {
    return [
      `managed-npx-policy: resolved npm bundled npx-cli.js path is missing: ${resolvedNpxCliPath}. ${MANAGED_NPX_CLI_GUIDANCE}`
    ];
  }

  return [];
}

async function validateTooling(options = {}) {
  const {
    existsSyncFn = fs.existsSync,
    statSyncFn = fs.statSync,
    requireFn = require,
    importFn = importToolEntry,
    resolveModuleFn = resolveRepoModule,
    resolveBundledNpxCliPathFn = resolveBundledNpxCliPath,
    execPath = process.execPath,
    toolSpecs = TOOL_SPECS,
    integrityTargets = INTEGRITY_TARGETS,
    probeIntegrityFn = probeIntegrity,
    enforceIntegrityProbe = true,
    scriptSources,
    scriptsDir = SCRIPTS_DIR,
    readdirSyncFn = fs.readdirSync,
    readFileSyncFn = fs.readFileSync,
    enforceManagedNpxCliAvailability = true,
    enforceVersionParity = true,
    probeDependencyVersionParityFn = probeDependencyVersionParity
  } = options;
  const violations = [];

  // Dependency version parity: every exact-pinned direct dependency's
  // INSTALLED version (and the local lockfile's resolved version, when
  // present) must equal the package.json pin; range pins must be satisfied.
  // This is the offline guard against the gitignored-lockfile drift class
  // (cspell-lib 10.0.0-vs-10.0.1) that file-presence/resolver probes miss.
  if (enforceVersionParity) {
    const parity = probeDependencyVersionParityFn({ repoRoot: REPO_ROOT });
    if (parity && !parity.ok) {
      for (const line of formatDriftLines(parity)) {
        violations.push(`dependency-version-parity: ${line}`);
      }
    }
  }

  if (enforceIntegrityProbe) {
    const integrity = probeIntegrityFn({
      repoRoot: REPO_ROOT,
      existsSyncFn,
      statSyncFn,
      targets: integrityTargets
    });
    if (integrity && !integrity.ok) {
      for (const entry of integrity.missing) {
        if (entry.reason === "missing") {
          violations.push(`${entry.tool}: missing ${entry.relPath}`);
        } else if (entry.reason === "empty") {
          violations.push(`${entry.tool}: ${entry.relPath} is empty (size 0)`);
        } else {
          violations.push(
            `${entry.tool}: ${entry.relPath} integrity probe failed (${entry.reason})`
          );
        }
      }
    }
  }

  for (const tool of toolSpecs) {
    for (const requiredFile of tool.requiredFiles) {
      const absPath = toAbs(requiredFile);
      if (!existsSyncFn(absPath)) {
        violations.push(`${tool.name}: missing ${requiredFile}`);
      }
    }
    // (note: file existence checks above duplicate the integrity probe
    // below for unmanaged callers passing a custom toolSpecs. The integrity
    // probe covers INTEGRITY_TARGETS specifically; the loop above honors
    // any extra requiredFiles a caller supplies.)

    if (tool.entry && tool.load === "require") {
      try {
        requireFn(toAbs(tool.entry));
      } catch (error) {
        violations.push(`${tool.name}: failed to load ${tool.entry}: ${error.message}`);
      }
    }

    if (tool.entry && tool.load === "import") {
      try {
        await importFn(toAbs(tool.entry));
      } catch (error) {
        violations.push(`${tool.name}: failed to import ${tool.entry}: ${error.message}`);
      }
    }

    if (tool.entry && tool.load === "resolve") {
      const resolvedPath = resolveModuleFn(tool.entry);
      if (!resolvedPath) {
        violations.push(`${tool.name}: failed to resolve ${tool.entry}`);
      } else if (!existsSyncFn(resolvedPath)) {
        violations.push(`${tool.name}: resolved ${tool.entry} to missing file: ${resolvedPath}`);
      } else {
        try {
          requireFn(resolvedPath);
        } catch (error) {
          violations.push(
            `${tool.name}: resolved ${tool.entry} could not be loaded: ${error.message}`
          );
        }
      }
    }
  }

  violations.push(
    ...validateManagedNpxPolicy({
      scriptSources,
      scriptsDir,
      readdirSyncFn,
      readFileSyncFn
    })
  );

  if (enforceManagedNpxCliAvailability) {
    violations.push(
      ...validateManagedNpxCliAvailability({
        execPath,
        resolveBundledNpxCliPathFn,
        existsSyncFn
      })
    );
  }

  return violations;
}

async function main() {
  const violations = await validateTooling();
  if (violations.length === 0) {
    console.log("Node tooling dependency health validation passed.");
    return 0;
  }

  console.error(`Found ${violations.length} Node tooling health violation(s):`);
  for (const violation of violations) {
    console.error(`- ${violation}`);
  }
  console.error(formatInstallGuidance());
  return 1;
}

module.exports = {
  REPO_ROOT,
  SCRIPTS_DIR,
  TOOL_SPECS,
  INTEGRITY_TARGETS,
  UNSAFE_DIRECT_NPX_PATTERNS,
  MANAGED_NPX_POLICY_GUIDANCE,
  toAbs,
  toRepoRelative,
  formatInstallGuidance,
  importToolEntry,
  resolveRepoModule,
  hasUnsafeDirectNpxUsage,
  collectNpxCommandVariableNames,
  collectUnsafeProcessInvokerNames,
  collectScriptPaths,
  validateManagedNpxPolicy,
  validateManagedNpxCliAvailability,
  validateTooling,
  main
};

if (require.main === module) {
  main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`validate-node-tooling: fatal error: ${error.stack || error.message}`);
      process.exit(1);
    }
  );
}
