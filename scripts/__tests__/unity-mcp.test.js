"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const net = require("node:net");
const os = require("node:os");
const path = require("node:path");
const { before, test } = require("node:test");
const { pathToFileURL } = require("node:url");

/**
 * `scripts/mcp/unity-mcp.mjs` is ESM because the MCP SDK and smol-toml are ESM-only, while
 * `scripts/index.js` (the entry point `node --test scripts/` resolves to on Node 21+) can only
 * `require` CJS `*.test.js` files. Loading the module lazily inside each test keeps registration
 * synchronous, so the suite is discovered exactly like the rest of the repository's tests.
 * ESM module caching makes every call after the first free.
 */
const MODULE_URL = pathToFileURL(path.join(__dirname, "..", "mcp", "unity-mcp.mjs")).href;

let DEFAULTS;
let buildRelayArgs;
let clientConfigPaths;
let configure;
let describeAttempts;
let discoverEndpoint;
let endpointCandidates;
let endpointUrl;
let findRelay;
let mergeCodexToml;
let parseArgs;
let parseDotEnv;
let prepareJsonServer;
let probeEndpoint;
let procNetRouteGateways;
let relayCandidates;
let requireProjectPath;
let resolveOptions;
let resolvConfHosts;
let transactionalWrite;
let validateEndpointPath;
let validateHost;

before(async () => {
  ({
    DEFAULTS,
    buildRelayArgs,
    clientConfigPaths,
    configure,
    describeAttempts,
    discoverEndpoint,
    endpointCandidates,
    endpointUrl,
    findRelay,
    mergeCodexToml,
    parseArgs,
    parseDotEnv,
    prepareJsonServer,
    probeEndpoint,
    procNetRouteGateways,
    relayCandidates,
    requireProjectPath,
    resolveOptions,
    resolvConfHosts,
    transactionalWrite,
    validateEndpointPath,
    validateHost
  } = await import(MODULE_URL));
});

// Duplicated deliberately: the data-driven cases below are built at registration time, before the
// module can be awaited. The `DEFAULTS.protocolVersion` test guards the duplication from drifting.
const PROTOCOL_VERSION = "2025-11-25";

function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "unity-mcp-test-"));
}

/** A closed-on-cleanup TCP listener, so reachability is exercised for real rather than stubbed. */
async function listeningPort(t) {
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  return server.address().port;
}

/** A port nothing listens on: bind it, record the number, release it. */
async function closedPort() {
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  await new Promise((resolve) => server.close(resolve));
  return port;
}

function probeOptions(overrides = {}) {
  return {
    protocolVersion: PROTOCOL_VERSION,
    timeout: 2_000,
    connectTimeout: 500,
    bearerToken: undefined,
    ...overrides
  };
}

function initializeResponse(
  body,
  { status = 200, contentType = "application/json", sessionId } = {}
) {
  const headers = new Map([["content-type", contentType]]);
  if (sessionId) {
    headers.set("mcp-session-id", sessionId);
  }
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (key) => headers.get(key.toLowerCase()) ?? null },
    text: async () => body
  };
}

const OK_PAYLOAD = JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  result: {
    protocolVersion: PROTOCOL_VERSION,
    capabilities: {},
    serverInfo: { name: "unity", version: "1" }
  }
});

// ---------------------------------------------------------------------------
// Argument and environment parsing
// ---------------------------------------------------------------------------

test("DEFAULTS.protocolVersion matches the protocol version pinned in this suite", () => {
  assert.equal(DEFAULTS.protocolVersion, PROTOCOL_VERSION);
});

test("parseArgs accepts values, equals form, and flags", () => {
  const parsed = parseArgs(["--host", "1.2.3.4", "--port=9100", "--no-discover"]);
  assert.equal(parsed.host, "1.2.3.4");
  assert.equal(parsed.port, "9100");
  assert.equal(parsed["no-discover"], true);
  assert.deepEqual(parsed._, []);
});

for (const [label, argv, message] of [
  ["unknown option", ["--nope", "x"], /Unknown option: --nope/],
  ["missing value", ["--host"], /Missing value for --host/],
  ["value that looks like an option", ["--host", "--port"], /Missing value for --host/],
  ["value given to a flag", ["--no-discover=1"], /--no-discover does not take a value/]
]) {
  test(`parseArgs rejects ${label}`, () => {
    assert.throws(() => parseArgs(argv), message);
  });
}

test("parseDotEnv handles quoting, comments, and export prefixes", () => {
  const parsed = parseDotEnv(
    [
      "# comment",
      "export UNITY_MCP_BRIDGE_HOST=10.0.0.5",
      "UNITY_MCP_BRIDGE_PORT=9100 # trailing",
      'A="quoted # value"',
      "B='single'"
    ].join("\n")
  );
  assert.deepEqual(parsed, {
    UNITY_MCP_BRIDGE_HOST: "10.0.0.5",
    UNITY_MCP_BRIDGE_PORT: "9100",
    A: "quoted # value",
    B: "single"
  });
});

test("parseDotEnv rejects malformed entries", () => {
  assert.throws(() => parseDotEnv("not an assignment"), /Invalid .* entry on line 1/);
  assert.throws(() => parseDotEnv('A="unterminated'), /Invalid quoted value/);
});

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

for (const value of ["127.0.0.1", "host.docker.internal", "::1", "example.com.", "a-b.example"]) {
  test(`validateHost accepts ${value}`, () => assert.equal(validateHost(value), value));
}

for (const value of ["", "-bad.example", "bad-.example", "a..b", "has space", "has\nnewline"]) {
  test(`validateHost rejects ${JSON.stringify(value)}`, () =>
    assert.throws(() => validateHost(value)));
}

test("validateEndpointPath normalizes and rejects traversal", () => {
  assert.equal(validateEndpointPath("mcp"), "/mcp");
  assert.equal(validateEndpointPath("/mcp"), "/mcp");
  for (const bad of ["/a//b", "/../etc", "/a/./b", "/%zz"]) {
    assert.throws(() => validateEndpointPath(bad), /Invalid MCP endpoint path/);
  }
});

test("endpointUrl brackets IPv6 literals", () => {
  assert.equal(
    endpointUrl({ host: "10.0.0.5", port: 9020, endpointPath: "/mcp" }),
    "http://10.0.0.5:9020/mcp"
  );
  assert.equal(
    endpointUrl({ host: "::1", port: 9020, endpointPath: "/mcp" }),
    "http://[::1]:9020/mcp"
  );
});

// ---------------------------------------------------------------------------
// Option resolution
// ---------------------------------------------------------------------------

test("resolveOptions prefers args over env over .env.local over defaults", () => {
  const local = { UNITY_MCP_BRIDGE_HOST: "local.example", UNITY_MCP_BRIDGE_PORT: "9001" };
  const environment = { UNITY_MCP_BRIDGE_HOST: "env.example" };

  const fromArgs = resolveOptions({ host: "arg.example" }, environment, local, "/repo");
  assert.equal(fromArgs.host, "arg.example");
  assert.equal(fromArgs.port, 9001, "port still falls through to .env.local");

  const fromEnv = resolveOptions({}, environment, local, "/repo");
  assert.equal(fromEnv.host, "env.example");

  const fromDefaults = resolveOptions({}, {}, {}, "/repo");
  assert.equal(fromDefaults.host, DEFAULTS.host);
  assert.equal(fromDefaults.port, DEFAULTS.port);
  assert.equal(fromDefaults.explicitHost, undefined, "an unset host must not look explicit");
  assert.equal(fromDefaults.explicitPort, undefined);
});

test("resolveOptions does not require a Unity project path", () => {
  const options = resolveOptions({}, {}, {}, "/repo");
  assert.equal(options.projectPath, undefined);
  assert.throws(() => requireProjectPath(options), /Unity project path is required/);
});

test("requireProjectPath rejects a path that is not a directory", () => {
  const directory = temporaryDirectory();
  const file = path.join(directory, "not-a-directory");
  fs.writeFileSync(file, "");
  assert.throws(
    () => requireProjectPath({ projectPath: file }),
    /Unity project directory does not exist/
  );
  assert.equal(requireProjectPath({ projectPath: directory }), directory);
});

test("resolveOptions rejects invalid scalars", () => {
  assert.throws(() => resolveOptions({ port: "70000" }, {}, {}, "/repo"), /Port must be between/);
  assert.throws(
    () => resolveOptions({ "log-level": "loud" }, {}, {}, "/repo"),
    /Log level must be/
  );
  assert.throws(() => resolveOptions({ "protocol-version": "v1" }, {}, {}, "/repo"), /YYYY-MM-DD/);
  assert.throws(() => resolveOptions({ token: "short" }, {}, {}, "/repo"), /Bearer token must be/);
});

// ---------------------------------------------------------------------------
// Endpoint discovery
// ---------------------------------------------------------------------------

test("resolvConfHosts extracts IPv4 nameservers only", () => {
  const raw = [
    "# generated",
    "nameserver 10.255.255.254",
    "nameserver fe80::1",
    "options ndots:0"
  ].join("\n");
  assert.deepEqual(resolvConfHosts(raw), ["10.255.255.254"]);
  assert.deepEqual(resolvConfHosts(""), []);
});

test("procNetRouteGateways decodes little-endian default routes", () => {
  const raw = [
    "Iface\tDestination\tGateway\tFlags\tRefCnt\tUse\tMetric\tMask\tMTU\tWindow\tIRTT",
    "eth0\t00000000\t0100A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0",
    "eth0\t0000A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0"
  ].join("\n");
  assert.deepEqual(procNetRouteGateways(raw), ["192.168.0.1"]);
  assert.deepEqual(procNetRouteGateways(""), []);
});

test("endpointCandidates puts explicit settings first and de-duplicates", () => {
  const readFile = (filePath) =>
    filePath === "/etc/resolv.conf" ? "nameserver 10.255.255.254\n" : "";
  const options = { explicitHost: "10.0.0.5", explicitPort: 9500, endpointPath: "/mcp" };
  const candidates = endpointCandidates(options, { readFile });

  assert.deepEqual(candidates[0], { host: "10.0.0.5", port: 9500, endpointPath: "/mcp" });
  assert.equal(
    new Set(candidates.map((c) => `${c.host}:${c.port}`)).size,
    candidates.length,
    "no duplicates"
  );
  assert.ok(
    candidates.some((c) => c.host === "10.255.255.254"),
    "resolv.conf nameserver is probed"
  );
  assert.ok(
    candidates.some((c) => c.port === 9003),
    "the legacy supergateway port stays reachable"
  );
});

test("endpointCandidates without explicit settings still covers the fallbacks", () => {
  const candidates = endpointCandidates({ endpointPath: "/mcp" }, { readFile: () => "" });
  assert.deepEqual(candidates[0], {
    host: "host.docker.internal",
    port: 9020,
    endpointPath: "/mcp"
  });
  assert.ok(candidates.some((c) => c.host === "127.0.0.1"));
});

test("probeEndpoint reports an unreachable port without issuing a request", async () => {
  let called = false;
  const result = await probeEndpoint(
    { host: "127.0.0.1", port: await closedPort(), endpointPath: "/mcp" },
    probeOptions(),
    () => {
      called = true;
    }
  );
  assert.equal(result.ok, false);
  assert.equal(result.status, "unreachable");
  assert.equal(called, false, "a closed port must not cost an HTTP round trip");
});

for (const [label, response, expected] of [
  ["a healthy handshake", initializeResponse(OK_PAYLOAD), "ok"],
  ["a rejected token", initializeResponse("nope", { status: 401 }), "unauthorized"],
  ["a server error", initializeResponse("boom", { status: 500 }), "http-error"],
  ["a non-JSON body", initializeResponse("<html>", { status: 200 }), "malformed"],
  [
    "a JSON-RPC error",
    initializeResponse(
      JSON.stringify({ jsonrpc: "2.0", id: 1, error: { code: -1, message: "no" } })
    ),
    "malformed"
  ]
]) {
  test(`probeEndpoint classifies ${label}`, async (t) => {
    const port = await listeningPort(t);
    const result = await probeEndpoint(
      { host: "127.0.0.1", port, endpointPath: "/mcp" },
      probeOptions(),
      async () => response
    );
    assert.equal(result.status, expected);
    assert.equal(result.ok, expected === "ok");
  });
}

test("probeEndpoint parses server-sent event payloads and closes the session it opened", async (t) => {
  const port = await listeningPort(t);
  const methods = [];
  const fetchImpl = async (_url, init) => {
    methods.push(init.method);
    return init.method === "POST"
      ? initializeResponse(`event: message\ndata: ${OK_PAYLOAD}\n\n`, {
          contentType: "text/event-stream",
          sessionId: "session-1"
        })
      : initializeResponse("", { status: 202 });
  };

  const result = await probeEndpoint(
    { host: "127.0.0.1", port, endpointPath: "/mcp" },
    probeOptions(),
    fetchImpl
  );
  assert.equal(result.ok, true);
  assert.equal(result.protocolVersion, DEFAULTS.protocolVersion);
  assert.deepEqual(methods, ["POST", "DELETE"], "the probe must not leak a relay session");
});

test("discoverEndpoint walks candidates in order and stops at the first success", async (t) => {
  const livePort = await listeningPort(t);
  const deadPort = await closedPort();
  // An explicit candidate list keeps this hermetic: the real fallbacks include host.docker.internal,
  // which may genuinely be listening on the developer machine running these tests.
  const candidates = [
    { host: "127.0.0.1", port: deadPort, endpointPath: "/mcp" },
    { host: "127.0.0.1", port: livePort, endpointPath: "/mcp" },
    { host: "127.0.0.1", port: livePort, endpointPath: "/never-reached" }
  ];
  const runtime = { candidates, fetchImpl: async () => initializeResponse(OK_PAYLOAD) };

  const { found, attempts } = await discoverEndpoint(probeOptions(), runtime);
  assert.equal(found?.ok, true);
  assert.equal(found.port, livePort);
  assert.equal(found.endpointPath, "/mcp");
  assert.equal(attempts.length, 2, "discovery stops before the third candidate");
  assert.equal(attempts[0].status, "unreachable");
});

test("discoverEndpoint reports every attempt when nothing responds", async () => {
  const deadPort = await closedPort();
  const candidates = [
    { host: "127.0.0.1", port: deadPort, endpointPath: "/mcp" },
    { host: "127.0.0.1", port: deadPort, endpointPath: "/other" }
  ];
  const { found, attempts } = await discoverEndpoint(probeOptions(), {
    candidates,
    fetchImpl: async () => initializeResponse(OK_PAYLOAD)
  });
  assert.equal(found, undefined);
  assert.equal(attempts.length, 2);
  assert.ok(attempts.every((attempt) => attempt.ok === false));
});

test("describeAttempts surfaces classified failures ahead of plain unreachability", () => {
  const description = describeAttempts([
    { url: "http://a:1/mcp", status: "unreachable", detail: "no TCP listener" },
    { url: "http://b:2/mcp", status: "unauthorized", detail: "HTTP 401" }
  ]);
  assert.match(description, /unauthorized/);
  assert.doesNotMatch(
    description,
    /a:1/,
    "noise from dead ports is dropped when a real failure exists"
  );
});

// ---------------------------------------------------------------------------
// Client configuration
// ---------------------------------------------------------------------------

test("prepareJsonServer creates, merges, and rejects malformed documents", () => {
  const directory = temporaryDirectory();
  const filePath = path.join(directory, "mcp.json");
  const server = { type: "http", url: "http://h:1/mcp" };

  assert.equal(
    JSON.parse(prepareJsonServer(filePath, "mcpServers", server)).mcpServers["unity-mcp"].url,
    "http://h:1/mcp"
  );

  fs.writeFileSync(
    filePath,
    JSON.stringify({ mcpServers: { other: { url: "keep" } }, unrelated: 1 })
  );
  const merged = JSON.parse(prepareJsonServer(filePath, "mcpServers", server));
  assert.equal(merged.mcpServers.other.url, "keep", "sibling servers survive");
  assert.equal(merged.unrelated, 1, "unrelated keys survive");

  fs.writeFileSync(filePath, JSON.stringify({ mcpServers: [] }));
  assert.throws(
    () => prepareJsonServer(filePath, "mcpServers", server),
    /Expected mcpServers to be an object/
  );

  fs.writeFileSync(filePath, "{ not json");
  assert.throws(() => prepareJsonServer(filePath, "mcpServers", server), /Invalid JSON/);
});

test("mergeCodexToml appends, replaces in place, and preserves neighbours", () => {
  const fresh = mergeCodexToml("", "http://h:1/mcp", "t".repeat(32));
  assert.match(fresh, /\[mcp_servers\.unity-mcp\]/);
  assert.match(fresh, /Authorization = "Bearer t{32}"/);

  const withNeighbour = `[other]\nkeep = true\n\n${fresh}`;
  const replaced = mergeCodexToml(withNeighbour, "http://h:2/mcp", "u".repeat(32));
  assert.match(replaced, /keep = true/, "neighbouring tables survive");
  assert.equal(
    replaced.match(/\[mcp_servers\.unity-mcp\]/g).length,
    1,
    "the table is replaced, not duplicated"
  );
  assert.match(replaced, /http:\/\/h:2\/mcp/);
  assert.doesNotMatch(replaced, /http:\/\/h:1\/mcp/);

  // A table following ours must not be swallowed by the replacement.
  const trailing = mergeCodexToml(
    `${fresh}\n[after]\nvalue = 1\n`,
    "http://h:3/mcp",
    "v".repeat(32)
  );
  assert.match(trailing, /\[after\]/);
  assert.match(trailing, /value = 1/);
});

test("mergeCodexToml refuses inputs it cannot safely rewrite", () => {
  assert.throws(
    () => mergeCodexToml("[unclosed", "http://h:1/mcp", "t".repeat(32)),
    /Invalid TOML/
  );
  assert.throws(
    () => mergeCodexToml('mcp_servers.unity-mcp = { url = "x" }', "http://h:1/mcp", "t".repeat(32)),
    /Unsupported inline or dotted/
  );
  // A literally duplicated table is already invalid TOML, so the parser rejects it first.
  const duplicated = `[mcp_servers.unity-mcp]\nurl = "a"\n\n[mcp_servers.unity-mcp]\nurl = "b"\n`;
  assert.throws(() => mergeCodexToml(duplicated, "http://h:1/mcp", "t".repeat(32)), /Invalid TOML/);

  // The duplicate guard covers what line scanning alone cannot see: a header-shaped line inside a
  // multi-line string. The document parses, but two lines look like our table, so the rewrite region
  // is ambiguous and the only safe move is to refuse rather than splice over a string literal.
  const ambiguous = [
    'note = """',
    "[mcp_servers.unity-mcp]",
    '"""',
    "",
    "[mcp_servers.unity-mcp]",
    'url = "real"',
    ""
  ].join("\n");
  assert.throws(
    () => mergeCodexToml(ambiguous, "http://h:1/mcp", "t".repeat(32)),
    /Duplicate unity-mcp table/
  );
});

test("transactionalWrite commits every file or none", () => {
  const directory = temporaryDirectory();
  const first = path.join(directory, "first.json");
  const second = path.join(directory, "nested", "second.json");
  fs.writeFileSync(first, "original\n");

  const written = transactionalWrite([
    [first, "updated\n"],
    [second, "created\n"]
  ]);
  assert.deepEqual(written.sort(), [first, second].sort());
  assert.equal(fs.readFileSync(first, "utf8"), "updated\n");
  assert.equal(fs.readFileSync(second, "utf8"), "created\n");

  assert.deepEqual(
    transactionalWrite([[first, "updated\n"]]),
    [],
    "unchanged content is not rewritten"
  );

  assert.throws(
    () =>
      transactionalWrite(
        [
          [first, "second-update\n"],
          [second, "second-create\n"]
        ],
        (index) => {
          if (index === 1) {
            throw new Error("commit failure");
          }
        }
      ),
    /commit failure/
  );
  assert.equal(fs.readFileSync(first, "utf8"), "updated\n", "the committed file is rolled back");
  assert.equal(fs.readFileSync(second, "utf8"), "created\n");
});

test("transactionalWrite removes files it created when a later commit fails", () => {
  const directory = temporaryDirectory();
  const created = path.join(directory, "created.json");
  const other = path.join(directory, "other.json");

  assert.throws(
    () =>
      transactionalWrite(
        [
          [created, "new\n"],
          [other, "new\n"]
        ],
        (index) => {
          if (index === 1) {
            throw new Error("boom");
          }
        }
      ),
    /boom/
  );
  assert.equal(
    fs.existsSync(created),
    false,
    "a file that did not exist before is removed on rollback"
  );
});

test("configure writes every client config and is idempotent", () => {
  const repoRoot = temporaryDirectory();
  const options = { repoRoot, bearerToken: "a".repeat(32) };
  const endpoint = { host: "10.0.0.5", port: 9020, endpointPath: "/mcp" };

  const firstRun = configure(options, endpoint);
  assert.equal(firstRun.url, "http://10.0.0.5:9020/mcp");
  const paths = clientConfigPaths(repoRoot);
  assert.deepEqual(firstRun.written.sort(), Object.values(paths).sort());

  assert.equal(
    JSON.parse(fs.readFileSync(paths.claudeCode, "utf8")).mcpServers["unity-mcp"].headers
      .Authorization,
    `Bearer ${"a".repeat(32)}`
  );
  assert.equal(
    JSON.parse(fs.readFileSync(paths.vscode, "utf8")).servers["unity-mcp"].url,
    firstRun.url
  );
  assert.equal(
    JSON.parse(fs.readFileSync(paths.cursor, "utf8")).mcpServers["unity-mcp"].url,
    firstRun.url
  );
  assert.match(fs.readFileSync(paths.codex, "utf8"), /\[mcp_servers\.unity-mcp\]/);

  assert.deepEqual(configure(options, endpoint).written, [], "a second run changes nothing");
});

test("configure generates and persists a bearer token when none is supplied", () => {
  const repoRoot = temporaryDirectory();
  configure({ repoRoot, bearerToken: undefined }, { host: "h", port: 1, endpointPath: "/mcp" });
  const envLocal = fs.readFileSync(path.join(repoRoot, ".env.local"), "utf8");
  assert.match(envLocal, /^UNITY_MCP_BEARER_TOKEN=[0-9a-f]{64}$/m);
});

// ---------------------------------------------------------------------------
// Relay discovery
// ---------------------------------------------------------------------------

test("relayCandidates is platform specific", () => {
  const windows = relayCandidates({ platform: "win32", home: "/home/u" });
  assert.ok(windows[0].endsWith("relay_win.exe"));
  const linux = relayCandidates({ platform: "linux", arch: "x64", home: "/home/u" });
  assert.ok(linux[0].endsWith("relay_linux_x64"));
  assert.deepEqual(relayCandidates({ platform: "aix", home: "/home/u" }), []);
});

test("findRelay requires an existing file and reports what it searched", () => {
  const directory = temporaryDirectory();
  const relay = path.join(directory, "relay_linux_x64");
  assert.throws(() => findRelay(relay, { platform: "linux" }), /Unity MCP relay not found/);
  assert.throws(() => findRelay(directory, { platform: "linux" }), /Unity MCP relay not found/);

  fs.writeFileSync(relay, "#!/bin/sh\n", { mode: 0o755 });
  assert.equal(findRelay(relay, { platform: "linux" }), relay);
});

// Windows has no execute bit: fs.accessSync(X_OK) succeeds for any existing file, so this
// assertion is only meaningful on a POSIX filesystem.
test("findRelay rejects a non-executable relay", { skip: process.platform === "win32" }, () => {
  const relay = path.join(temporaryDirectory(), "relay_linux_x64");
  fs.writeFileSync(relay, "#!/bin/sh\n", { mode: 0o644 });
  assert.throws(() => findRelay(relay, { platform: "linux" }), /not found or not executable/);
});

test("buildRelayArgs passes the resolved project path", () => {
  assert.deepEqual(buildRelayArgs("/tmp/project"), [
    "--mcp",
    "--project-path",
    path.resolve("/tmp/project")
  ]);
});
