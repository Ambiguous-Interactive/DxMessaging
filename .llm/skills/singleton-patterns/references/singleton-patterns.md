# RuntimeSingleton and ScriptableObject Singleton Patterns

> **One-line summary**: Implement thread-safe, scene-persistent singletons for MonoBehaviours and lazy-loaded singletons for ScriptableObjects.

## Overview

Unity games often need global managers (AudioManager, GameManager, etc.). This pattern provides:

1. **RuntimeSingleton<T>**: MonoBehaviour singleton that survives scene loads
1. **ScriptableObjectSingleton<T>**: Lazy-loaded SO from Resources folder
1. **Thread-safe initialization**
1. **Graceful handling of duplicates**

## Problem Statement

```csharp
// BAD: Race conditions, no duplicate handling
public class BadSingleton : MonoBehaviour
{
    public static BadSingleton Instance;

    void Awake()
    {
        Instance = this; // What if there are two?
    }
}

// BAD: Destroyed on scene load
public class BadManager : MonoBehaviour
{
    public static BadManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
        // Destroyed when scene changes!
    }
}
```

## Solution

## Performance Notes

- **First Access**: May cause frame spike due to instantiation
- **Thread Safety**: Lock overhead is minimal (~10ns)
- **Memory**: One instance, no leaks (properly nulled on destroy)

## Best Practices

### Do

- Use `HasInstance` to check existence without creating
- Override `OnSingletonAwake()` instead of `Awake()`
- Use `[AutoLoadSingleton]` for critical managers
- Place ScriptableObject assets in Resources folder

### Don't

- Don't access `Instance` in `OnDestroy()` of other objects (may be null)
- Don't create singletons from non-main threads
- Don't rely on singleton order of initialization
- Don't overuse singletons (creates hidden dependencies)

### Testability Pattern

```csharp
// Interface for mocking
public interface IAudioManager
{
    void PlayMusic(AudioClip clip);
}

// Singleton implements interface
public class AudioManager : RuntimeSingleton<AudioManager>, IAudioManager
{
    // ...
}

// Injected dependency for testing
public class MusicPlayer : MonoBehaviour
{
    [SerializeField] private Component audioManagerOverride; // For testing

    private IAudioManager AudioManager =>
        (IAudioManager)audioManagerOverride ?? AudioManager.Instance;
}
```

## Related Patterns

- [Serializable Dictionary](../../serializable-dictionary/references/serializable-dictionary.md) - For SO configuration
- [Runtime Singleton Pattern](./singleton-runtime.md) - MonoBehaviour singleton base
- [ScriptableObject Singleton Pattern](./singleton-scriptableobject.md) - Asset-backed singleton
- [Auto-Load Singleton Attribute](./singleton-autoload.md) - Bootstrap singleton at load
- [Singleton Usage Examples](./singleton-usage-examples.md) - Common application patterns
