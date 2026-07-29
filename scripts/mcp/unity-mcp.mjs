#!/usr/bin/env node

/**
 * Unity MCP endpoint discovery, client auto-configuration, and streamable-HTTP bridge.
 *
 *   node scripts/mcp/unity-mcp.mjs probe       Find a live Unity MCP endpoint and handshake with it.
 *   node scripts/mcp/unity-mcp.mjs configure   Discover, then write every MCP client config.
 *   node scripts/mcp/unity-mcp.mjs bridge      Serve the Unity relay over authenticated HTTP.
 *
 * Topology: the Unity editor and its relay binary run on the host; agents run in the devcontainer.
 * `bridge` runs beside Unity, `probe` and `configure` run beside the agent. Only `bridge` needs the
 * Unity project directory, which is why `--project` is validated for that command alone -- the
 * project path names a host filesystem location that does not exist inside the container.
 */

import { spawn } from "node:child_process";
import { randomBytes, randomUUID, timingSafeEqual } from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath, pathToFileURL } from "node:url";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { parse as parseToml } from "smol-toml";

export const REPO_ROOT = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));

export const DEFAULTS = Object.freeze({
  bindHost: "0.0.0.0",
  host: "host.docker.internal",
  port: 9020,
  endpointPath: "/mcp",
  protocolVersion: "2025-11-25",
  probeTimeout: 5_000,
  connectTimeout: 750,
  requestTimeout: 300_000,
  sessionTimeout: 60_000,
  bodyLimitBytes: 1_048_576
});

// Ports tried during discovery, after any explicitly configured one. 9020 is the bridge default;
// 9003 is the port the retired supergateway bridge used, kept so an already-running host keeps working.
export const FALLBACK_PORTS = Object.freeze([9020, 9003]);

// Hosts tried during discovery, after any explicitly configured one. host.docker.internal is the
// Docker Desktop bridge; the resolv.conf nameserver and default gateway cover WSL2 and plain Linux
// bridge networking respectively; localhost covers running the agent on the same box as Unity.
export const FALLBACK_HOSTS = Object.freeze(["host.docker.internal", "127.0.0.1"]);

const OPTION_NAMES = new Set([
  "bind",
  "host",
  "port",
  "path",
  "project",
  "relay",
  "request-timeout",
  "session-timeout",
  "timeout",
  "connect-timeout",
  "protocol-version",
  "log-level",
  "token",
  "no-discover"
]);

const FLAG_NAMES = new Set(["no-discover"]);

const ENV_KEYS = Object.freeze({
  bindHost: "UNITY_MCP_BIND_HOST",
  host: "UNITY_MCP_BRIDGE_HOST",
  port: "UNITY_MCP_BRIDGE_PORT",
  endpointPath: "UNITY_MCP_BRIDGE_PATH",
  projectPath: "UNITY_PROJECT_PATH",
  relayPath: "UNITY_MCP_RELAY_PATH",
  requestTimeout: "UNITY_MCP_REQUEST_TIMEOUT",
  sessionTimeout: "UNITY_MCP_SESSION_TIMEOUT",
  timeout: "UNITY_MCP_PROBE_TIMEOUT",
  connectTimeout: "UNITY_MCP_CONNECT_TIMEOUT",
  protocolVersion: "UNITY_MCP_PROTOCOL_VERSION",
  logLevel: "UNITY_MCP_LOG_LEVEL",
  bearerToken: "UNITY_MCP_BEARER_TOKEN"
});

function fail(message) {
  throw new Error(message);
}

function first(...values) {
  return values.find((value) => value !== undefined && value !== "");
}

// ---------------------------------------------------------------------------
// Argument and .env.local parsing
// ---------------------------------------------------------------------------

export function parseArgs(argv) {
  const result = { _: [] };
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      result._.push(token);
      continue;
    }
    const separator = token.indexOf("=");
    const name = token.slice(2, separator === -1 ? undefined : separator);
    if (!OPTION_NAMES.has(name)) {
      fail(`Unknown option: --${name}`);
    }
    if (FLAG_NAMES.has(name)) {
      if (separator !== -1) {
        fail(`--${name} does not take a value`);
      }
      result[name] = true;
      continue;
    }
    const value = separator === -1 ? argv[++index] : token.slice(separator + 1);
    if (value === undefined || value.startsWith("--")) {
      fail(`Missing value for --${name}`);
    }
    result[name] = value;
  }
  return result;
}

export function parseDotEnv(raw, source = ".env.local") {
  const values = {};
  for (const [index, original] of raw.split(/\r?\n/).entries()) {
    const line = original.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }
    const match = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (!match) {
      fail(`Invalid ${source} entry on line ${index + 1}`);
    }
    let value = match[2].trim();
    if (value.startsWith('"') || value.startsWith("'")) {
      const quote = value[0];
      let closing = -1;
      for (let cursor = 1; cursor < value.length; cursor += 1) {
        if (value[cursor] === quote && (quote === "'" || value[cursor - 1] !== "\\")) {
          closing = cursor;
          break;
        }
      }
      if (closing === -1 || !/^\s*(?:#.*)?$/.test(value.slice(closing + 1))) {
        fail(`Invalid quoted value in ${source} on line ${index + 1}`);
      }
      value = value.slice(1, closing);
    } else {
      const comment = value.search(/\s+#/);
      if (comment !== -1) {
        value = value.slice(0, comment).trimEnd();
      }
    }
    values[match[1]] = value;
  }
  return values;
}

function readLocalEnv(repoRoot) {
  const envPath = path.join(repoRoot, ".env.local");
  return fs.existsSync(envPath) ? parseDotEnv(fs.readFileSync(envPath, "utf8"), envPath) : {};
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

function integer(value, name, minimum, maximum) {
  if (!/^\d+$/.test(String(value))) {
    fail(`${name} must be an integer`);
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    fail(`${name} must be between ${minimum} and ${maximum}`);
  }
  return parsed;
}

function validateText(value, name) {
  if (/[\0\r\n]/.test(value)) {
    fail(`${name} contains an invalid control character`);
  }
  return value;
}

export function validateHost(value, name = "Host") {
  validateText(value, name);
  if (net.isIP(value)) {
    return value;
  }
  const candidate = value.endsWith(".") ? value.slice(0, -1) : value;
  const labels = candidate.split(".");
  const labelPattern = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/;
  if (
    candidate.length === 0 ||
    candidate.length > 253 ||
    !labels.every((label) => labelPattern.test(label))
  ) {
    fail(`Invalid ${name.toLowerCase()}: ${value}`);
  }
  return value;
}

export function validateEndpointPath(value) {
  const normalized = value.startsWith("/") ? value : `/${value}`;
  if (
    !/^\/[A-Za-z0-9._~!$&'()*+,;=:@%/-]*$/.test(normalized) ||
    normalized.includes("//") ||
    /%(?![0-9A-Fa-f]{2})/.test(normalized)
  ) {
    fail(`Invalid MCP endpoint path: ${value}`);
  }
  const decoded = decodeURIComponent(normalized);
  if (decoded.includes("//") || decoded.split("/").some((part) => part === "." || part === "..")) {
    fail(`Invalid MCP endpoint path: ${value}`);
  }
  return normalized;
}

function validateToken(value) {
  if (value === undefined) {
    return undefined;
  }
  if (!/^[A-Za-z0-9._~-]{32,256}$/.test(value)) {
    fail("Bearer token must be 32-256 URL-safe characters");
  }
  return value;
}

// ---------------------------------------------------------------------------
// Option resolution
// ---------------------------------------------------------------------------

/**
 * `repoRoot` is where MCP client configs and `.env.local` live; it is always this repository.
 * `projectPath` is the Unity project the relay opens and is only meaningful on the host, so it is
 * resolved lazily and validated by `requireProjectPath` from the `bridge` command alone.
 */
export function resolveOptions(args, environment = process.env, localValues, repoRoot = REPO_ROOT) {
  const local = localValues ?? readLocalEnv(repoRoot);
  const get = (argName, key, fallback) =>
    first(args[argName], environment[ENV_KEYS[key]], local[ENV_KEYS[key]], fallback);

  const explicitHost = first(args.host, environment[ENV_KEYS.host], local[ENV_KEYS.host]);
  const explicitPort = first(args.port, environment[ENV_KEYS.port], local[ENV_KEYS.port]);
  const projectPath = first(
    args.project,
    environment[ENV_KEYS.projectPath],
    local[ENV_KEYS.projectPath]
  );

  const options = {
    repoRoot,
    bindHost: validateHost(get("bind", "bindHost", DEFAULTS.bindHost), "Bind host"),
    host: validateHost(explicitHost ?? DEFAULTS.host),
    explicitHost: explicitHost === undefined ? undefined : validateHost(explicitHost),
    port: integer(explicitPort ?? DEFAULTS.port, "Port", 1, 65_535),
    explicitPort: explicitPort === undefined ? undefined : integer(explicitPort, "Port", 1, 65_535),
    endpointPath: validateEndpointPath(get("path", "endpointPath", DEFAULTS.endpointPath)),
    projectPath: projectPath === undefined ? undefined : path.resolve(projectPath),
    relayPath: first(args.relay, environment[ENV_KEYS.relayPath], local[ENV_KEYS.relayPath]),
    requestTimeout: integer(
      get("request-timeout", "requestTimeout", DEFAULTS.requestTimeout),
      "Request timeout",
      1,
      86_400_000
    ),
    sessionTimeout: integer(
      get("session-timeout", "sessionTimeout", DEFAULTS.sessionTimeout),
      "Session timeout",
      1,
      86_400_000
    ),
    timeout: integer(get("timeout", "timeout", DEFAULTS.probeTimeout), "Probe timeout", 1, 300_000),
    connectTimeout: integer(
      get("connect-timeout", "connectTimeout", DEFAULTS.connectTimeout),
      "Connect timeout",
      1,
      60_000
    ),
    protocolVersion: validateText(
      get("protocol-version", "protocolVersion", DEFAULTS.protocolVersion),
      "Protocol version"
    ),
    logLevel: get("log-level", "logLevel", "info"),
    bearerToken: validateToken(get("token", "bearerToken", undefined)),
    discover: args["no-discover"] !== true
  };

  if (options.relayPath) {
    options.relayPath = path.resolve(options.repoRoot, options.relayPath);
  }
  if (!/^(?:debug|info|none)$/.test(options.logLevel)) {
    fail("Log level must be debug, info, or none");
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(options.protocolVersion)) {
    fail("Protocol version must use YYYY-MM-DD format");
  }
  return options;
}

export function requireProjectPath(options) {
  if (!options.projectPath) {
    fail(`Unity project path is required. Pass --project or set ${ENV_KEYS.projectPath}.`);
  }
  if (!fs.existsSync(options.projectPath) || !fs.statSync(options.projectPath).isDirectory()) {
    fail(`Unity project directory does not exist: ${options.projectPath}`);
  }
  return options.projectPath;
}

export function endpointUrl({ host, port, endpointPath }) {
  const formatted = net.isIP(host) === 6 ? `[${host}]` : host;
  return `http://${formatted}:${port}${endpointPath}`;
}

// ---------------------------------------------------------------------------
// Endpoint discovery
// ---------------------------------------------------------------------------

/** Nameserver entries in /etc/resolv.conf. Under WSL2 this is the Windows host. */
export function resolvConfHosts(raw) {
  if (!raw) {
    return [];
  }
  return raw
    .split(/\r?\n/)
    .map((line) => /^\s*nameserver\s+(\S+)\s*$/.exec(line))
    .filter(Boolean)
    .map((match) => match[1])
    .filter((address) => net.isIP(address) === 4);
}

/** Default-route gateways from /proc/net/route (little-endian hex IPv4). */
export function procNetRouteGateways(raw) {
  if (!raw) {
    return [];
  }
  const gateways = [];
  for (const line of raw.split(/\r?\n/).slice(1)) {
    const fields = line.trim().split(/\s+/);
    if (fields.length < 3 || fields[1] !== "00000000" || !/^[0-9A-Fa-f]{8}$/.test(fields[2])) {
      continue;
    }
    const value = Number.parseInt(fields[2], 16);
    if (value === 0) {
      continue;
    }
    const octets = [
      value & 0xff,
      (value >>> 8) & 0xff,
      (value >>> 16) & 0xff,
      (value >>> 24) & 0xff
    ];
    gateways.push(octets.join("."));
  }
  return gateways;
}

function readTextOrEmpty(filePath) {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch {
    return "";
  }
}

/**
 * Candidate endpoints in priority order, de-duplicated. An explicitly configured host or port always
 * comes first so discovery can never silently override a deliberate setting.
 */
export function endpointCandidates(options, runtime = {}) {
  const readFile = runtime.readFile ?? readTextOrEmpty;
  const hosts = [
    options.explicitHost,
    ...FALLBACK_HOSTS,
    ...resolvConfHosts(readFile("/etc/resolv.conf")),
    ...procNetRouteGateways(readFile("/proc/net/route"))
  ].filter(Boolean);
  const ports = [options.explicitPort, ...FALLBACK_PORTS].filter(Boolean);

  const seen = new Set();
  const candidates = [];
  for (const port of ports) {
    for (const host of hosts) {
      const key = `${host}:${port}`;
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
      candidates.push({ host, port, endpointPath: options.endpointPath });
    }
  }
  return candidates;
}

export function tcpReachable(host, port, timeout) {
  return new Promise((resolve) => {
    const socket = new net.Socket();
    const settle = (value) => {
      socket.removeAllListeners();
      socket.destroy();
      resolve(value);
    };
    socket.setTimeout(timeout);
    socket.once("connect", () => settle(true));
    socket.once("timeout", () => settle(false));
    socket.once("error", () => settle(false));
    socket.connect(port, host);
  });
}

function parseProbePayload(contentType, body) {
  const candidates = contentType.includes("text/event-stream")
    ? body
        .split(/\r?\n/)
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).trim())
        .filter((line) => line && line !== "[DONE]")
    : [body];
  for (const candidate of candidates) {
    try {
      const message = JSON.parse(candidate);
      if (message?.jsonrpc === "2.0" && message.id === 1) {
        return message;
      }
    } catch {
      /* Continue to the next server-sent event. */
    }
  }
  return undefined;
}

/**
 * Complete an MCP `initialize` handshake against one endpoint. Returns a classified result rather
 * than throwing so discovery can report every attempt; `status: "unauthorized"` in particular means
 * a bridge is running but the local bearer token does not match it, which needs different advice
 * from "nothing is listening".
 */
export async function probeEndpoint(candidate, options, fetchImpl = fetch) {
  const url = endpointUrl(candidate);
  if (!(await tcpReachable(candidate.host, candidate.port, options.connectTimeout))) {
    return { url, ok: false, status: "unreachable", detail: "no TCP listener" };
  }

  const headers = {
    Accept: "application/json, text/event-stream",
    "Content-Type": "application/json",
    "MCP-Protocol-Version": options.protocolVersion
  };
  if (options.bearerToken) {
    headers.Authorization = `Bearer ${options.bearerToken}`;
  }

  let response;
  let body;
  try {
    response = await fetchImpl(url, {
      method: "POST",
      headers,
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
          protocolVersion: options.protocolVersion,
          capabilities: {},
          clientInfo: { name: "unity-mcp-probe", version: "1.0.0" }
        }
      }),
      signal: AbortSignal.timeout(options.timeout)
    });
    body = await response.text();
  } catch (error) {
    return { url, ok: false, status: "unreachable", detail: error.message };
  }

  if (response.status === 401 || response.status === 403) {
    return { url, ok: false, status: "unauthorized", detail: `HTTP ${response.status}` };
  }
  if (!response.ok) {
    return {
      url,
      ok: false,
      status: "http-error",
      detail: `HTTP ${response.status} ${body.slice(0, 120)}`
    };
  }

  const message = parseProbePayload(response.headers.get("content-type") ?? "", body);
  if (!message || message.error || typeof message.result?.protocolVersion !== "string") {
    return {
      url,
      ok: false,
      status: "malformed",
      detail: body.slice(0, 120) || "no JSON-RPC result"
    };
  }

  const sessionId = response.headers.get("mcp-session-id");
  const negotiated = message.result.protocolVersion;
  if (sessionId) {
    // Close the session we just opened so probing does not leak relay child processes on the host.
    await fetchImpl(url, {
      method: "DELETE",
      headers: {
        Accept: "application/json, text/event-stream",
        ...(options.bearerToken ? { Authorization: `Bearer ${options.bearerToken}` } : {}),
        "Mcp-Session-Id": sessionId,
        "MCP-Protocol-Version": negotiated
      },
      signal: AbortSignal.timeout(options.timeout)
    }).catch(() => {});
  }

  return { url, ok: true, status: "ok", sessionId, protocolVersion: negotiated, ...candidate };
}

/** Probe every candidate in order and return the first that completes a handshake. */
export async function discoverEndpoint(options, runtime = {}) {
  const fetchImpl = runtime.fetchImpl ?? fetch;
  const candidates = runtime.candidates ?? endpointCandidates(options, runtime);
  const attempts = [];
  for (const candidate of candidates) {
    const result = await probeEndpoint(candidate, options, fetchImpl);
    attempts.push(result);
    if (result.ok) {
      return { found: result, attempts };
    }
  }
  return { found: undefined, attempts };
}

export function describeAttempts(attempts) {
  const interesting = attempts.filter((attempt) => attempt.status !== "unreachable");
  const shown = interesting.length > 0 ? interesting : attempts;
  return shown
    .map((attempt) => `  ${attempt.url} - ${attempt.status} (${attempt.detail ?? "no detail"})`)
    .join("\n");
}

// ---------------------------------------------------------------------------
// Client configuration
// ---------------------------------------------------------------------------

function stageFile(filePath, content) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temporary = `${filePath}.${process.pid}.${randomBytes(8).toString("hex")}.tmp`;
  fs.writeFileSync(temporary, content, { encoding: "utf8", mode: 0o600, flag: "wx" });
  return temporary;
}

function atomicWrite(filePath, content) {
  const temporary = stageFile(filePath, content);
  try {
    fs.renameSync(temporary, filePath);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

/**
 * Write several files as one unit. Every file is staged before any is committed, and a failure part
 * way through rolls back the files already renamed, so a crash cannot leave one agent pointed at a
 * new endpoint while another still holds the old one.
 */
export function transactionalWrite(writes, beforeCommit = () => {}) {
  const changed = writes.filter(
    ([filePath, content]) =>
      !fs.existsSync(filePath) || fs.readFileSync(filePath, "utf8") !== content
  );
  const staged = changed.map(([filePath, content]) => ({
    filePath,
    content,
    temporary: stageFile(filePath, content),
    existed: fs.existsSync(filePath),
    original: fs.existsSync(filePath) ? fs.readFileSync(filePath, "utf8") : undefined
  }));
  const committed = [];
  try {
    for (let index = 0; index < staged.length; index += 1) {
      beforeCommit(index, staged[index].filePath);
      fs.renameSync(staged[index].temporary, staged[index].filePath);
      committed.push(staged[index]);
    }
  } catch (error) {
    for (const item of committed.reverse()) {
      if (item.existed) {
        atomicWrite(item.filePath, item.original);
      } else {
        fs.rmSync(item.filePath, { force: true });
      }
    }
    throw error;
  } finally {
    for (const item of staged) {
      fs.rmSync(item.temporary, { force: true });
    }
  }
  return changed.map(([filePath]) => filePath);
}

function ensureBearerToken(options) {
  if (options.bearerToken) {
    return options;
  }
  const bearerToken = randomBytes(32).toString("hex");
  const envPath = path.join(options.repoRoot, ".env.local");
  const current = fs.existsSync(envPath) ? fs.readFileSync(envPath, "utf8") : "";
  const prefix = current && !current.endsWith("\n") ? "\n" : "";
  atomicWrite(envPath, `${current}${prefix}${ENV_KEYS.bearerToken}=${bearerToken}\n`);
  return { ...options, bearerToken };
}

function readJsonObject(filePath) {
  if (!fs.existsSync(filePath) || !fs.readFileSync(filePath, "utf8").trim()) {
    return {};
  }
  let parsed;
  try {
    parsed = JSON.parse(fs.readFileSync(filePath, "utf8"));
  } catch (error) {
    fail(`Invalid JSON in ${filePath}: ${error.message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    fail(`Expected a JSON object in ${filePath}`);
  }
  return parsed;
}

export function prepareJsonServer(filePath, collection, server) {
  const document = readJsonObject(filePath);
  const existing = document[collection];
  if (
    existing !== undefined &&
    (!existing || typeof existing !== "object" || Array.isArray(existing))
  ) {
    fail(`Expected ${collection} to be an object in ${filePath}`);
  }
  document[collection] = { ...(existing ?? {}), "unity-mcp": server };
  return `${JSON.stringify(document, null, 2)}\n`;
}

function tomlString(value) {
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

/**
 * Decide whether a TOML header line opens the table this tool owns. Parsing the single line with a
 * sentinel key is how a `[mcp_servers.unity-mcp]` header is told apart from any other header without
 * hand-writing a TOML grammar.
 */
function classifyTomlHeader(line) {
  if (!line.trimStart().startsWith("[")) {
    return undefined;
  }
  const marker = "__dxm_mcp_table_marker_7d0f__";
  try {
    const parsed = parseToml(`${line}\n${marker} = true\n`);
    const owned = parsed.mcp_servers?.["unity-mcp"]?.[marker] === true;
    const hasMarker = JSON.stringify(parsed).includes(`"${marker}":true`);
    return hasMarker ? { owned } : undefined;
  } catch {
    return undefined;
  }
}

export function mergeCodexToml(raw, url, bearerToken) {
  let parsed;
  try {
    parsed = raw.trim() ? parseToml(raw) : {};
  } catch (error) {
    fail(`Invalid TOML in Codex config: ${error.message}`);
  }
  const block = [
    "[mcp_servers.unity-mcp]",
    `url = ${tomlString(url)}`,
    `http_headers = { Authorization = ${tomlString(`Bearer ${bearerToken}`)} }`,
    "startup_timeout_sec = 20",
    "tool_timeout_sec = 120",
    "enabled = true",
    ""
  ].join("\n");

  const lines = raw.replace(/\r\n/g, "\n").split("\n");
  const owned = lines
    .map((line, index) => ({ index, header: classifyTomlHeader(line) }))
    .filter((item) => item.header?.owned)
    .map((item) => item.index);
  if (owned.length > 1) {
    fail("Duplicate unity-mcp table in Codex config");
  }
  if (owned.length === 0) {
    if (parsed.mcp_servers?.["unity-mcp"] !== undefined) {
      fail("Unsupported inline or dotted unity-mcp definition in Codex config");
    }
    return `${raw.trimEnd()}${raw.trim() ? "\n\n" : ""}${block}`;
  }

  const start = owned[0];
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (classifyTomlHeader(lines[index])) {
      end = index;
      break;
    }
  }
  lines.splice(start, end - start, ...block.trimEnd().split("\n"), "");
  const result = lines.join("\n").replace(/\n*$/, "\n");
  try {
    parseToml(result);
  } catch (error) {
    fail(`Generated invalid Codex TOML: ${error.message}`);
  }
  return result;
}

/** Every MCP client config this repository owns, keyed by the schema each client expects. */
export function clientConfigPaths(repoRoot) {
  return {
    claudeCode: path.join(repoRoot, ".mcp.json"),
    cursor: path.join(repoRoot, ".cursor", "mcp.json"),
    vscode: path.join(repoRoot, ".vscode", "mcp.json"),
    codex: path.join(repoRoot, ".codex", "config.toml")
  };
}

export function configure(inputOptions, endpoint, beforeCommit) {
  const options = ensureBearerToken(inputOptions);
  const url = endpointUrl(endpoint);
  const server = { type: "http", url, headers: { Authorization: `Bearer ${options.bearerToken}` } };
  const paths = clientConfigPaths(options.repoRoot);
  const codexRaw = fs.existsSync(paths.codex) ? fs.readFileSync(paths.codex, "utf8") : "";

  const written = transactionalWrite(
    [
      [paths.claudeCode, prepareJsonServer(paths.claudeCode, "mcpServers", server)],
      [paths.cursor, prepareJsonServer(paths.cursor, "mcpServers", server)],
      [paths.vscode, prepareJsonServer(paths.vscode, "servers", server)],
      [paths.codex, mergeCodexToml(codexRaw, url, options.bearerToken)]
    ],
    beforeCommit
  );
  return { url, written };
}

// ---------------------------------------------------------------------------
// Relay discovery and the bridge server
// ---------------------------------------------------------------------------

export function relayCandidates({
  platform = process.platform,
  arch = process.arch,
  home = os.homedir()
} = {}) {
  const root = path.join(home, ".unity", "relay");
  const names =
    platform === "win32"
      ? ["relay_win.exe", "relay_windows.exe", "relay.exe"]
      : platform === "darwin"
        ? [
            `relay_mac_${arch}.app/Contents/MacOS/relay_mac_${arch}`,
            `relay_macos_${arch}.app/Contents/MacOS/relay_macos_${arch}`,
            `relay_mac_${arch}`,
            "relay_mac",
            "relay"
          ]
        : platform === "linux"
          ? [`relay_linux_${arch}`, "relay_linux", "relay"]
          : [];
  return names.map((name) => path.join(root, ...name.split("/")));
}

export function findRelay(override, runtime = {}) {
  const candidates = override ? [path.resolve(override)] : relayCandidates(runtime);
  const found = candidates.find((candidate) => {
    if (!fs.existsSync(candidate) || !fs.statSync(candidate).isFile()) {
      return false;
    }
    if ((runtime.platform ?? process.platform) !== "win32") {
      try {
        fs.accessSync(candidate, fs.constants.X_OK);
      } catch {
        return false;
      }
    }
    return true;
  });
  if (!found) {
    fail(
      `Unity MCP relay not found or not executable. ${
        override ? `Checked: ${candidates[0]}` : `Searched: ${candidates.join(", ")}`
      }`
    );
  }
  return found;
}

export function buildRelayArgs(projectPath) {
  return ["--mcp", "--project-path", path.resolve(projectPath)];
}

export async function assertPortAvailable(port, host = DEFAULTS.bindHost) {
  await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once("error", (error) =>
      reject(new Error(`Port ${port} is unavailable on ${host}: ${error.message}`))
    );
    server.listen({ port, host, exclusive: true }, () => server.close(resolve));
  });
}

function authorized(request, token) {
  const received = Buffer.from(request.headers.authorization ?? "");
  const expected = Buffer.from(`Bearer ${token}`);
  return received.length === expected.length && timingSafeEqual(received, expected);
}

function log(options, level, message) {
  if (options.logLevel === "none" || (level === "debug" && options.logLevel !== "debug")) {
    return;
  }
  (level === "error" ? console.error : console.log)(message);
}

function readJsonBody(request, limitBytes) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size > limitBytes) {
        reject(new Error("Request body too large"));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.once("error", reject);
    request.once("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8");
      if (!raw.trim()) {
        resolve(undefined);
        return;
      }
      try {
        resolve(JSON.parse(raw));
      } catch (error) {
        reject(new Error(`Invalid JSON body: ${error.message}`));
      }
    });
  });
}

function sendJson(response, statusCode, payload) {
  const body = JSON.stringify(payload);
  response.writeHead(statusCode, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  });
  response.end(body);
}

export async function startBridge(inputOptions, runtime = {}) {
  const options = ensureBearerToken(inputOptions);
  const projectPath = requireProjectPath(options);
  const relayPath = findRelay(options.relayPath, runtime.relayRuntime);
  await assertPortAvailable(options.port, options.bindHost);

  const sessions = new Map();
  const provisionalSessions = new Set();

  const disposeSession = async (session) => {
    if (!session || session.disposed) {
      return;
    }
    session.disposed = true;
    provisionalSessions.delete(session);
    if (session.sessionId) {
      sessions.delete(session.sessionId);
    }
    clearTimeout(session.timer);
    if (!session.stopping) {
      session.stopping = true;
      if (session.child.exitCode === null && session.child.signalCode === null) {
        session.child.kill("SIGTERM");
      }
      const force = setTimeout(() => {
        if (session.child.exitCode === null && session.child.signalCode === null) {
          session.child.kill("SIGKILL");
        }
      }, 3_000);
      force.unref();
    }
    await session.transport.close().catch(() => {});
  };

  const touch = (sessionId) => {
    const session = sessions.get(sessionId);
    if (!session || session.pendingRequests.size) {
      return;
    }
    clearTimeout(session.timer);
    session.timer = setTimeout(() => disposeSession(session), options.sessionTimeout);
    session.timer.unref();
  };

  const armRequestTimeout = (sessionId) => {
    const session = sessions.get(sessionId);
    if (!session) {
      return;
    }
    clearTimeout(session.timer);
    session.timer = setTimeout(() => disposeSession(session), options.requestTimeout);
    session.timer.unref();
  };

  const createSession = async () => {
    let sessionId;
    const transport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => randomUUID(),
      enableJsonResponse: true,
      onsessioninitialized: (id) => {
        sessionId = id;
        session.sessionId = id;
        provisionalSessions.delete(session);
        sessions.set(id, session);
        touch(id);
      }
    });
    const server = new Server(
      { name: "dxmessaging-unity-mcp-bridge", version: "1.0.0" },
      { capabilities: {} }
    );
    await server.connect(transport);
    const relayArgs = buildRelayArgs(projectPath);
    const child = runtime.spawnRelay
      ? runtime.spawnRelay(relayPath, relayArgs)
      : spawn(relayPath, relayArgs, {
          stdio: ["pipe", "pipe", "pipe"],
          shell: false,
          windowsHide: true
        });
    const session = {
      child,
      server,
      transport,
      timer: undefined,
      stopping: false,
      disposed: false,
      sessionId: undefined,
      pendingRequests: new Set()
    };
    provisionalSessions.add(session);
    session.timer = setTimeout(() => disposeSession(session), options.requestTimeout);
    session.timer.unref();

    let buffer = "";
    child.stdout.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      const lines = buffer.split(/\r?\n/);
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        if (!line.trim()) {
          continue;
        }
        try {
          const message = JSON.parse(line);
          if (message.id !== undefined && !message.method) {
            session.pendingRequests.delete(`${typeof message.id}:${message.id}`);
          }
          if (sessionId) {
            touch(sessionId);
          }
          Promise.resolve(transport.send(message)).catch((error) =>
            log(options, "error", `Relay response failed: ${error.message}`)
          );
        } catch {
          log(options, "error", `Unity relay emitted non-JSON output: ${line.slice(0, 200)}`);
        }
      }
    });
    child.stderr.on("data", (chunk) =>
      log(options, "error", `Unity relay: ${chunk.toString("utf8").trimEnd()}`)
    );
    child.stdin.on("error", (error) => {
      log(options, "error", `Unity relay input failed: ${error.message}`);
      disposeSession(session).catch(() => {});
    });
    child.once("error", (error) => {
      log(options, "error", `Unity relay failed: ${error.message}`);
      disposeSession(session).catch(() => {});
    });
    child.once("exit", () => {
      if (!session.stopping) {
        disposeSession(session).catch(() => {});
      }
    });

    transport.onmessage = (message) => {
      const startsRequest = message.id !== undefined && message.method;
      const wasIdle = session.pendingRequests.size === 0;
      if (startsRequest) {
        session.pendingRequests.add(`${typeof message.id}:${message.id}`);
      }
      child.stdin.write(`${JSON.stringify(message)}\n`);
      if (sessionId && startsRequest && wasIdle && session.pendingRequests.size) {
        armRequestTimeout(sessionId);
      } else if (sessionId) {
        touch(sessionId);
      }
    };
    transport.onclose = () => disposeSession(session);
    transport.onerror = (error) => {
      log(options, "error", `MCP transport error: ${error.message}`);
      disposeSession(session).catch(() => {});
    };
    return transport;
  };

  const httpServer = http.createServer((request, response) => {
    void (async () => {
      try {
        if (!authorized(request, options.bearerToken)) {
          response.setHeader("WWW-Authenticate", "Bearer");
          sendJson(response, 401, { error: "Unauthorized" });
          return;
        }
        const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "localhost"}`);
        if (url.pathname === "/healthz") {
          response.writeHead(200, { "Content-Type": "text/plain" });
          response.end("ok");
          return;
        }
        if (
          url.pathname !== options.endpointPath ||
          !["POST", "GET", "DELETE"].includes(request.method ?? "")
        ) {
          sendJson(response, 404, { error: "Not found" });
          return;
        }

        const body =
          request.method === "POST"
            ? await readJsonBody(request, DEFAULTS.bodyLimitBytes)
            : undefined;
        const sessionId = request.headers["mcp-session-id"];
        let transport = sessionId ? sessions.get(sessionId)?.transport : undefined;
        if (!transport && request.method === "POST" && !sessionId && isInitializeRequest(body)) {
          transport = await createSession();
        }
        if (!transport) {
          sendJson(response, sessionId ? 404 : 400, {
            jsonrpc: "2.0",
            id: null,
            error: {
              code: -32001,
              message: sessionId ? "Session not found" : "Initialize request required"
            }
          });
          return;
        }
        if (sessionId) {
          touch(sessionId);
        }
        await transport.handleRequest(request, response, body);
      } catch (error) {
        if (!response.headersSent) {
          sendJson(response, 500, {
            jsonrpc: "2.0",
            id: null,
            error: { code: -32603, message: "Bridge failure" }
          });
        }
        log(options, "error", `Bridge request failed: ${error.message}`);
      }
    })();
  });

  await new Promise((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(options.port, options.bindHost, resolve);
  });

  let closeResolve;
  const closed = new Promise((resolve) => {
    closeResolve = resolve;
  });
  let closing = false;
  const close = async () => {
    if (closing) {
      return closed;
    }
    closing = true;
    await Promise.all(
      [...new Set([...sessions.values(), ...provisionalSessions])].map(disposeSession)
    );
    await new Promise((resolve) => httpServer.close(resolve));
    closeResolve();
    return closed;
  };
  return { close, closed, httpServer, options, bearerToken: options.bearerToken };
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async function resolveEndpoint(options, runtime) {
  if (!options.discover) {
    return {
      endpoint: { host: options.host, port: options.port, endpointPath: options.endpointPath },
      attempts: []
    };
  }
  const { found, attempts } = await discoverEndpoint(options, runtime);
  if (found) {
    return {
      endpoint: { host: found.host, port: found.port, endpointPath: found.endpointPath },
      attempts,
      found
    };
  }
  return { endpoint: undefined, attempts };
}

export async function runProbe(options, runtime = {}) {
  const { found, attempts } = await resolveEndpoint(options, runtime);
  if (!found) {
    fail(`No Unity MCP endpoint responded. Attempts:\n${describeAttempts(attempts)}`);
  }
  console.log(`Unity MCP is reachable at ${found.url} (protocol ${found.protocolVersion}).`);
  return found;
}

export async function runConfigure(options, runtime = {}) {
  const { endpoint, attempts, found } = await resolveEndpoint(options, runtime);
  const target = endpoint ?? {
    host: options.host,
    port: options.port,
    endpointPath: options.endpointPath
  };
  if (!found && options.discover) {
    console.warn(
      `No Unity MCP endpoint responded; configuring the default ${endpointUrl(target)} anyway. Attempts:\n${describeAttempts(attempts)}`
    );
  }
  const { url, written } = configure(options, target);
  const summary = written.length
    ? written.map((filePath) => path.relative(options.repoRoot, filePath)).join(", ")
    : "no changes";
  console.log(`Configured Unity MCP endpoint ${url} (${summary}).`);
  return url;
}

export async function runBridge(options) {
  const running = await startBridge(options);
  console.log(`Unity project: ${running.options.projectPath}`);
  console.log(
    `Unity MCP bridge: http://${running.options.bindHost}:${running.options.port}${running.options.endpointPath} (bearer authentication required)`
  );
  const stop = () => {
    running.close().catch((error) => console.error(`Bridge shutdown failed: ${error.message}`));
  };
  process.once("SIGINT", stop);
  process.once("SIGTERM", stop);
  await running.closed;
  process.removeListener("SIGINT", stop);
  process.removeListener("SIGTERM", stop);
}

function usage() {
  return [
    "Usage: node scripts/mcp/unity-mcp.mjs <probe|configure|bridge> [options]",
    "",
    "  probe      Discover a live Unity MCP endpoint and complete an initialize handshake.",
    "  configure  Discover, then write .mcp.json, .cursor/mcp.json, .vscode/mcp.json, .codex/config.toml.",
    "  bridge     Serve the Unity relay over authenticated streamable HTTP (run next to Unity).",
    "",
    "Options:",
    "  --host HOST                 Endpoint host; skips discovery of other hosts",
    "  --port PORT                 Endpoint port; skips discovery of other ports",
    "  --path PATH                 Streamable HTTP path (default: /mcp)",
    "  --no-discover               Use the configured host/port without probing",
    "  --bind HOST                 Bridge bind interface (default: 0.0.0.0)",
    "  --project PATH              Unity project directory (bridge only)",
    "  --relay PATH                Unity relay executable override",
    "  --token TOKEN               32-256 character bearer token (generated into .env.local if omitted)",
    "  --timeout MS                Per-endpoint handshake timeout (default: 5000)",
    "  --connect-timeout MS        Per-endpoint TCP connect timeout (default: 750)",
    "  --session-timeout MS        Idle session timeout (default: 60000)",
    "  --request-timeout MS        Active-request hard limit (default: 300000)",
    "  --protocol-version VERSION  MCP protocol version (default: 2025-11-25)",
    "  --log-level LEVEL           debug, info, or none"
  ].join("\n");
}

export async function main(argv = process.argv.slice(2)) {
  const [command, ...rest] = argv;
  if (!command || command === "--help" || command === "-h") {
    console.log(usage());
    return;
  }
  if (!["bridge", "configure", "probe"].includes(command)) {
    fail(`Unknown command: ${command}`);
  }
  if (rest.includes("--help") || rest.includes("-h")) {
    console.log(usage());
    return;
  }
  const args = parseArgs(rest);
  if (args._.length) {
    fail(`Unexpected argument: ${args._[0]}`);
  }
  const options = resolveOptions(args);
  if (command === "probe") {
    await runProbe(options);
  }
  if (command === "configure") {
    await runConfigure(options);
  }
  if (command === "bridge") {
    await runBridge(options);
  }
}

const entry = process.argv[1] ? pathToFileURL(path.resolve(process.argv[1])).href : "";
if (import.meta.url === entry) {
  main().catch((error) => {
    console.error(`unity-mcp: ${error.message}`);
    process.exitCode = 1;
  });
}
