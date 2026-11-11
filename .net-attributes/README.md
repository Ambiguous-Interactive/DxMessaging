# DxMessaging Attributes

The `WallstopStudios.DxMessaging.Attributes` package provides DxMessaging attributes and the existing C# source generators for shared rules code. Its runtime assembly targets .NET Standard 2.0 and has no Unity or messaging-runtime dependency. The generator and analyzer DLLs are compiler inputs; they are not runtime dependencies.

This package is free and MIT licensed, like DxMessaging. The repository is preparing its first NuGet release. Local builds use `0.0.0-local`; no public version is implied by this source tree.

Add a `PackageReference` to the published version when available. Do not link attribute source files or add analyzer paths. The package supplies both.

```csharp
namespace SharedRules
{
    using DxMessaging.Core.Attributes;

    [DxAutoConstructor]
    public readonly partial struct RuleState
    {
        public readonly int Count;
        [DxOptionalParameter("unset")]
        public readonly string Label;
    }

    public static class Rules
    {
        public static RuleState Create() => new RuleState(42);
    }
}
```

`Rules.Create()` returns `Count == 42` and `Label == "unset"` through the generated constructor. C# 9 and the supported Roslyn 3.8 compiler floor are preserved.

In Unity, install the normal DxMessaging UPM package and reference `WallstopStudios.DxMessaging.Attributes` from an assembly definition that uses these attributes. A rules assembly can set `noEngineReferences` to `true`. Assemblies that also use messaging APIs retain their reference to `WallstopStudios.DxMessaging`.

The package contains the seven public attributes. Message attributes still need the messaging runtime when their generated interfaces are used; `[DxAutoConstructor]` and `[DxOptionalParameter]` work without it. Attribute namespaces and names remain `DxMessaging.Core.Attributes`. The runtime forwards its former attribute types to the new assembly for binary lookup compatibility.

See [DxMessaging documentation](https://ambiguous-interactive.github.io/DxMessaging/) for generator behavior and supported message APIs.
