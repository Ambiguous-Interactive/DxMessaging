"use strict";

const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const fs = require("node:fs");
const http = require("node:http");
const net = require("node:net");
const os = require("node:os");
const path = require("node:path");
const { PassThrough, Writable } = require("node:stream");
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
let assertPortAvailable;
let buildRelayArgs;
let clientConfigPaths;
let configure;
let describeAttempts;
let discoverEndpoint;
let endpointCandidates;
let endpointUrl;
let findRelay;
let main;
let mergeCodexToml;
let parseArgs;
let parseDotEnv;
let prepareJsonServer;
let probeEndpoint;
let procNetRouteGateways;
let readLocalEnv;
let relayCandidates;
let requireProjectPath;
let resolveOptions;
let resolvConfHosts;
let runConfigure;
let runProbe;
let startBridge;
let stripJsonComments;
let transactionalWrite;
let validateEndpointPath;
let validateHost;

before(async () => {
  ({
    DEFAULTS,
    assertPortAvailable,
    buildRelayArgs,
    clientConfigPaths,
    configure,
    describeAttempts,
    discoverEndpoint,
    endpointCandidates,
    endpointUrl,
    findRelay,
    main,
    mergeCodexToml,
    parseArgs,
    parseDotEnv,
    prepareJsonServer,
    probeEndpoint,
    procNetRouteGateways,
    readLocalEnv,
    relayCandidates,
    requireProjectPath,
    resolveOptions,
    resolvConfHosts,
    runConfigure,
    runProbe,
    startBridge,
    stripJsonComments,
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

/** Swap a console method out for the duration of one test and collect what it was given. */
function captureConsole(t, method) {
  const captured = [];
  const original = console[method];
  console[method] = (message) => captured.push(message);
  t.after(() => {
    console[method] = original;
  });
  return captured;
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
  ["value given to a flag", ["--no-discover=1"], /--no-discover does not take a value/],
  // `--host=` used to resolve to "" and then silently fall back to the default host.
  ["empty equals-form value", ["--host="], /--host requires a non-empty value/],
  ["empty separate value", ["--host", ""], /--host requires a non-empty value/]
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

// A quoted Windows path ends in a backslash, which a naive scanner reads as escaping the closing
// quote; the docs recommend exactly that shape for UNITY_PROJECT_PATH.
for (const [label, line, expected] of [
  ["a trailing backslash", 'A="D:\\Program Files\\Proj\\"', "D:\\Program Files\\Proj\\"],
  ["an escaped quote", 'A="say \\"hi\\""', 'say "hi"'],
  ["an escaped backslash", 'A="a\\\\b"', "a\\b"],
  ["an unescaped interior backslash", 'A="D:\\Path\\To"', "D:\\Path\\To"],
  ["single quotes taken literally", "A='keep\\this'", "keep\\this"],
  ["a comment after a quoted value", 'A="value" # note', "value"]
]) {
  test(`parseDotEnv handles ${label}`, () => {
    assert.deepEqual(parseDotEnv(line), { A: expected });
  });
}

test("readLocalEnv skips unparsable lines instead of aborting every command", (t) => {
  const repoRoot = temporaryDirectory();
  fs.writeFileSync(
    path.join(repoRoot, ".env.local"),
    ["UNITY_MCP_BRIDGE_HOST=10.0.0.5", "this is not an assignment", 'B="unterminated', "C=3"].join(
      "\n"
    )
  );
  const warnings = captureConsole(t, "warn");

  assert.deepEqual(readLocalEnv(repoRoot), { UNITY_MCP_BRIDGE_HOST: "10.0.0.5", C: "3" });
  assert.equal(warnings.length, 2, "each bad line is reported once");
  assert.match(warnings[0], /line 2/);
  assert.match(warnings[1], /line 3/);
  assert.deepEqual(readLocalEnv(path.join(repoRoot, "absent")), {});
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
  // /%FF is a syntactically valid escape that is not valid UTF-8: decodeURIComponent throws a raw
  // URIError, which used to escape as "URI malformed" instead of the normal message.
  for (const bad of ["/a//b", "/../etc", "/a/./b", "/%zz", "/%FF", "/%C3%28"]) {
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
  assert.throws(
    () => resolveOptions({ "max-sessions": "0" }, {}, {}, "/repo"),
    /Max sessions must be between/
  );
  assert.equal(resolveOptions({}, {}, {}, "/repo").maxSessions, DEFAULTS.maxSessions);
  assert.equal(resolveOptions({ "max-sessions": "3" }, {}, {}, "/repo").maxSessions, 3);
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

const RESOLV_CONF = (filePath) =>
  filePath === "/etc/resolv.conf" ? "nameserver 10.255.255.254\n" : "";

// An explicit host or port is a deliberate setting, so it is the ONLY candidate on that axis.
// Cross-producing it with the fallbacks used to probe host.docker.internal even for --host X.
for (const [label, options, expected] of [
  ["host and port", { explicitHost: "10.0.0.5", explicitPort: 9500 }, ["10.0.0.5:9500"]],
  ["host only", { explicitHost: "10.0.0.5" }, ["10.0.0.5:9020", "10.0.0.5:9003"]],
  [
    "port only",
    { explicitPort: 9500 },
    ["host.docker.internal:9500", "127.0.0.1:9500", "10.255.255.254:9500"]
  ]
]) {
  test(`endpointCandidates probes only what was configured: ${label}`, () => {
    const candidates = endpointCandidates(
      { ...options, endpointPath: "/mcp" },
      { readFile: RESOLV_CONF }
    );
    assert.deepEqual(
      candidates.map((candidate) => `${candidate.host}:${candidate.port}`),
      expected
    );
    assert.ok(candidates.every((candidate) => candidate.endpointPath === "/mcp"));
  });
}

test("endpointCandidates without explicit settings still covers the fallbacks", () => {
  const candidates = endpointCandidates({ endpointPath: "/mcp" }, { readFile: RESOLV_CONF });
  assert.deepEqual(candidates[0], {
    host: "host.docker.internal",
    port: 9020,
    endpointPath: "/mcp"
  });
  assert.equal(
    new Set(candidates.map((c) => `${c.host}:${c.port}`)).size,
    candidates.length,
    "no duplicates"
  );
  assert.ok(candidates.some((c) => c.host === "127.0.0.1"));
  assert.ok(
    candidates.some((c) => c.host === "10.255.255.254"),
    "resolv.conf nameserver is probed"
  );
  assert.ok(
    candidates.some((c) => c.port === 9003),
    "the legacy supergateway port stays reachable"
  );
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

// A bridge only answers an authenticated probe, so a probe that forgets the header reports
// `unauthorized` for a perfectly healthy endpoint.
for (const [label, token, expected] of [
  ["sends the configured bearer token", "t".repeat(32), `Bearer ${"t".repeat(32)}`],
  ["sends no Authorization header when no token is configured", undefined, undefined]
]) {
  test(`probeEndpoint ${label}`, async (t) => {
    const port = await listeningPort(t);
    const seen = [];
    const fetchImpl = async (_url, init) => {
      seen.push(init.headers);
      return init.method === "POST"
        ? initializeResponse(OK_PAYLOAD, { sessionId: "session-1" })
        : initializeResponse("", { status: 202 });
    };

    const result = await probeEndpoint(
      { host: "127.0.0.1", port, endpointPath: "/mcp" },
      probeOptions({ bearerToken: token }),
      fetchImpl
    );
    assert.equal(result.ok, true);
    assert.equal(seen.length, 2, "the handshake POST and the cleanup DELETE");
    assert.equal(seen[0].Authorization, expected);
    assert.equal(seen[1].Authorization, expected, "the cleanup DELETE is authenticated too");
  });
}

test("probeEndpoint failures name the endpoint that produced them", async (t) => {
  const port = await listeningPort(t);
  const result = await probeEndpoint(
    { host: "127.0.0.1", port, endpointPath: "/mcp" },
    probeOptions(),
    async () => initializeResponse("nope", { status: 401 })
  );
  assert.equal(result.status, "unauthorized");
  assert.equal(result.host, "127.0.0.1");
  assert.equal(result.port, port);
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

// --log-level debug used to be accepted and then never emit anything, because nothing called log()
// at the debug level.
test("discoverEndpoint emits per-candidate detail only at log level debug", async (t) => {
  const dead = await closedPort();
  const candidates = [{ host: "127.0.0.1", port: dead, endpointPath: "/mcp" }];
  const runtime = { candidates, fetchImpl: async () => initializeResponse(OK_PAYLOAD) };

  const quiet = captureConsole(t, "log");
  await discoverEndpoint(probeOptions({ logLevel: "info" }), runtime);
  assert.deepEqual(quiet, [], "info level stays quiet");

  await discoverEndpoint(probeOptions({ logLevel: "debug" }), runtime);
  assert.match(quiet.join("\n"), new RegExp(`Probing http://127\\.0\\.0\\.1:${dead}/mcp`));
  assert.match(quiet.join("\n"), /unreachable/);
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

// .vscode/mcp.json is JSONC and VS Code's own "MCP: Add Server" scaffolding writes a comment into
// it, so refusing comments meant `configure` could not run at all for those users.
for (const [label, raw, expected] of [
  ["a line comment", '{\n  // hint\n  "a": 1\n}', { a: 1 }],
  ["a block comment", '{\n  /* hint\n     more */\n  "a": 1\n}', { a: 1 }],
  ["a trailing comma in an object", '{\n  "a": 1,\n}', { a: 1 }],
  ["a trailing comma in an array", '{\n  "a": [1, 2,],\n}', { a: [1, 2] }],
  [
    "comment markers inside strings",
    '{ "a": "http://h:1//mcp", "b": "/* not */" }',
    {
      a: "http://h:1//mcp",
      b: "/* not */"
    }
  ],
  ["a comma before a brace inside a string", '{ "a": "x,}" }', { a: "x,}" }],
  ["an escaped quote inside a string", '{ "a": "say \\" // no" }', { a: 'say " // no' }],
  ["plain JSON left untouched", '{"a":1,"b":[2,3]}', { a: 1, b: [2, 3] }]
]) {
  test(`stripJsonComments handles ${label}`, () => {
    assert.deepEqual(JSON.parse(stripJsonComments(raw)), expected);
  });
}

test("stripJsonComments stays linear in the number of closing brackets", () => {
  // Re-scanning the accumulated output on every `}` or `]` (or indexing into it) flattens the rope
  // V8 builds from repeated concatenation, making the pass quadratic: this input took 150 seconds
  // before, and a few hundred KB of real `.mcp.json` was enough to make `configure` look hung.
  // The bound is deliberately loose; only a return to quadratic can breach it.
  const document = `[${Array.from({ length: 64_000 }, () => "{}").join(",")}]`;
  const started = process.hrtime.bigint();
  const stripped = stripJsonComments(document);
  const elapsedMs = Number(process.hrtime.bigint() - started) / 1e6;

  assert.equal(stripped, document, "comment-free JSON must round-trip unchanged");
  assert.ok(elapsedMs < 2_000, `took ${elapsedMs.toFixed(0)}ms; expected well under 2000ms`);
});

test("prepareJsonServer merges into a JSONC document", () => {
  const filePath = path.join(temporaryDirectory(), "mcp.json");
  fs.writeFileSync(
    filePath,
    ["{", "  // Inputs are prompted on first server start.", '  "servers": {},', "}"].join("\n")
  );
  const merged = JSON.parse(
    prepareJsonServer(filePath, "servers", { type: "http", url: "http://h:1/mcp" })
  );
  assert.equal(merged.servers["unity-mcp"].url, "http://h:1/mcp");
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

// With exactly ONE header-shaped line inside a multi-line string the duplicate guard never fires,
// so the line scanner used to splice across the string literal and then blame its own generator.
test("mergeCodexToml refuses a lone header-shaped line inside a multi-line value", () => {
  const raw = ['note = """', "[mcp_servers.unity-mcp]", '"""', ""].join("\n");
  assert.throws(
    () => mergeCodexToml(raw, "http://h:1/mcp", "t".repeat(32)),
    (error) => {
      assert.match(error.message, /inside a multi-line value/);
      assert.match(error.message, /\.codex\/config\.toml/, "the message names the file to fix");
      assert.match(error.message, /re-run configure/, "the message says what to do");
      return true;
    }
  );
});

test("mergeCodexToml normalizes CRLF input so a second run is a no-op", () => {
  const appended = mergeCodexToml("[other]\r\nkeep = true\r\n", "http://h:1/mcp", "t".repeat(32));
  assert.doesNotMatch(appended, /\r/, "the append path must not emit mixed line endings");
  assert.equal(
    mergeCodexToml(appended, "http://h:1/mcp", "t".repeat(32)),
    appended,
    "configure converges on run 2, not run 3"
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

// Windows returns EPERM from rename whenever an editor holds the destination open, which is exactly
// what these four config files are. A rollback that throws must not replace the original error nor
// abandon the files it has not restored yet.
test("transactionalWrite finishes rollback and rethrows the original error", () => {
  const directory = temporaryDirectory();
  const first = path.join(directory, "first.json");
  const second = path.join(directory, "second.json");
  const third = path.join(directory, "third.json");
  for (const filePath of [first, second, third]) {
    fs.writeFileSync(filePath, "original\n");
  }

  const originalChmod = fs.chmodSync;
  // atomicWrite is the rollback primitive; failing its chmod fails exactly one restore.
  fs.chmodSync = (target, mode) => {
    if (String(target).includes("second.json")) {
      throw new Error("rollback failure");
    }
    return originalChmod(target, mode);
  };
  let thrown;
  try {
    transactionalWrite(
      [
        [first, "a\n"],
        [second, "b\n"],
        [third, "c\n"]
      ],
      (index) => {
        if (index === 2) {
          throw new Error("commit failure");
        }
      }
    );
  } catch (error) {
    thrown = error;
  } finally {
    fs.chmodSync = originalChmod;
  }

  assert.equal(thrown?.message, "commit failure", "the original error survives");
  assert.ok(thrown.cause instanceof AggregateError, "rollback failures are attached, not thrown");
  assert.equal(thrown.cause.errors.length, 1);
  assert.equal(
    fs.readFileSync(first, "utf8"),
    "original\n",
    "rollback continues past the failure to the remaining files"
  );
  assert.equal(fs.readFileSync(third, "utf8"), "original\n", "third was never committed");
});

test("transactionalWrite cleans up temporaries when staging itself fails", () => {
  const directory = temporaryDirectory();
  const good = path.join(directory, "good.json");
  const blocker = path.join(directory, "blocker");
  fs.writeFileSync(blocker, "");
  // Staging the second write must fail: its parent directory cannot be created over a file.
  assert.throws(() =>
    transactionalWrite([
      [good, "new\n"],
      [path.join(blocker, "nested.json"), "new\n"]
    ])
  );
  assert.deepEqual(
    fs.readdirSync(directory).filter((entry) => entry.endsWith(".tmp")),
    [],
    "a token-bearing temporary must never be left behind"
  );
  assert.equal(fs.existsSync(good), false, "nothing is committed when staging fails");
});

// Only POSIX carries a meaningful permission bit; on Windows chmod moves the read-only flag alone.
test(
  "transactionalWrite rollback restores the original file mode",
  { skip: process.platform === "win32" },
  () => {
    const directory = temporaryDirectory();
    const first = path.join(directory, "first.json");
    const second = path.join(directory, "second.json");
    fs.writeFileSync(first, "original\n", { mode: 0o644 });
    fs.chmodSync(first, 0o644);
    fs.writeFileSync(second, "original\n");

    assert.throws(
      () =>
        transactionalWrite(
          [
            [first, "a\n"],
            [second, "b\n"]
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
      fs.statSync(first).mode & 0o777,
      0o644,
      "rollback must not silently tighten a pre-existing 0644 config to 0600"
    );
  }
);

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

test("assertPortAvailable rejects a port that is already bound", async (t) => {
  const port = await listeningPort(t);
  await assert.rejects(
    () => assertPortAvailable(port, "127.0.0.1"),
    /is unavailable on 127\.0\.0\.1/
  );
  await assertPortAvailable(await closedPort(), "127.0.0.1");
});

// ---------------------------------------------------------------------------
// Bridge server
// ---------------------------------------------------------------------------

const BRIDGE_TOKEN = "b".repeat(32);

/**
 * A relay stand-in: it speaks the same newline-delimited JSON-RPC over stdio that the real Unity
 * relay does, so the bridge is exercised end to end without a Unity install or a network dependency.
 */
function createFakeRelay() {
  const child = new EventEmitter();
  child.exitCode = null;
  child.signalCode = null;
  child.signals = [];
  child.received = [];
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  let buffer = "";
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      buffer += chunk.toString("utf8");
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines.filter((entry) => entry.trim())) {
        const message = JSON.parse(line);
        child.received.push(message);
        if (message.id === undefined) {
          continue;
        }
        const result =
          message.method === "initialize"
            ? {
                protocolVersion: PROTOCOL_VERSION,
                capabilities: {},
                serverInfo: { name: "fake-relay", version: "1.0.0" }
              }
            : { echoed: message.method };
        child.stdout.write(`${JSON.stringify({ jsonrpc: "2.0", id: message.id, result })}\n`);
      }
      callback();
    }
  });
  child.kill = (signal) => {
    child.signals.push(signal);
    if (child.exitCode === null) {
      child.exitCode = 0;
      child.emit("exit", 0, null);
    }
    return true;
  };
  return child;
}

async function startTestBridge(t, overrides = {}) {
  const repoRoot = temporaryDirectory();
  const projectPath = path.join(repoRoot, "project");
  fs.mkdirSync(projectPath);
  const relayPath = path.join(repoRoot, "relay");
  fs.writeFileSync(relayPath, "#!/bin/sh\n", { mode: 0o755 });

  const relays = [];
  const running = await startBridge(
    {
      repoRoot,
      projectPath,
      relayPath,
      bindHost: "127.0.0.1",
      // Port 0 lets the OS pick a free port, which keeps parallel test runs hermetic.
      port: 0,
      endpointPath: "/mcp",
      bearerToken: BRIDGE_TOKEN,
      sessionTimeout: 60_000,
      requestTimeout: 300_000,
      maxSessions: 8,
      logLevel: "none",
      ...overrides
    },
    {
      spawnRelay: () => {
        const relay = createFakeRelay();
        relays.push(relay);
        return relay;
      }
    }
  );
  t.after(() => running.close());
  return { running, relays, repoRoot, port: running.httpServer.address().port };
}

/** `null` means "send no Authorization header at all", which is a distinct case from a bad token. */
function mcpHeaders(token = BRIDGE_TOKEN, extra = {}) {
  return {
    Accept: "application/json, text/event-stream",
    "Content-Type": "application/json",
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...extra
  };
}

function initializeBody(id = 1) {
  return JSON.stringify({
    jsonrpc: "2.0",
    id,
    method: "initialize",
    params: {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: "unity-mcp-test", version: "1.0.0" }
    }
  });
}

/** A raw HTTP request, for the cases fetch cannot express (a stalled or over-large body). */
function rawRequest(port, { method = "POST", requestPath = "/mcp", headers = {}, body, stall }) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (action, value) => {
      if (!settled) {
        settled = true;
        action(value);
      }
    };
    const request = http.request(
      { host: "127.0.0.1", port, method, path: requestPath, headers },
      (response) => {
        let text = "";
        response.setEncoding("utf8");
        response.on("data", (chunk) => {
          text += chunk;
        });
        response.on("end", () => finish(resolve, { status: response.statusCode, text }));
      }
    );
    request.on("error", (error) => finish(reject, error));
    if (stall) {
      request.flushHeaders();
    } else {
      request.end(body);
    }
  });
}

async function openSession(port) {
  const response = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: "POST",
    headers: mcpHeaders(),
    body: initializeBody()
  });
  return { response, sessionId: response.headers.get("mcp-session-id") };
}

for (const [label, token, expected] of [
  ["no Authorization header", null, 401],
  ["a wrong bearer token", "z".repeat(32), 401],
  ["a right-length wrong token", "b".repeat(31) + "c", 401],
  ["the configured bearer token", BRIDGE_TOKEN, 200]
]) {
  test(`startBridge answers an initialize with ${label} as ${expected}`, async (t) => {
    const { port } = await startTestBridge(t);
    const response = await fetch(`http://127.0.0.1:${port}/mcp`, {
      method: "POST",
      headers: mcpHeaders(token),
      body: initializeBody()
    });
    assert.equal(response.status, expected);
    if (expected === 401) {
      assert.equal(response.headers.get("www-authenticate"), "Bearer");
      assert.deepEqual(await response.json(), { error: "Unauthorized" });
    } else {
      assert.equal((await response.json()).result.protocolVersion, PROTOCOL_VERSION);
    }
  });
}

test("startBridge completes a handshake, reuses the session, and tears it down", async (t) => {
  const { port, relays } = await startTestBridge(t);

  const { response, sessionId } = await openSession(port);
  assert.equal(response.status, 200);
  assert.ok(sessionId, "the transport assigns a session id");
  assert.equal(relays.length, 1, "one relay child per session");
  assert.equal(relays[0].received[0].method, "initialize", "the relay saw the handshake");

  const reused = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: "POST",
    headers: mcpHeaders(BRIDGE_TOKEN, {
      "mcp-session-id": sessionId,
      "MCP-Protocol-Version": PROTOCOL_VERSION
    }),
    body: JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} })
  });
  assert.equal(reused.status, 200);
  assert.deepEqual((await reused.json()).result, { echoed: "tools/list" });
  assert.equal(relays.length, 1, "an existing session must not spawn a second relay");

  const deleted = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: "DELETE",
    headers: mcpHeaders(BRIDGE_TOKEN, {
      "mcp-session-id": sessionId,
      "MCP-Protocol-Version": PROTOCOL_VERSION
    })
  });
  assert.ok(deleted.status < 300, `DELETE tears the session down (got ${deleted.status})`);
  assert.deepEqual(relays[0].signals, ["SIGTERM"], "DELETE reaps the relay child");

  const afterDelete = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: "POST",
    headers: mcpHeaders(BRIDGE_TOKEN, {
      "mcp-session-id": sessionId,
      "MCP-Protocol-Version": PROTOCOL_VERSION
    }),
    body: JSON.stringify({ jsonrpc: "2.0", id: 3, method: "tools/list", params: {} })
  });
  assert.equal(afterDelete.status, 404);
  assert.equal((await afterDelete.json()).error.code, -32001);
});

test("startBridge close() reaps every relay child", async (t) => {
  const { port, relays, running } = await startTestBridge(t);
  await openSession(port);
  assert.equal(relays.length, 1);
  assert.equal(relays[0].exitCode, null);

  await running.close();
  assert.deepEqual(relays[0].signals, ["SIGTERM"]);
  assert.equal(relays[0].exitCode, 0, "the relay child is not left running");
  await running.close();
  await running.closed;
});

// /healthz reveals nothing, and a liveness probe an orchestrator has to authenticate is useless.
for (const [label, token] of [
  ["without a token", undefined],
  ["with a token", BRIDGE_TOKEN]
]) {
  test(`startBridge serves /healthz ${label}`, async (t) => {
    const { port } = await startTestBridge(t);
    const response = await fetch(`http://127.0.0.1:${port}/healthz`, {
      headers: token === undefined ? {} : { Authorization: `Bearer ${token}` }
    });
    assert.equal(response.status, 200);
    assert.equal(await response.text(), "ok");
  });
}

// A client error reported as an internal server error makes well-behaved clients retry forever.
for (const [label, body, status, code] of [
  ["malformed JSON", "{ not json", 400, -32700],
  ["a non-initialize first request", '{"jsonrpc":"2.0","id":1,"method":"tools/list"}', 400, -32001]
]) {
  test(`startBridge answers ${label} with HTTP ${status} and ${code}`, async (t) => {
    const { port } = await startTestBridge(t);
    const response = await rawRequest(port, {
      headers: mcpHeaders(),
      body
    });
    assert.equal(response.status, status);
    assert.equal(JSON.parse(response.text).error.code, code);
  });
}

test("startBridge answers an over-large body with 413 rather than a reset connection", async (t) => {
  const { port } = await startTestBridge(t);
  const response = await rawRequest(port, {
    headers: mcpHeaders(),
    body: `{"padding":"${"x".repeat(DEFAULTS.bodyLimitBytes + 1024)}"}`
  });
  assert.equal(response.status, 413);
  assert.match(JSON.parse(response.text).error.message, /too large/);
});

test("startBridge abandons a stalled body and still shuts down promptly", async (t) => {
  // The body timeout is capped by the session timeout, so a short session timeout shortens it.
  const { port, running } = await startTestBridge(t, { sessionTimeout: 250 });
  const response = await rawRequest(port, {
    headers: mcpHeaders(BRIDGE_TOKEN, { "Content-Length": "4096" }),
    stall: true
  });
  assert.equal(response.status, 408);

  const started = Date.now();
  await running.close();
  assert.ok(
    Date.now() - started < 10_000,
    "close() must not wait out Node's 300s request timeout on a half-open connection"
  );
});

test("startBridge caps concurrent sessions, each of which owns a relay child", async (t) => {
  const { port, relays } = await startTestBridge(t, { maxSessions: 1 });
  const first = await openSession(port);
  assert.equal(first.response.status, 200);

  // Concurrent initializes: the cap counts sessions still being created, not just registered ones.
  const rejected = await Promise.all(
    [2, 3].map((id) =>
      fetch(`http://127.0.0.1:${port}/mcp`, {
        method: "POST",
        headers: mcpHeaders(),
        body: initializeBody(id)
      })
    )
  );
  for (const response of rejected) {
    assert.equal(response.status, 503);
    const payload = await response.json();
    assert.equal(payload.error.code, -32000);
    assert.match(payload.error.message, /--max-sessions/);
  }
  assert.equal(relays.length, 1, "a rejected initialize must not spawn a relay");
});

test("startBridge rejects unknown paths and methods", async (t) => {
  const { port } = await startTestBridge(t);
  for (const [requestPath, method] of [
    ["/nope", "POST"],
    ["/mcp", "PUT"]
  ]) {
    const response = await fetch(`http://127.0.0.1:${port}${requestPath}`, {
      method,
      headers: mcpHeaders()
    });
    assert.equal(response.status, 404, `${method} ${requestPath}`);
  }
});

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

function commandOptions(repoRoot, overrides = {}) {
  return {
    repoRoot,
    discover: true,
    host: DEFAULTS.host,
    port: DEFAULTS.port,
    endpointPath: "/mcp",
    ...probeOptions(),
    ...overrides
  };
}

test("runProbe reports the endpoint it handshook with", async (t) => {
  const port = await listeningPort(t);
  const logged = captureConsole(t, "log");
  const found = await runProbe(commandOptions(temporaryDirectory()), {
    candidates: [{ host: "127.0.0.1", port, endpointPath: "/mcp" }],
    fetchImpl: async () => initializeResponse(OK_PAYLOAD)
  });
  assert.equal(found.port, port);
  assert.match(logged.join("\n"), /Unity MCP is reachable at http:\/\/127\.0\.0\.1:/);

  const dead = await closedPort();
  await assert.rejects(
    () =>
      runProbe(commandOptions(temporaryDirectory()), {
        candidates: [{ host: "127.0.0.1", port: dead, endpointPath: "/mcp" }],
        fetchImpl: async () => initializeResponse(OK_PAYLOAD)
      }),
    /No Unity MCP endpoint responded/
  );
});

// Deliberately no injected `candidates`: passing one masks the narrowing --no-discover performs,
// which is how `probe --no-discover` shipped always-failing with an empty attempts list. The
// endpoint has to come from options.host/options.port.
test("--no-discover probes the configured endpoint rather than skipping the handshake", async (t) => {
  const runtime = { fetchImpl: async () => initializeResponse(OK_PAYLOAD) };
  captureConsole(t, "log");
  captureConsole(t, "warn");

  const live = { discover: false, host: "127.0.0.1", port: await listeningPort(t) };
  assert.equal(
    (await runProbe(commandOptions(temporaryDirectory(), live), runtime)).port,
    live.port
  );

  const repoRoot = temporaryDirectory();
  const dead = { discover: false, host: "127.0.0.1", port: await closedPort() };
  // The regression was an EMPTY attempts list, so naming the endpoint is the assertion.
  await assert.rejects(
    () => runProbe(commandOptions(repoRoot, dead), runtime),
    (error) => {
      assert.match(error.message, new RegExp(`127\\.0\\.0\\.1:${dead.port}`));
      return true;
    }
  );

  // configure still writes the endpoint the caller named when the probe finds nothing there.
  const url = await runConfigure(commandOptions(repoRoot, dead), runtime);
  assert.equal(url, `http://127.0.0.1:${dead.port}/mcp`);
  const written = JSON.parse(fs.readFileSync(clientConfigPaths(repoRoot).claudeCode, "utf8"));
  assert.equal(written.mcpServers["unity-mcp"].url, url);
});

// `unauthorized` means a bridge IS running there; only the token is wrong. Falling back to the
// default endpoint and minting a fresh token guarantees a 401 and persists the bogus token.
test("runConfigure refuses to write when a bridge rejects the token", async (t) => {
  const repoRoot = temporaryDirectory();
  const port = await listeningPort(t);
  await assert.rejects(
    () =>
      runConfigure(commandOptions(repoRoot), {
        candidates: [{ host: "127.0.0.1", port, endpointPath: "/mcp" }],
        fetchImpl: async () => initializeResponse("nope", { status: 401 })
      }),
    (error) => {
      assert.match(error.message, new RegExp(`http://127\\.0\\.0\\.1:${port}/mcp`));
      assert.doesNotMatch(error.message, /host\.docker\.internal/, "not the default endpoint");
      assert.match(error.message, /UNITY_MCP_BEARER_TOKEN/);
      assert.match(error.message, /--token/);
      return true;
    }
  );
  assert.equal(
    fs.existsSync(path.join(repoRoot, ".env.local")),
    false,
    "no bogus token is minted into .env.local"
  );
  assert.deepEqual(
    Object.values(clientConfigPaths(repoRoot)).filter((filePath) => fs.existsSync(filePath)),
    [],
    "no client config is written"
  );
});

test("runConfigure still configures when a later candidate handshakes", async (t) => {
  const repoRoot = temporaryDirectory();
  captureConsole(t, "log");
  const rejecting = await listeningPort(t);
  const accepting = await listeningPort(t);
  const url = await runConfigure(commandOptions(repoRoot, { logLevel: "none" }), {
    candidates: [
      { host: "127.0.0.1", port: rejecting, endpointPath: "/mcp" },
      { host: "127.0.0.1", port: accepting, endpointPath: "/mcp" }
    ],
    fetchImpl: async (target) =>
      target.includes(`:${rejecting}/`)
        ? initializeResponse("nope", { status: 401 })
        : initializeResponse(OK_PAYLOAD)
  });
  assert.equal(url, `http://127.0.0.1:${accepting}/mcp`);
  assert.ok(fs.existsSync(clientConfigPaths(repoRoot).claudeCode));
});

test("runConfigure falls back to the configured endpoint when nothing is listening", async (t) => {
  const repoRoot = temporaryDirectory();
  const warnings = captureConsole(t, "warn");
  captureConsole(t, "log");
  const url = await runConfigure(commandOptions(repoRoot, { host: "10.0.0.5", port: 9020 }), {
    candidates: [{ host: "127.0.0.1", port: await closedPort(), endpointPath: "/mcp" }],
    fetchImpl: async () => initializeResponse(OK_PAYLOAD)
  });
  assert.equal(url, "http://10.0.0.5:9020/mcp");
  assert.match(warnings.join("\n"), /No Unity MCP endpoint responded/);
  assert.match(
    fs.readFileSync(path.join(repoRoot, ".env.local"), "utf8"),
    /^UNITY_MCP_BEARER_TOKEN=[0-9a-f]{64}$/m,
    "an unreachable endpoint is a fresh setup, so a token is generated"
  );
});

for (const [label, argv, expected] of [
  ["no command", [], /Usage: node scripts\/mcp\/unity-mcp\.mjs/],
  ["--help", ["--help"], /Usage: node scripts\/mcp\/unity-mcp\.mjs/],
  ["a per-command --help", ["probe", "--help"], /--max-sessions COUNT/]
]) {
  test(`main prints usage for ${label}`, async (t) => {
    const logged = captureConsole(t, "log");
    await main(argv);
    assert.match(logged.join("\n"), expected);
  });
}

for (const [label, argv, expected] of [
  ["an unknown command", ["explode"], /Unknown command: explode/],
  ["a stray positional", ["probe", "extra"], /Unexpected argument: extra/],
  ["an unknown option", ["probe", "--nope=1"], /Unknown option: --nope/]
]) {
  test(`main rejects ${label}`, async () => {
    await assert.rejects(() => main(argv), expected);
  });
}
