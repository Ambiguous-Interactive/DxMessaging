"use strict";
const net = require("node:net");
const { createHash } = require("node:crypto");
const { TextDecoder } = require("node:util");
const REVIEWED_TEXT_EXTENSIONS = Object.freeze([
  ".asm",
  ".cpp",
  ".csv",
  ".h",
  ".json",
  ".jsonl",
  ".log",
  ".map",
  ".marker",
  ".md",
  ".sha256",
  ".tsv",
  ".txt",
  ".xml"
]);
const MAXIMUM_STRAY_NUL_BYTES = 8;
const SERIALIZED_WINDOW_CHARACTERS = 4 * 1024 * 1024;
const SERIALIZED_ESCAPE =
  /\\(?=u[0-9A-Fa-f]{4}|["\\/bfnrt])|&(?=#(?:x[0-9A-Fa-f]|[0-9])|(?:amp|apos|gt|lt|quot);)/;
const ENCODED_LIMIT_FINDING = Object.freeze({
  id: "encoded-sensitive-data",
  description: "data encoded beyond the inspection limit"
});
function canonicalWebHostname(raw) {
  try {
    return new URL(`http://${raw}/`).hostname.replace(/\.+$/, "").toLowerCase();
  } catch {
    return undefined;
  }
}
function isSensitiveWebHostname(raw) {
  if (/\p{Cf}/u.test(raw)) return true;
  const hostname = canonicalWebHostname(raw);
  return (
    hostname === undefined ||
    (hostname !== "localhost" &&
      (net.isIP(hostname) > 0 ||
        !hostname.includes(".") ||
        hostname.endsWith(".internal") ||
        hostname.endsWith(".local")))
  );
}
function quotedAssignmentReplacement(id) {
  return (match) => {
    const placeholder = `[redacted:${id}]`;
    if (match[2] !== undefined) return `${match[1]}"${placeholder}"`;
    if (match[3] !== undefined) return `${match[1]}'${placeholder}'`;
    return `${match[1]}${placeholder}`;
  };
}
// prettier-ignore
const CREDENTIAL_PATTERNS = Object.freeze([
  {
    id: "pem-private-key",
    description: "a PEM private key",
    pattern:
      /-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----|-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*/
  },
  {
    id: "unity-license-id",
    description: "a Unity license identifier",
    pattern:
      /(<License\b[^>]*\bid\s*=\s*)(?:"((?!\[redacted:)(?:\\.|[^"\\])+)"|'((?!\[redacted:)(?:\\.|[^'\\])+)'|"((?!\[redacted:)[^"\r\n]+)(?=\r?$)|'((?!\[redacted:)[^'\r\n]+)(?=\r?$))/im,
    replacement: (match) => {
      const quote = match[2] !== undefined || match[4] !== undefined ? '"' : "'";
      const closing = match[2] !== undefined || match[3] !== undefined ? quote : "";
      return `${match[1]}${quote}[redacted:unity-license-id]${closing}`;
    }
  },
  { id: "unity-serial", description: "a Unity serial", pattern: /\bS[CBP]-[0-9A-Z]{4}(?:-[0-9A-Z]{4}){4}\b/ },
  { id: "github-token", description: "a GitHub token", pattern: /\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b/ },
  { id: "aws-access-key-id", description: "an AWS access key id", pattern: /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/ },
  { id: "http-bearer-token", description: "an HTTP bearer token", pattern: /(\bBearer\s+)[A-Za-z0-9._~+/=-]{20,}/i, prefixGroup: 1 },
  { id: "unity-password-assignment", description: "a Unity password assignment", pattern: /((?:\bUNITY_PASSWORD["']?\s*[=:]\s*|(?<![\w-])-password(?:[ \t]+|[ \t]*\r?\n[ \t]*)))(?:"((?:""|\\.|[^"\\\r\n])*)"|'((?:''|\\.|[^'\\\r\n])*)'|([^\r\n]+))/i, accept: (match) => isNonEmptyUnmasked(match[2] ?? match[3] ?? match[4]), replacement: quotedAssignmentReplacement("unity-password-assignment") },
  { id: "unity-email-assignment", description: "a Unity account email", pattern: /((?:\bUNITY_EMAIL["']?\s*[=:]\s*|(?<![\w-])-username(?:[ \t]+|[ \t]*\r?\n[ \t]*)))(?:"((?:""|\\.|[^"\\\r\n])*)"(?!@)|'((?:''|\\.|[^'\\\r\n])*)'(?!@)|([^\r\n]+))/i, accept: (match) => isNonEmptyUnmasked(match[2] ?? match[3] ?? match[4]), replacement: quotedAssignmentReplacement("unity-email-assignment") },
  { id: "password-assignment", description: "a password assignment", pattern: /(\b(?!UNITY_PASSWORD\b)[A-Z0-9_]*PASSWORD["']?\s*[=:]\s*)(?:"((?:""|\\.|[^"\\\r\n])*)"|'((?:''|\\.|[^'\\\r\n])*)'|([^\r\n]+))/i, accept: (match) => isNonEmptyUnmasked(match[2] ?? match[3] ?? match[4]), replacement: quotedAssignmentReplacement("password-assignment") },
  {
    id: "credential-assignment",
    description: "a credential assignment",
    pattern:
      /(\b(?:UNITY_SERIAL|[A-Z0-9_]*(?:TOKEN|SECRET|API_?KEY|ACCESS_?KEY))["']?\s*[=:]\s*)(?:"((?:""|\\.|[^"\\\r\n])+)"|'((?:''|\\.|[^'\\\r\n])+)'|([^\r\n]+))/i,
    accept: (match) => {
      const value = match[2] ?? match[3] ?? match[4];
      return isUnmaskedValue(value.trim()) && value.trim().length >= 12;
    },
    replacement: quotedAssignmentReplacement("credential-assignment")
  }
]);
// prettier-ignore
const IDENTIFIER_PATTERNS = Object.freeze([
  { id: "web-hostname", description: "a JSON-encoded web host name", pattern: /((")https?:[\/\\]*)(?!\[redacted:)(?=[^"\/\\?#]*\\[bfnrt])(?:\\[bfnrt]|[^"\/\\?#])+(?=[\/\\?#]|")/iu, prefixGroup: 1 },
  { id: "web-hostname", description: "a control-bearing private web authority", pattern: /(\bhttps?:[\/\\]*)(?!\[redacted:)(?=(?:[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]|[^\s\/\\?#])*[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000])((?:[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]|[^\s\/\\?#])+)(?=[\/\\?#]|$)/u, prefixGroup: 1, accept: (match) => match[2].includes("@") || isSensitiveWebHostname(match[2].replace(/[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]/gu, "")) },
  { id: "web-hostname", description: "web authority user information and host name", pattern: /(\bhttps?:[\/\\]*)(?!\[redacted:)(?:(?:\p{Cf}|[^/\\?#\s])*@)(?:\[[^\]\s]+\]|(?:\p{Cf}|[^:/\\?#\s"'`()\[\]}>},;])+)(?=[:/\\?#\s"'`)\]}>},;]|$)/iu, prefixGroup: 1 },
  { id: "web-hostname", description: "an encoded web host name", pattern: /(\bhttps?:[\/\\]*)(?!\[redacted:)(?=[^\/?\s"'`()\[\]}>},]*?(?:\\u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);))(?:(?:\\u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);)|[^\/\\?#\s"'`()\[\]}>},])+(?=[\/\\?#\s"'`()\[\]}>},]|$)/iu, prefixGroup: 1 },
  { id: "web-hostname", description: "a private web host name", pattern: /(\bhttps?:[\/\\]*)(?!(?:\p{Cf}|[^/\\?#\s])*@|\[redacted:)((?:\p{Cf}|[^:\/\\?#\s"'`()\[\]}>},;])+)(?=[:/\\?#\s"'`)\]}>},;]|$)/iu, prefixGroup: 1, accept: (match) => isSensitiveWebHostname(match[2]) },
  { id: "file-uri-hostname", description: "a JSON-encoded file URI host name", pattern: /((")file:[\/\\]*)(?!\[redacted:)(?=[^"\/\\?#]*\\[bfnrt])(?:\\[bfnrt]|[^"\/\\?#])+(?=[\/\\?#]|")/iu, prefixGroup: 1 },
  { id: "file-uri-hostname", description: "a control-bearing or encoded file URI host name", pattern: /(\bfile:[\/\\]*)(?!\[redacted:)(?=(?:(?:[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]|\\u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);)|[^\s\/\\?#])*(?:[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]|\\u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);))(?:(?:[\u0000-\u001F\u007F-\u009F\p{Cf}\p{Zl}\p{Zp}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000]|\\u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);)|[^\s\/\\?#])+(?=[\/\\?#]|$)/u, prefixGroup: 1 },
  { id: "file-uri-hostname", description: "a file URI host name", pattern: /((")file:\/\/)(?!localhost(?=")|\[redacted:)(?:(?:\\\\)*\\"|""|[^/"\\\r\n])+(?=")/iu, prefixGroup: 1 },
  { id: "file-uri-hostname", description: "a file URI host name", pattern: /((['`|])file:\/\/)(?!localhost(?=\2)|\[redacted:)(?:(?:\2){2}|(?!\2)[^/\r\n])+(?=\2)/iu, prefixGroup: 1 },
  { id: "file-uri-hostname", description: "a file URI host name", pattern: /(\bfile:\/\/)(?!localhost(?:[/:]|["'`|)\]}>},;\s]|$)|\[redacted:)(?:[\p{L}\p{N}._~!$&'()*+,;=:%@-]+(?=\/)|(?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|'(?=[\p{L}\p{N}\p{M}_-])|[^&/\s"'`()\[\]};,>|])+)/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing Windows volume home path", pattern: /(((?:\\\\){1,2}\?(?:\\\\|[\\/])Volume\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}(?:\\\\|[\\/])(?:Users|Documents and Settings)(?:\\\\|[\\/])))(?!\[redacted:)(?:\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/"'\r\n])+?(?=(?:\\\\|[\\/])|["`|]|'(?![\p{L}\p{N}\p{M}_-])|$)/iu, prefixGroup: 1 },
  { id: "windows-volume-id", description: "a Windows volume identifier", pattern: /\bVolume\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}/i },
  { id: "account-home-path", description: "an account-bearing extended UNC home path", pattern: /(([\\]{2}(?:[\\]{2})?\?[\\]{1,2}UNC[\\]{1,2})(?!\[redacted:)(?:[^\\\s"']+)[\\]{1,2}(?:(?:(?:[\p{L}\p{N}\p{M}.$_-]+[\\]{1,2})?(?:Users|Documents and Settings)|home)[\\]{1,2}))(?!\[redacted:)(?:\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/"'\r\n])+?(?=[\\]{1,2}|["`|]|'(?![\p{L}\p{N}\p{M}_-])|$)/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing WSL UNC home path", pattern: /((?:(?:^|[^A-Za-z0-9:?$\\\]])|(?:\b(?:path|share|unc):))(?:\\\\){1,2}(?:wsl\$|wsl\.localhost)(?:\\\\|[\\/])(?:[^\\/\r\n]+)(?:\\\\|[\\/])(?:home|mnt(?:\\\\|[\\/])[A-Za-z](?:\\\\|[\\/])Users)(?:\\\\|[\\/]))(?!\[redacted:)(?:\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/"'\r\n])+?(?=(?:\\\\|[\\/])|["`|]|'(?![\p{L}\p{N}\p{M}_-])|$)/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing UNC home path", pattern: /((?:(?:(?:^|[^A-Za-z0-9:?$\\\]])|(?:\b(?:path|share|unc):))(?:\\\\){1,2})(?![?.](?:\\\\|[\\/]))(?:\\u[0-9A-Fa-f]{4}|\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/\s"'])+(?:\\\\|[\\/])(?:(?:(?:[\p{L}\p{N}\p{M}.$_-]+(?:\\\\|[\\/]))?(?:Users|Documents and Settings)|home)(?:\\\\|[\\/])))(?!\[redacted:)(?:\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/"'\r\n])+?(?=(?:\\\\|[\\/])|["`|]|'(?![\p{L}\p{N}\p{M}_-])|$)/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing forward UNC home path", pattern: /((?:\b(?:path|share|unc)\s*[=:]\s*["']?|\bfile:)\/\/(?:[^\/\\\s"']+)(?:\\\\|[\\/])(?:(?:(?:[\p{L}\p{N}\p{M}.$_-]+(?:\\\\|[\\/]))?(?:Users|Documents and Settings)|home)(?:\\\\|[\\/])))(?!\[redacted:)(?:\\'|'(?=[\p{L}\p{N}\p{M}_-])|[^\\/"'\r\n])+?(?=(?:\\\\|[\\/])|["`|]|'(?![\p{L}\p{N}\p{M}_-])|$)/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing home path", pattern: /((")(?:file:\/\/(?:localhost)?\/(?:[A-Za-z]:\/(?:Users|Documents and Settings)\/|(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/)|[A-Za-z]:(?:\\\\|[\\/])(?:Users|Documents and Settings)(?:\\\\|[\\/])|\/(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/))(?!\[redacted:)(?:(?:\\\\)*\\"|""|[^\\/"\r\n])+(?=")/iu, prefixGroup: 1 },
  { id: "account-home-path", description: "an account-bearing home path", pattern: /((['`|])(?:file:\/\/(?:localhost)?\/(?:[A-Za-z]:\/(?:Users|Documents and Settings)\/|(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/)|[A-Za-z]:(?:\\\\|[\\/])(?:Users|Documents and Settings)(?:\\\\|[\\/])|\/(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/))(?!\[redacted:)(?:(?:\\\\)+|(?:\\\\)*\\\2|(?:\2){2}|(?!\2)[^\\\/\r\n])+(?=\2)/iu, prefixGroup: 1 },
  {
    id: "account-home-path",
    description: "an account-bearing home path",
    pattern:
      /((?:file:\/\/(?:localhost)?\/(?:[A-Za-z]:\/(?:Users|Documents and Settings)\/|(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/))|(?:(?:^|[^A-Za-z0-9_./])(?:[A-Za-z]:(?:\\\\|[\\/])(?:Users|Documents and Settings)(?:\\\\|[\\/])|\/(?:home|var\/home|Users|System\/Volumes\/Data\/Users|Network\/Servers\/[^/]+\/Users|Volumes\/[^/]+\/Users|(?:mnt\/[A-Za-z]|[A-Za-z]|cygdrive\/[A-Za-z])\/(?:Users|Documents and Settings))\/)))(?!\[redacted:)(?:((?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|\\'|''|["'](?=[\p{L}\p{N}\p{M}_-])|'(?=[ \t]+[\p{L}\p{N}\p{M}_-]+(?=(?:\\\\|[\\/])))|[^&\\/"'\r\n])+)(?=(?:\\\\|[\\/]))|((?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|\\'|[)\]};](?=[\p{L}\p{N}\p{M}_-])|["'](?=[\p{L}\p{N}\p{M}_-])|[;,>"`|](?=[ \t]*[\p{L}\p{N}\p{M}_-])|[^&\\/"'`)\]};,>|\r\n])+)(?=(?:[`|"]|[\])};,>](?![\p{L}\p{N}\p{M}_-])|'(?=[\s,\]};>]|$)))|((?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|\\'|[)\]};](?=[\p{L}\p{N}\p{M}_-])|["'](?=[\p{L}\p{N}\p{M}_-])|[;,>"`|](?=[ \t]*[\p{L}\p{N}\p{M}_-])|[^&\\/"'`)\]};>|\r\n])+?)(?=\r?$))/imu,
    prefixGroup: 1,
    suffix: (match) => {
      if (/\/home\/$/i.test(match[1]) && match[4] !== undefined) {
        const whitespace = match[4].search(/\s/);
        if (whitespace >= 0) return match[4].slice(whitespace);
      }
      return "";
    }
  },
  { id: "root-home-path", description: "the root account home path", pattern: /((?:^|file:\/\/(?:localhost)?|[^A-Za-z0-9_./<])\/)(?!\[redacted:)root(?=\/|[\\\s"'`|.,;:)\]}>]|$)/, prefixGroup: 1 },
  { id: "extended-unc-hostname", description: "an extended UNC host name", pattern: /([\\]{2}(?:[\\]{2})?\?[\\]{1,2}UNC[\\]{1,2})(?!\[redacted:)(?:\\{1,2}u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);|[\p{L}\p{N}\p{M}\p{Cf}._-])+(?:\$)?(?=\\)/iu, prefixGroup: 1 },
  { id: "unc-hostname", description: "a UNC host name", pattern: /((?:\b(?:path|share|unc)\s*[=:]\s*["']?)\/\/)(?![?.][\\/]|\[redacted:)(?:\\{1,2}u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);|[\p{L}\p{N}\p{M}\p{Cf}._-])+(?:\$)?(?=[\\/])/iu, prefixGroup: 1 },
  { id: "unc-hostname", description: "a JSON-encoded UNC host name", pattern: /((?:^|")(?:\\\\){2})(?!\[redacted:)(?=(?:\\[bfnrt]|[^"\\])*\\[bfnrt])(?:\\[bfnrt]|[\p{L}\p{N}\p{M}\p{Cf}._-])+(?=(?:\\\\){1,2})/iu, prefixGroup: 1 },
  { id: "unc-hostname", description: "a UNC host name", pattern: /((?:(?:^|[^A-Za-z0-9:?.}$\\\]])|(?:\b(?:path|share|unc):))(?:\\\\){1,2})(?![?.](?:\\\\|[\\/])|\[redacted:)(?:\\{1,2}u[0-9A-Fa-f]{4}|&#(?:[0-9]+|x[0-9A-Fa-f]+);|[\p{L}\p{N}\p{M}\p{Cf}._-])+(?:\$)?(?=[\\/])/iu, prefixGroup: 1 },
  { id: "ipv6-address", description: "an IPv6 address", pattern: /(?<![0-9A-Za-z_.])(\[?(?:[0-9A-Fa-f]{1,4}|:)[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*(?:%[A-Za-z0-9_.-]+)?\]?)(?![0-9A-Za-z_%[])/, accept: (match) => parseIPv6Candidate(match[1]) !== undefined, suffix: (match) => parseIPv6Candidate(match[1]).suffix },
  { id: "mac-address", description: "a MAC or EUI address", pattern: /(?:\b(?:[0-9A-Fa-f]{2}[:-]){7}[0-9A-Fa-f]{2}\b|\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b|\b(?:[0-9A-Fa-f]{4}\.){3}[0-9A-Fa-f]{4}\b|\b(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4}\b)/ },
  { id: "ipv4-address", description: "an IPv4 address", pattern: /(^|[^0-9.])((?:\d{1,3}\.){3}\d{1,3})(?![0-9]|\.\d)/, prefixGroup: 1, accept: (match) => net.isIP(match[2]) === 4 },
  { id: "unity-machine-id", description: "a Unity machine identifier", pattern: /(\bMachine (?:ID|Identification)["']?:[ \t]*["']?)(?!\[?redacted:)(?:\[)?(?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|'(?=[\p{L}\p{N}\p{M}_-])|[^&\\<\s,"'`()\]};>|])+(?:\])?/iu, prefixGroup: 1 },
  { id: "unity-editor-hostname", description: "a Unity Editor host name", pattern: /((?:Windows|Linux|OSX)Editor\([0-9]+,["']?)(?!redacted:|\[redacted:)(?:\\.|'(?=[\p{L}\p{N}\p{M}_-])|[^\\<$)\s"'`|])+/u, prefixGroup: 1 },
  { id: "unity-license-client-hostname", description: "a Unity License Client host name", pattern: /((?:^|[^A-Za-z-])LicenseClient-["']?)(?!redacted:|\[redacted:)(?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|'(?=[\p{L}\p{N}\p{M}_-])|[^&\\<$)\]};,>\s"'`|])+/u, prefixGroup: 1 },
  { id: "unity-ipc-hostname", description: "a Unity IPC host name", pattern: /((?:\\\\\.\\pipe\\)?Unity-(?:LicenseClient|LicensingClient)-["']?)(?!redacted:|\[redacted:)(?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|'(?=[\p{L}\p{N}\p{M}_-])|[^&\\<$,\s"'`()\[\]};>|])+/u, prefixGroup: 1 },
  {
    id: "named-account-or-host",
    description: "a named runner account or host",
    pattern:
      /(\b(?:Account Name|Computer Name|Host Name|Machine Name|machineName|COMPUTERNAME|HOSTNAME|LOGNAME|RUNNER_NAME|Runner Name|USER|User Name|USERNAME)["']?[ \t]*[:=][ \t]*)(?:"((?:""|\\.|[^"\\\r\n])+)"|'((?:''|\\.|[^'\\\r\n])+)'|(\S(?:[^\r\n]*?\S)?)([ \t]*)(?=\r?$))/im,
    accept: (match) => !isRedactionPlaceholder(match[2] ?? match[3] ?? match[4]),
    replacement: (match) => {
      const placeholder = "[redacted:named-account-or-host]";
      if (match[2] !== undefined) return `${match[1]}"${placeholder}"`;
      if (match[3] !== undefined) return `${match[1]}'${placeholder}'`;
      return `${match[1]}${placeholder}${match[5]}`;
    }
  },
  { id: "unity-accelerator-endpoint", description: "a Unity Accelerator endpoint", pattern: /(AcceleratorClientConnectionCallback[^\r\n]*?[ \t]+-[ \t]+(?:connected|disconnected)[ \t]+-[ \t]+)(?!\[redacted:)[^\s\r\n][^\r\n]*/, prefixGroup: 1 },
  { id: "unity-cache-server-endpoint", description: "a Unity Cache Server endpoint", pattern: /(-cacheServerEndpoint(?:[ \t]+|[ \t]*\r?\n[ \t]*)["']?)(?!\[redacted:)(?:&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|&(?!(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);)|\\u[0-9A-Fa-f]{4}|'(?=[\p{L}\p{N}\p{M}_-])|[^&\\\s"'`()\]};,>|])+/u, prefixGroup: 1 },
  { id: "unity-connect-host", description: "a Unity connection host", pattern: /("connectToHost"[ \t]*:[ \t]*")(?!\[redacted:)(?:\\.|[^"\\])+/, prefixGroup: 1 }
]);
const SENSITIVE_PATTERNS = Object.freeze([...CREDENTIAL_PATTERNS, ...IDENTIFIER_PATTERNS]);
function parseIPv6Candidate(raw) {
  const bracketed = raw.startsWith("[");
  if (bracketed && !raw.endsWith("]")) return undefined;
  let candidate = bracketed ? raw.slice(1, -1) : raw.replace(/\]$/, "");
  let suffix = "";
  if (!bracketed && raw.endsWith("]")) suffix = "]";
  if (!bracketed && candidate.endsWith(".")) {
    candidate = candidate.slice(0, -1);
    suffix = ".";
  }
  const withoutZone = candidate.replace(/%.*/, "");
  const macShape = /^(?:[0-9A-Fa-f]{2}:){5,7}[0-9A-Fa-f]{2}$/;
  if (!macShape.test(withoutZone) && net.isIP(withoutZone) === 6) return { suffix };
  if (!bracketed && suffix.length === 0 && candidate.endsWith(":")) {
    const withoutPunctuation = candidate.slice(0, -1).replace(/%.*/, "");
    if (!macShape.test(withoutPunctuation) && net.isIP(withoutPunctuation) === 6)
      return { suffix: ":" };
  }
  return undefined;
}
function isRedactionPlaceholder(value) {
  return /^\[redacted(?::[^\]]+)?\]$/i.test(value);
}
function isUnmaskedValue(value) {
  return !/^\*+$/.test(value) && !isRedactionPlaceholder(value);
}
function isNonEmptyUnmasked(value) {
  return value.trim().length > 0 && isUnmaskedValue(value.trim());
}
function hasBinaryMagic(bytes) {
  const offset =
    bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf ? 3 : 0;
  const startsWith = (...signature) =>
    bytes.length >= offset + signature.length &&
    signature.every((byte, index) => bytes[offset + index] === byte);
  const prefix = bytes.subarray(offset, Math.min(bytes.length, offset + 1024));
  const pdfOffset = prefix.indexOf(Buffer.from("%PDF-"));
  const pdfTail = pdfOffset < 0 ? Buffer.alloc(0) : bytes.subarray(offset + pdfOffset);
  const isPdf =
    pdfOffset >= 0 &&
    (prefix.subarray(0, pdfOffset).every((byte) => [0x09, 0x0a, 0x0d, 0x20].includes(byte)) ||
      pdfTail.includes(Buffer.from("%%EOF")) ||
      /\d+ \d+ obj/.test(pdfTail.toString("latin1")));
  return (
    startsWith(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a) ||
    startsWith(0x50, 0x4b, 0x03, 0x04) ||
    startsWith(0x50, 0x4b, 0x05, 0x06) ||
    startsWith(0x50, 0x4b, 0x07, 0x08) ||
    isPdf ||
    startsWith(0x4d, 0x5a) ||
    startsWith(0x7f, 0x45, 0x4c, 0x46) ||
    startsWith(0x1f, 0x8b) ||
    startsWith(0x21, 0x3c, 0x61, 0x72, 0x63, 0x68, 0x3e, 0x0a)
  );
}
function reviewedText(text, encoding) {
  const nulCount = text.split("\0").length - 1;
  // eslint-disable-next-line no-control-regex -- these code points distinguish text from binaries.
  if (
    hasTooManyNuls(text, nulCount) ||
    /[\u0001-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(text)
  ) {
    return undefined;
  }
  return { text, encoding };
}
function hasTooManyNuls(text, nulCount = text.split("\0").length - 1) {
  return nulCount > MAXIMUM_STRAY_NUL_BYTES || (nulCount > 0 && nulCount * 4 >= text.length);
}
function decodeText(bytes) {
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    try {
      const text = new TextDecoder("utf-16le", { fatal: true, ignoreBOM: true }).decode(bytes);
      return reviewedText(text, "utf16le");
    } catch {
      return undefined;
    }
  }
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    try {
      const text = new TextDecoder("utf-16be", { fatal: true, ignoreBOM: true }).decode(bytes);
      return reviewedText(text, "utf16be");
    } catch {
      return undefined;
    }
  }
  if (hasBinaryMagic(bytes)) return undefined;
  try {
    const encoding = bytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf]))
      ? "utf8bom"
      : "utf8";
    return reviewedText(new TextDecoder("utf-8", { fatal: true }).decode(bytes), encoding);
  } catch {}
  return reviewedText(bytes.toString("latin1"), "latin1");
}
function encodeText(text, encoding) {
  if (encoding === "utf8bom") {
    return Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text, "utf8")]);
  }
  if (encoding === "utf16le") return Buffer.from(text, "utf16le");
  if (encoding === "utf16be") return Buffer.from(text, "utf16le").swap16();
  return Buffer.from(text, encoding);
}
function globalRegExp(entry) {
  return new RegExp(entry.pattern.source, `${entry.pattern.flags}g`);
}
function matchesPattern(text, entry) {
  for (const match of text.matchAll(globalRegExp(entry))) {
    if (!entry.accept || entry.accept(match)) return true;
  }
  return false;
}
function decodeSerialized(value, mode) {
  // cspell:ignore bfnrt
  const decodeJson = (value, full) => {
    const unicode = value
      .replace(/\\u([0-9A-Fa-f]{4})/g, (_, hex) => String.fromCharCode(Number.parseInt(hex, 16)))
      .replaceAll("\\/", "/");
    return full
      ? unicode.replace(/\\(["\\bfnrt])/g, (_, escaped) => JSON.parse(`"\\${escaped}"`))
      : unicode;
  };
  const xmlNames = Object.freeze({ amp: "&", apos: "'", gt: ">", lt: "<", quot: '"' });
  const decodeXml = (value) =>
    value.replace(/&#(x[0-9A-Fa-f]+|[0-9]+);|&(amp|apos|gt|lt|quot);/g, (entity, numeric, name) => {
      const code = numeric
        ? Number.parseInt(numeric.replace(/^x/i, ""), numeric[0].toLowerCase() === "x" ? 16 : 10)
        : undefined;
      return code !== undefined && code <= 0x10ffff
        ? String.fromCodePoint(code)
        : (xmlNames[name] ?? entity);
    });
  return mode === 2 ? decodeXml(value) : decodeJson(value, mode === 1);
}
function serializedShadows(text) {
  if (!SERIALIZED_ESCAPE.test(text)) return { values: [text], truncated: false };
  const values = new Set([text]);
  const pending = [{ value: text, depth: 0 }];
  let retainedCharacters = text.length;
  let truncated = false;
  while (pending.length > 0 && !truncated) {
    const { value, depth } = pending.shift();
    for (const mode of [0, 1, 2]) {
      const decoded = decodeSerialized(value, mode);
      if (values.has(decoded)) continue;
      if (
        depth >= 8 ||
        values.size >= 256 ||
        retainedCharacters + decoded.length > 16 * 1024 * 1024
      ) {
        truncated = true;
        break;
      }
      values.add(decoded);
      retainedCharacters += decoded.length;
      pending.push({ value: decoded, depth: depth + 1 });
    }
  }
  return { values: [...values], truncated };
}
function addSensitiveData(found, shadows) {
  for (const shadow of shadows) {
    for (const entry of [...findCredentials(shadow), ...findIdentifiers(shadow)])
      found.set(entry.id, entry);
  }
}
function recordRuns(text) {
  const runs = [];
  let retained = 0;
  for (const [record] of text.matchAll(/[^\r\n]*(?:\r\n?|\n)|[^\r\n]+$/g)) {
    const previous = runs.at(-1);
    if (previous?.[0] === record) previous[1] += 1;
    else {
      retained += record.length;
      if (runs.length >= 4096 || retained > 2 ** 20) return undefined;
      runs.push([record, 1]);
    }
  }
  return runs;
}
function decodedCandidate(text, modes, runs, seen) {
  const chunks = [];
  const parts = [];
  const cache = new Map();
  let retained = 0;
  for (const [record, count = 1] of runs ?? text.matchAll(/[^\r\n]*(?:\r\n?|\n)|[^\r\n]+$/g)) {
    let decoded = cache.get(record) ?? record;
    if (!cache.has(record)) {
      if (record.length > SERIALIZED_WINDOW_CHARACTERS && SERIALIZED_ESCAPE.test(record))
        return undefined;
      for (const mode of modes) decoded = decodeSerialized(decoded, mode);
      const size = record.length + decoded.length;
      if (size <= 2 ** 20) {
        // Renew admission after a cold prefix; never pin its records for the whole file.
        if (cache.size >= 4096 || retained + size > 2 ** 20) {
          cache.clear();
          retained = 0;
        }
        cache.set(record, decoded);
        retained += size;
      }
    }
    parts.push(runs ? [decoded, count] : decoded);
    if (!runs && parts.length >= 1024) {
      chunks.push(parts.join(""));
      parts.length = 0;
    }
  }
  const candidate = runs ? JSON.stringify(parts) : chunks.join("") + parts.join("");
  const hash = createHash("sha256");
  for (let offset = 0; offset < candidate.length; offset += 65536)
    hash.update(candidate.slice(offset, offset + 65536), "utf16le");
  const fingerprint = hash.digest("hex");
  if (seen.has(fingerprint)) return null;
  seen.add(fingerprint);
  return runs ? parts.map(([record, count]) => record.repeat(count)).join("") : candidate;
}
function findSensitiveData(text) {
  const found = new Map();
  let truncated = false;
  if (text.length <= SERIALIZED_WINDOW_CHARACTERS) {
    const shadows = serializedShadows(text);
    addSensitiveData(found, shadows.values);
    truncated = shadows.truncated;
  } else {
    addSensitiveData(found, [text]);
    if (!SERIALIZED_ESCAPE.test(text)) return [...found.values()];
    // Keep decoder programs and hashes, not full-file shadow sets. Each complete candidate
    // preserves multiline matches while record-wise decoding bounds replacement allocations.
    const runs = recordRuns(text);
    const seen = new Set();
    const pending = [[]];
    while (pending.length > 0) {
      const modes = pending.shift();
      const decoded = decodedCandidate(text, modes, runs, seen);
      if (decoded === undefined) {
        truncated = true;
        break;
      }
      if (decoded === null) continue;
      if (modes.length > 8 || seen.size > 256) {
        truncated = true;
        break;
      }
      if (modes.length > 0) addSensitiveData(found, [decoded]);
      for (const mode of [0, 1, 2]) pending.push([...modes, mode]);
    }
  }
  if (truncated) found.set(ENCODED_LIMIT_FINDING.id, ENCODED_LIMIT_FINDING);
  return [...found.values()];
}
function findCredentials(text) {
  return CREDENTIAL_PATTERNS.filter((entry) => matchesPattern(text, entry));
}
function findIdentifiers(text) {
  const matches = IDENTIFIER_PATTERNS.filter((entry) => matchesPattern(text, entry));
  return [...new Map(matches.map((entry) => [entry.id, entry])).values()];
}
/**
 * Replace every credential value in `text`. Returns the rewritten text and a count per pattern id
 * so a caller can report what it removed without ever echoing what it removed.
 */
function redactPatterns(text, patterns) {
  const counts = new Map();
  let redacted = text;
  for (const entry of patterns) {
    let replaced = 0;
    redacted = redacted.replace(globalRegExp(entry), (...match) => {
      if (entry.accept && !entry.accept(match)) return match[0];
      replaced += 1;
      if (entry.replacement) return entry.replacement(match);
      const prefix = entry.prefixGroup ? match[entry.prefixGroup] : "";
      const suffix = entry.suffix ? entry.suffix(match) : "";
      return `${prefix}[redacted:${entry.id}]${suffix}`;
    });
    if (replaced > 0) counts.set(entry.id, (counts.get(entry.id) || 0) + replaced);
  }
  return { redacted, counts };
}
function redactCredentials(text) {
  return redactPatterns(text, CREDENTIAL_PATTERNS);
}
function redactSensitiveData(text) {
  return redactPatterns(text, SENSITIVE_PATTERNS);
}
function hasBrokenRedaction(text) {
  return (
    /\[redacted:account-home-path\](?:\\["'`|]|[^"'`|\\/\r\n])+(?=["'`|\])}])/.test(text) ||
    /\[redacted:file-uri-hostname\](?=[^/\s"'`|)\]}>},;])/u.test(text) ||
    /\[redacted:account-home-path\][>;"`|](?=[\p{L}\p{N}])/u.test(text)
  );
}
function isSerializedRedactionSafe(text, redacted) {
  const large =
    text.length > SERIALIZED_WINDOW_CHARACTERS || redacted.length > SERIALIZED_WINDOW_CHARACTERS;
  if (large) {
    return (
      !findSensitiveData(text).includes(ENCODED_LIMIT_FINDING) &&
      findSensitiveData(redacted).length === 0 &&
      !hasBrokenRedaction(redacted)
    );
  }
  const source = serializedShadows(text);
  const result = serializedShadows(redacted);
  if (source.truncated || result.truncated) return false;
  try {
    JSON.parse(text);
    JSON.parse(redacted);
    if (redactSensitiveData(text).counts.size > 0 && findSensitiveData(redacted).length === 0)
      return true;
  } catch {}
  if (hasBrokenRedaction(redacted)) return false;
  const redactedShadows = new Set(result.values);
  return source.values.every((shadow) => {
    const expected = redactSensitiveData(shadow);
    return expected.counts.size === 0 || redactedShadows.has(expected.redacted);
  });
}
module.exports = {
  CREDENTIAL_PATTERNS,
  IDENTIFIER_PATTERNS,
  MAXIMUM_STRAY_NUL_BYTES,
  REVIEWED_TEXT_EXTENSIONS,
  SENSITIVE_PATTERNS,
  decodeText,
  encodeText,
  findCredentials,
  findIdentifiers,
  findSensitiveData,
  hasBinaryMagic,
  hasTooManyNuls,
  isSerializedRedactionSafe,
  redactCredentials,
  redactSensitiveData
};
