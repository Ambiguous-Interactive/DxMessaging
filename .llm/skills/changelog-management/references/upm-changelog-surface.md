<!-- trigger: changelog, package manager, changelogUrl, release notes, version history, _upm, upmReserved | Where Unity shows a package changelog | Reference -->

# Where Unity Shows a Package Changelog

> **One-line summary**: The Version History tab renders `package.json`'s
> `_upm.changelog` (kept in sync by `npm run sync:upm-changelog`), and the details-panel
> Changelog link comes from `changelogUrl` with the packaged `CHANGELOG.md` as its offline
> option.

Both surfaces were traced in the host editor (Unity 6000.4.6f1) rather than inferred from the
manual, which documents neither.

## Surface 1: the Version History changelog text

`PackageDetailsVersionHistoryItem.RefreshChangeLog` is the renderer. Its IL reduces to:

```csharp
var info = m_UpmCache.GetBestMatchPackageInfo(
    m_Version.name, m_Version.package.product.id, m_Version.isInstalled, m_Version.versionString);
var text = m_UpmCache.ParseUpmReserved(info).GetString("changelog");
if (!string.IsNullOrEmpty(text)) { /* show title, label, container */ }
```

`ParseUpmReserved` parses `PackageInfo.upmReserved`, which the editor populates from the
resolved package's OWN `package.json` `_upm` object. Measured across the host project: every
registry package whose `package.json` on disk carries `_upm` reports a populated
`upmReserved`, and the two packages without it report an empty one. Unity's first-party
packages ship the field in the manifest for exactly this reason.

`IPackageVersion.localReleaseNotes` is NOT this surface. It stayed empty for every UPM package
in the project, Unity's own included; it belongs to the Asset Store path.

That is why `scripts/release/sync-upm-changelog.js` writes the `## [version]` section into
`package.json`, `release-prepare.yml` regenerates it after the version bump, and
`check:upm-changelog` gates drift in `validate:all`.

### Why the manifest, and not the registry metadata

Unity's own registry serves the same string as `_upm.changelog` in the package document
(`https://packages.unity.com/com.unity.ide.rider` carries it), and Unity does read it from a
scoped registry: a local registry serving a package with `_upm.changelog`, added through
`Client.AddScopedRegistry`, produced a `PackageInfo` whose `upmReserved` held the value, and
`GetBestMatchPackageInfo` + `ParseUpmReserved` returned it.

The manifest is still the right carrier, because the registry route cannot be reached from
this repository's publishing path:

| Route                                                      | Result                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| ---------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `npm publish <tarball>` or `npm publish <dir>` with `_upm` | Stripped from the uploaded metadata. `@npmcli/package-json`'s `_attributes` normalize step deletes every `_`-prefixed key, and both `prepareSteps` and `pacote.manifest` include it. No opt-out flag.                                                                                                                                                                                                                                                                                                             |
| `libnpmpublish.publish(...)` called directly               | Preserves `_upm`: npm 12.0.2 normalizes only `fixName` in `libnpmpublish`, then puts that manifest in the version document. npm now documents its [OIDC token exchange](https://github.com/npm/api-documentation/blob/main/api/registry.npmjs.com/oidc.yaml), so this route no longer needs an undocumented exchange protocol. It remains a candidate, not a release fix: DxMessaging has not rehearsed it against npm's real registry, and OpenUPM still republishes through `npm publish` and strips the field. |
| `_upm` in the shipped `package.json`                       | Survives `npm pack` byte-for-byte, so it reaches npm, OpenUPM (which repacks from the Git tag), Git-URL installs, and the `.unitypackage` alike.                                                                                                                                                                                                                                                                                                                                                                  |

npm [staged publishing](https://docs.npmjs.com/staged-publishing/) now offers a supported,
owner-reviewed rehearsal mechanism. A disposable real-registry package must still prove that the
approved version document retains `_upm` before the irreversible DxMessaging release step changes.

OpenUPM matters here: it is the recommended install path and republishes the package through
its own pipeline, whose stored document carries the `_from` / `_resolved` / `_integrity` /
`readmeFilename` shape that only an npm-CLI tarball publish produces. Anything that depends on
our upload preserving `_upm` would therefore miss most consumers; the manifest field does not.

### Reproducing the read

Through `Unity_RunCommand` (see the `unity-mcp-test-loop` skill), no window required:

1. Resolve `ServicesContainer.instance` via
   `typeof(ScriptableSingleton<>).MakeGenericType(servicesContainerType)`.
1. `Resolve<IPackageDatabase>()` for the package, `Resolve<IUpmCache>()` for the cache.
1. `GetBestMatchPackageInfo(name, 0L, isInstalled, versionString)`, then `ParseUpmReserved`,
   then read `["changelog"]`.

The sandbox rejects the token `System.Reflection.BindingFlags`, but a cast
(`(System.Reflection.BindingFlags)(-1)`) passes and gives full non-public access.

## Surface 2: the Changelog link

`IPackageLinkFactory.CreateUpmChangelogLink` (details panel) and
`CreateVersionHistoryChangelogLink` (Version History) both build from `changelogUrl`, with
`<resolvedPath>/CHANGELOG.md` as `offlinePath` -- which is why `CHANGELOG.md` must stay in the
`files` allowlist. Unity's own packages point at a rendered
`https://docs.unity3d.com/Packages/<name>@<major.minor>/changelog/CHANGELOG.html`, so
`changelogUrl` must name a page a browser renders; a raw Markdown URL opens as plain text.

Construct either link type directly and every package reports `Changelog unavailable` -- the
bare constructor misses the services the factory injects. Always go through the factory.

## See Also

- [changelog-entry-writing.md](./changelog-entry-writing.md) - entry template and anti-patterns.
- [Package Publishing](../../package-publishing/SKILL.md) - the `files` allowlist and `.meta`
  pairing rules that keep `CHANGELOG.md` in the tarball.
