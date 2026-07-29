---
name: singleton-patterns
description: "Implement Unity global managers with RuntimeSingleton<T> for MonoBehaviours that survive scene loads and ScriptableObjectSingleton<T> for Resources-backed config assets, including duplicate handling and quit-time safety. Use when writing a GameManager or AudioManager, when a static Instance field is null after a scene change or on quit, when two copies of a manager exist, or when bootstrapping a manager before the first scene."
metadata:
  category: "performance"
  tags: "unity, singleton, patterns, scriptable-object, architecture"
---

# RuntimeSingleton and ScriptableObject Singleton Patterns

Two base types cover Unity's global-manager needs: `RuntimeSingleton<T>` for
MonoBehaviour managers that persist across scenes, and
`ScriptableObjectSingleton<T>` for configuration assets loaded lazily from
`Resources`.

## When to use

- Writing a `GameManager`, `AudioManager`, or similar single global manager.
- A `public static Instance` field is null after a scene load or during quit.
- Two instances of a manager exist after loading a scene that also contains one.
- Global settings need to be an asset a designer edits, not a scene object.
- A manager must exist before the first scene loads.

## Rules

### RuntimeSingleton

- Derive as `class Foo : RuntimeSingleton<Foo>` (`where T : RuntimeSingleton<T>`).
- Override `OnSingletonAwake()`, never `Awake()`. The base `Awake()` owns
  instance assignment, `DontDestroyOnLoad`, and duplicate destruction.
- `Instance` lazily resolves in this order: existing static field, then
  `FindObjectOfType<T>()`, then a new `GameObject($"[{typeof(T).Name}]")` with
  the component added. Every branch runs inside `lock (lockObject)`.
- Use `HasInstance` to test for existence. Reading `Instance` CREATES one.
- Do not touch `Instance` from another object's `OnDestroy()`; teardown order is
  not defined and the instance may already be gone.
- The base sets an `applicationIsQuitting` latch in `OnApplicationQuit`, after
  which `Instance` logs a warning and returns `null` instead of resurrecting a
  destroyed manager. Callers during shutdown must handle null.
- `OnDestroy` nulls the static field only when `instance == this`, so a
  destroyed duplicate cannot clear the live instance.
- Override `protected virtual bool Preserve => false` for a scene-scoped
  singleton. When `Preserve` is true the base calls `transform.SetParent(null)`
  before `DontDestroyOnLoad(gameObject)`, because Unity only preserves root
  objects.
- Duplicates are destroyed, not merged: the second instance logs a warning and
  destroys its own GameObject.
- Create singletons from the main thread only. The lock protects the field, not
  Unity API access.
- Do not depend on initialization ORDER between singletons; the lazy `Instance`
  getter gives no ordering guarantee.

### ScriptableObjectSingleton

- Derive as `class Settings : ScriptableObjectSingleton<Settings>`.
- Place the asset under a `Resources` folder. `LoadInstance` calls
  `Resources.Load<T>(path)`, where `path` comes from
  `[ScriptableSingletonPath("Config/GameSettings")]` on the type, defaulting to
  `Config/{typeof(T).Name}`.
- A missing asset logs an error and falls back to `CreateInstance<T>()` with
  default field values, so a silent config reset is a load failure, not a
  designer choice - check the log.
- `OnEnable` assigns the static field when it is null, so an asset Unity loads
  on its own becomes the instance without a `Resources.Load` round trip.
  Overrides must call `base.OnEnable()`.
- `HasInstance` avoids triggering the load.
- Expose fields as read-only properties over `[SerializeField]` backing fields.

### Bootstrapping and testability

- Annotate a manager that must exist early with `[AutoLoadSingleton]`.
  `SingletonAutoLoader` runs at
  `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`,
  scans loaded assemblies for the attribute, and reads each type's static
  `Instance` property to force creation. That scan is reflection over every
  assembly, so apply the attribute only to managers that genuinely need it.
- Have the singleton implement an interface, for example
  `AudioManager : RuntimeSingleton<AudioManager>, IAudioManager`, and let
  consumers accept an injectable override field that falls back to `Instance`.
  That is the seam tests use.
- Do not reach for a singleton by default. Each one is a hidden dependency; use
  them for genuinely global services only.

## References

| Document                                                                    | Purpose                                                                                                                  |
| --------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| [singleton-patterns.md](./references/singleton-patterns.md)                 | The race-condition and scene-destruction failures being solved, plus the do/do-not list and the interface seam for tests |
| [singleton-runtime.md](./references/singleton-runtime.md)                   | `RuntimeSingleton<T>` implementation: lazy `Instance`, `HasInstance`, duplicate destruction, `Preserve`, quit latch      |
| [singleton-scriptableobject.md](./references/singleton-scriptableobject.md) | `ScriptableObjectSingleton<T>` and `ScriptableSingletonPathAttribute` resolution and fallback                            |
| [singleton-autoload.md](./references/singleton-autoload.md)                 | `AutoLoadSingletonAttribute` and the `BeforeSceneLoad` reflection bootstrapper                                           |
| [singleton-usage-examples.md](./references/singleton-usage-examples.md)     | Manager, settings asset, auto-load, and scene-scoped declarations                                                        |
