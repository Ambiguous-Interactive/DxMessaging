"use strict";

function expandExtensionPattern(pattern) {
  if (pattern === "ya?ml") {
    return ["yaml", "yml"];
  }
  return [pattern];
}

function getCspellHookExtensions(blockText) {
  const match = /files:\s*['"]\(\?i\)\\\.\(([^)]+)\)\$['"]/.exec(blockText || "");
  if (!match) {
    return [];
  }

  return match[1].split("|").flatMap(expandExtensionPattern).sort();
}

function getPackageCspellAllExtensions(script) {
  const match = /"\*\*\/\*\.\{([^}]+)\}"/.exec(script || "");
  if (!match) {
    return [];
  }

  return match[1]
    .split(",")
    .map((extension) => extension.trim())
    .filter(Boolean)
    .sort();
}

module.exports = {
  expandExtensionPattern,
  getCspellHookExtensions,
  getPackageCspellAllExtensions
};
