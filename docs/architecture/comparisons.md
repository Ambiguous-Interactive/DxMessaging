# Comparisons: DxMessaging and Alternatives

**TL;DR:** This guide compares DxMessaging with other messaging approaches. Each has legitimate strengths and trade-offs. DxMessaging takes a different approach with its own trade-offs.

## This guide shows

- Characteristics of each approach (with real code examples)
- How DxMessaging takes a different approach (with real code examples)
- Trade-offs for each option (what you give up, what you gain)
- When to use each approach based on your needs

### Table of Contents

- [Performance Benchmarks](#performance-benchmarks)
- [Unity Messaging Frameworks](#unity-messaging-frameworks)
  - [UniRx](#unirx-reactive-extensions-for-unity)
  - [MessagePipe](#messagepipe-high-performance-messaging)
  - [Zenject Signals](#zenject-signals-di-based-messaging)
- [Scriptable Object Architecture (SOA)](#scriptable-object-architecture-soa)
- [Traditional Approaches](#traditional-approaches)
  - [C# Events/Delegates](#standard-c-eventsactions)
  - [UnityEvents](#unityevents-inspector-wiring)
  - [Unity SendMessage](#unity-sendmessage)
  - [Static Event Buses](#global-event-bus-singletons)
- [Trade-offs](#honest-trade-offs-what-you-give-up-what-you-gain)
- [Feature Matrix](#feature-by-feature-comparison-matrix)
- [Decision Guide](#when-each-approach-actually-wins)

## Performance Benchmarks

These sections are auto-updated by the PlayMode comparison benchmarks in the [Comparison Performance PlayMode tests](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Editor/Comparisons/ComparisonPerformanceTests.cs). Run the suite locally to refresh the tables.

### Comparisons (Windows)

| Message Tech                       | Operations / Second | Allocations? |
| ---------------------------------- | ------------------- | ------------ |
| DxMessaging (Untargeted) - No-Copy | 17,074,646          | No           |
| UniRx MessageBroker                | 17,919,648          | No           |
| MessagePipe (Global)               | 94,913,633          | No           |
| Zenject SignalBus                  | 2,495,730           | Yes          |

### Comparisons (macOS)

Run the PlayMode comparison benchmarks on macOS to populate this section.

### Comparisons (Linux)

Run the PlayMode comparison benchmarks on Linux to populate this section.

---

## Unity Messaging Frameworks

This section compares DxMessaging with other popular Unity messaging/eventing libraries. Each offers different approaches to solving communication and decoupling problems in Unity.

### Quick Summary: Which Framework to Choose?

#### TL;DR Decision Tree

```text
Need absolute simplest pub/sub setup (zero boilerplate)?
  -> Use UniRx MessageBroker (publish/receive in 2 lines)

Need complex event stream transformations (debounce, throttle, combine)?
  -> Use UniRx (reactive programming paradigm)

Already using Dependency Injection (Zenject, VContainer, Reflex)?
  -> Use MessagePipe (DI-first, best performance) or Zenject Signals (if on Zenject)
  -> Or DxMessaging (integrates with DI, see Integrations guides for Zenject/VContainer/Reflex)

Need Unity-specific features (GameObject targeting, Inspector debugging, global observers)?
  -> Use DxMessaging (Unity-first design)

Want plug-and-play with zero dependencies?
  -> Use DxMessaging (no setup required)

Maximum raw throughput is a top priority?
  -> Use MessagePipe (highest ops/sec in benchmarks)

Need message validation, interception, or ordered execution?
  -> Use DxMessaging (interceptor pipeline, priority-based ordering)

Simple pub/sub with automatic lifecycle management and debugging?
  -> Use DxMessaging (automatic cleanup, priorities, validation, Inspector)
```

##### One-Line Summary for Each

- **DxMessaging:** Unity-first pub/sub with automatic lifecycle and Inspector debugging
- **UniRx:** Reactive programming with LINQ-style stream operators for complex event transformations
- **MessagePipe:** DI-first, highest throughput for high-frequency messaging in DI architectures
- **Zenject Signals:** Decoupled messaging integrated with Zenject dependency injection

> **💡 Note:** DxMessaging works both standalone (zero dependencies) AND with DI frameworks. See [Integration Guides](../integrations/index.md) for Zenject, VContainer, and Reflex.

---

### UniRx (Reactive Extensions for Unity)

**What It Is:** A reactive programming library that treats events as observable streams. Based on .NET Reactive Extensions (Rx), reimplemented for Unity with IL2CPP compatibility.

**Core Philosophy:** Everything is a stream that can be observed, filtered, combined, and transformed using LINQ-style operators.

#### Key Features

- **Stream-based programming:** Transform events into observable sequences
- **LINQ operators:** `Where`, `Select`, `Merge`, `CombineLatest`, `Buffer`, etc.
- **Async operations:** Convert coroutines to observables with cancellation support
- **Multithreading:** Thread-safe operations with main thread synchronization
- **Time operators:** Frame-based and time-based event handling
- **UI integration:** Observable extensions for Unity UI events

#### Code Example

#### Simple MessageBroker Setup (Pub/Sub)

```csharp
using UniRx;
using UnityEngine;

public struct EnemySpawned
{
    public int EnemyId;
    public Vector3 Position;
}

// Publisher - straightforward, no setup required
public class EnemySpawner : MonoBehaviour
{
    void SpawnEnemy(int id)
    {
        MessageBroker.Default.Publish(new EnemySpawned
        {
            EnemyId = id,
            Position = transform.position
        });
    }
}

// Subscriber - also straightforward
public class AchievementSystem : MonoBehaviour
{
    void Start()
    {
        MessageBroker.Default.Receive<EnemySpawned>()
            .Subscribe(msg => Debug.Log($"Enemy {msg.EnemyId} spawned!"))
            .AddTo(this); // Automatic cleanup on destroy
    }
}
```

#### Advanced Stream Transformations (Reactive Programming)

```csharp
// Double-click detection using reactive operators
Observable.EveryUpdate()
    .Where(_ => Input.GetMouseButtonDown(0))
    .Buffer(Observable.Timer(TimeSpan.FromMilliseconds(250)))
    .Where(xs => xs.Count >= 2)
    .Subscribe(_ => Debug.Log("Double Click!"));

// Combine multiple input streams
var leftClick = Observable.EveryUpdate().Where(_ => Input.GetMouseButtonDown(0));
var rightClick = Observable.EveryUpdate().Where(_ => Input.GetMouseButtonDown(1));
leftClick.Merge(rightClick).Subscribe(_ => Debug.Log("Any click!"));
```

#### What Problems It Solves

- [x] **Complex event streams:** Chain, filter, combine, and transform events with operators
- [x] **Async operations:** Better async/await alternative with cancellation
- [x] **Temporal logic:** Time-based operations (throttle, debounce, sample)
- [x] **UI reactivity:** Bind UI elements to data streams reactively
- [x] **Memory management:** Disposable subscriptions prevent leaks

#### What Problems It Doesn't Solve Well

- **Simple pub/sub:** MessageBroker handles this well, but reactive operators may add complexity for simple use cases
- [ ] **Execution order control:** No built-in priority system for handler ordering
- [ ] **Message validation/interception:** No pre-processing pipeline to validate or transform messages before handlers
- [ ] **Unity Inspector debugging:** No Inspector integration to visualize message flow
- [ ] **GameObject/Component targeting:** Not designed for Unity-specific targeting patterns
- [ ] **Global message observation:** Cannot easily listen to all instances of a message type across different sources

#### Performance Characteristics

- **Allocations:** MessageBroker is zero-allocation for basic operations. Stream operators (Select, Where, Buffer) may allocate. For simple pub/sub, UniRx matches DxMessaging's allocation profile.
- **Overhead:** Higher than simple events due to observable infrastructure
- **Use case:** Best for complex event transformations; overhead justified by functionality

#### Learning Curve

- **Simple MessageBroker (basic pub/sub):** Very easy - just `Publish()` and `Receive()`, similar to events
- **Advanced stream operators:** Steep - requires understanding reactive programming paradigm
- **Mental model shift:** For complex features, must think in streams, not events
- **Documentation:** Extensive examples, but reactive concepts take time to master
- **Estimated learning time:** 15 minutes for MessageBroker; 1-2 weeks for reactive stream mastery

#### Ease of Understanding

- (5/5) (Very easy) - MessageBroker pub/sub is intuitive and straightforward
- (3/5) (Moderate to difficult) - Advanced reactive operators require learning
- Stream operator code is concise but requires understanding of reactive patterns
- Hard to debug complex observable chains without Rx knowledge
- For advanced features: Team buy-in essential; not intuitive for traditional event-driven developers

#### When UniRx Wins

- [x] Simple pub/sub with minimal setup (MessageBroker is straightforward)
- [x] Complex event transformations (e.g., double-click, gesture detection)
- [x] Combining multiple input sources
- [x] Time-based logic (debounce, throttle, sample)
- [x] UI data binding with reactive updates
- [x] Teams familiar with reactive programming

#### When DxMessaging Wins

- [x] Need Unity-specific features (GameObject targeting, lifecycle management)
- [x] Execution order matters (priority-based ordering)
- [x] Message validation/interception needed (interceptor pipeline)
- [x] Inspector debugging required (message history, registration view)
- [x] Direct GameObject/Component targeting
- [x] Global message observation (listen to all instances of a message type)
- [x] Late-stage processing (post-processors after all handlers)
- [x] Automatic lifecycle management (prevents common memory leaks)
- [x] Teams unfamiliar with reactive programming (and don't need reactive features)

#### Direct Comparison

| Aspect                   | UniRx                  | DxMessaging       |
| ------------------------ | ---------------------- | ----------------- |
| **Primary Use Case**     | Stream transformations | Pub/sub messaging |
| **Unity Compatibility**  | Built for Unity        | Built for Unity   |
| **Dependencies**         | Standalone             | Standalone        |
| **Performance**          | 18M ops/sec            | 14M ops/sec       |
| **Allocations**          | Can allocate           | Zero (structs)    |
| **Learning Curve**       | Steep (Rx paradigm)    | Moderate          |
| **Setup Complexity**     | Low                    | Plug-and-play     |
| **DI Integration**       | Optional               | Optional          |
| **Async/Await**          | Observables            | Manual            |
| **Type Safety**          | Strong                 | Strong            |
| **Lifecycle Management** | Manual dispose         | Automatic         |
| **Execution Order**      | Not built-in           | Priority-based    |
| **GameObject Targeting** | Not designed for       | Built-in          |
| **Unity Integration**    | Good (UI)              | Deep              |
| **Inspector Debugging**  | No                     | History + stats   |
| **Interceptors**         | Not built-in           | Full pipeline     |
| **Global Observers**     | Not built-in           | Listen to all     |
| **Post-Processing**      | Not built-in           | Dedicated stage   |
| **Testability**          | Good                   | Excellent         |
| **Decoupling**           | Excellent              | Excellent         |
| **Temporal Operators**   | Extensive (Rx)         | Not built-in      |
| **Complex Stream Logic** | LINQ-style             | Not designed for  |

**Bottom Line:** UniRx is well-suited for complex event stream transformations and reactive programming patterns, with MessageBroker providing straightforward pub/sub setup. DxMessaging focuses on straightforward pub/sub communication with control, validation, debugging, and Unity-specific features. Both are viable options depending on your needs: choose UniRx when you need stream operators, reactive patterns, or simple zero-setup pub/sub; choose DxMessaging when you need Unity-native lifecycle integration, execution control, and debugging tools.

---

### MessagePipe (High-Performance Messaging)

**What It Is:** A high-performance, DI-first messaging library by Cysharp (creators of UniTask). Designed for in-memory and distributed messaging with zero-allocation focus.

**Core Philosophy:** Maximum performance with dependency injection integration. Support all messaging patterns with a unified, generic interface.

#### Key Features

- **Multiple patterns:** Pub/Sub, Request/Response, Mediator patterns
- **Sync and async:** Full async/await support with configurable strategies (sequential/parallel)
- **Keyed messaging:** Type-based or key-based message routing
- **DI-first design:** Deep integration with dependency injection containers
- **Filters:** Pre/post execution customization (similar to interceptors)
- **Zero allocation:** Struct messages with zero GC per publish
- **Roslyn analyzer:** Detects subscription leaks at compile-time
- **Global and scoped:** Support for global message bus or scoped instances

#### Code Example

```csharp
// Using MessagePipe with DI
public class GameManager : MonoBehaviour
{
    private IPublisher<EnemySpawned> _publisher;
    private IDisposable _subscription;

    void Start()
    {
        // Injected via DI container
        _publisher = GlobalMessagePipe.GetPublisher<EnemySpawned>();
        var subscriber = GlobalMessagePipe.GetSubscriber<EnemySpawned>();

        _subscription = subscriber.Subscribe(msg =>
        {
            Debug.Log($"Enemy spawned: {msg.EnemyId}");
        });
    }

    void SpawnEnemy(int id)
    {
        _publisher.Publish(new EnemySpawned { EnemyId = id });
    }

    void OnDestroy() => _subscription?.Dispose();
}

// Async handler with filters
public class AchievementSystem
{
    public AchievementSystem(IAsyncSubscriber<EnemyKilled> subscriber)
    {
        subscriber.Subscribe(async (msg, cancellationToken) =>
        {
            await SaveAchievementAsync(msg.EnemyType);
        });
    }
}
```

#### What Problems It Solves

- [x] **Performance:** Zero allocations with struct-based messages (see [benchmarks](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Tests/Editor/Comparisons) for comparison data)
- [x] **DI integration:** First-class support for dependency injection
- [x] **Async messaging:** Native async/await without blocking
- [x] **Leak detection:** Analyzer catches forgotten subscriptions at compile-time
- [x] **Flexibility:** Keyed, keyless, buffered, request/response patterns
- [x] **Cross-platform:** Works in Unity, .NET, Blazor, etc.

#### What Problems It Doesn't Solve Well

- [ ] **Unity-specific integration:** No built-in Unity MonoBehaviour lifecycle management or GameObject targeting
- [ ] **Inspector debugging:** No visual debugging or message history in Unity Inspector
- [ ] **Execution order control:** No priority system (handlers execute in subscription order)
- [ ] **Setup complexity:** Requires DI container configuration (VContainer/Zenject setup needed)
- [ ] **Global message observation:** No built-in way to listen to all instances of a message across different keys/sources
- [ ] **Standalone use:** Designed for DI-first architecture (less suitable for non-DI projects)

#### Performance Characteristics

- **High throughput:** MessagePipe is optimized for high-frequency messaging scenarios
- **Zero allocation:** Struct-based messages with no GC per publish
- **Benchmark data:** See performance section above for actual numbers
- **Use case:** Optimized for high-frequency messaging (thousands/frame)

#### Learning Curve

- **Moderate:** Requires understanding of dependency injection
- **DI knowledge:** Must be comfortable with service provider pattern
- **Generic interfaces:** Multiple generic types can be confusing initially
- **Estimated learning time:** 2-3 days with DI experience; 1 week without

#### Ease of Understanding

- (4/5) (Moderate)
- Clean, generic interfaces once you understand DI
- Code is straightforward for developers familiar with DI patterns
- Harder for teams without DI experience

#### When MessagePipe Wins

- [x] Performance-critical applications (high message throughput)
- [x] Projects already using DI (VContainer, Zenject, etc.)
- [x] Cross-platform .NET projects (not Unity-only)
- [x] Need async messaging with cancellation
- [x] Large-scale projects with DI architecture
- [x] Teams experienced with DI patterns

#### When DxMessaging Wins

- [x] Unity-first projects (not cross-platform .NET)
- [x] Unity lifecycle management needed (automatic MonoBehaviour cleanup)
- [x] Inspector debugging essential (message history visualization)
- [x] Execution order control needed (priority-based handlers)
- [x] Message validation/interception required (interceptor pipeline)
- [x] Global message observation needed (listen to all message instances)
- [x] Post-processing stage needed (analytics, logging after handlers)
- [x] Teams without DI experience or projects not using DI
- [x] Plug-and-play simplicity preferred over DI configuration

#### Direct Comparison

| Aspect                   | MessagePipe            | DxMessaging         |
| ------------------------ | ---------------------- | ------------------- |
| **Primary Use Case**     | High-perf DI messaging | Pub/sub messaging   |
| **Unity Compatibility**  | Built for Unity        | Built for Unity     |
| **Dependencies**         | DI container required  | Standalone          |
| **Performance**          | 97M ops/sec            | 14M ops/sec         |
| **Allocations**          | Zero (structs)         | Zero (structs)      |
| **Learning Curve**       | Moderate (DI)          | Moderate            |
| **Setup Complexity**     | DI setup required      | Plug-and-play       |
| **DI Integration**       | First-class            | Optional            |
| **Async/Await**          | Native                 | Manual              |
| **Type Safety**          | Strong                 | Strong              |
| **Lifecycle Management** | Manual dispose         | Automatic           |
| **Execution Order**      | Subscription order     | Priority-based      |
| **GameObject Targeting** | Not built-in           | Built-in            |
| **Unity Integration**    | Basic (no lifecycle)   | Deep                |
| **Inspector Debugging**  | No                     | History + stats     |
| **Interceptors**         | Filters                | Full pipeline       |
| **Global Observers**     | Not built-in           | Listen to all       |
| **Post-Processing**      | Via filters            | Dedicated stage     |
| **Testability**          | DI mocking             | Local buses         |
| **Decoupling**           | Excellent              | Excellent           |
| **Leak Detection**       | Roslyn analyzer        | Automatic lifecycle |

**Bottom Line:** MessagePipe is the performance king with DI-first design. DxMessaging is Unity-first with lifecycle awareness and debugging. Use MessagePipe if you have DI infrastructure and need maximum performance. Use DxMessaging if you want Unity-native messaging with automatic lifecycle management.

**Note on Performance:** MessagePipe's ~95M ops/sec vs DxMessaging's ~20M ops/sec shows a significant throughput advantage. This matters primarily for high-frequency messaging scenarios (thousands of messages per frame). For typical gameplay events, both are fast enough that performance is not a distinguishing factor.

> **💡 Want both?** DxMessaging integrates with DI frameworks! See [DI Integration Guides](../integrations/index.md) for Zenject, VContainer, and Reflex. Use DI for service construction, DxMessaging for event communication.

---

### Zenject Signals (DI-Based Messaging)

**What It Is:** The built-in messaging system for Zenject (Extenject), a dependency injection framework for Unity. Signals are an optional extension that provides decoupled communication.

**Core Philosophy:** Loosely coupled messaging integrated with dependency injection. Reduce direct dependencies between classes while maintaining testability.

#### Key Features

- **DI-integrated:** Signals declared and resolved via Zenject container
- **Typed signals:** Strongly-typed signal classes with parameters
- **Synchronous and async:** Sync (RunSync) and async (RunAsync) execution modes
- **Subscription modes:** Require, optional, or optional-with-warning subscribers
- **Installer-based setup:** Declare signals in installers for container binding
- **Multiple subscription methods:** Direct binding, SignalBus subscription, stream-based (with UniRx)
- **Testable:** Easy to mock and test with dependency injection

#### Code Example

```csharp
// 1. Define signal
public class EnemyKilledSignal
{
    public string EnemyType;
    public int Score;
}

// 2. Install and declare in installer
public class GameInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        SignalBusInstaller.Install(Container);
        Container.DeclareSignal<EnemyKilledSignal>();
        Container.BindSignal<EnemyKilledSignal>()
            .ToMethod<AchievementSystem>(x => x.OnEnemyKilled)
            .FromResolve();
    }
}

// 3. Fire signal
public class Enemy : MonoBehaviour
{
    [Inject] private SignalBus _signalBus;

    void Die()
    {
        _signalBus.Fire(new EnemyKilledSignal
        {
            EnemyType = "Orc",
            Score = 100
        });
    }
}

// 4. Subscribe to signal
public class AchievementSystem
{
    [Inject] private SignalBus _signalBus;

    public void Initialize()
    {
        _signalBus.Subscribe<EnemyKilledSignal>(OnEnemyKilled);
    }

    void OnEnemyKilled(EnemyKilledSignal signal)
    {
        Debug.Log($"Killed {signal.EnemyType} for {signal.Score} points!");
    }
}
```

#### What Problems It Solves

- [x] **Decoupling:** Classes communicate without direct references
- [x] **DI integration:** Binds directly into Zenject containers
- [x] **Testability:** Easy to mock SignalBus in tests
- [x] **Type safety:** Strongly-typed signal classes
- [x] **Subscriber validation:** Can enforce required subscribers
- [x] **Async support:** Fire signals synchronously or asynchronously

#### What Problems It Doesn't Solve Well

- [ ] **Zenject dependency:** Must use Zenject/Extenject framework; not standalone
- [ ] **Performance overhead:** Higher than lightweight messaging (DI resolution cost)
- [ ] **Execution order control:** No priority system for handler ordering
- [ ] **Inspector debugging:** No visual message history or flow visualization
- [ ] **Allocations:** Signal parameters can cause allocations depending on usage
- [ ] **Validation pipeline:** No built-in interceptor or pre-processing stage
- [ ] **Global observation:** Cannot easily listen to all signal fires across the system
- [ ] **Post-processing:** No dedicated after-handler stage for analytics/logging

#### Performance Characteristics

- **Overhead:** Higher than lightweight messaging (DI resolution + boxing)
- **Allocations:** Signal parameters can cause allocations (depends on implementation)
- **Benchmark data:** See performance section above for actual numbers
- **Use case:** Performance trade-off for testability and DI benefits

#### Learning Curve

- **Moderate to steep:** Requires understanding Zenject dependency injection
- **Zenject knowledge:** Must learn Zenject before signals
- **Setup overhead:** Installers, bindings, container configuration
- **Estimated learning time:** 1 week for Zenject + signals together

#### Ease of Understanding

- (3/5) (Moderate)
- Clear once you understand Zenject
- Signal concept is straightforward
- Setup (installers, bindings) adds complexity

#### When Zenject Signals Win

- [x] Already using Zenject for dependency injection
- [x] Testability is critical (DI makes mocking easy)
- [x] Need subscriber validation (ensure handlers exist)
- [x] Team experienced with Zenject
- [x] Want DI-managed lifecycle

#### When DxMessaging Wins

- [x] Not using Zenject/Extenject (or prefer standalone solution)
- [x] Performance critical (lower overhead than DI-based signals)
- [x] Execution order control needed (priority-based handlers)
- [x] Inspector debugging required (message history visualization)
- [x] Message validation/interception needed (interceptor pipeline)
- [x] Global message observation needed (listen to all signal fires)
- [x] Post-processing stage needed (analytics after handlers)
- [x] Zero-allocation messaging essential (struct-based)
- [x] GameObject/Component targeting needed (Unity-specific patterns)
- [x] Plug-and-play simplicity preferred over DI setup

#### Direct Comparison

| Aspect                   | Zenject Signals         | DxMessaging       |
| ------------------------ | ----------------------- | ----------------- |
| **Primary Use Case**     | DI-integrated messaging | Pub/sub messaging |
| **Unity Compatibility**  | Built for Unity         | Built for Unity   |
| **Dependencies**         | Zenject required        | Standalone        |
| **Performance**          | 2.5M ops/sec            | 14M ops/sec       |
| **Allocations**          | Can allocate            | Zero (structs)    |
| **Learning Curve**       | Steep (Zenject+Signals) | Moderate          |
| **Setup Complexity**     | Installers required     | Plug-and-play     |
| **DI Integration**       | Required (Zenject)      | Optional          |
| **Async/Await**          | RunAsync support        | Manual            |
| **Type Safety**          | Strong                  | Strong            |
| **Lifecycle Management** | DI-managed              | Automatic         |
| **Execution Order**      | Not built-in            | Priority-based    |
| **GameObject Targeting** | Not built-in            | Built-in          |
| **Unity Integration**    | DI-managed              | Deep              |
| **Inspector Debugging**  | No                      | History + stats   |
| **Interceptors**         | Subscriber validation   | Full pipeline     |
| **Global Observers**     | Not built-in            | Listen to all     |
| **Post-Processing**      | Not built-in            | Dedicated stage   |
| **Testability**          | DI mocking              | Local buses       |
| **Decoupling**           | Excellent               | Excellent         |

**Bottom Line:** Zenject Signals are well-suited if you're already invested in Zenject and value testability through DI. DxMessaging offers standalone messaging without requiring DI setup, and includes Unity-specific features.

> **💡 Using Zenject?** DxMessaging integrates with Zenject! See [DxMessaging + Zenject Integration Guide](../integrations/zenject.md) for step-by-step setup. Get DxMessaging's features (priorities, interceptors, Inspector debugging) with Zenject's DI.

---

## Scriptable Object Architecture (SOA)

**What It Is:** A Unity-specific pattern popularized by Ryan Hipple's [Unite 2017 talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) that uses ScriptableObject assets for runtime communication (GameEvent, FloatVariable, etc.).

**Core Philosophy:** Designer-driven, asset-based communication where systems communicate through serialized SO assets instead of direct references.

**Contested Pattern:** SOA has both proponents and critics. Supporters value its designer-friendly workflow and Inspector-based event wiring. Critics raise concerns about scalability and maintainability at scale. See [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture) for one perspective on the criticisms. Unity recommends ScriptableObjects for **immutable design data**, not mutable runtime state.

### Quick Comparison

| Aspect               | SOA (GameEvent/Variables)                                              | DxMessaging                      |
| -------------------- | ---------------------------------------------------------------------- | -------------------------------- |
| **Designer Control** | High (create events in Inspector)                                      | Low (code-driven)                |
| **Type Safety**      | Mixed (SO refs typed, but UnityEvent wiring loses compile-time safety) | Strong (compile-time validation) |
| **Lifecycle**        | Manual (assets persist)                                                | Automatic (tokens clean up)      |
| **Performance**      | List iteration, UnityAction overhead                                   | Zero-allocation structs          |
| **Testability**      | Requires SO asset cleanup                                              | Isolated buses per test          |

### When to Use Each

#### Choose SOA when

- Designers need to create and wire events in the Inspector without code
- Your team is already deeply invested in SOA with existing assets
- Designer empowerment is more important than code maintainability

##### Choose DxMessaging when

- You need type-safe, code-driven messaging
- Performance and zero-allocation are priorities
- You want automatic lifecycle management
- You need interceptors, priorities, or global observers

###### Use Both when

- ScriptableObjects for **immutable config data** (weapon stats, level configs)
- DxMessaging for **runtime events and communication**
- This is the recommended approach - use each tool correctly

### Full Comparison Guide

For detailed migration patterns, interoperability strategies, and code examples, see:

#### to [SOA Compatibility Guide](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa)

Includes:

- Pattern A: Bridging SOA GameEvents to DxMessaging
- Pattern B: Proper ScriptableObject usage (configs + messaging)
- Migration path from SOA to DxMessaging
- When to keep using ScriptableObjects

##### Resources

- [Unite 2017 Talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) - Original SOA presentation
- [Anti-SOA Critique](https://github.com/cathei/AntiScriptableObjectArchitecture) - Detailed criticisms
- [Unity Official Guide](https://unity.com/how-to/architect-game-code-scriptable-objects) - Unity's perspective

---

## Traditional Approaches

### Standard C# Events/Actions

**What It Is:** C#'s built-in event and delegate system. The default way to handle callbacks and notifications in .NET and Unity.

**Core Philosophy:** Direct, type-safe callbacks between objects. Simple, familiar, and built into the language.

#### Key Features

- **Language-native:** Built into C#, no dependencies
- **Type-safe:** Compile-time checking of event signatures
- **Return values:** Events can return values and use `out` parameters
- **Inline lambdas:** Subscribe with anonymous functions
- **Multicast:** Multiple subscribers per event
- **Fast:** Direct method invocation with minimal overhead

#### Code Example

```csharp
// Define and use C# events
public class GameManager : MonoBehaviour
{
    public event Action<int> OnScoreChanged;

    public void AddScore(int points)
    {
        OnScoreChanged?.Invoke(points);
    }
}

public class UI : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    void OnEnable()
    {
        gameManager.OnScoreChanged += UpdateScore;
    }

    void OnDisable()
    {
        gameManager.OnScoreChanged -= UpdateScore;
    }

    void UpdateScore(int points)
    {
        Debug.Log($"Score: {points}");
    }
}
```

#### What Problems It Solves

- [x] **Simple callbacks:** Straightforward notification pattern
- [x] **Type safety:** Compile-time checking prevents errors
- [x] **Return values:** Can get feedback from event handlers
- [x] **Performance:** Minimal overhead, direct invocation
- [x] **Familiarity:** Every C# developer knows events
- [x] **No dependencies:** Built into the language

#### What Problems It Doesn't Solve Well

- [ ] **Memory leaks:** Forgetting to unsubscribe causes leaks
- [ ] **Tight coupling:** Subscribers need direct references to event sources
- [ ] **Execution order:** Undefined handler invocation order
- [ ] **Lifecycle management:** Manual subscribe/unsubscribe in OnEnable/OnDisable
- [ ] **Debugging:** No visibility into who's subscribed or when events fire
- [ ] **Validation/interception:** No pipeline to modify or validate before handlers
- [ ] **Global observation:** Cannot listen to all events across the system

#### Performance Characteristics

- **Fastest option:** Direct method invocation (~50ns per call)
- **Zero allocation:** No GC pressure for basic events
- **Inline-able:** JIT can optimize simple event calls
- **Use case:** Best raw performance for simple notifications

#### Learning Curve

- **Zero for C# developers:** Standard language feature
- **Immediate productivity:** No new concepts to learn
- **Estimated learning time:** Already know it

#### Ease of Understanding

- (5/5) (Very easy)
- Familiar to all C# developers
- Straightforward mental model
- Easy to debug with breakpoints

#### When C# Events Win

- [x] Small, stable scope (5-10 events max)
- [x] Need return values or `out` parameters
- [x] Writing a library (DxMessaging is Unity-specific)
- [x] Simple, local communication within a class or module
- [x] Team is C# experts, Unity beginners
- [x] Performance is absolutely critical (lowest overhead)
- [x] Quick prototypes or game jams

#### When DxMessaging Wins

- [x] Memory leaks are a problem (automatic lifecycle management)
- [x] Need decoupling (systems don't reference each other)
- [x] Execution order matters (priority-based handlers)
- [x] Debugging "what fired when" (Inspector message history)
- [x] Message validation/interception needed (interceptor pipeline)
- [x] Global observation needed (listen to all message instances)
- [x] Cross-system communication (10+ systems)
- [x] Long-term maintenance (months/years)
- [x] GameObject/Component targeting needed
- [x] Post-processing stage needed (analytics after handlers)

#### Direct Comparison

| Aspect                   | C# Events            | DxMessaging       |
| ------------------------ | -------------------- | ----------------- |
| **Primary Use Case**     | Simple callbacks     | Pub/sub messaging |
| **Unity Compatibility**  | Built into C#        | Built for Unity   |
| **Dependencies**         | None (language)      | Standalone        |
| **Performance**          | ~50ns/call (fastest) | ~60ns/call        |
| **Allocations**          | Zero (basic)         | Zero (structs)    |
| **Learning Curve**       | None                 | Moderate          |
| **Setup Complexity**     | Minimal              | Moderate          |
| **DI Integration**       | Manual               | Optional          |
| **Async/Await**          | Manual               | Manual            |
| **Type Safety**          | Strong               | Strong            |
| **Lifecycle Management** | Manual unsubscribe   | Automatic         |
| **Execution Order**      | Undefined            | Priority-based    |
| **GameObject Targeting** | Not built-in         | Built-in          |
| **Unity Integration**    | None                 | Deep              |
| **Inspector Debugging**  | No                   | History + stats   |
| **Interceptors**         | Not built-in         | Full pipeline     |
| **Global Observers**     | Not built-in         | Listen to all     |
| **Post-Processing**      | Not built-in         | Dedicated stage   |
| **Testability**          | Hard to isolate      | Local buses       |
| **Decoupling**           | Tight coupling       | Excellent         |
| **Return Values**        | Yes                  | Fire-and-forget   |

**Bottom Line:** C# events are the fastest and simplest for basic callbacks. DxMessaging is better for complex, decoupled systems where lifecycle management, debugging, and execution control matter.

---

### UnityEvents (Inspector Wiring)

**What It Is:** Unity's serializable event system that allows wiring callbacks in the Inspector. Designed for designer-friendly event hookups without code.

**Core Philosophy:** Visual, Inspector-based event connections. Enable non-programmers to wire game logic through the editor.

#### Key Features

- **Inspector wiring:** Drag-and-drop connections in Unity Inspector
- **Serializable:** Events saved with scenes and prefabs
- **Designer-friendly:** Non-programmers can wire logic
- **Persistent references:** Connections survive across sessions
- **Dynamic parameters:** Pass values from Inspector to callbacks
- **No code required:** Can wire entire behaviors without scripting

#### Code Example

```csharp
using UnityEngine;
using UnityEngine.Events;

public class Button : MonoBehaviour
{
    public UnityEvent onClick;

    void OnMouseDown()
    {
        onClick?.Invoke();
    }
}

public class UI : MonoBehaviour
{
    public void ShowMenu()
    {
        Debug.Log("Menu shown");
    }

    public void HideMenu()
    {
        Debug.Log("Menu hidden");
    }
}

// In Inspector: Drag UI component to Button's onClick event
// Select ShowMenu from dropdown
// No additional code needed
```

#### What Problems It Solves

- [x] **Visual wiring:** See connections in Inspector
- [x] **No code required:** Designers can hook up events
- [x] **Persistence:** Connections saved with scenes/prefabs
- [x] **Rapid prototyping:** Quick iteration without scripting
- [x] **Prefab workflows:** Events work across prefab instances

#### What Problems It Doesn't Solve Well

- [ ] **Hidden dependencies:** Connections invisible in code, hard to find during refactoring
- [ ] **Brittle at scale:** Renaming methods breaks wiring, no compile-time safety
- [ ] **Execution order:** Undefined call order for multiple subscribers
- [ ] **No validation:** No way to validate or intercept before invocation
- [ ] **Performance:** Slower than C# events due to reflection and boxing
- [ ] **Debugging:** Hard to trace "who called what" at runtime
- [ ] **Merge conflicts:** Inspector changes cause git conflicts
- [ ] **Refactoring challenges:** Renaming/moving methods silently breaks connections

#### Performance Characteristics

- **Slow compared to alternatives:** Reflection overhead, boxing for value types
- **Allocations:** Parameters boxed as objects, causes GC pressure
- **Use case:** Acceptable for UI and low-frequency events, avoid for high-frequency gameplay

#### Learning Curve

- **Very easy:** Point-and-click in Inspector
- **No coding knowledge needed:** Accessible to designers
- **Estimated learning time:** 5-10 minutes

#### Ease of Understanding

- (4/5) (Easy for wiring, hard for debugging)
- Simple to connect in Inspector
- Difficult to understand flow when reading code
- Hard to track down at scale (where is this method called from?)

#### When UnityEvents Win

- [x] Designers need to wire logic without code
- [x] Rapid prototyping with prefabs
- [x] Very simple games (mobile casual, hyper-casual)
- [x] UI interactions with minimal logic
- [x] Small projects (<5 scripts)
- [x] One-off connections that rarely change

#### When DxMessaging Wins

- [x] Code-first development (programmers prefer code visibility)
- [x] Refactoring frequently (compile-time safety)
- [x] Execution order matters (priority-based handlers)
- [x] Need validation/interception (interceptor pipeline)
- [x] Performance-sensitive (zero allocation required)
- [x] Debugging observability (message history)
- [x] Cross-system communication (10+ components)
- [x] Team collaboration (merge-friendly code over Inspector)
- [x] Long-term maintenance (find usages, refactor safely)

#### Direct Comparison

| Aspect                   | UnityEvents          | DxMessaging         |
| ------------------------ | -------------------- | ------------------- |
| **Primary Use Case**     | Inspector wiring     | Pub/sub messaging   |
| **Unity Compatibility**  | Built into Unity     | Built for Unity     |
| **Dependencies**         | None (Unity)         | Standalone          |
| **Performance**          | Slow (serialization) | ~60ns/call          |
| **Allocations**          | Boxing               | Zero (structs)      |
| **Learning Curve**       | Minimal              | Moderate            |
| **Setup Complexity**     | Inspector            | Code-based          |
| **DI Integration**       | No                   | Optional            |
| **Async/Await**          | No                   | Manual              |
| **Type Safety**          | Weak (serialized)    | Strong              |
| **Lifecycle Management** | Unity-managed        | Automatic           |
| **Execution Order**      | Undefined            | Priority-based      |
| **GameObject Targeting** | Manual references    | Built-in            |
| **Unity Integration**    | Inspector-based      | Deep                |
| **Inspector Debugging**  | Connections only     | History + stats     |
| **Interceptors**         | Not built-in         | Full pipeline       |
| **Global Observers**     | Not possible         | Listen to all       |
| **Post-Processing**      | Not built-in         | Dedicated stage     |
| **Testability**          | Scene setup          | Local buses         |
| **Decoupling**           | Hidden refs          | Excellent           |
| **Refactoring Safety**   | Silent breakage      | Compile-time errors |
| **Code Visibility**      | Hidden in Inspector  | Explicit in code    |

**Bottom Line:** UnityEvents are well-suited for simple Inspector-based wiring and designer workflows. DxMessaging is better for code-first development, refactoring safety, and complex messaging needs.

---

### Unity SendMessage

**What It Is:** Unity's legacy reflection-based message system. Calls methods by name on GameObjects and their components.

**Core Philosophy:** String-based, reflection-driven communication. Designed for simplicity and GameObject hierarchy traversal.

#### Key Features

- **String-based:** Call methods by name without references
- **Hierarchy traversal:** SendMessageUpwards, BroadcastMessage for parent/child searching
- **No dependencies:** Built into Unity GameObject
- **Simple API:** One-line method calls
- **GameObject-centric:** Works with Unity's component model
- **Optional receivers:** Methods don't need to exist (SendMessageOptions.DontRequireReceiver)

#### Code Example

```csharp
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void TakeDamage(int amount)
    {
        Debug.Log($"Took {amount} damage");
    }
}

public class Weapon : MonoBehaviour
{
    void Attack(GameObject target)
    {
        // Call TakeDamage on target GameObject
        target.SendMessage("TakeDamage", 10);
    }

    void AttackUpwards()
    {
        // Call on this GameObject and all parents
        SendMessageUpwards("TakeDamage", 5, SendMessageOptions.DontRequireReceiver);
    }

    void AttackChildren()
    {
        // Call on this GameObject and all children
        BroadcastMessage("TakeDamage", 3);
    }
}
```

#### What Problems It Solves

- [x] **No references needed:** Call methods without GetComponent
- [x] **Hierarchy traversal:** Easy parent/child communication
- [x] **Simple API:** One-line method invocation
- [x] **Optional receivers:** Can call non-existent methods safely
- [x] **Built-in:** No setup or dependencies

#### What Problems It Doesn't Solve Well

- [ ] **No type safety:** String-based, typos cause silent failures
- [ ] **Slow performance:** Reflection overhead on every call
- [ ] **Limited parameters:** Only 0 or 1 parameter supported
- [ ] **Boxing allocations:** Value types boxed to object, causes GC
- [ ] **Hard to debug:** No compile-time checking, no IDE "Find Usages"
- [ ] **Refactoring difficulty:** Renaming methods breaks string references
- [ ] **No validation:** No way to validate or intercept messages
- [ ] **Execution order:** Undefined call order for multiple receivers

#### Performance Characteristics

- **Very slow:** Reflection overhead much worse than events or messaging systems
- **Allocations:** Boxing value type parameters causes GC pressure
- **Use case:** Legacy code only; avoid for new development

#### Learning Curve

- **Very easy:** Simple one-line API
- **Immediate productivity:** No setup required
- **Estimated learning time:** 5 minutes

#### Ease of Understanding

- (3/5) (Simple to use, hard to maintain)
- Easy to write initially
- Difficult to track method calls (no Find Usages)
- Refactoring breaks string references silently

#### When Unity SendMessage Wins

- [x] Legacy code that already uses it
- [x] Quick prototypes (throwaway code)
- [x] Simple tutorials or learning examples
- [x] Calling optional methods that may not exist
- [x] GameObject hierarchies with optional components (DontRequireReceiver pattern)

#### When DxMessaging Wins

- [x] Type safety required (compile-time checking)
- [x] Performance matters (zero allocation, no reflection)
- [x] Multiple parameters needed (struct fields)
- [x] Refactoring frequently (find usages, rename safely)
- [x] Debugging observability (message history)
- [x] Execution order control (priority-based handlers)
- [x] Message validation/interception (interceptor pipeline)
- [x] Production code (maintainability over simplicity)
- [x] Modern projects (avoid legacy patterns)

#### Direct Comparison

| Aspect                   | Unity SendMessage       | DxMessaging               |
| ------------------------ | ----------------------- | ------------------------- |
| **Primary Use Case**     | Legacy GameObject calls | Pub/sub messaging         |
| **Unity Compatibility**  | Built into Unity        | Built for Unity           |
| **Dependencies**         | None (Unity)            | Standalone                |
| **Performance**          | Very slow (reflection)  | ~60ns/call                |
| **Allocations**          | Heavy boxing            | Zero (structs)            |
| **Learning Curve**       | Minimal                 | Moderate                  |
| **Setup Complexity**     | None                    | Moderate                  |
| **DI Integration**       | No                      | Optional                  |
| **Async/Await**          | No                      | Manual                    |
| **Type Safety**          | String-based            | Strong                    |
| **Lifecycle Management** | None                    | Automatic                 |
| **Execution Order**      | Undefined               | Priority-based            |
| **GameObject Targeting** | Hierarchy traversal     | Built-in (ID-based)       |
| **Unity Integration**    | Legacy API              | Deep                      |
| **Inspector Debugging**  | No                      | History + stats           |
| **Interceptors**         | Not built-in            | Full pipeline             |
| **Global Observers**     | Not possible            | Listen to all             |
| **Post-Processing**      | Not built-in            | Dedicated stage           |
| **Testability**          | Requires GameObjects    | Local buses               |
| **Decoupling**           | String-based            | Excellent                 |
| **Refactoring Safety**   | Silent breakage         | Compile-time errors       |
| **Parameters**           | 0 or 1 only             | Unlimited (struct fields) |

**Bottom Line:** SendMessage is legacy Unity API. Use only for maintaining old code. DxMessaging provides all the same capabilities with type safety, performance, and modern tooling.

**Migration Path:** DxMessaging provides `ReflexiveMessage` to bridge legacy SendMessage behavior:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Messages;

// Legacy SendMessage equivalent
InstanceId target = gameObject;
var msg = new ReflexiveMessage("OnHit", ReflexiveSendMode.Upwards, 10);
MessageHandler.MessageBus.TargetedBroadcast(ref target, ref msg);

// Prefer typed messages for new code:
// - Multiple parameters via struct fields
// - By-ref handlers avoid boxing
// - Compile-time safety
```

---

### Global Event Bus Singletons

**What It Is:** A static/singleton class that centralizes all events in one global location. Common pattern for decoupling without dependency injection.

**Core Philosophy:** Central event hub accessible from anywhere. Simplify communication through a single global entry point.

#### Key Features

- **Global access:** Static class available everywhere
- **No references needed:** No GetComponent or serialized fields
- **Simple pattern:** Easy to understand and implement
- **Decoupling:** Publishers and subscribers don't know about each other
- **Flexibility:** Can add events without changing existing code

#### Code Example

```csharp
using System;
using UnityEngine;

public static class EventHub
{
    public static event Action<int> OnDamage;
    public static event Action<string> OnEnemyKilled;
    public static event Action OnGameOver;

    public static void RaiseDamage(int amount) => OnDamage?.Invoke(amount);
    public static void RaiseEnemyKilled(string enemyType) => OnEnemyKilled?.Invoke(enemyType);
    public static void RaiseGameOver() => OnGameOver?.Invoke();
}

// Producer
public class Enemy : MonoBehaviour
{
    void Die()
    {
        EventHub.RaiseEnemyKilled("Orc");
    }
}

// Consumer
public class UI : MonoBehaviour
{
    void OnEnable()
    {
        EventHub.OnEnemyKilled += HandleEnemyKilled;
    }

    void OnDisable()
    {
        EventHub.OnEnemyKilled -= HandleEnemyKilled;
    }

    void HandleEnemyKilled(string enemyType)
    {
        Debug.Log($"Enemy killed: {enemyType}");
    }
}
```

#### What Problems It Solves

- [x] **Global decoupling:** No direct references between systems
- [x] **Easy to add events:** Just add to static class
- [x] **Simple pattern:** Straightforward to implement and understand
- [x] **No setup:** No DI container or framework needed

#### What Problems It Doesn't Solve Well

- [ ] **Memory leaks:** Still manual subscribe/unsubscribe (same as C# events)
- [ ] **Global state:** Everything in one bag, hard to organize at scale
- [ ] **Execution order:** Undefined handler invocation order
- [ ] **Testing difficulty:** Global state makes unit testing hard
- [ ] **Naming conflicts:** All events in same namespace, naming gets messy
- [ ] **No validation:** No way to intercept or validate messages
- [ ] **No observability:** Can't see who's subscribed or message history
- [ ] **Ownership unclear:** Who manages what events?
- [ ] **Lifecycle management:** Manual subscribe/unsubscribe required

#### Performance Characteristics

- **Good performance:** Similar to C# events (static overhead is minimal)
- **Zero allocation:** No GC pressure for basic events
- **Use case:** Acceptable for most scenarios

#### Learning Curve

- **Very easy:** Just a static class with events
- **Immediate productivity:** No new concepts
- **Estimated learning time:** 10 minutes

#### Ease of Understanding

- (4/5) (Easy initially, hard at scale)
- Simple pattern to grasp
- Becomes messy with 20+ events
- Hard to track ownership and responsibilities

#### When Static Event Bus Wins

- [x] You've already built one and it works
- [x] Very simple use cases (just need globals)
- [x] Small projects (<10 events)
- [x] No framework dependencies desired
- [x] Quick prototypes

#### When DxMessaging Wins

- [x] More than 10-15 events (organization becomes important)
- [x] Memory leaks are a concern (automatic lifecycle management)
- [x] Execution order matters (priority-based handlers)
- [x] Need message validation/interception (interceptor pipeline)
- [x] Testing is important (local buses for isolation)
- [x] Observability needed (Inspector debugging, message history)
- [x] Multiple subsystems (namespacing and organization)
- [x] GameObject/Component targeting needed
- [x] Global observation needed (listen to all message instances)
- [x] Post-processing needed (analytics after handlers)
- [x] Long-term maintenance (structure prevents chaos)

#### Direct Comparison

| Aspect                   | Static Event Bus   | DxMessaging       |
| ------------------------ | ------------------ | ----------------- |
| **Primary Use Case**     | Global event hub   | Pub/sub messaging |
| **Unity Compatibility**  | Works in Unity     | Built for Unity   |
| **Dependencies**         | None (custom)      | Standalone        |
| **Performance**          | ~50ns/call (fast)  | ~60ns/call        |
| **Allocations**          | Zero (basic)       | Zero (structs)    |
| **Learning Curve**       | Minimal            | Moderate          |
| **Setup Complexity**     | Minimal            | Moderate          |
| **DI Integration**       | Manual             | Optional          |
| **Async/Await**          | Manual             | Manual            |
| **Type Safety**          | Strong             | Strong            |
| **Lifecycle Management** | Manual unsubscribe | Automatic         |
| **Execution Order**      | Undefined          | Priority-based    |
| **GameObject Targeting** | Not built-in       | Built-in          |
| **Unity Integration**    | None               | Deep              |
| **Inspector Debugging**  | No                 | History + stats   |
| **Interceptors**         | Not built-in       | Full pipeline     |
| **Global Observers**     | Not built-in       | Listen to all     |
| **Post-Processing**      | Not built-in       | Dedicated stage   |
| **Testability**          | Hard (global)      | Local buses       |
| **Decoupling**           | Good               | Excellent         |
| **Organization**         | One big class      | Structured        |

**Bottom Line:** Static event buses solve global access but inherit all the problems of C# events (leaks, undefined order, no observability). DxMessaging provides the same global access with lifecycle safety, structure, and debugging tools.

**Migration Path:** DxMessaging can replace static event buses gradually:

```csharp
// Old static event bus
EventHub.RaiseDamage(5);

// DxMessaging equivalent (global bus)
var damage = new TookDamage(5);
damage.Emit();

// Or use local buses for subsystems
var combatBus = new MessageBus();
var combatDamage = new TookDamage(5);
combatDamage.Emit(combatBus);
```

---

## Honest Trade-offs: What You Give Up, What You Gain

DxMessaging involves trade-offs like any architectural choice. This section describes what you gain and what you sacrifice when adopting it.

**Bottom line first:** For game jam prototypes, C# events are faster to write. For projects you'll maintain for months, DxMessaging becomes more valuable as project complexity increases.

### Learning Curve

#### What You Give Up

- [ ] **Immediate productivity** - ~1-2 days to feel comfortable (reading docs, trying examples)
- [ ] **Familiarity** - Your team knows C# events already; DxMessaging is new
- [ ] **"Just works" intuition** - You need to think: "Which message type? What priority?"

Your first message will take 15 minutes. By the 10th message, you'll be faster than with events.

#### What You Gain

- [x] **Long-term velocity** - Adding new features doesn't require touching 5 existing systems
- [x] **Debugging is faster** - Inspector shows "what fired when" instantly
- [x] **Onboarding is easier** - New devs see explicit message contracts, not hidden event chains

**Example:** Junior dev asks "How does damage work?"

- **C# events:** "Uh, Player has an OnDamaged event, and HealthBar subscribes in line 47, and..."
- **DxMessaging:** "Search for `TookDamage` message, see who emits it and who listens."

##### Verdict

- Game jam (1 week project): Learning curve not worth it -> Stick with C# events
- Mid-size game (1+ month): Pays off by week 2
- Large game (6+ months): Highly beneficial

### Boilerplate

#### What You Give Up

- [ ] "One-liners" - C# events can be `public event Action OnClick;` done
- [ ] Quick and dirty - Need to define message struct, attributes, handler registration

#### What You Gain

- [x] Explicit contracts - Messages are discoverable types, not hidden delegates
- [x] Auto-generated code - `[DxAutoConstructor]` reduces boilerplate
- [x] Compile-time safety - Refactors update all usages

#### Example Comparison

```csharp
// C# Event (minimal boilerplate)
public event Action<int> OnDamage;
OnDamage?.Invoke(5);

// DxMessaging (more upfront definition)
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }

var msg = new TookDamage(5);
msg.EmitGameObjectBroadcast(gameObject);
```

**Verdict:** For 1-3 simple events, C# events win on brevity. For 10+ events with complex flows, DxMessaging's structure pays dividends.

### Performance

#### What You Give Up

- [ ] Absolute minimal overhead - Raw C# events/delegates are faster (~10ns per call)
- [ ] Zero abstraction cost - Direct calls can be inlined by the compiler
- [ ] Simplicity in profiler - One extra layer in call stack

#### What You Gain

- [x] Zero-allocation struct messages - No GC pressure from boxing
- [x] Predictable performance - No hidden allocations from lambdas
- [x] Scalable diagnostics - Built-in profiling/logging without custom instrumentation

#### Hard Numbers

- C# event invoke: ~50ns baseline
- DxMessaging handler: ~60ns (~10ns overhead)
- Memory: Zero allocations for struct messages

**Verdict:** For UI, gameplay events, scene management -> DxMessaging overhead is negligible. For ECS architectures processing millions of events per frame -> consider raw delegates or native code, which are better suited for that specific use case.

### Flexibility

#### What You Give Up

- [ ] Return values - DxMessaging is fire-and-forget (no synchronous responses)
- [ ] Out parameters - Can't use `out` or `ref` for bidirectional communication
- [ ] Dynamic subscriptions - Can't easily pass lambdas inline

#### What You Gain

- [x] Interception - Validate/transform messages before handlers
- [x] Post-processing - Analytics/logging without polluting handlers
- [x] Priority control - Explicit execution order
- [x] Context - Always know who sent/received

#### When Limitations Hurt

```csharp
// C# events can return values (DxMessaging can't)
public delegate bool DamageValidator(int amount);
public event DamageValidator OnValidateDamage;

if (OnValidateDamage?.Invoke(damage) == true) {
    // Allowed
}

// DxMessaging workaround: Use interceptors or separate query pattern
```

**Verdict:** If you need synchronous request/response, C# delegates/events or direct method calls are better. DxMessaging is designed for notifications and commands.

### Debuggability

#### What You Give Up

- [ ] Simplicity - Stack traces show message bus internals
- [ ] Step-through - Can't F11 directly from emit to handler (need breakpoints)

#### What You Gain

- [x] Message history - See last N messages in Inspector
- [x] Registration view - Know exactly who's listening
- [x] Global observability - Track all messages without instrumenting code
- [x] Filtering - Intercept messages for debugging without changing code

#### Example: Finding Who Fired a Message

```csharp
// C# events: Set breakpoint on every possible Invoke(), or add logging everywhere
OnDamage?.Invoke(5); // Where did this come from??

// DxMessaging: Check Inspector message history or add global logger
_ = debugToken.RegisterBroadcastWithoutSource<TookDamage>(
    (src, msg) => Debug.Log($"Damage from {src}: {msg.amount}")
);
```

**Verdict:** Initial debugging is slightly harder (extra layer), but systemic debugging is easier (observability tools).

### Coupling and Architecture

#### What You Give Up

- [ ] Quick hacks - Can't just `GetComponent<T>().DoThing()` anymore
- [ ] Direct inspector wiring - Can't drag-and-drop references to emit messages

#### What You Gain

- [x] True decoupling - Systems don't know about each other
- [x] Testability - Easy to isolate with local buses
- [x] Refactorability - Move/rename components without breaking wiring

#### Impact on Architecture

Before (tight coupling):

```text
UI -> References 15 systems
System A -> References System B, C, D
Every change ripples through dependencies
```

After (loose coupling):

```text
All systems -> Emit messages
All systems -> Listen to messages
Add/remove systems without affecting others
```

**Verdict:** If your project is <5k lines, tight coupling is manageable. For larger projects, DxMessaging's decoupling significantly improves maintainability.

### Testing

#### What You Give Up

- [ ] Simplicity - Can't just mock an event subscription

#### What You Gain

- [x] Isolation - Local buses per test, zero global state
- [x] Observability - Count messages, inspect payloads easily
- [x] Determinism - Priority-based ordering eliminates flakiness

##### Example

```csharp
// Test with isolated bus
[Test]
public void TestAchievementSystem() {
    var testBus = new MessageBus();
    var token = MessageRegistrationToken.Create(achievementHandler, testBus);

    var msg = new EnemyKilled("Boss", 10);
    msg.EmitGameObjectBroadcast(enemy, testBus);

    Assert.IsTrue(achievementSystem.Unlocked("BossSlayer"));
}
```

**Verdict:** DxMessaging makes integration testing easier, unit testing slightly more verbose.

## Feature-by-Feature Comparison Matrix

### Unity Messaging Frameworks Comparison

| Aspect                   | DxMessaging               | UniRx                  | MessagePipe            | Zenject Signals         |
| ------------------------ | ------------------------- | ---------------------- | ---------------------- | ----------------------- |
| **Primary Use Case**     | Pub/sub messaging         | Stream transformations | High-perf DI messaging | DI-integrated messaging |
| **Unity Compatibility**  | Built for Unity           | Built for Unity        | Built for Unity        | Built for Unity         |
| **Performance**          | Good (14M)                | Good (18M)             | Best (97M)             | Moderate (2.5M)         |
| **Zero Allocations**     | (structs)                 | Can allocate           | (structs)              | Can allocate            |
| **Unity Integration**    | Deep (lifecycle)          | Good (UI/async)        | Basic (no lifecycle)   | Good (DI-managed)       |
| **Inspector Debugging**  | (history + stats)         | No                     | No                     | No                      |
| **Execution Order**      | Priority-based            | Not built-in           | Subscription order     | Not built-in            |
| **Lifecycle Management** | Automatic (MonoBehaviour) | Manual dispose         | Manual dispose         | DI-managed              |
| **Learning Curve**       | Moderate                  | Steep (Rx paradigm)    | Moderate (DI)          | Steep (DI+Signals)      |
| **Setup Complexity**     | Plug-and-play             | Low                    | DI setup required      | Installers required     |
| **DI Integration**       | Optional                  | Optional               | First-class            | Required (Zenject)      |
| **Async/Await**          | Manual                    | Native (observables)   | Native                 | Yes                     |
| **Message Validation**   | Interceptor pipeline      | Not built-in           | Filters (middleware)   | Not built-in            |
| **GameObject Targeting** | Built-in                  | Not designed for       | Not built-in           | Not built-in            |
| **Global Observers**     | Listen to all sources     | Not built-in           | Not built-in           | Not built-in            |
| **Post-Processing**      | Dedicated stage           | Not built-in           | Via filters            | Not built-in            |
| **Stream Operators**     | Not built-in              | Extensive (LINQ)       | Not built-in           | With UniRx              |
| **Testability**          | Local buses               | Good                   | DI mocking             | DI mocking              |
| **Decoupling**           | Excellent                 | Excellent              | Excellent              | Excellent               |
| **Type Safety**          | Strong                    | Strong                 | Strong                 | Strong                  |
| **Dependencies**         | None                      | None                   | MessagePipe package    | Zenject required        |

### Traditional Approaches Comparison

| Aspect               | C# Events     | UnityEvents   | SOA (GameEvent) | Static Bus | DxMessaging    |
| -------------------- | ------------- | ------------- | --------------- | ---------- | -------------- |
| **Setup Complexity** | Minimal       | Simple        | Asset creation  | Moderate   | Moderate       |
| **Boilerplate**      | Low           | Low           | High            | Medium     | Medium         |
| **Performance**      | Fastest       | Slow (boxing) | Moderate        | Fast       | Fast           |
| **Decoupling**       | Tight         | Hidden        | Good            | Good       | Excellent      |
| **Designer Control** | None          | High          | High            | None       | None           |
| **Lifecycle Safety** | Manual        | Unity-managed | Manual persist  | Manual     | Automatic      |
| **Observability**    | None          | None          | Inspector only  | None       | Built-in       |
| **Execution Order**  | Undefined     | Undefined     | Undefined       | Undefined  | Priority-based |
| **Type Safety**      | Strong        | Weak          | Mixed           | Varies     | Strong         |
| **Testability**      | Hard          | Hard          | Very Hard       | Very Hard  | Easy           |
| **Learning Curve**   | Minimal       | Minimal       | Moderate        | Low        | Moderate       |
| **Memory Safety**    | Leak-prone    | Unity-managed | Asset persist   | Leak-prone | Leak-free      |
| **Debugging**        | Hard at scale | Hard at scale | Inspector-only  | Very Hard  | Excellent      |

### Overall Verdict by Use Case

- **Small prototype/jam:** C# Events or UnityEvents win (simplicity > all)
- **Mid-size game (5-20k lines):** DxMessaging starts paying off (decoupling, debugging)
- **Large game (20k+ lines):** DxMessaging essential for maintainability
- **Designer-driven workflow:** SOA has value (Inspector wiring) but consider maintenance costs
- **Legacy SOA project:** Use Pattern B (keep SOs for configs, migrate events to DxMessaging)
- **Performance-critical (millions of messages/frame):** MessagePipe offers the highest throughput
- **Performance-critical (Unity-specific):** DxMessaging (strong performance + Unity integration)
- **UI-heavy:** DxMessaging provides decoupled updates and global observers for UI state
- **Complex event transformations:** UniRx provides reactive stream operators
- **DI-first architecture:** MessagePipe or Zenject Signals win (DI integration)
- **Analytics/diagnostics heavy:** DxMessaging provides built-in support (global observers, post-processors, Inspector)
- **Need execution control:** DxMessaging offers priorities, interceptors, and ordered stages

## When Each Approach ACTUALLY Wins

### DxMessaging Wins When

- [x] Unity-first projects (MonoBehaviour lifecycle integration)
- [x] 10+ systems that communicate (pub/sub decoupling)
- [x] Observability essential (Inspector debugging, message history)
- [x] Memory leaks are a concern (automatic lifecycle management)
- [x] Cross-team development (clear message contracts)
- [x] Long-term maintenance (years, not weeks)
- [x] GameObject/Component targeting needed (Unity-specific patterns)
- [x] Execution order control essential (priority-based handlers)
- [x] Message validation/transformation needed (interceptor pipeline)
- [x] Global observation needed (listen to all message instances)
- [x] Post-processing needed (analytics, logging after handlers)
- [x] Late update semantics needed (timing-specific processing)
- [x] Teams without DI experience (no framework dependencies)
- [x] Want plug-and-play solution (zero dependencies, immediate use)

### UniRx Wins When

- [x] Simple pub/sub with minimal setup (MessageBroker is straightforward)
- [x] Complex event stream transformations needed
- [x] Time-based operations (throttle, debounce, buffer)
- [x] Combining multiple input sources
- [x] Reactive UI data binding
- [x] Team familiar with reactive programming
- [x] Need LINQ-style query operators on events
- [x] Async operations with cancellation and composition

### MessagePipe Wins When

- [x] Performance is THE priority (highest throughput)
- [x] Already using DI (VContainer, Zenject, etc.)
- [x] Cross-platform .NET projects (not Unity-only)
- [x] Need native async/await support
- [x] Large-scale projects with DI architecture
- [x] Want compile-time leak detection (Roslyn analyzer)
- [x] High message frequency (thousands/frame)

### Zenject Signals Win When

- [x] Already using Zenject for dependency injection
- [x] Testability through DI is critical
- [x] Need subscriber validation (ensure handlers exist)
- [x] Team experienced with Zenject
- [x] Want DI-managed lifecycle
- [x] Integration with existing Zenject architecture

### C# Events Win When

- [x] You need return values or out parameters
- [x] Writing a library (DxMessaging is Unity-specific)
- [x] Small, stable scope (5-10 events max)
- [x] Team is C# experts, Unity beginners

### UnityEvents Win When

- [x] Designers need to wire logic without code
- [x] Rapid prototyping with prefabs
- [x] Very simple games (mobile casual, hyper-casual)

### SOA (GameEvent/Variables) Wins When

- [x] Designers must create and wire events without touching code
- [x] Team is already heavily invested in SOA with many existing assets
- [x] Designer empowerment is the absolute top priority
- **BUT:** Consider migration costs and maintainability issues (see [Anti-SOA critique](https://github.com/cathei/AntiScriptableObjectArchitecture))
- **Alternative:** Use ScriptableObjects for configs only + DxMessaging for events (Pattern B in [SOA Guide](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa))

### Static Event Bus Wins When

- [x] You've already built one and it works
- [x] Very simple use cases (just need globals)

## Cost-Benefit Summary

### Costs

1. Learning curve (~1-2 days to feel comfortable)
1. More upfront code (message definitions)
1. Slightly slower than raw C# events (~10ns/call)
1. Can't return values (fire-and-forget only)

### Benefits

1. Automatic lifecycle prevents common memory leaks
1. Full decoupling (systems don't reference each other)
1. Observability (Inspector diagnostics, message history)
1. Predictable ordering (priority-based execution)
1. Interception/validation (before handlers run)
1. Testability (isolated buses)

**Break-even point:** Usually around 10-20 hours into a project, when event management becomes painful.

## Making the Decision (Be Honest With Yourself)

### Answer these questions honestly

### 1. Project Lifespan?

- **<1 week (game jam):** Skip DxMessaging -> Use C# events or direct calls
- **1-4 weeks (prototype):** Maybe -> If you plan to continue, use DxMessaging
- **1+ months (real project):** Yes -> DxMessaging will save you time
- **6+ months (production):** Absolutely -> You'll thank yourself later

### 2. Team Size?

- **Solo dev:** Optional -> Depends on project complexity
- **2-3 devs:** Valuable -> Reduces communication overhead
- **4+ devs:** Highly recommended -> Clear contracts between systems
- **Remote/distributed team:** Essential -> Explicit message contracts prevent miscommunication

### 3. Codebase Size?

- **<1k lines:** Skip it -> Direct method calls are fine
- **1k-5k lines:** Consider it -> If you're growing fast
- **5k-20k lines:** Recommended -> Coupling becomes painful
- **20k+ lines:** Absolutely -> Refactoring becomes significantly more challenging without it

### 4. How Many Systems Need to Communicate?

- **1-2 systems:** Skip -> Just call methods directly
- **3-5 systems:** Consider -> If they don't share references
- **6-10 systems:** Recommended -> Coupling becomes unmanageable
- **10+ systems:** Essential -> Managing many SerializeField references becomes difficult

### 5. Have You Had Memory Leaks From Forgotten Unsubscribes?

- **Never:** Optional -> You may not need automatic lifecycle management
- **Once or twice:** Consider it -> Prevention is cheaper than debugging
- **Multiple times:** Recommended -> Automatic cleanup would help
- **Currently debugging one:** Strongly recommended -> Consider adopting DxMessaging

### 6. How Often Do You Debug "What Fired When?"

- **Never:** Likely not needed -> Small projects may not require message debugging tools
- **Rarely:** Optional, but would help
- **Monthly:** Recommended -> Inspector diagnostics will save hours
- **Weekly:** Strongly recommended -> Debugging tools would provide significant time savings

### Quick Decision Matrix

```text
Game Jam         -> C# Events (speed over safety)
Prototype        -> DxMessaging IF continuing, else C# Events
Production       -> DxMessaging (unless <1k lines)
Legacy codebase  -> Migrate gradually (see Migration Guide)
```

### The Real Question

#### "Will this project still exist in 3 months?"

- **No:** C# events are fine
- **Yes:** Use DxMessaging

##### "Will anyone else work on this code?"

- **No:** C# events might be okay
- **Yes:** Use DxMessaging (future you counts as "someone else")

### Rule of Thumb

If you're reading this and thinking:

- **"I've experienced these pain points"** to DxMessaging will help
- **"This seems like overkill"** to You probably don't need it yet
- **"I need this yesterday"** to DxMessaging may be a good fit

See also

- [Message Types](../concepts/message-types.md)
- [Diagnostics (Editor inspector)](../guides/diagnostics.md)
- [Migration Guide](../guides/migration-guide.md) - How to adopt gradually
