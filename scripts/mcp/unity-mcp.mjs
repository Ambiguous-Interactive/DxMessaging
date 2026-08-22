#!/usr/bin/env node
/**
 * Unity MCP endpoint discovery, client auto-configuration, and streamable-HTTP bridge.
 *
 *   node scripts/mcp/unity-mcp.mjs probe       Find an endpoint advertising Unity_RunCommand.
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
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import {
  StreamableHTTPClientTransport,
  StreamableHTTPError
} from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest, McpError } from "@modelcontextprotocol/sdk/types.js";
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
  bodyLimitBytes: 1_048_576,
  // Upper bound on how long the bridge waits for a request body. Capped by the session timeout so a
  // client that sends headers and then stalls cannot hold a socket (and shutdown) open.
  bodyTimeout: 15_000,
  maxSessions: 8
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
  "max-sessions",
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
  maxSessions: "UNITY_MCP_MAX_SESSIONS",
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
// Argument and .env.local parsing
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
    if (value === "") {
      fail(`--${name} requires a non-empty value`);
    }
    result[name] = value;
  }
  return result;
}
// A quoted value runs to the last quote that leaves only whitespace or a comment behind. The
// alternation lets the engine backtrack over a trailing backslash, so a Windows path written as
// "D:\Program Files\Proj\" parses while an escaped quote inside the value ("say \"hi\"") still does.
const QUOTED_VALUE = Object.freeze({
  '"': /^"((?:\\"|[^"])*)"\s*(?:#.*)?$/,
  "'": /^'((?:\\'|[^'])*)'\s*(?:#.*)?$/
});
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
    const quote = QUOTED_VALUE[value[0]] ? value[0] : undefined;
    if (quote) {
      const quoted = QUOTED_VALUE[quote].exec(value);
      if (!quoted) {
        fail(`Invalid quoted value in ${source} on line ${index + 1}`);
      }
      // Only double quotes carry escapes, matching POSIX shell and dotenv semantics.
      value = quote === '"' ? quoted[1].replace(/\\(["\\])/g, "$1") : quoted[1];
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
/**
 * `.env.local` is shared with unrelated tooling, so one line this parser cannot read must not abort
 * `probe`, `configure`, or `bridge`. Each line is parsed on its own and a bad one is warned about and
 * skipped; `parseDotEnv` itself stays strict.
 */
export function readLocalEnv(repoRoot) {
  const envPath = path.join(repoRoot, ".env.local");
  if (!fs.existsSync(envPath)) {
    return {};
  }
  const values = {};
  for (const [index, line] of fs.readFileSync(envPath, "utf8").split(/\r?\n/).entries()) {
    try {
      Object.assign(values, parseDotEnv(line, envPath));
    } catch {
      console.warn(`unity-mcp: ignoring unparsable ${envPath} line ${index + 1}: ${line.trim()}`);
    }
  }
  return values;
}
// Validation
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
  let decoded;
  try {
    // Syntactically valid escapes can still be invalid UTF-8 (for example /%FF), which throws.
    decoded = decodeURIComponent(normalized);
  } catch {
    fail(`Invalid MCP endpoint path: ${value}`);
  }
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
// Option resolution

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
    maxSessions: integer(
      get("max-sessions", "maxSessions", DEFAULTS.maxSessions),
      "Max sessions",
      1,
      1_024
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
  if (options.protocolVersion !== DEFAULTS.protocolVersion) {
    fail(`Protocol version must be ${DEFAULTS.protocolVersion}`);
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

// Endpoint discovery

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
 * Candidate endpoints in priority order, de-duplicated. An explicitly configured host or port is the
 * ONLY candidate on that axis, so discovery can never override a deliberate setting: `--host X`
 * probes X against the fallback ports, and `--host X --port Y` yields exactly one candidate.
 */
export function endpointCandidates(options, runtime = {}) {
  const readFile = runtime.readFile ?? readTextOrEmpty;
  const hosts = options.explicitHost
    ? [options.explicitHost]
    : [
        ...FALLBACK_HOSTS,
        ...resolvConfHosts(readFile("/etc/resolv.conf")),
        ...procNetRouteGateways(readFile("/proc/net/route"))
      ].filter(Boolean);
  const ports = options.explicitPort ? [options.explicitPort] : [...FALLBACK_PORTS];

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

/**
 * The tool that proves a live editor is behind the relay, and the read-only action to ask it for.
 * The relay keeps advertising its whole registry after the editor's discovery record goes stale,
 * so `tools/list` alone reports green while every editor-backed call answers "Unity not detected"
 * (#418). This one is a pure read: no scene, asset, or play-state change, and no modal.
 */
const EDITOR_READY_TOOL = "Unity_ManageEditor";
const EDITOR_READY_ARGUMENTS = { Action: "GetState" };

/**
 * Complete the pinned MCP lifecycle and optionally inspect the editor tool registry.
 * `readiness` is `false` (lifecycle only), `"tools"` (Unity_RunCommand is advertised), or
 * `"editor"` (a live editor answered as well).
 */
export async function probeEndpoint(candidate, options, fetchImpl = fetch, readiness = false) {
  const url = endpointUrl(candidate);
  const classify = (status, detail) => ({ ...candidate, url, ok: false, status, detail });
  const succeed = (extra) => ({ ...candidate, url, ok: true, status: "ok", ...extra });
  if (!(await tcpReachable(candidate.host, candidate.port, options.connectTimeout))) {
    return classify("unreachable", "no TCP listener");
  }
  const lifecycleSignal = AbortSignal.timeout(options.timeout);
  const authorization = options.bearerToken
    ? { Authorization: `Bearer ${options.bearerToken}` }
    : {};
  const cleanupWarnings = [];
  const cleanup = async (sessionId, protocolVersion) => {
    if (!sessionId) return;
    try {
      const response = await fetchImpl(url, {
        method: "DELETE",
        headers: {
          ...authorization,
          "Mcp-Session-Id": sessionId,
          "MCP-Protocol-Version": protocolVersion
        },
        signal: AbortSignal.timeout(Math.min(options.timeout, 1_000))
      });
      await response.body?.cancel();
      if (!response.ok && response.status !== 405) {
        cleanupWarnings.push(`session cleanup returned HTTP ${response.status}`);
      }
    } catch (error) {
      cleanupWarnings.push(`session cleanup failed: ${error.message}`);
    }
  };
  const failure = (error, operation) => {
    const message = error?.message ?? String(error);
    if (error?.probeStatus) return classify(error.probeStatus, message);
    if (error instanceof StreamableHTTPError) {
      const status = [401, 403].includes(error.code)
        ? "unauthorized"
        : error.code === -1
          ? "malformed"
          : "http-error";
      return classify(status, `${operation}: HTTP ${error.code}: ${message}`);
    }
    if (error instanceof McpError) {
      const status = lifecycleSignal.aborted ? "transport-error" : "jsonrpc-error";
      return classify(status, `${operation}: ${message}`);
    }
    const malformed = error instanceof SyntaxError || Array.isArray(error?.issues);
    return classify(malformed ? "malformed" : "transport-error", `${operation}: ${message}`);
  };
  let result;
  for (let attempt = 0; attempt < 2; attempt += 1) {
    result = undefined;
    let operation = "initialize";
    let sessionId;
    const transport = new StreamableHTTPClientTransport(new URL(url), {
      requestInit: { headers: authorization },
      fetch: async (target, init = {}) => {
        if (lifecycleSignal.aborted) throw lifecycleSignal.reason;
        const request = init.body ? JSON.parse(init.body) : undefined;
        operation = request?.method ?? operation;
        const lastEventId = new Headers(init.headers).get("last-event-id");
        if (init.method === "GET" && !lastEventId) return new Response(null, { status: 405 });
        const signal = init.signal
          ? AbortSignal.any([init.signal, lifecycleSignal])
          : lifecycleSignal;
        const response = await fetchImpl(target, { ...init, signal });
        sessionId ||= response.headers.get("mcp-session-id") ?? undefined;
        const responseType = response.headers.get("content-type") ?? "";
        const isSse = /^text\/event-stream\s*(?:;|$)/i.test(responseType);
        const resumeOk = response.status === 200 && isSse;
        if (lastEventId && response.ok && !resumeOk) {
          await response.body?.cancel();
          const error = new Error(`resume GET returned HTTP ${response.status} ${responseType}`);
          error.probeStatus = "malformed";
          throw error;
        }
        const messages = Array.isArray(request) ? request : [request];
        const expectsResponse = messages.some((message) => message?.method && "id" in message);
        const expectedStatus = expectsResponse ? 200 : 202;
        if (request && response.ok && response.status !== expectedStatus) {
          await response.body?.cancel();
          const error = new Error(`${operation}: HTTP ${response.status}, want ${expectedStatus}`);
          error.probeStatus = "malformed";
          throw error;
        }
        return response;
      }
    });
    const client = new Client({ name: "unity-mcp-probe", version: "1.0.0" });
    let reportTransportError;
    const transportError = new Promise((_, reject) => (reportTransportError = reject));
    client.onerror = reportTransportError;
    const awaited = (promise) => Promise.race([promise, transportError]);
    let retry = false;
    try {
      await awaited(client.connect(transport, { signal: lifecycleSignal }));
      const protocolVersion = transport.protocolVersion;
      if (protocolVersion !== options.protocolVersion) {
        const error = new Error(`server negotiated unsupported protocol ${protocolVersion}`);
        error.probeStatus = "malformed";
        throw error;
      }
      if (!readiness) {
        result = succeed({ sessionId, protocolVersion });
      } else if (!client.getServerCapabilities()?.tools) {
        result = classify("not-ready", "server did not advertise MCP tools");
      } else {
        const cursors = new Set();
        let cursor;
        let toolCount = 0;
        let editorToolAdvertised = false;
        operation = "tools/list";
        for (let page = 0; page < 100; page += 1) {
          const params = cursor === undefined ? {} : { cursor };
          const listed = await awaited(client.listTools(params, { signal: lifecycleSignal }));
          toolCount += listed.tools.length;
          editorToolAdvertised ||= listed.tools.some((t) => t.name === EDITOR_READY_TOOL);
          if (listed.tools.some((tool) => tool.name === "Unity_RunCommand")) {
            result = succeed({ sessionId, protocolVersion, toolCount, editorToolAdvertised });
            break;
          }
          if (listed.nextCursor === undefined) {
            result = classify("not-ready", "Unity_RunCommand was not advertised");
            break;
          }
          if (cursors.has(listed.nextCursor)) {
            result = classify("malformed", "tools/list returned a repeated cursor");
            break;
          }
          cursors.add(listed.nextCursor);
          cursor = listed.nextCursor;
        }
        result ??= classify("malformed", "tools/list exceeded 100 pages");
        // A relay whose registry has no editor tool cannot be asked whether an editor is behind
        // it, so that stays a tools-level verdict rather than a false red.
        if (result.ok && readiness === "editor" && result.editorToolAdvertised) {
          operation = "tools/call";
          const call = await awaited(
            client.callTool(
              { name: EDITOR_READY_TOOL, arguments: EDITOR_READY_ARGUMENTS },
              undefined,
              { signal: lifecycleSignal }
            )
          );
          const reply = (call.content ?? [])
            .map((part) => part.text ?? "")
            .join(" ")
            .trim();
          if (call.isError || !/"IsCompiling"/.test(reply)) {
            result = classify(
              "not-ready",
              `${EDITOR_READY_TOOL}: ${reply.slice(0, 160) || "returned no editor state"}`
            );
          }
        }
      }
    } catch (error) {
      const expired = error instanceof StreamableHTTPError && error.code === 404;
      retry = attempt === 0 && Boolean(sessionId) && expired;
      result = failure(error, operation);
    } finally {
      await client.close().catch(() => {});
      await cleanup(sessionId, transport.protocolVersion ?? options.protocolVersion);
    }
    if (!retry || lifecycleSignal.aborted) break;
  }
  if (cleanupWarnings.length) result.cleanupWarning = cleanupWarnings.join("; ");
  return result;
}

/** Probe candidates in order and return the first that meets the requested readiness level. */
export async function discoverEndpoint(options, runtime = {}) {
  const fetchImpl = runtime.fetchImpl ?? fetch;
  const candidates = runtime.candidates ?? endpointCandidates(options, runtime);
  const attempts = [];
  for (const candidate of candidates) {
    log(options, "debug", `Probing ${endpointUrl(candidate)}`);
    const result = await probeEndpoint(candidate, options, fetchImpl, runtime.readiness);
    log(options, "debug", `  ${result.status}: ${result.detail ?? "ok"}`);
    if (result.cleanupWarning) console.warn(`${result.url}: ${result.cleanupWarning}`);
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
    .map(
      (attempt) =>
        `  ${attempt.url} - ${attempt.status} (${[attempt.detail, attempt.cleanupWarning].filter(Boolean).join("; ") || "no detail"})`
    )
    .join("\n");
}

// Client configuration

function stageFile(filePath, content) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temporary = `${filePath}.${process.pid}.${randomBytes(8).toString("hex")}.tmp`;
  fs.writeFileSync(temporary, content, { encoding: "utf8", mode: 0o600, flag: "wx" });
  return temporary;
}

function atomicWrite(filePath, content, mode) {
  const temporary = stageFile(filePath, content);
  try {
    if (mode !== undefined) {
      // Rollback must restore the permissions the file had, not the 0600 staging default.
      fs.chmodSync(temporary, mode);
    }
    fs.renameSync(temporary, filePath);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

/**
 * Write several files as one unit. Every file is staged before any is committed, and a failure part
 * way through rolls back the files already renamed, so a crash cannot leave one agent pointed at a
 * new endpoint while another still holds the old one.
 *
 * Rollback is itself failure-safe: every restore is attempted even when an earlier one fails (Windows
 * `rename` returns EPERM whenever an editor holds the destination open, which is exactly these four
 * config files), and the ORIGINAL error is rethrown with the rollback failures attached as `cause`.
 */
export function transactionalWrite(writes, beforeCommit = () => {}) {
  const changed = writes.filter(
    ([filePath, content]) =>
      !fs.existsSync(filePath) || fs.readFileSync(filePath, "utf8") !== content
  );
  const staged = [];
  const committed = [];
  try {
    for (const [filePath, content] of changed) {
      const existed = fs.existsSync(filePath);
      staged.push({
        filePath,
        existed,
        original: existed ? fs.readFileSync(filePath, "utf8") : undefined,
        mode: existed ? fs.statSync(filePath).mode & 0o777 : undefined,
        temporary: stageFile(filePath, content)
      });
    }
    for (let index = 0; index < staged.length; index += 1) {
      beforeCommit(index, staged[index].filePath);
      fs.renameSync(staged[index].temporary, staged[index].filePath);
      committed.push(staged[index]);
    }
  } catch (error) {
    const suppressed = [];
    for (const item of committed.reverse()) {
      try {
        if (item.existed) {
          atomicWrite(item.filePath, item.original, item.mode);
        } else {
          fs.rmSync(item.filePath, { force: true });
        }
      } catch (rollbackError) {
        suppressed.push(rollbackError);
      }
    }
    if (suppressed.length > 0) {
      error.cause = new AggregateError(
        suppressed,
        `Rollback failed for ${suppressed.length} file(s)`
      );
    }
    throw error;
  } finally {
    // Staging can throw part way through, so only the temporaries actually created are removed.
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

/**
 * Strip `//` and block comments plus trailing commas so JSONC parses. `.vscode/mcp.json` is JSONC and
 * VS Code's own "MCP: Add Server" scaffolding writes a comment into it, so refusing JSONC means
 * `configure` cannot run at all for those users. String contents are tracked so a `//` inside a URL
 * or a comma inside a string value is never mistaken for syntax.
 */
export function stripJsonComments(raw) {
  let out = "";
  let inString = false;
  let escaped = false;
  // Index of the last non-whitespace character already in `out`. Re-scanning `out` with a regex on
  // every closing bracket flattens the rope V8 builds from `out += char`, which makes the pass
  // quadratic in the number of closing brackets: a 364 KB config took 9 s, a 1.5 MB one took 150 s.
  let lastNonSpace = -1;
  // The character at `lastNonSpace`, carried separately: `out[lastNonSpace]` would flatten the same
  // rope the regex did, which is most of the remaining cost.
  let lastNonSpaceChar = "";
  const append = (text) => {
    if (text.trim() !== "") {
      lastNonSpace = out.length + text.length - 1;
      lastNonSpaceChar = text[text.length - 1];
    }
    out += text;
  };
  for (let index = 0; index < raw.length; index += 1) {
    const char = raw[index];
    if (inString) {
      append(char);
      if (escaped) {
        escaped = false;
      } else if (char === "\\") {
        escaped = true;
      } else if (char === '"') {
        inString = false;
      }
      continue;
    }
    if (char === '"') {
      inString = true;
    } else if (char === "/" && raw[index + 1] === "/") {
      const end = raw.indexOf("\n", index + 2);
      index = end === -1 ? raw.length : end - 1;
      continue;
    } else if (char === "/" && raw[index + 1] === "*") {
      const end = raw.indexOf("*/", index + 2);
      index = end === -1 ? raw.length : end + 1;
      continue;
    } else if ((char === "}" || char === "]") && lastNonSpaceChar === ",") {
      // Drop a trailing comma in place. Valid JSON never takes this branch. `append` below restores
      // `lastNonSpace` to the closing bracket, so a nested `[1,],}` still sees its own comma.
      out = out.slice(0, lastNonSpace) + out.slice(lastNonSpace + 1);
    }
    append(char);
  }
  return out;
}

function readJsonObject(filePath) {
  if (!fs.existsSync(filePath) || !fs.readFileSync(filePath, "utf8").trim()) {
    return {};
  }
  let parsed;
  try {
    parsed = JSON.parse(stripJsonComments(fs.readFileSync(filePath, "utf8")));
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

const CODEX_AMBIGUOUS_MESSAGE = (reason) =>
  `${reason} in .codex/config.toml, so this tool cannot tell which lines it owns. ` +
  "Delete the [mcp_servers.unity-mcp] table from .codex/config.toml (or move it to the end of the " +
  "file, after every multi-line value) and re-run configure.";

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

  // Normalize line endings once so both the append and the replace path emit LF only; mixing CRLF
  // input with an LF block would otherwise leave the file churning on every run under Windows.
  const normalized = raw.replace(/\r\n/g, "\n");
  const lines = normalized.split("\n");
  const owned = lines
    .map((line, index) => ({ index, header: classifyTomlHeader(line) }))
    .filter((item) => item.header?.owned)
    .map((item) => item.index);
  if (owned.length > 1) {
    fail(CODEX_AMBIGUOUS_MESSAGE("Duplicate unity-mcp table"));
  }
  if (owned.length === 0) {
    if (parsed.mcp_servers?.["unity-mcp"] !== undefined) {
      fail("Unsupported inline or dotted unity-mcp definition in Codex config");
    }
    return `${normalized.trimEnd()}${normalized.trim() ? "\n\n" : ""}${block}`;
  }

  const start = owned[0];
  // A `[mcp_servers.unity-mcp]` line inside a multi-line string is not a table header. Everything
  // before a real header is itself complete TOML, so a prefix that will not parse proves the line
  // scanner is about to splice through a string literal.
  try {
    parseToml(lines.slice(0, start).join("\n"));
  } catch {
    fail(CODEX_AMBIGUOUS_MESSAGE("A unity-mcp header line appears inside a multi-line value"));
  }
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
  } catch {
    fail(CODEX_AMBIGUOUS_MESSAGE("Rewriting the unity-mcp table produced invalid TOML"));
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

// Relay discovery and the bridge server

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

/**
 * Client-caused body failures carry the HTTP status and JSON-RPC error to report. Without this a
 * malformed body came back as HTTP 500 / -32603 "internal error", which clients retry forever.
 */
function bodyError(message, httpStatus, code, rpcMessage) {
  return Object.assign(new Error(message), { httpStatus, rpc: { code, message: rpcMessage } });
}

function readJsonBody(request, limitBytes, timeoutMs) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    const timer = setTimeout(() => {
      // Without this a client that sends Content-Length headers and no body pins the socket (and
      // therefore shutdown) until Node's request timeout, which defaults to five minutes.
      request.pause();
      reject(bodyError("Request body timed out", 408, -32001, "Request body timed out"));
    }, timeoutMs);
    timer.unref();
    const settle = (action, value) => {
      clearTimeout(timer);
      action(value);
    };
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size > limitBytes) {
        // Pause rather than destroy: the handler still has to write a 413, and destroying the socket
        // first is what turns an over-large body into an opaque ECONNRESET for the client.
        request.pause();
        settle(reject, bodyError("Request body too large", 413, -32600, "Request body too large"));
        return;
      }
      chunks.push(chunk);
    });
    request.once("error", (error) => settle(reject, error));
    request.once("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8");
      if (!raw.trim()) {
        settle(resolve, undefined);
        return;
      }
      try {
        settle(resolve, JSON.parse(raw));
      } catch (error) {
        settle(
          reject,
          bodyError(`Invalid JSON body: ${error.message}`, 400, -32700, "Parse error")
        );
      }
    });
  });
}

function sendJson(response, statusCode, payload, closeConnection = false) {
  const body = JSON.stringify(payload);
  const headers = {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  };
  if (closeConnection) {
    // The request body was never drained, so the connection cannot be reused.
    headers.Connection = "close";
  }
  response.writeHead(statusCode, headers);
  response.end(body);
}

export async function startBridge(inputOptions, runtime = {}) {
  const options = ensureBearerToken(inputOptions);
  const projectPath = requireProjectPath(options);
  const relayPath = findRelay(options.relayPath, runtime.relayRuntime);
  await assertPortAvailable(options.port, options.bindHost);

  const maxSessions = options.maxSessions ?? DEFAULTS.maxSessions;
  const bodyTimeout = Math.min(options.sessionTimeout ?? Infinity, DEFAULTS.bodyTimeout);
  const sessions = new Map();
  const provisionalSessions = new Set();
  let starting = 0;

  const disposeSession = async (session) => {
    if (!session || session.disposed) {
      return;
    }
    log(options, "debug", `Disposing session ${session.sessionId ?? "(provisional)"}`);
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
    session.timer = setTimeout(() => {
      disposeSession(session).catch(() => {});
    }, options.sessionTimeout);
    session.timer.unref();
  };

  const armRequestTimeout = (sessionId) => {
    const session = sessions.get(sessionId);
    if (!session) {
      return;
    }
    clearTimeout(session.timer);
    session.timer = setTimeout(() => {
      disposeSession(session).catch(() => {});
    }, options.requestTimeout);
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
        log(options, "debug", `Session ${id} initialized (${sessions.size}/${maxSessions})`);
        touch(id);
      }
    });
    const server = new Server(
      { name: "dxmessaging-unity-mcp-bridge", version: "1.0.0" },
      { capabilities: {} }
    );
    await server.connect(transport);
    const relayArgs = buildRelayArgs(projectPath);
    log(options, "debug", `Spawning relay: ${relayPath} ${relayArgs.join(" ")}`);
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
    // A session that never reaches `onsessioninitialized` holds a live relay child, so it gets the
    // short idle timeout rather than the multi-minute active-request budget.
    session.timer = setTimeout(() => {
      disposeSession(session).catch(() => {});
    }, options.sessionTimeout);
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
    transport.onclose = () => {
      disposeSession(session).catch(() => {});
    };
    transport.onerror = (error) => {
      log(options, "error", `MCP transport error: ${error.message}`);
      disposeSession(session).catch(() => {});
    };
    return transport;
  };

  const handle = async (request, response) => {
    try {
      const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "localhost"}`);
      // Liveness only; it reveals nothing, so it is deliberately outside the bearer check. A probe
      // that has to hold the token is not a probe an orchestrator can run.
      if (url.pathname === "/healthz") {
        response.writeHead(200, { "Content-Type": "text/plain" });
        response.end("ok");
        return;
      }
      if (!authorized(request, options.bearerToken)) {
        response.setHeader("WWW-Authenticate", "Bearer");
        sendJson(response, 401, { error: "Unauthorized" });
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
          ? await readJsonBody(request, DEFAULTS.bodyLimitBytes, bodyTimeout)
          : undefined;
      const sessionId = request.headers["mcp-session-id"];
      let transport = sessionId ? sessions.get(sessionId)?.transport : undefined;
      if (!transport && request.method === "POST" && !sessionId && isInitializeRequest(body)) {
        // Every session owns a relay child process, so the count is capped rather than unbounded.
        // `starting` is bumped synchronously because createSession awaits before it registers.
        if (sessions.size + provisionalSessions.size + starting >= maxSessions) {
          sendJson(response, 503, {
            jsonrpc: "2.0",
            id: null,
            error: {
              code: -32000,
              message: `Too many concurrent MCP sessions (limit ${maxSessions}); close one or raise --max-sessions`
            }
          });
          return;
        }
        starting += 1;
        try {
          transport = await createSession();
        } finally {
          starting -= 1;
        }
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
      const status = error.httpStatus ?? 500;
      if (!response.headersSent) {
        // 413 and 408 both leave the request body undrained, so the socket cannot be reused.
        sendJson(
          response,
          status,
          {
            jsonrpc: "2.0",
            id: null,
            error: error.rpc ?? { code: -32603, message: "Bridge failure" }
          },
          status === 413 || status === 408
        );
      }
      log(options, status === 500 ? "error" : "debug", `Bridge request failed: ${error.message}`);
    }
  };

  const httpServer = http.createServer((request, response) => {
    // The catch inside `handle` can itself throw (a socket that died mid-response), and an unhandled
    // rejection is fatal to the process by default, so the outer promise is always caught.
    handle(request, response).catch((error) => {
      log(options, "error", `Bridge handler crashed: ${error.message}`);
      response.destroy();
    });
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
    const stopped = new Promise((resolve) => httpServer.close(resolve));
    // Without this, an idle keep-alive socket or a client that stalled mid-body keeps `close()`
    // pending until Node's 300s request timeout expires.
    httpServer.closeAllConnections();
    await stopped;
    closeResolve();
    return closed;
  };
  return { close, closed, httpServer, options, bearerToken: options.bearerToken };
}

// Commands

/**
 * `--no-discover` narrows the candidate list to the configured endpoint; it does not skip the
 * readiness check. Returning early without probing left `runProbe` with no `found` and empty attempts
 * list, so `probe --no-discover` always failed and said nothing about why.
 */
async function resolveEndpoint(options, runtime = {}) {
  const configured = {
    host: options.host,
    port: options.port,
    endpointPath: options.endpointPath
  };
  const narrowed = options.discover
    ? runtime
    : { ...runtime, candidates: runtime.candidates ?? [configured] };

  const { found, attempts } = await discoverEndpoint(options, narrowed);
  if (found) {
    return {
      endpoint: { host: found.host, port: found.port, endpointPath: found.endpointPath },
      attempts,
      found
    };
  }
  // With discovery off the caller named the endpoint, so keep it: `configure` still writes it, and
  // `probe` reports the attempt that failed rather than an empty list.
  return { endpoint: options.discover ? undefined : configured, attempts };
}

export async function runProbe(options, runtime = {}) {
  const { found, attempts } = await resolveEndpoint(options, { ...runtime, readiness: "editor" });
  if (!found) {
    fail(
      `No Unity MCP endpoint is ready for editor-backed calls. Attempts:\n${describeAttempts(attempts)}`
    );
  }
  // Say which of the two things was actually proven. A bridge that advertises Unity_RunCommand
  // without an editor tool cannot be asked the second question, and claiming otherwise is the
  // false green #418 is about.
  const proven = found.editorToolAdvertised
    ? `is ready for editor-backed calls (${EDITOR_READY_TOOL} answered)`
    : `advertises Unity_RunCommand, but has no ${EDITOR_READY_TOOL} to prove an editor is behind it`;
  console.log(`Unity MCP at ${found.url} ${proven} (protocol ${found.protocolVersion}).`);
  return found;
}

export async function runConfigure(options, runtime = {}) {
  const { endpoint, attempts, found } = await resolveEndpoint(options, runtime);
  // `unauthorized` means a bridge IS running there and only the token is wrong. Falling back to the
  // default endpoint and minting a fresh token would guarantee a 401 and persist the bogus token
  // into .env.local and all four configs, so this refuses to write anything.
  const unauthorized = found ? undefined : attempts.find((a) => a.status === "unauthorized");
  if (unauthorized) {
    fail(
      `A Unity MCP bridge is running at ${unauthorized.url} but rejected the bearer token ` +
        `(${unauthorized.detail}). Nothing was written and no token was generated: copy ` +
        `${ENV_KEYS.bearerToken} from the host's .env.local into ` +
        `${path.join(options.repoRoot, ".env.local")}, or pass --token, then re-run configure.`
    );
  }
  const target = endpoint ?? {
    host: options.host,
    port: options.port,
    endpointPath: options.endpointPath
  };
  if (!found) {
    console.warn(
      `No Unity MCP endpoint completed initialization; configuring ${endpointUrl(target)} anyway. Attempts:\n${describeAttempts(attempts)}`
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
    "  probe      Discover an endpoint that advertises Unity_RunCommand.",
    "  configure  Discover, then write .mcp.json, .cursor/mcp.json, .vscode/mcp.json, .codex/config.toml.",
    "  bridge     Serve the Unity relay over authenticated streamable HTTP (run next to Unity).",
    "",
    "Options:",
    "  --host HOST                 Endpoint host; the only host discovery probes",
    "  --port PORT                 Endpoint port; the only port discovery probes",
    "  --path PATH                 Streamable HTTP path (default: /mcp)",
    "  --no-discover               Probe only the configured host/port, not the fallbacks",
    "  --bind HOST                 Bridge bind interface (default: 0.0.0.0)",
    "  --project PATH              Unity project directory (bridge only)",
    "  --relay PATH                Unity relay executable override",
    "  --token TOKEN               32-256 character bearer token (generated into .env.local if omitted)",
    "  --timeout MS                Per-endpoint MCP lifecycle deadline (default: 5000)",
    "  --connect-timeout MS        Per-endpoint TCP connect timeout (default: 750)",
    "  --session-timeout MS        Idle session timeout (default: 60000)",
    "  --request-timeout MS        Active-request hard limit (default: 300000)",
    "  --max-sessions COUNT        Concurrent bridge sessions, one relay each (default: 8)",
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
