<!-- trigger: changelog, package manager, changelogUrl, release notes, version history, _upm | Where Unity shows a package changelog | Reference -->

# Where Unity Shows a Package Changelog

> **One-line summary**: The Package Manager exposes a package changelog through one
> `changelogUrl` link plus the packaged `CHANGELOG.md`; the inline per-version notes in
> Version History come from registry metadata that `npm publish` removes.

## The two surfaces

`UnityEditor.PackageManager.UI.Internal.IPackageLinkFactory` builds both changelog entry points
from the same data:

| Factory method                      | Where it appears in the editor   |
| ----------------------------------- | -------------------------------- |
| `CreateUpmChangelogLink`            | Details panel **Changelog** link |
| `CreateVersionHistoryChangelogLink` | Version History changelog link   |

Each link carries a `url` (opened in the browser) and an `offlinePath` (opened locally through
the link's right-click menu). For DxMessaging the editor resolves them as:

- `url` -> `package.json`'s `changelogUrl`,
- `offlinePath` -> `<resolvedPath>/CHANGELOG.md`, which is why `CHANGELOG.md` must stay in the
  `files` allowlist in `package.json`.

Unity's own packages take the same path, with a rendered
`https://docs.unity3d.com/Packages/<name>@<major.minor>/changelog/CHANGELOG.html` URL. A raw
`raw.githubusercontent.com/.../CHANGELOG.md` URL resolves and opens, but renders as unformatted
text, so `changelogUrl` must name a page a browser renders.

### Reproducing the link state

Run this through `Unity_RunCommand` against the host editor (see the `unity-mcp-test-loop`
skill). It reports exactly what the Package Manager would show, without opening a window:

1. Resolve `ServicesContainer.instance` through
   `typeof(ScriptableSingleton<>).MakeGenericType(servicesContainerType)`.
1. `Resolve<IPackageDatabase>()`, then find the package in `allPackages` by `uniqueId`.
1. `Resolve<IPackageLinkFactory>()` and call `CreateUpmChangelogLink(version)`.
1. Read `isVisible`, `isEnabled`, `isEmpty`, `url`, `offlinePath`, and `tooltip`.

Construct the link type directly and every package reports `Changelog unavailable`: the bare
constructor does not receive the services the factory injects. Always go through the factory.

The `Unity_RunCommand` sandbox rejects `System.Reflection.BindingFlags`, so this probe can read
only public members. That is enough: the UI-internal types are internal classes with public
members.

## Inline per-version notes: what is proven

`IPackageVersion.localReleaseNotes` is what Version History renders under an expanded version.
Unity's registry fills it from a `_upm.changelog` field in the package's registry metadata:
`https://packages.unity.com/com.unity.inputsystem` carries the changelog section body per
version, while the DxMessaging documents served by both the public npm registry and
`package.openupm.com` have no `_upm` field at all.

Three measurements bound what is reachable, all taken with npm 11.17.0 and Unity 6000.4.6f1:

| Route                                                      | Result                                                                                                               |
| ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `npm publish <tarball>` with `_upm` in the manifest        | Stripped. The PUT body kept only npm's own `_id`, `_integrity`, `_nodeVersion`, `_npmVersion`, `_from`, `_resolved`. |
| `libnpmpublish.publish(manifest, tarball, opts)`           | Preserved. `_upm.changelog` arrived intact at a local registry stub.                                                 |
| `_upm.changelog` in the installed package's `package.json` | Ignored. With the field present in the embedded manifest, `localReleaseNotes` stayed unset after a refresh.          |

So the stripping is the npm CLI's manifest normalization, not the publish library: a direct
`libnpmpublish` call could carry the field. Two things remain unmeasured, and BOTH must hold
before that is worth building:

1. that npmjs and OpenUPM preserve an unrecognized `_upm` key server-side rather than dropping
   it, and
1. that Unity honors `_upm.changelog` from a scoped (non-Unity) registry.

Measure them with a throwaway package name published to the real registry, installed into a
scratch project through a scoped registry, then read back with the link probe above. Until then
the reachable improvements are the link target and the packaged file, both covered above, and
the irreversible `npm publish` step in `release.yml` stays as it is.

## See Also

- [changelog-entry-writing.md](./changelog-entry-writing.md) - entry template and anti-patterns.
- [Package Publishing](../../package-publishing/SKILL.md) - the `files` allowlist and `.meta`
  pairing rules that keep `CHANGELOG.md` in the tarball.
