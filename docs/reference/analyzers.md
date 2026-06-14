# Roslyn Analyzers & Diagnostics

[Back to Reference](reference.md) | [Troubleshooting](troubleshooting.md) | [Quick Reference](quick-reference.md) | [FAQ](faq.md)

---

DxMessaging ships a Roslyn analyzer (`WallstopStudios.DxMessaging.SourceGenerators.dll`) that catches the most common authoring mistakes at compile time. This page is the canonical reference for every diagnostic the package emits.

## Quick reference table

| ID                                                                            | Severity | Title                                                                                 | Source                                  |
| ----------------------------------------------------------------------------- | -------- | ------------------------------------------------------------------------------------- | --------------------------------------- |
| [`DXMSG002`](#dxmsg002-multiple-message-attributes)                           | Error    | Multiple Message Attributes                                                           | `DxMessageIdGenerator`                  |
| [`DXMSG003`](#dxmsg003-containing-type-must-be-partial-for-nested-generation) | Warning  | Containing type must be partial for nested generation                                 | both generators                         |
| [`DXMSG004`](#dxmsg004-add-partial-keyword-to-containing-type)                | Info     | Add 'partial' keyword to containing type                                              | both generators                         |
| [`DXMSG005`](#dxmsg005-invalid-optional-default-value)                        | Error    | Invalid optional default value                                                        | `DxAutoConstructorGenerator`            |
| [`DXMSG006`](#dxmsg006-missing-base-call)                                     | Warning  | Missing `base.{method}()` call                                                        | `MessageAwareComponentBaseCallAnalyzer` |
| [`DXMSG007`](#dxmsg007-new-hides-unity-method)                                | Warning  | Unity lifecycle method hidden with `new`                                              | `MessageAwareComponentBaseCallAnalyzer` |
| [`DXMSG008`](#dxmsg008-opt-out-marker)                                        | Info     | Type opted out of base-call check                                                     | `MessageAwareComponentBaseCallAnalyzer` |
| [`DXMSG009`](#dxmsg009-implicit-hide-and-missing-modifier)                    | Warning  | Method implicitly hides MessageAwareComponent lifecycle method (no `override`/`new`)  | `MessageAwareComponentBaseCallAnalyzer` |
| [`DXMSG010`](#dxmsg010-broken-transitive-base-call-chain)                     | Warning  | `base.{method}()` chains into an override that does not reach `MessageAwareComponent` | `MessageAwareComponentBaseCallAnalyzer` |

!!! tip
All diagnostic IDs can be customised per project in `.editorconfig` -- e.g. `dotnet_diagnostic.DXMSG006.severity = error` to upgrade missing base calls to a build break.

---

## DXMSG002: Multiple Message Attributes

- **Severity:** Error
- **Source:** `DxMessageIdGenerator`
- **Triggered when:** A type carries more than one of `[DxBroadcastMessage]`, `[DxTargetedMessage]`, or `[DxUntargetedMessage]`.
- **Message:** `Type '{0}' cannot have more than one Dx message attribute ([DxBroadcastMessage], [DxTargetedMessage], [DxUntargetedMessage]).`

### Fix

Pick exactly one message-shape attribute. A message can be Broadcast, Targeted, or Untargeted -- not two at once. If you genuinely need both shapes, define two separate types.

```csharp
//  Multiple shapes on one type
[DxBroadcastMessage]
[DxTargetedMessage]
public readonly partial struct Healed { public readonly int amount; }

//  One shape per type
[DxTargetedMessage]
public readonly partial struct Healed { public readonly int amount; }
```

---

## DXMSG003: Containing type must be partial for nested generation

- **Severity:** Warning
- **Source:** Both `DxMessageIdGenerator` and `DxAutoConstructorGenerator`
- **Triggered when:** A type that needs source generation (i.e. carries a `[DxAutoConstructor]` or any `[Dx*Message]` attribute) is nested inside one or more containing types that are not declared `partial`.
- **Message:** `Type '{0}' is nested inside non-partial container(s): {1}. Suggested fix: add the 'partial' keyword to the containing type declaration(s).`

### Fix

Add `partial` to every enclosing type declaration. Roslyn cannot emit additional members into a nested type unless every container is partial.

```csharp
//  Container is not partial; generation cannot continue
public sealed class GameSystems
{
    [DxUntargetedMessage]
    public readonly partial struct SceneLoaded { public readonly int buildIndex; }
}

//
public sealed partial class GameSystems
{
    [DxUntargetedMessage]
    public readonly partial struct SceneLoaded { public readonly int buildIndex; }
}
```

---

## DXMSG004: Add 'partial' keyword to containing type

- **Severity:** Info
- **Source:** Both `DxMessageIdGenerator` and `DxAutoConstructorGenerator`
- **Triggered when:** Same condition as [`DXMSG003`](#dxmsg003-containing-type-must-be-partial-for-nested-generation), but emitted as an Info-level suggestion alongside the warning so IDEs can surface it as a lightbulb action.
- **Message:** `Add 'partial' to the declaration of '{0}' to enable generation for nested type '{1}'.`

### Fix

Identical to [`DXMSG003`](#dxmsg003-containing-type-must-be-partial-for-nested-generation) -- add `partial` to the named container.

---

## DXMSG005: Invalid optional default value

- **Severity:** Error
- **Source:** `DxAutoConstructorGenerator`
- **Triggered when:** A field marked `[DxOptionalParameter]` carries a default expression that is not a valid C# constant for the field's type (e.g. a method call, a non-constant member access, an expression of the wrong type).
- **Message:** `Field '{0}' default value expression '{1}' is not a valid optional parameter default for type '{2}'.`

### Fix

Replace the expression with a constant literal, an enum member, `default`, `null` (for reference types and nullable value types), or a `const` field reference.

```csharp
//  Method calls and non-constant expressions are not legal C# defaults
[DxAutoConstructor]
public readonly partial struct Damage
{
    [DxOptionalParameter(GetDefaultAmount())] public readonly int amount;
}

//  Constants only
[DxAutoConstructor]
public readonly partial struct Damage
{
    [DxOptionalParameter(0)] public readonly int amount;
}
```

---

## DXMSG006: Missing base call

- **Severity:** Warning (lowered to Info under the smart-case described below)
- **Source:** `MessageAwareComponentBaseCallAnalyzer`
- **Triggered when:** A class deriving from `DxMessaging.Unity.MessageAwareComponent` overrides one of the **five guarded methods** without invoking the base implementation.
- **Messages (per guarded method):** the analyzer emits a per-method consequence sentence so the diagnostic is actionable rather than generic. The message strings are sourced verbatim from `MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod`:

| Method                    | Message format (`{0}` is the offending type)                                                                                                                                                                                            |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Awake`                   | `'{0}' overrides MessageAwareComponent.Awake but does not call base.Awake(); the message registration token will never be created and handlers cannot register.`                                                                        |
| `OnEnable`                | `'{0}' overrides MessageAwareComponent.OnEnable but does not call base.OnEnable(); handlers will not be re-enabled when this component is enabled.`                                                                                     |
| `OnDisable`               | `'{0}' overrides MessageAwareComponent.OnDisable but does not call base.OnDisable(); handlers will not be disabled when this component is disabled, causing unwanted message processing.`                                               |
| `OnDestroy`               | `'{0}' overrides MessageAwareComponent.OnDestroy but does not call base.OnDestroy(); handlers will not be deregistered and the registration token will not be released, causing a memory leak.`                                         |
| `RegisterMessageHandlers` | `'{0}' overrides MessageAwareComponent.RegisterMessageHandlers but does not call base.RegisterMessageHandlers(); default string-message handlers will not be registered (override RegisterForStringMessages to suppress this warning).` |

A generic fallback (`'{0}' overrides MessageAwareComponent.{1} but does not call base.{1}(); the messaging system may not function correctly on this component.`) exists as a safety net for any future guarded method whose entry has not yet been populated. A meta-test forces the per-method dictionary to stay aligned with the guarded set so the fallback is never reached in practice.

### Guarded methods

| Method                    | Why the base call matters                                                                                 |
| ------------------------- | --------------------------------------------------------------------------------------------------------- |
| `Awake`                   | The message registration token will never be created and handlers cannot register.                        |
| `OnEnable`                | Handlers will not be re-enabled when this component is enabled.                                           |
| `OnDisable`               | Handlers will not be disabled when this component is disabled, causing unwanted message processing.       |
| `OnDestroy`               | Handlers will not be deregistered and the registration token will not be released, causing a memory leak. |
| `RegisterMessageHandlers` | Default string-message handlers will not be registered. Override `RegisterForStringMessages` to suppress. |

!!! note
`OnApplicationQuit` is intentionally **not** guarded. The base implementation is a documented no-op -- missing a base call there is harmless and the analyzer ignores it.

### Detection policy (good-faith textual match)

The analyzer looks for any `base.<methodName>(...)` invocation anywhere inside the override body -- including invocations nested inside lambdas or local functions. It does **not** perform reachability or data-flow analysis. The single known false-positive shape is **helper indirection**:

```csharp
//  False positive: analyzer cannot follow the indirection and emits DXMSG006
protected override void Awake() => CallHelper();
private void CallHelper() => base.Awake();   // analyzer sees this in CallHelper, not Awake
```

If you genuinely need to delegate to a helper, suppress the warning with `[DxIgnoreMissingBaseCall]` (see [Suppression precedence](#suppression-precedence) below).

### Smart case: `RegisterForStringMessages => false`

When the same class overrides `RegisterForStringMessages` to literally `false`, DXMSG006 on `RegisterMessageHandlers` is **lowered to Info severity** (same diagnostic ID -- still configurable via `.editorconfig`). The interpretation is **strict literal-only**:

| Form                                                                                              | Lowered to Info?       |
| ------------------------------------------------------------------------------------------------- | ---------------------- |
| `protected override bool RegisterForStringMessages => false;`                                     | yes                    |
| `protected override bool RegisterForStringMessages { get => false; }`                             | yes                    |
| `protected override bool RegisterForStringMessages { get { return false; } }`                     | yes (single statement) |
| `protected override bool RegisterForStringMessages => default;`                                   | no -- stays Warning    |
| `protected override bool RegisterForStringMessages => !true;`                                     | no -- stays Warning    |
| `protected override bool RegisterForStringMessages => Constants.Disable;`                         | no -- stays Warning    |
| `protected override bool RegisterForStringMessages { get { if (x) return false; return true; } }` | no -- stays Warning    |

The smart-case is deliberately conservative: anything that introduces a conditional, a non-literal expression, or even one extra statement is treated as ambiguous and stays at Warning severity.

### Suppression options

See [Suppression precedence](#suppression-precedence) for the full ordering.

---

## DXMSG007: `new` hides Unity method

- **Severity:** Warning
- **Source:** `MessageAwareComponentBaseCallAnalyzer`
- **Triggered when:** A subclass uses the `new` modifier (instead of `override`) on one of the five guarded method names.
- **Message:** `'{0}' hides MessageAwareComponent.{1} with 'new'; replace with 'override' and call base.{1}() so the messaging system continues to function.`

### Why this is worse than DXMSG006

`new` doesn't override -- it shadows. Unity calls the **base** lifecycle method, which still runs correctly, but if you also expect your hidden method to run (e.g. via `someComponent.OnEnable()` from a polymorphic call site) you'll get the wrong dispatch. More commonly: developers reach for `new` thinking it suppresses a CS0114 hide-warning, and the result is a silently broken component.

### Fix

Replace `new` with `override` and add the base call.

```csharp
//  Hides the lifecycle method; Unity still calls the base, your code never runs
public sealed class HealthComponent : MessageAwareComponent
{
    new void OnEnable() { _hud.Show(); }
}

//  Override and chain
public sealed class HealthComponent : MessageAwareComponent
{
    protected override void OnEnable()
    {
        base.OnEnable();
        _hud.Show();
    }
}
```

---

## DXMSG008: Opt-out marker

- **Severity:** Info
- **Source:** `MessageAwareComponentBaseCallAnalyzer`
- **Triggered when:** A method or class is excluded from the base-call check via `[DxIgnoreMissingBaseCall]` or via the project-level ignored-types file, **and** the analyzer would otherwise have emitted `DXMSG006`, `DXMSG007`, `DXMSG009`, or `DXMSG010` for that method.
- **Message:** `'{0}' is excluded from the DxMessaging base-call check ({1}).`

### Purpose

DXMSG008 is purely informational. It tells you "yes, the analyzer noticed this would be a problem, but you've explicitly opted out -- here's where the suppression came from". The placeholder `{1}` reports the suppression source: either the literal `[DxIgnoreMissingBaseCall]` or the file name `DxMessaging.BaseCallIgnore.txt`.

### Quieting it

Most users leave DXMSG008 enabled because it's a useful audit signal. To silence it for a specific project, add to `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.DXMSG008.severity = none
```

---

## DXMSG009: Implicit hide and missing modifier

- **Severity:** Warning
- **Source:** `MessageAwareComponentBaseCallAnalyzer`
- **Triggered when:** A subclass of `MessageAwareComponent` declares a method whose name matches one of the five guarded lifecycle methods (`Awake`, `OnEnable`, `OnDisable`, `OnDestroy`, `RegisterMessageHandlers`), with neither `override` nor `new`, AND the signature is parameter-less, returns `void`, is non-static, and is non-generic. C# treats this as implicit hiding (compiler warning [CS0114](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0114)) -- the base method never runs and the messaging system will not function.
- **Message:** `'{0}' declares {1} without 'override' or 'new'; this implicitly hides MessageAwareComponent.{1} (CS0114) and the messaging system will not function. Add 'override' and call base.{1}(), or add 'new' if the hiding is intentional.`

### Why this exists

DXMSG009 is the most common Unity footgun. Forgetting `override` on `private void OnEnable()` is silent at runtime -- Unity calls the subclass method directly, the base implementation never gets a chance to enable the messaging token, and every registered handler stops working. C# already emits CS0114 for this, but in many Unity projects compiler warnings get ignored. DXMSG009 surfaces it to the inspector overlay and the project's analyzer report.

### Fix

```csharp
//  Implicit hiding  --  DXMSG009 fires (alongside CS0114)
public class BrokenThing : MessageAwareComponent
{
    private void OnEnable() { }
}

//  Override and chain
public class FixedThing : MessageAwareComponent
{
    protected override void OnEnable()
    {
        base.OnEnable();
        // ... your logic ...
    }
}
```

Use `new` instead of `override` only if you have a deliberate reason to disable the base implementation; in that case DXMSG007 will fire and DXMSG009 will not. The recommended fix is almost always `override` + `base.{method}()`.

### Suppression

DXMSG009 honors all the same suppression paths as DXMSG006 -- see [Suppression precedence](#suppression-precedence) below.

### Signature filter

DXMSG009 fires only when the method shape matches a Unity lifecycle method:

- Parameter-less.
- Returns `void`.
- Non-static.
- Non-generic.

So unrelated overloads like `void OnEnable(int discriminator) {}`, unrelated static helpers, and generic same-name methods (`void Awake<T>()`, which C# does not treat as hiding because the type-parameter arity differs from the base) all stay silent -- they aren't actually hiding the base.

### Coexistence with other diagnostics

DXMSG009 is mutually exclusive with DXMSG006 and DXMSG007 _for the same method_ (a method either has `override`, `new`, or neither). However, a single subclass can carry **both** DXMSG009 (on one method that's missing the modifier) and DXMSG006 (on a different method that overrides without `base.X()`) -- in that case the inspector overlay lists both methods in its HelpBox.

### Editor inspector overlay

The inspector overlay's `BaseCallTypeScanner` is an IL-reflection scanner -- it reads each override's IL bytes via `MethodInfo.GetMethodBody()` and checks for the `call`/`callvirt` shape that `base.X()` compiles to. The C# compiler emits **the same IL** for `new void X()` (DXMSG007) and for a same-named declaration with the modifier missing (DXMSG009 / CS0114): both produce a non-virtual hide-by-sig method. **The IL scanner cannot distinguish DXMSG009 from DXMSG007 from IL alone**, so it conservatively records the diagnostic id as `DXMSG007` in the cached snapshot for both cases. The compile-time analyzer remains authoritative for the precise classification -- when the cached `Snapshot.diagnosticIds` (or the JSON file at `Library/DxMessaging/baseCallReport.json`) shows `DXMSG007` but the analyzer console output is `DXMSG009`, **trust the analyzer**. The HelpBox itself lights up correctly either way because the overlay reads `missingBaseFor` (the method name list) for its rendering -- the user-visible behaviour is identical.

---

## DXMSG010: Broken transitive base-call chain

- **Severity:** Warning
- **Source:** `MessageAwareComponentBaseCallAnalyzer`
- **Triggered when:** A class deriving from `MessageAwareComponent` correctly calls `base.{method}()` from one of the five guarded overrides, BUT the inherited override on an intermediate ancestor does **not** itself chain to `base.{method}()`. The chain is broken at the parent, so `MessageAwareComponent`'s lifecycle work never runs on this component even though the user's override looks correct in isolation.
- **Message:** `'{0}' calls base.{1}() but the inherited override on '{2}' does not chain to MessageAwareComponent.{1}; the messaging system will not function correctly on this component.`

### Why this exists

DXMSG006 is a per-method syntactic check: "does this override contain a textual `base.X()` call?". That check fires on the broken intermediate (e.g., a parent `ddd.OnEnable() {}`), but it cannot see across the inheritance boundary into a descendant. Without DXMSG010, the user editing the descendant only sees a clean override -- no diagnostic -- even though their component is silently broken.

```csharp
//  Both warnings now fire.
public class ddd : MessageAwareComponent
{
    protected override void OnEnable() { }              // DXMSG006 here  --  chain dies here
}

public class BrokenThing : ddd
{
    protected override void OnEnable()
    {
        base.OnEnable();                                 // DXMSG010 here  --  chain still broken
    }
}
```

### Semantic difference vs DXMSG006

- **DXMSG006** is a per-method, per-class textual check: a single override either contains `base.X()` or it doesn't. It runs in isolation.
- **DXMSG010** is a transitive chain walk: it follows `IMethodSymbol.OverriddenMethod` from this override up the inheritance graph (normalising via `OriginalDefinition` so generic intermediates like `MyBase<T>` don't confuse the lookup) and confirms every link calls base before terminating at `MessageAwareComponent`. If any intermediate link is broken, every descendant in the chain warns -- not just the original offender.

### Cross-assembly assume-clean caveat

If an ancestor's override has no `DeclaringSyntaxReferences` -- typically because it lives in a binary-only third-party package -- the analyzer cannot inspect its body. In that case DXMSG010 trusts the ancestor and does **not** fire. Emitting the diagnostic against a type the user can't edit would be unactionable.

### Suppression options

DXMSG010 honours all the same suppression paths as DXMSG006 -- see [Suppression precedence](#suppression-precedence) below. Class-level `[DxIgnoreMissingBaseCall]` on the descendant, a method-level attribute, or a project ignore-list entry all convert DXMSG010 into the informational DXMSG008.

### Fix

In order of preference:

- **Fix the broken intermediate.** Open the parent class's override and add the missing `base.{method}()`. This is the correct fix in almost every case -- every descendant in the chain becomes clean automatically.
- **Override directly from `MessageAwareComponent`.** If you control the descendant but not the intermediate, change the descendant's base type to skip the broken intermediate.
- **Suppress with `[DxIgnoreMissingBaseCall]`.** Only when the broken chain is genuinely intentional (e.g. a deliberate adapter that shouldn't participate in messaging). Document the reason in a comment alongside the attribute.

### Known limitation

DXMSG010 reuses the same good-faith textual check as DXMSG006 (`ContainsBaseInvocation`). If an ancestor's body literally contains `base.X()` after a `return;` (i.e. unreachable but syntactically present), the chain check considers it clean -- mirroring DXMSG006's policy. Both diagnostics share a single textual policy so users get consistent results; if you genuinely need flow-aware analysis, suppress with `[DxIgnoreMissingBaseCall]` and review manually.

---

## Suppression precedence

When DxMessaging suppresses a base-call check, it consults the following sources in order. The **first** match wins:

1. **Method-level attribute** -- `[DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]` placed directly on the override.
1. **Class-level attribute** -- `[DxIgnoreMissingBaseCall]` placed on the type declaration; suppresses all guarded overrides inside that type.
1. **Project ignore list** -- fully-qualified type names mirrored to the generated `Assets/Editor/DxMessaging.BaseCallIgnore.txt` sidecar (one per line). Manage entries with the Inspector overlay's **Ignore this type** / **Stop ignoring** actions, or by editing the ignore-list field on `Assets/Editor/DxMessagingSettings.asset`.
1. **`.editorconfig` rule** -- `dotnet_diagnostic.DXMSG006.severity = none` (or `DXMSG007.severity = none`, `DXMSG009.severity = none`, `DXMSG010.severity = none`) disables the diagnostic project-wide.

```csharp
// Method-level  --  only this override is exempt
public sealed class FlashyComponent : MessageAwareComponent
{
    [DxIgnoreMissingBaseCall]
    protected override void Awake() => CallHelperThatChainsToBase();
}

// Class-level  --  every guarded override on this type is exempt
[DxIgnoreMissingBaseCall]
public sealed class LegacyAdapter : MessageAwareComponent { /* ... */ }
```

!!! warning
Suppressing the diagnostic does not change the runtime behaviour: if your override genuinely never reaches `base.Awake()`, the messaging system on that component will still be dead. The suppression only silences the analyzer.

---

## Inspector integration

The Inspector overlay's data source is the **`BaseCallTypeScanner`** -- a deterministic IL-reflection scanner that walks every loaded `MessageAwareComponent` subclass via `UnityEditor.TypeCache.GetTypesDerivedFrom<MessageAwareComponent>()` and inspects each override's IL body for the base-call shape (`call`/`callvirt` to a parent's same-named method). The scanner runs on every `AssemblyReloadEvents.afterAssemblyReload` and on every `CompilationPipeline.assemblyCompilationFinished` burst (debounced via `EditorApplication.delayCall`).

**Why IL reflection?** The previous console-scrape harvester read warnings from `UnityEditor.LogEntries` and from per-assembly `CompilerMessage[]` payloads. Both stores are downstream of Unity's decision to actually surface analyzer warnings -- and on Unity 2021 with Bee/csc cache hits (which happen on most domain reloads after the first), Unity skips that surface entirely. The scrape returned nothing, even though the analyzer ran successfully on the original compile. The result was an intermittent "missing warnings" bug: warnings would appear after a fresh compile and then disappear after a domain reload, with no user-visible cause. IL reflection over loaded types is deterministic -- the assemblies are in the AppDomain, the methods have IL bodies, the same scan produces the same result on every reload regardless of compile-pipeline state.

**Cross-assembly assume-clean.** Ancestors whose IL is unavailable (`MethodInfo.GetMethodBody()` returns null -- abstract methods, P/Invoke, IL2CPP-stripped bodies, closed-source third-party libraries) are trusted. Emitting an unactionable warning against code the user can't edit would be hostile, and the compile-time analyzer remains the authoritative source for CI builds.

**OpCodes-table walker.** The scanner's IL walker decodes every CIL instruction by looking up its `OpCode` in the static tables built from `System.Reflection.Emit.OpCodes` reflection (single-byte and two-byte 0xFE-prefix forms) and steps the operand-size that the opcode declares (`OpCode.OperandType`). Misalignment past multi-byte-operand opcodes (`switch` jump tables, `ldstr` 4-byte tokens, 8-byte literal constants, etc.) is therefore impossible -- the walker either consumes every byte correctly or stops at the first unrecognised opcode and returns the assume-clean default. Phantom DXMSG006 from a misread `0x28` inside a wider operand is no longer a failure mode. The compile-time analyzer remains authoritative for CI; if you hit a phantom warning that the analyzer doesn't agree with, please open an issue.

**DXMSG009 classified as DXMSG007 in the cache.** The scanner's IL-only probe cannot distinguish DXMSG007 (`new` modifier) from DXMSG009 (missing `override` / CS0114) -- Roslyn emits the same IL for both. The cached snapshot conservatively classifies both as `DXMSG007`. The compile-time analyzer remains authoritative for the precise classification -- see the [DXMSG009: Editor inspector overlay subsection](#editor-inspector-overlay) above.

**Legacy console-scrape bridge (opt-in).** The `DxMessagingSettings.UseConsoleBridge` field on `Assets/Editor/DxMessagingSettings.asset` re-enables the old data sources (`UnityEditor.LogEntries` reflection + `CompilationPipeline.assemblyCompilationFinished` `CompilerMessage[]`) and unions them INTO the IL scanner's snapshot -- never overrides it. Default off. Enable only if you want the union of both data sources, e.g. to surface a regression in the IL byte-walker that is correctly captured by the compile-time analyzer's console output.

The unified per-FQN snapshot is persisted to `Library/DxMessaging/baseCallReport.json` so the overlay has data to render before the first post-load rescan completes; it is rewritten on every successful rescan. A manual `Tools -> DxMessaging -> Rescan Base-Call Warnings` menu is available for force-rescan.

The overlay itself uses two complementary editor-injection paths, each with its own entry point in `MessageAwareComponentInspectorOverlay`:

- **`Editor.finishedDefaultHeaderGUI`** (entry point: `RenderForHeaderHook`) -- the cross-version path that fires after Unity draws the default component header. Reliable on Unity 2022+. Because this hook runs _post-body_, gating the render on `EventType.Repaint` is safe -- the inspector's Layout pass for the editor has already settled.
- **Fallback `[CustomEditor(typeof(MessageAwareComponent), editorForChildClasses: true, isFallback: true)]`** (entry point: `RenderInsideOnInspectorGUI`) -- needed on Unity 2021, where `finishedDefaultHeaderGUI` does not always fire for `MonoBehaviour` subclasses without a registered custom editor. The `isFallback: true` flag means a user-defined `[CustomEditor]` for the same component type still wins precedence; the fallback only renders when no other editor is registered.

**Layout/Repaint balance.** Inside `Editor.OnInspectorGUI`, Unity invokes the editor twice per frame: once with `Event.current.type == EventType.Layout` (control registration) and once with `EventType.Repaint` (drawing). Both passes must emit _identical_ sequences of `EditorGUILayout.*` calls -- short-circuiting `OnInspectorGUI` on event type corrupts the inspector window's layout cache and breaks adjacent components. The `RenderInsideOnInspectorGUI` entry point therefore performs all "should we render?" gating up-front (before any `EditorGUILayout` call) and never gates on `EventType`. Cross-path dedupe with the header hook is handled by an unconditional skip inside `DrawHeader` when the editor instance is our fallback editor -- so the two paths never both render for the same target on the same frame, regardless of Unity version. The fallback editor also walks `SerializedObject` directly and skips `m_Script` rather than calling `DrawDefaultInspector()`, which would otherwise duplicate the script row that Unity already draws in the header.

Components that emit DXMSG006, DXMSG007, DXMSG009, or DXMSG010 show a HelpBox at the top of their Inspector with three actions:

- **Open Script** -- jumps to the offending override in your IDE of choice.
- **Ignore this type** -- appends the type's fully-qualified name to the ignore list in `Assets/Editor/DxMessagingSettings.asset`; the generated sidecar is refreshed for the analyzer.
- **Stop ignoring** -- appears instead of "Ignore this type" when the type is already in the ignore list; removes it.

The HelpBox respects the per-project master toggle in `DxMessagingSettings.BaseCallCheckEnabled` on `Assets/Editor/DxMessagingSettings.asset`. When this toggle is off, the Inspector overlay is silenced; the underlying DXMSG006/DXMSG007/DXMSG009 compile-time warnings still emit unless you suppress them via `.editorconfig` (e.g. `dotnet_diagnostic.DXMSG006.severity = none`).

A snapshot of the latest harvest is also persisted to `Library/DxMessaging/baseCallReport.json` so the overlay has data to show on first open before the post-load rescan completes.

**Eager-load and "cached from previous session" indicator.** The harvester's static constructor synchronously calls `LoadFromDisk` BEFORE scheduling the first scan via `EditorApplication.delayCall`. The on-disk cache populates `SnapshotInternal` immediately, so the inspector overlay can render warnings the moment the user clicks into a `MessageAwareComponent` -- even before the first post-reload scan has had a chance to run. Until that first scan completes (typically within a few `EditorApplication.update` ticks after assembly reload), the harvester's `IsFreshThisSession` flag stays `false` and the overlay annotates each warning with a `(cached from previous session  --  refreshing...)` suffix so the user understands the data may be stale. Once the first `RescanNow` post-startup writes a new snapshot and raises `ReportUpdated`, `RepaintAllInspectors` fires and the suffix disappears for the rest of the session. This eliminates the perceived flakiness where the warning sometimes appeared and sometimes didn't, depending on how fast the user clicked into the inspector after a domain reload.

---

## Analyzer Activation And `csc.rsp` Wiring

`Editor/SetupCscRsp.cs` activates the analyzer/source-generator DLLs by copying
them into `Assets/Plugins/Editor/WallstopStudios.DxMessaging/`, tagging the
compiler-host DLLs with Unity's `RoslynAnalyzer` asset label, and excluding
those DLLs from every build platform. Unity feeds `RoslynAnalyzer`-labeled DLLs
to the C# compiler as analyzers without requiring `-a:` entries in `csc.rsp`.

`csc.rsp` is still used for the base-call ignore sidecar. `SetupCscRsp` keeps
that response file in sync by removing stale DxMessaging analyzer entries and
ensuring there is at most one ignore-list entry when the sidecar exists:

```text
-additionalfile:"Assets/Editor/DxMessaging.BaseCallIgnore.txt"
```

The `-additionalfile:` line is only emitted when the ignore-list sidecar
physically exists. Sidecar regeneration also schedules a follow-up `csc.rsp`
sync, so deferred `OnValidate` writes and the Inspector overlay's ignore-list
buttons repair missing response-file wiring during the same editor session.

Manual edits to `csc.rsp` are rarely necessary; the setup helper detects
existing lines and only appends or removes the DxMessaging lines that need
normalization.

---

## Unity 2021 setup notes

DxMessaging ships **two** Roslyn DLLs because Unity 2021's analyzer loader has a hard requirement that Unity 2022+ does not:

- `WallstopStudios.DxMessaging.Analyzer.dll` -- the base-call analyzer (DXMSG006/007/008/009/010). Pinned to **Roslyn 3.8.0**. Unity 2021 rejects analyzer DLLs built against Roslyn 4.x; Microsoft's `Microsoft.Unity.Analyzers` package pins 3.8.0 for the same reason.
- `WallstopStudios.DxMessaging.SourceGenerators.dll` -- the source generators (DXMSG002/003/004/005). Also pinned to **Roslyn 3.8.0** and implemented with classic `ISourceGenerator` APIs so Unity 2021 can instantiate the generators.

Both DLLs are copied into the consuming project's
`Assets/Plugins/Editor/WallstopStudios.DxMessaging/` folder, tagged
`RoslynAnalyzer`, and excluded from every build platform (the Editor included).
Excluding every platform is what keeps Unity from treating them as managed
precompiled assemblies: a `RoslynAnalyzer`-labeled DLL is fed to the C# compiler
as an analyzer regardless of platform inclusion, and an Editor-enabled copy would
instead be registered as a precompiled assembly that collides with the same-named
package copy, aborting Unity 2021.3 with _Multiple precompiled assemblies with
the same name_.

If you are upgrading from a prior version and DXMSG warnings stop appearing on Unity 2021:

1. Delete the package's `Library/ScriptAssemblies` folder so Unity's compiler cache re-evaluates the analyzer DLL hashes -- Unity 2021 caches "rejected analyzer" decisions per-DLL-hash.
1. Reimport the package's `Editor/Analyzers/` folder (right-click -> Reimport).
1. Force a clean rebuild via `Tools -> DxMessaging -> Rescan Base-Call Warnings` after the next compile finishes.

The Inspector overlay also has a Unity 2021 fallback: a `[CustomEditor(typeof(MessageAwareComponent), editorForChildClasses: true, isFallback: true)]` is registered alongside the cross-version `Editor.finishedDefaultHeaderGUI` hook. User-defined `[CustomEditor]`s for the same component type still win precedence over the fallback -- see the [Inspector integration](#inspector-integration) section above.

---

## See also

- [Troubleshooting](troubleshooting.md) -- runtime symptoms and how they map to diagnostics.
- [Inheritance and base calls](../guides/unity-integration.md#important-inheritance-and-base-calls) -- the inheritance contract this analyzer enforces.
- [Unity Integration](../guides/unity-integration.md) -- broader Unity-side guidance for inheritance and lifecycle.
- [Quick Reference](quick-reference.md) -- concise listing of all diagnostic IDs.
