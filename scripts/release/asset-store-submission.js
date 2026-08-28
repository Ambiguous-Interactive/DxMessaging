#!/usr/bin/env node
"use strict"; // cspell:ignore IDAT IEND
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { crc32, inflateSync } = require("node:zlib");
const { extractSection } = require("./changelog.js");
const { isPathOutsideDirectory, toPosixPath } = require("../lib/path-classifier.js");
const REQUIRED_ARGS =
  "outDir packageFile packageChecksum unitypackageFile unitypackageChecksum".split(" ");
const STORE_MEDIA = [
  "dxmessaging-store-icon-160.png|160|160|dxmessaging-icon-tile.svg|7519ef9ee3a299f377a53ef308e09db00a0e3f47a1692c5ed3666caf79a75843|b704fcd9e1bdbbd16b0ea8937192794ffee8124f8df62f66b35e636d061af2b5||",
  "dxmessaging-store-card-420x280.png|420|280|dxmessaging-store-card-420x280.svg|eebe18742357a122c49bb55457037870404f4b1ac0b985f945284a5db4c63a92|e19ee873e9fd95b04ffedee05b4fb085885e9fea40c9c50238f6f23561144926|inspector-overlay/flow-graph.png|9d5a2b2649730c31f9e99342ca926397973346a971c330ba965c78e6936f631f",
  "dxmessaging-store-cover-1950x1300.png|1950|1300|dxmessaging-store-cover-1950x1300.svg|c4edf98b793cc96ac23797515886c3e23ff7c2c1a33c0a0af546db055cbf96e1|a87697ca78419245b70ee92062d714a51e4e5e00e052ddf24cc8df3f153748bb|inspector-overlay/flow-graph.png|9d5a2b2649730c31f9e99342ca926397973346a971c330ba965c78e6936f631f"
].map((entry) => entry.split("|"));
// prettier-ignore
const hasExactKeys = (value, expected) => value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).sort().join("\0") === [...expected].sort().join("\0"), isHttpsUrl = (value) => { try { const parsed = new URL(value); return parsed.protocol === "https:" && Boolean(parsed.hostname) && !parsed.username && !parsed.password; } catch { return false; } };
function sha256(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}
function copyFile(source, target) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.copyFileSync(source, target);
}
function writeText(dir, name, content) {
  fs.writeFileSync(path.join(dir, name), `${content}\n`, "utf8");
}
function assertFile(filePath, label) {
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    throw new Error(`${label} is missing: ${toPosixPath(filePath)}`);
  }
}
// prettier-ignore
function validatePng(filePath, expectedWidth = 0, expectedHeight = 0) {
  assertFile(filePath, "Store media");
  const content = fs.readFileSync(filePath);
  if (content.subarray(0, 8).toString("hex") !== "89504e470d0a1a0a")
    throw new Error(`Store media lacks the required PNG structure: ${toPosixPath(filePath)}`);
  let offset = 8;
  let width, height, ended = false;
  const imageData = [];
  while (offset + 12 <= content.length) {
    const length = content.readUInt32BE(offset);
    const end = offset + 12 + length;
    const type = content.toString("ascii", offset + 4, offset + 8);
    const dataEnd = offset + 8 + length;
    if (end > content.length || content.readUInt32BE(dataEnd) !== crc32(content.subarray(offset + 4, dataEnd)))
      throw new Error(`Store media has an invalid PNG chunk: ${toPosixPath(filePath)}`);
    if (offset === 8 && type === "IHDR" && length === 13) {
      width = content.readUInt32BE(offset + 8);
      height = content.readUInt32BE(offset + 12);
    } else if (type === "IDAT") imageData.push(content.subarray(offset + 8, dataEnd));
    else if (type === "IEND") {
      ended = length === 0 && end === content.length;
      break;
    }
    offset = end;
  }
  try {
    if (!width || !height || !ended || imageData.length === 0) throw new Error("missing chunk");
    inflateSync(Buffer.concat(imageData));
  } catch {
    throw new Error(`Store media lacks the required PNG structure: ${toPosixPath(filePath)}`);
  }
  if (expectedWidth && (width !== expectedWidth || height !== expectedHeight))
    throw new Error(`Store media ${toPosixPath(filePath)} is ${width}x${height}; expected ${expectedWidth}x${expectedHeight}.`);
}
// prettier-ignore
function readListing(repoRoot, pkg, changelogSection) {
  const configPath = path.join(repoRoot, ".github", "asset-store-listing.json"); assertFile(configPath, "Asset Store listing source");
  const listing = JSON.parse(fs.readFileSync(configPath, "utf8"));
  const requiredStrings = ["locale", "title", "description", "keywords"];
  const listingKeys = ["schemaVersion", ...requiredStrings, "links", "artwork", "screenshots"];
  const linkKeys = ["documentation", "source", "support"], artworkKeys = ["icon", "card", "cover"];
  if (
    !hasExactKeys(listing, listingKeys) || listing.schemaVersion !== 1 || listing.locale !== "en-US" ||
    requiredStrings.some((key) => typeof listing[key] !== "string" || !listing[key].trim()) ||
    listing.keywords.length > 255 || listing.keywords.split(/\s+/).some((keyword) => !/^[a-z0-9][a-z0-9-]*$/i.test(keyword)) ||
    !hasExactKeys(listing.links, linkKeys) || Object.values(listing.links).some((url) => !isHttpsUrl(url)) ||
    !hasExactKeys(listing.artwork, artworkKeys) || artworkKeys.some((role, index) => listing.artwork[role] !== `media/${STORE_MEDIA[index][0]}`) ||
    !Array.isArray(listing.screenshots) || listing.screenshots.length === 0
  ) {
    throw new Error("Asset Store listing source is incomplete or invalid.");
  }
  const screenshotRoot = path.join(repoRoot, "docs", "images", "inspector-overlay");
  if (!fs.existsSync(screenshotRoot) || !fs.statSync(screenshotRoot).isDirectory() || fs.lstatSync(screenshotRoot).isSymbolicLink()) throw new Error("Asset Store screenshot root is unsafe or invalid.");
  const realScreenshotRoot = fs.realpathSync(screenshotRoot);
  if (isPathOutsideDirectory(realScreenshotRoot, fs.realpathSync(repoRoot))) throw new Error("Asset Store screenshot root is unsafe or invalid.");
  const fileNames = new Set();
  const screenshots = listing.screenshots.map((declaration) => {
    const { source, fileName, caption } = declaration || {};
    const sourcePath = path.resolve(repoRoot, typeof source === "string" ? source : "");
    const portableName = typeof fileName === "string" && /^[a-z0-9][a-z0-9._-]*\.png$/i.test(fileName) && !/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(fileName);
    let realSourcePath;
    try { realSourcePath = fs.realpathSync(sourcePath); } catch { realSourcePath = ""; }
    if (
      !hasExactKeys(declaration, ["source", "fileName", "caption"]) || typeof source !== "string" || !source.trim() || !portableName || fileNames.has(fileName.toLowerCase()) ||
      typeof caption !== "string" || !caption.trim() || !realSourcePath ||
      isPathOutsideDirectory(realSourcePath, realScreenshotRoot)
    )
      throw new Error("Asset Store screenshot declaration is unsafe or invalid.");
    fileNames.add(fileName.toLowerCase());
    validatePng(realSourcePath);
    return { sourcePath: realSourcePath, fileName, caption };
  });
  return {
    listing: { ...listing, packageVersion: pkg.version, minimumUnityVersion: pkg.unity || "", releaseNotes: changelogSection,
      screenshots: screenshots.map(({ fileName, caption }) => ({ file: `screenshots/${fileName}`, caption })) },
    screenshots
  };
}
// prettier-ignore
function readChecksum(checksumPath) {
  const lines = fs.readFileSync(checksumPath, "utf8").split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (lines.length !== 1)
    throw new Error(`Checksum file must contain one line: ${toPosixPath(checksumPath)}`);
  const match = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(lines[0]);
  if (!match)
    throw new Error(`Checksum file is not sha256sum formatted: ${toPosixPath(checksumPath)}`);
  return { hash: match[1].toLowerCase(), fileName: match[2].trim() };
}
// prettier-ignore
function validateChecksum(filePath, checksumPath) {
  assertFile(filePath, "Release file");
  assertFile(checksumPath, "Checksum file");
  const expected = readChecksum(checksumPath);
  const actualName = path.basename(filePath);
  if (expected.fileName !== actualName)
    throw new Error(`Checksum file ${toPosixPath(checksumPath)} references ${expected.fileName}; expected ${actualName}.`);
  const actualHash = sha256(filePath);
  if (expected.hash !== actualHash)
    throw new Error(`Checksum mismatch for ${toPosixPath(filePath)}: expected ${expected.hash}, got ${actualHash}.`);
}
function collectFiles(root) {
  const result = [];
  function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(fullPath);
      else if (entry.isFile() && entry.name !== "MANIFEST.json") result.push(fullPath);
    }
  }
  walk(root);
  return result.sort((left, right) => toPosixPath(left).localeCompare(toPosixPath(right), "en"));
}
// prettier-ignore
function relativeEntry(root, filePath) {
  const stat = fs.statSync(filePath);
  return { path: toPosixPath(path.relative(root, filePath)), bytes: stat.size, sha256: sha256(filePath) };
}
// prettier-ignore
function buildChecklist({ mode, pkg, tag, packageName, unitypackageName, changelogSection }) {
  const isClassic = mode === "classic";
  const heading = `# ${isClassic ? "Classic" : "UPM"} Asset Store Upload Checklist (${tag})`;
  const payload = isClassic ? `Classic source payload: ${unitypackageName}` : `UPM payload: ${packageName}`;
  const uploadSteps = isClassic
    ? `1. Import \`${unitypackageName}\` into a clean project.
1. Confirm the import created \`Assets/WallstopStudios/DxMessaging/\` and no unrelated top-level content. Resolve every import or duplicate-GUID error in the Unity Console.
1. Open \`Tools > Asset Store > Validator\`, add \`Assets/WallstopStudios/DxMessaging/\`, run validation, and resolve every finding.
1. Open \`Tools > Asset Store > Uploader\` and select the package draft.
1. Run \`Export and Upload\`. The official tool creates a new archive from the inspected imported assets; archive hashes are not expected to match.`
    : `1. Continue only after UPM enrollment reaches Active admittance and the UPM Publisher Portal appears.
1. Open a clean project in an editor version supported by the installed official UPM publishing tool.
1. In \`Window > Package Manager\`, choose \`Add package from tarball...\` and select \`${packageName}\` from this artifact.
1. Open \`Window > Tools > Asset Store > Validator\`, select the UPM validation type, and validate the installed package.
1. Open \`Window > Tools > Asset Store > Uploader\`, select the \`UPM Packages\` tab, select ${pkg.name} ${pkg.version}, and upload it.
1. After Unity publishes the version, use a clean project where it is not installed. In Package Manager Version History, expand this version and confirm its notes match \`EXPECTED-UPM-FIELDS.json\`.
1. If supported Unity tooling exposes the version manifest, compare its field values with \`EXPECTED-UPM-FIELDS.json\`. Do not query undocumented endpoints.`;
  return `${heading}

Package: ${pkg.displayName || pkg.name} (${pkg.name})
Version: ${pkg.version}
Unity ${pkg.unity || "not declared"}
${payload}

## Before Upload

1. Verify every \`.sha256\` file in this artifact.
1. Use the staged files from this artifact; do not re-export from a working tree.
1. Apply the exact fields, artwork, and ordered screenshots from \`ASSET-STORE-LISTING.json\` to the Unity Publisher Portal draft.

## Upload

${uploadSteps}
1. Set the listing version to the package version above.
1. Paste the release notes from the changelog excerpt below.
1. Submit for Unity review.

## Changelog Excerpt

${changelogSection}`;
}
// prettier-ignore
function assertSafeOutputDir(repoRoot, outDir) {
  const resolved = path.resolve(repoRoot, outDir);
  const resolvedRepoRoot = path.resolve(repoRoot);
  const relativePath = path.relative(resolvedRepoRoot, resolved);
  if (!relativePath || relativePath.startsWith("..") || path.isAbsolute(relativePath) || resolved === path.parse(resolved).root)
    throw new Error(`Refusing unsafe output directory: ${toPosixPath(resolved)}`);
  const segments = relativePath.split(path.sep).filter(Boolean);
  if (segments[0] !== ".artifacts" || segments.length < 2)
    throw new Error(`Refusing unsafe output directory outside .artifacts/: ${toPosixPath(resolved)}`);
  let current = resolvedRepoRoot;
  for (const segment of segments) {
    current = path.join(current, segment);
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) {
      throw new Error(`Refusing unsafe symlinked output path: ${toPosixPath(current)}`);
    }
  }
  return resolved;
}
// prettier-ignore
function stageAssetStoreSubmission(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || path.join(__dirname, "..", ".."));
  for (const arg of REQUIRED_ARGS)
    if (!options[arg]) throw new Error(`Missing required option: ${arg}`);
  const outDir = assertSafeOutputDir(repoRoot, options.outDir);
  const packageFile = path.resolve(repoRoot, options.packageFile), packageChecksum = path.resolve(repoRoot, options.packageChecksum);
  const unitypackageFile = path.resolve(repoRoot, options.unitypackageFile), unitypackageChecksum = path.resolve(repoRoot, options.unitypackageChecksum);
  validateChecksum(packageFile, packageChecksum);
  validateChecksum(unitypackageFile, unitypackageChecksum);
  const pkg = JSON.parse(fs.readFileSync(path.join(repoRoot, "package.json"), "utf8"));
  const tag = options.tag || `v${pkg.version}`;
  if (tag !== `v${pkg.version}`)
    throw new Error(`Release tag ${tag} does not match package version ${pkg.version}.`);
  const changelogSection = extractSection(fs.readFileSync(path.join(repoRoot, "CHANGELOG.md"), "utf8"), pkg.version);
  if (!pkg._upm || pkg._upm.changelog !== changelogSection)
    throw new Error(`package.json _upm.changelog does not match the CHANGELOG.md section for ${pkg.version}.`);
  const listing = readListing(repoRoot, pkg, changelogSection);
  const mediaRoot = path.join(repoRoot, "docs", "images");
  for (const [name, width, height, source, sourceHash, pngHash, dependency, dependencyHash] of STORE_MEDIA) {
    const file = path.join(mediaRoot, name);
    const sourceFile = path.join(mediaRoot, source);
    validatePng(file, Number(width), Number(height));
    assertFile(sourceFile, "Store media source");
    if (dependency) assertFile(path.join(mediaRoot, dependency), "Store media dependency");
    if (sha256(sourceFile) !== sourceHash || sha256(file) !== pngHash || (dependency && sha256(path.join(mediaRoot, dependency)) !== dependencyHash))
      throw new Error(`Store media source/output lock is stale for ${name}; re-render and update it.`);
  }
  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });
  for (const source of [packageFile, packageChecksum, unitypackageFile, unitypackageChecksum])
    copyFile(source, path.join(outDir, path.basename(source)));
  for (const [name] of STORE_MEDIA)
    copyFile(path.join(mediaRoot, name), path.join(outDir, "media", name));
  for (const screenshot of listing.screenshots)
    copyFile(screenshot.sourcePath, path.join(outDir, "screenshots", screenshot.fileName));
  writeText(outDir, "ASSET-STORE-LISTING.json", JSON.stringify(listing.listing, null, 2));
  const checklistInput = { pkg, tag, packageName: path.basename(packageFile), unitypackageName: path.basename(unitypackageFile), changelogSection };
  writeText(outDir, "CLASSIC-UPLOAD-CHECKLIST.md", buildChecklist({ ...checklistInput, mode: "classic" }));
  writeText(outDir, "UPM-UPLOAD-CHECKLIST.md", buildChecklist({ ...checklistInput, mode: "upm" }));
  // prettier-ignore
  const expectedFields = { name: pkg.name, version: pkg.version, _upm: { changelog: pkg._upm.changelog } };
  writeText(outDir, "EXPECTED-UPM-FIELDS.json", JSON.stringify(expectedFields, null, 2));
  const manifest = {
    schemaVersion: 1,
    package: {
      name: pkg.name,
      displayName: pkg.displayName || "",
      version: pkg.version,
      unity: pkg.unity || "",
      description: pkg.description || "",
      documentationUrl: pkg.documentationUrl || "",
      licensesUrl: pkg.licensesUrl || ""
    },
    tag,
    upload: {
      sanctionedAutomation: false,
      note: "Unity Asset Store upload is manual until Unity publishes a supported non-interactive API."
    },
    files: collectFiles(outDir).map((file) => relativeEntry(outDir, file))
  };
  writeText(outDir, "MANIFEST.json", JSON.stringify(manifest, null, 2));
  return { outDir, version: pkg.version, files: manifest.files };
}
// prettier-ignore
function parseArgs(argv) {
  const out = {};
  const values = { "--out": "outDir", "--package-file": "packageFile", "--package-checksum": "packageChecksum",
    "--unitypackage-file": "unitypackageFile", "--unitypackage-checksum": "unitypackageChecksum", "--tag": "tag" };
  for (let index = 0; index < argv.length; index += 1) {
    const key = values[argv[index]];
    if (!key) throw new Error(`Unknown argument: ${argv[index]}`);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) throw new Error(`Missing value for ${argv[index]}`);
    out[key] = value;
    index += 1;
  }
  const missing = REQUIRED_ARGS.filter((arg) => !out[arg]);
  if (missing.length > 0) throw new Error(`Missing required arguments: ${missing.join(", ")}`);
  return out;
}
// prettier-ignore
function main() {
  try {
    const result = stageAssetStoreSubmission(parseArgs(process.argv.slice(2)));
    console.log(`asset-store-submission: staged ${result.files.length} files for v${result.version} at ${toPosixPath(result.outDir)}`);
  } catch (error) {
    console.error(`asset-store-submission failed: ${error.message}`); process.exit(1);
  }
}
module.exports = { parseArgs, stageAssetStoreSubmission };
if (require.main === module) main();
