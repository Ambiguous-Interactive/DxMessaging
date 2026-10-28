# Install (Unity UPM)

This page helps you install DxMessaging into a Unity 2021.3+ project using the Unity Package Manager (UPM).

## Quick Reference

| Method                       | Command/URL                                                | Update action                       |
| ---------------------------- | ---------------------------------------------------------- | ----------------------------------- |
| **OpenUPM** (Recommended)    | `openupm add com.wallstop-studios.dxmessaging`             | Select a version in UPM             |
| **Git URL**                  | `https://github.com/Ambiguous-Interactive/DxMessaging.git` | Reinstall at a chosen revision      |
| **npm Scoped Registry**      | Add registry + resolve                                     | Select a version in UPM             |
| **From Releases**            | Download npm `.tgz`                                        | Download and import a new package   |
| **From Source**              | Clone/download zip                                         | Update the embedded checkout        |
| **Manual** (not recommended) | Edit manifest.json                                         | Edit the dependency in the manifest |

Registry installs expose available versions in Package Manager. To upgrade, select a version and
apply the update as described in [Unity's package update guide](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-ui-update.html).

## Methods

### OpenUPM (Recommended)

The easiest way to install DxMessaging is via [OpenUPM](https://openupm.com/packages/com.wallstop-studios.dxmessaging/).

If you have the [OpenUPM CLI](https://openupm.com/docs/getting-started-cli) installed, run:

```bash
openupm add com.wallstop-studios.dxmessaging
```

#### Benefits of OpenUPM

- Available versions and update controls in Unity Package Manager
- Easy dependency management
- Simple one-command installation
- Version history and changelogs available on the [OpenUPM page](https://openupm.com/packages/com.wallstop-studios.dxmessaging/)

**Don't want to install the CLI?** You can also [add OpenUPM as a scoped registry manually](https://openupm.com/docs/getting-started.html).

### Git URL

- Unity Package Manager > Add package from git URL...
- Paste:

```text
https://github.com/Ambiguous-Interactive/DxMessaging.git
```

- Click Add. Unity imports the package and its analyzers/generators.

### npm Scoped Registry

1. Open Unity Package Manager
1. Open the Advanced Package Settings
1. Add an entry for a new "Scoped Registry"
   - Name: `NPM`
   - URL: `https://registry.npmjs.org`
   - Scope(s): `com.wallstop-studios.dxmessaging`
1. Resolve the latest `DxMessaging`

Unity will notify you of version updates when using scoped registries.

### From Releases

Open the [latest package release](https://github.com/Ambiguous-Interactive/DxMessaging/releases/latest)
and download the npm `.tgz` package or classic `.unitypackage`, plus its matching `.sha256` checksum.
Verify the download with `sha256sum -c <file>.sha256`.

### From Source

Embed the package under your Unity project's `Packages` directory, preserving
the package manifest and analyzer layout:

```bash
git clone https://github.com/Ambiguous-Interactive/DxMessaging.git Packages/com.wallstop-studios.dxmessaging
```

For ZIP downloads, extract the repository contents into
`Packages/com.wallstop-studios.dxmessaging`. Do not copy the package into
`Assets`; that bypasses Unity Package Manager behavior.

### Manual - Manifest.json (not recommended)

- Open your project's `Packages/manifest.json` and add:

```json
{
  "dependencies": {
    "com.wallstop-studios.dxmessaging": "https://github.com/Ambiguous-Interactive/DxMessaging.git"
  }
}
```

## Minimum Requirements

- Unity 2021.3+ (LTS recommended). See [Compatibility](../reference/compatibility.md) for Unity x Render Pipeline support (Built-In, URP, HDRP).

## Source Generators and Assembly Definitions

DxMessaging's compiled source generators (`[DxUntargetedMessage]`, `[DxTargetedMessage]`,
`[DxBroadcastMessage]`, `[DxAutoConstructor]`) work whether or not your project
uses assembly definitions. The package ships the optimized analyzer assemblies so they reach the
DxMessaging assembly and every assembly that references it, including the default
`Assembly-CSharp` used by projects without any `.asmdef` files. You do not need to
create an assembly definition for `[Dx*Message]` types to generate.

If you previously worked around an earlier version by copying the analyzer DLLs
into your own `Assets/` folder, that is no longer necessary: the package now ships
them, and the old copy under `Assets/Plugins/Editor/` is removed automatically when
you upgrade. The shipped generators are compile-time only and are never included in
player builds.

## After Installation

- Jump to [Quick Start](quick-start.md). No bootstrap GameObject is required: DxMessaging creates
  the default message bus automatically.
- Add your `MessageAwareComponent` receiver to a GameObject. Unity adds its required
  `MessagingComponent` automatically.
- Use `MessagingComponent` directly only when another `MonoBehaviour` needs an owned registration
  token or a configured message-bus provider.
- Optional: enable diagnostics from the `MessagingComponent` inspector to inspect live emissions.
