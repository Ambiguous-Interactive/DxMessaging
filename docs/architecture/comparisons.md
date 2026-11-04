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

The live cross-library comparison matrix is now CI-generated into [Performance Benchmarks](./performance.md) on every push (Standalone IL2CPP Release, the build shape shipped games run); see the [comparison suite source](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Tests/Runtime/Comparisons) for the scenario bridges. The [live tables](./performance.md) are the source of truth; the snapshot below is retained only as dated historical context.

### Historical snapshot (early PlayMode Mono run, Windows)

These figures are from an early PlayMode Mono benchmark run on Windows, kept to
show the relative ordering at the time. They are **superseded** by the live
[Standalone IL2CPP tables](./performance.md) -- do not cite them as current
numbers.

| Message Tech                       | Operations / Second | Allocations? |
| ---------------------------------- | ------------------- | ------------ |
| DxMessaging (Untargeted) - No-Copy | 17,074,646          | No           |
| UniRx MessageBroker                | 17,919,648          | No           |
| MessagePipe (Global)               | 94,913,633          | No           |
| Zenject SignalBus                  | 2,495,730           | Yes          |

---

## Unity Messaging Frameworks

This section compares DxMessaging with other popular Unity messaging/eventing libraries. Each offers different approaches to solving communication and decoupling problems in Unity.

### Quick Summary: Which Framework to Choose?

#### TL;DR Decision Tree

```text
Need a publish/receive API with reactive operators?
  -> Evaluate UniRx MessageBroker and its subscription disposal

Need complex event stream transformations (debounce, throttle, combine)?
  -> Use UniRx (reactive programming paradigm)

Already using Dependency Injection (Zenject, VContainer, Reflex)?
  -> Use MessagePipe (high-throughput DI messaging) or Zenject Signals (if on Zenject)
  -> Or DxMessaging (integrates with DI, see Integrations guides for Zenject/VContainer/Reflex)

Need Unity-specific features (GameObject targeting, visual diagnostics, global observers)?
  -> Use DxMessaging (Unity-first design)

Want typed Unity messaging without a DI framework?
  -> Evaluate DxMessaging; choose the message route, bus and registration owner

Maximum raw throughput is a top priority?
  -> Compare the exact topology in the current CI results before choosing

Need message validation, interception, or ordered execution?
  -> Use DxMessaging (interceptor pipeline, priority-based ordering)

Need registration ownership helpers and bus inspection?
  -> Evaluate DxMessaging tokens, leases, component hooks and diagnostics
```

##### One-Line Summary for Each

- **DxMessaging:** Unity-first pub/sub with token, lease and component ownership helpers and visual diagnostics
- **UniRx:** Reactive programming with LINQ-style stream operators for complex event transformations
- **MessagePipe:** Typed messaging with DI integration and synchronous/asynchronous APIs
- **Zenject Signals:** Decoupled messaging integrated with Zenject dependency injection

> **Note:** The Unity package can use its default bus or an explicit bus without a DI framework. See the [integration guides](../integrations/index.md) for container-owned services and the [.NET source-build contract](../reference/compatibility.md#plain-net-runtime) for non-Unity hosts.

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
- **Try first:** Build a subscription, apply the needed stream operators, and verify disposal.

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
- [x] Visual diagnostics required (Message Monitor and Flow Graph)
- [x] Direct GameObject/Component targeting
- [x] Global message observation (listen to all instances of a message type)
- [x] Late-stage processing (post-processors after all handlers)
- [x] Components or leases can pair registrations with their actual owners
- [x] Teams unfamiliar with reactive programming (and don't need reactive features)

#### Direct Comparison

| Aspect                   | UniRx                                                                | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Stream transformations                                               | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built for Unity                                                      | Built for Unity                                                      |
| **Dependencies**         | Standalone                                                           | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Can allocate                                                         | Steady-state dispatch contract                                       |
| **Learning Curve**       | Steep (Rx paradigm)                                                  | Moderate                                                             |
| **Setup Complexity**     | Low                                                                  | Plug-and-play                                                        |
| **DI Integration**       | Optional                                                             | Optional                                                             |
| **Async/Await**          | Observables                                                          | Manual                                                               |
| **Type Safety**          | Strong                                                               | Strong                                                               |
| **Lifecycle Management** | Manual dispose                                                       | Tokens, leases or component hooks                                    |
| **Execution Order**      | Not built-in                                                         | Priority-based                                                       |
| **GameObject Targeting** | Not designed for                                                     | Built-in                                                             |
| **Unity Integration**    | Good (UI)                                                            | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Not built-in                                                         | Full pipeline                                                        |
| **Global Observers**     | Not built-in                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | Good                                                                 | Excellent                                                            |
| **Decoupling**           | Excellent                                                            | Excellent                                                            |
| **Temporal Operators**   | Extensive (Rx)                                                       | Not built-in                                                         |
| **Complex Stream Logic** | LINQ-style                                                           | Not designed for                                                     |

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
- **Allocation scope:** Measure the selected publish path, payload and subscriber work.
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

- [x] **Measurements:** Compare the exact supported [benchmark scenario](./performance.md#latest-ci-performance-results) and measure subscriber work separately.
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
- [ ] **Provider setup:** The Unity package uses a supported container. Its built-in
      `BuiltinContainerBuilder` avoids a separate DI library; check its supported lifetime/features and
      register the concrete message types required by the Unity backend. See the
      [pinned MessagePipe Unity setup](https://github.com/Cysharp/MessagePipe/tree/1.8.2#unity).

#### Performance Characteristics

- **High throughput:** MessagePipe is optimized for high-frequency messaging scenarios
- **Allocation scope:** Check the exact scenario and backend; callback work remains separate.
- **Benchmark data:** See performance section above for actual numbers
- **Workload:** Measure the actual message frequency, fan-out and callback work.

#### Learning Curve

- **Moderate:** Requires understanding of dependency injection
- **DI knowledge:** Must be comfortable with service provider pattern
- **Generic interfaces:** Multiple generic types can be confusing initially
- **Try first:** Configure a provider, publish a typed payload, and verify subscriber disposal.

#### Ease of Understanding

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
- [x] Visual diagnostics essential (emission history and route topology)
- [x] Execution order control needed (priority-based handlers)
- [x] Message validation/interception required (interceptor pipeline)
- [x] Global message observation needed (listen to all message instances)
- [x] Post-processing stage needed (analytics, logging after handlers)
- [x] Teams without DI experience or projects not using DI
- [x] Plug-and-play simplicity preferred over DI configuration

#### Direct Comparison

| Aspect                   | MessagePipe                                                          | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | High-perf DI messaging                                               | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built for Unity                                                      | Built for Unity                                                      |
| **Dependencies**         | Built-in or external DI provider                                     | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Steady-state dispatch contract                                       | Zero (structs)                                                       |
| **Learning Curve**       | Moderate (DI)                                                        | Moderate                                                             |
| **Setup Complexity**     | Provider and concrete Unity payload registration                     | Plug-and-play                                                        |
| **DI Integration**       | First-class                                                          | Optional                                                             |
| **Async/Await**          | Native                                                               | Manual                                                               |
| **Type Safety**          | Strong                                                               | Strong                                                               |
| **Lifecycle Management** | Manual dispose                                                       | Tokens, leases or component hooks                                    |
| **Execution Order**      | Subscription order                                                   | Priority-based                                                       |
| **GameObject Targeting** | Not built-in                                                         | Built-in                                                             |
| **Unity Integration**    | Basic (no lifecycle)                                                 | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Filters                                                              | Full pipeline                                                        |
| **Global Observers**     | Not built-in                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Via filters                                                          | Dedicated stage                                                      |
| **Testability**          | DI mocking                                                           | Local buses                                                          |
| **Decoupling**           | Excellent                                                            | Excellent                                                            |
| **Leak Detection**       | Roslyn analyzer                                                      | Token diagnostics and ownership helpers                              |

**Bottom Line:** MessagePipe is DI-first and leads DxMessaging in several
[current comparison scenarios](./performance.md#latest-ci-performance-results).
DxMessaging is Unity-first with lifecycle awareness and debugging. Choose from
the topology your game uses, not a blanket throughput ranking.

**Note on Performance:** Read the
[current Standalone IL2CPP comparison](./performance.md#latest-ci-performance-results)
instead of a copied rate. MessagePipe leads several current raw-dispatch
scenarios; DxMessaging leads keyed dispatch in some runs. Measure the topology
your game uses before choosing on throughput alone.

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
- **Try first:** Run a complete subscription and verify its callback and cleanup.

#### Ease of Understanding

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
- [x] Visual diagnostics required (emission history and route topology)
- [x] Message validation/interception needed (interceptor pipeline)
- [x] Global message observation needed (listen to all signal fires)
- [x] Post-processing stage needed (analytics after handlers)
- [x] Zero-allocation messaging essential (struct-based)
- [x] GameObject/Component targeting needed (Unity-specific patterns)
- [x] Plug-and-play simplicity preferred over DI setup

#### Direct Comparison

| Aspect                   | Zenject Signals                                                      | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | DI-integrated messaging                                              | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built for Unity                                                      | Built for Unity                                                      |
| **Dependencies**         | Zenject required                                                     | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Can allocate                                                         | Steady-state dispatch contract                                       |
| **Learning Curve**       | Steep (Zenject+Signals)                                              | Moderate                                                             |
| **Setup Complexity**     | Installers required                                                  | Plug-and-play                                                        |
| **DI Integration**       | Required (Zenject)                                                   | Optional                                                             |
| **Async/Await**          | RunAsync support                                                     | Manual                                                               |
| **Type Safety**          | Strong                                                               | Strong                                                               |
| **Lifecycle Management** | DI-managed                                                           | Tokens, leases or component hooks                                    |
| **Execution Order**      | Not built-in                                                         | Priority-based                                                       |
| **GameObject Targeting** | Not built-in                                                         | Built-in                                                             |
| **Unity Integration**    | DI-managed                                                           | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Subscriber validation                                                | Full pipeline                                                        |
| **Global Observers**     | Not built-in                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | DI mocking                                                           | Local buses                                                          |
| **Decoupling**           | Excellent                                                            | Excellent                                                            |

**Bottom Line:** Zenject Signals are well-suited if you're already invested in Zenject and value testability through DI. DxMessaging offers standalone messaging without requiring DI setup, and includes Unity-specific features.

> **💡 Using Zenject?** DxMessaging integrates with Zenject. See the [DxMessaging + Zenject Integration Guide](../integrations/zenject.md) for setup. Use DxMessaging's priorities, interceptors, and visual diagnostics with Zenject's DI.

---

## Scriptable Object Architecture (SOA)

**What It Is:** A Unity-specific pattern popularized by Ryan Hipple's [Unite 2017 talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) that uses ScriptableObject assets for runtime communication (GameEvent, FloatVariable, etc.).

**Core Philosophy:** Designer-driven, asset-based communication where systems communicate through serialized SO assets instead of direct references.

[Unity's architecture guide](https://unity.com/how-to/architect-game-code-scriptable-objects)
shows both runtime variables and event assets. ScriptableObjects can hold mutable state as well as
shared authored configuration. The
[Unity 2021.3 manual](https://docs.unity3d.com/2021.3/Documentation/Manual/class-ScriptableObject.html)
explains the distinction between using asset data in a player and saving asset changes. Define
runtime state ownership, reset and saved-game storage explicitly.

### Quick Comparison

| Aspect           | SOA (GameEvent/Variables)                              | DxMessaging                                                       |
| ---------------- | ------------------------------------------------------ | ----------------------------------------------------------------- |
| Authoring        | Asset references and project-specific Inspector wiring | Typed messages and registrations in code                          |
| State            | Assets can hold shared configuration or runtime values | The application owns state; messages report changes               |
| Payload contract | Defined by the event or variable implementation        | Typed message interfaces and handler signatures                   |
| Lifecycle        | The application owns asset state and listener cleanup  | Tokens, leases and components manage registrations when released  |
| Performance      | Measure the actual asset/listener implementation       | Measure the actual route, handlers and diagnostics                |
| Testing          | Reset shared state and remove listeners between tests  | Local buses isolate routing; shared asset state still needs reset |

### When to Use Each

#### Choose SOA when

- Serialized asset references fit the team's authoring workflow.
- Existing event assets or shared runtime variables already have suitable ownership and reset rules.

##### Choose DxMessaging when

- Code needs typed messages with targeting, source filtering, interceptors or priorities.
- Bus and token diagnostics help inspect delivery.
- Tokens, leases or components fit the application's registration ownership.

###### Use Both when

- Existing event assets need to notify code through a bridge.
- Shared configuration supplies code that emits runtime messages.
- An asset or service owns current state while messages report changes.

The examples do not establish a performance ranking. Preserve state queries and initial snapshots
when migrating variables or runtime sets; subscriptions alone do not supply either.

### Full Comparison Guide

For detailed migration patterns, interoperability strategies, and code examples, see:

#### to [SOA Compatibility Guide](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa)

Includes:

- Pattern A: Bridging SOA GameEvents to DxMessaging
- Pattern B: Configuration assets with runtime messages
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
- [x] **Familiarity:** Uses the C# event and delegate model
- [x] **No dependencies:** Built into the language

#### What Problems It Doesn't Solve Well

- [ ] **Memory leaks:** Forgetting to unsubscribe causes leaks
- [ ] **Tight coupling:** Subscribers need direct references to event sources
- [ ] **Priority policy:** Ordinary event invocation has no separate priority or interception stages
- [ ] **Lifecycle management:** Manual subscribe/unsubscribe in OnEnable/OnDisable
- [ ] **Debugging:** No visibility into who's subscribed or when events fire
- [ ] **Validation/interception:** No pipeline to modify or validate before handlers
- [ ] **Global observation:** Cannot listen to all events across the system

#### Performance Characteristics

- **Routing:** Ordinary event invocation does not add a message-bus routing layer.
- **Allocations:** Measure invocation, callback and subscription work separately.
- **Backend:** Measure the actual invocation and callback work on the target runtime.
- **Use case:** Best raw performance for simple notifications

#### Learning Curve

- **Zero for C# developers:** Standard language feature
- **Immediate productivity:** No new concepts to learn
- **Try first:** Run a complete subscription and verify its callback and cleanup.

#### Ease of Understanding

- Uses the C# event and delegate model
- Straightforward mental model
- Easy to debug with breakpoints

#### When C# Events Win

- [x] Subscribers can keep an explicit reference to a stable event publisher
- [x] Need return values or `out` parameters
- [x] A language-level event is sufficient for the required host and callback contract
- [x] Simple, local communication within a class or module
- [x] Team is C# experts, Unity beginners
- [x] Direct event invocation fits the measured workload and routing requirements
- [x] Quick prototypes or game jams

#### When DxMessaging Wins

- [x] Registration ownership needs component hooks, leases or disposable tokens
- [x] Need decoupling (systems don't reference each other)
- [x] Execution order matters (priority-based handlers)
- [x] Debugging "what fired when" (Message Monitor history)
- [x] Message validation/interception needed (interceptor pipeline)
- [x] Observation of all matching message instances on the selected bus is needed
- [x] Multiple systems need a shared message contract and bus scope
- [x] Changes need explicit message contracts and ownership review
- [x] GameObject/Component targeting needed
- [x] Post-processing stage needed (analytics after handlers)

#### Direct Comparison

| Aspect                   | C# Events                                                            | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Simple callbacks                                                     | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built into C#                                                        | Built for Unity                                                      |
| **Dependencies**         | None (language)                                                      | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Measure invocation and callbacks                                     | Steady-state dispatch contract                                       |
| **Learning Curve**       | None                                                                 | Moderate                                                             |
| **Setup Complexity**     | Minimal                                                              | Moderate                                                             |
| **DI Integration**       | Manual                                                               | Optional                                                             |
| **Async/Await**          | Manual                                                               | Manual                                                               |
| **Type Safety**          | Delegate signature                                                   | Typed contracts; runtime reflexive lookup                            |
| **Lifecycle Management** | Manual unsubscribe                                                   | Tokens, leases or component hooks                                    |
| **Execution Order**      | Invocation-list order                                                | Priority-based                                                       |
| **GameObject Targeting** | Not built-in                                                         | Built-in                                                             |
| **Unity Integration**    | None                                                                 | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Not built-in                                                         | Full pipeline                                                        |
| **Global Observers**     | Not built-in                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | Explicit publisher/subscriber fixture                                | Local buses and explicit owners                                      |
| **Decoupling**           | Publisher reference                                                  | Shared message contract and bus                                      |
| **Return Values**        | Yes                                                                  | Fire-and-forget                                                      |

**Bottom Line:** C# events lead the current basic callback scenarios and remain
simple for local callbacks. DxMessaging is better for complex, decoupled systems
where lifecycle management, debugging, and execution control matter. Read the
[current measurements](./performance.md#latest-ci-performance-results) for the
actual runner and topology.

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
- **Try first:** Run a complete subscription and verify its callback and cleanup.

#### Ease of Understanding

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
- [x] Debugging observability (Message Monitor and Flow Graph)
- [x] Components need typed routing beyond their serialized callback connections
- [x] Team collaboration (merge-friendly code over Inspector)
- [x] Long-term maintenance (find usages, refactor safely)

#### Direct Comparison

| Aspect                   | UnityEvents                                                          | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Inspector wiring                                                     | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built into Unity                                                     | Built for Unity                                                      |
| **Dependencies**         | None (Unity)                                                         | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Boxing                                                               | Steady-state dispatch contract                                       |
| **Learning Curve**       | Minimal                                                              | Moderate                                                             |
| **Setup Complexity**     | Inspector                                                            | Code-based                                                           |
| **DI Integration**       | No                                                                   | Optional                                                             |
| **Async/Await**          | No                                                                   | Manual                                                               |
| **Type Safety**          | Weak (serialized)                                                    | Strong                                                               |
| **Lifecycle Management** | Unity-managed                                                        | Tokens, leases or component hooks                                    |
| **Execution Order**      | Undefined                                                            | Priority-based                                                       |
| **GameObject Targeting** | Manual references                                                    | Built-in                                                             |
| **Unity Integration**    | Inspector-based                                                      | Deep                                                                 |
| **Visual Diagnostics**   | Connections only                                                     | Monitor + graph                                                      |
| **Interceptors**         | Not built-in                                                         | Full pipeline                                                        |
| **Global Observers**     | Not possible                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | Scene setup                                                          | Local buses                                                          |
| **Decoupling**           | Hidden refs                                                          | Excellent                                                            |
| **Refactoring Safety**   | Silent breakage                                                      | Compile-time errors                                                  |
| **Code Visibility**      | Hidden in Inspector                                                  | Explicit in code                                                     |

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
- **Try first:** Run a complete subscription and verify its callback and cleanup.

#### Ease of Understanding

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
- [x] Debugging observability (Message Monitor and Flow Graph)
- [x] Execution order control (priority-based handlers)
- [x] Message validation/interception (interceptor pipeline)
- [x] Production code (maintainability over simplicity)
- [x] Modern projects (avoid legacy patterns)

#### Direct Comparison

| Aspect                   | Unity SendMessage                                                    | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Legacy GameObject calls                                              | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Built into Unity                                                     | Built for Unity                                                      |
| **Dependencies**         | None (Unity)                                                         | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Heavy boxing                                                         | Steady-state dispatch contract                                       |
| **Learning Curve**       | Minimal                                                              | Moderate                                                             |
| **Setup Complexity**     | None                                                                 | Moderate                                                             |
| **DI Integration**       | No                                                                   | Optional                                                             |
| **Async/Await**          | No                                                                   | Manual                                                               |
| **Type Safety**          | String-based                                                         | Strong                                                               |
| **Lifecycle Management** | None                                                                 | Tokens, leases or component hooks                                    |
| **Execution Order**      | Undefined                                                            | Priority-based                                                       |
| **GameObject Targeting** | Hierarchy traversal                                                  | Built-in (ID-based)                                                  |
| **Unity Integration**    | Legacy API                                                           | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Not built-in                                                         | Full pipeline                                                        |
| **Global Observers**     | Not possible                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | Requires GameObjects                                                 | Local buses                                                          |
| **Decoupling**           | String-based                                                         | Excellent                                                            |
| **Refactoring Safety**   | Silent breakage                                                      | Compile-time errors                                                  |
| **Parameters**           | 0 or 1 only                                                          | Unlimited (struct fields)                                            |

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
- [ ] **Ordering:** Define the callback order and mutation behavior of the chosen static implementation
- [ ] **Testing difficulty:** Global state makes unit testing hard
- [ ] **Naming conflicts:** All events in same namespace, naming gets messy
- [ ] **No validation:** No way to intercept or validate messages
- [ ] **No observability:** Can't see who's subscribed or message history
- [ ] **Ownership unclear:** Who manages what events?
- [ ] **Lifecycle management:** Manual subscribe/unsubscribe required

#### Performance Characteristics

- **Good performance:** Similar to C# events (static overhead is minimal)
- **Allocations:** Measure invocation, callback and subscription work separately.
- **Use case:** Acceptable for most scenarios

#### Learning Curve

- **Very easy:** Just a static class with events
- **Immediate productivity:** No new concepts
- **Try first:** Run a complete subscription and verify its callback and cleanup.

#### Ease of Understanding

- Simple pattern to grasp
- Group channels and document the owner of each subscription.
- Hard to track ownership and responsibilities

#### When Static Event Bus Wins

- [x] You've already built one and it works
- [x] Very simple use cases (just need globals)
- [x] Small projects (<10 events)
- [x] No framework dependencies desired
- [x] Quick prototypes

#### When DxMessaging Wins

- [x] More than 10-15 events (organization becomes important)
- [x] Owners need explicit registration teardown through components, leases or tokens
- [x] Execution order matters (priority-based handlers)
- [x] Need message validation/interception (interceptor pipeline)
- [x] Testing is important (local buses for isolation)
- [x] Observability needed (Message Monitor and Flow Graph)
- [x] Multiple subsystems (namespacing and organization)
- [x] GameObject/Component targeting needed
- [x] Observation of all matching message instances on the selected bus is needed
- [x] Post-processing needed (analytics after handlers)
- [x] Long-term maintenance (structure prevents chaos)

#### Direct Comparison

| Aspect                   | Static Event Bus                                                     | DxMessaging                                                          |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Global event hub                                                     | Pub/sub messaging                                                    |
| **Unity Compatibility**  | Works in Unity                                                       | Built for Unity                                                      |
| **Dependencies**         | None (custom)                                                        | Standalone                                                           |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Allocations**          | Zero (basic)                                                         | Steady-state dispatch contract                                       |
| **Learning Curve**       | Minimal                                                              | Moderate                                                             |
| **Setup Complexity**     | Minimal                                                              | Moderate                                                             |
| **DI Integration**       | Manual                                                               | Optional                                                             |
| **Async/Await**          | Manual                                                               | Manual                                                               |
| **Type Safety**          | Strong                                                               | Strong                                                               |
| **Lifecycle Management** | Manual unsubscribe                                                   | Tokens, leases or component hooks                                    |
| **Execution Order**      | Invocation-list order in this example                                | Priority-based                                                       |
| **GameObject Targeting** | Not built-in                                                         | Built-in                                                             |
| **Unity Integration**    | None                                                                 | Deep                                                                 |
| **Visual Diagnostics**   | No                                                                   | Monitor + graph                                                      |
| **Interceptors**         | Not built-in                                                         | Full pipeline                                                        |
| **Global Observers**     | Not built-in                                                         | Observe selected-bus messages                                        |
| **Post-Processing**      | Not built-in                                                         | Dedicated stage                                                      |
| **Testability**          | Hard (global)                                                        | Local buses                                                          |
| **Decoupling**           | Good                                                                 | Excellent                                                            |
| **Organization**         | One big class                                                        | Structured                                                           |

**Bottom Line:** Static event buses solve global access but inherit all the problems of C# events (leaks, undefined order, no observability). DxMessaging provides the same global access with lifecycle safety, structure, and debugging tools.

**Migration Path:** DxMessaging can replace static event buses gradually:

```csharp
// Old static event bus
EventHub.RaiseDamage(5);

// DxMessaging equivalent (global bus, affected player retained as source)
var damage = new TookDamage(5);
damage.EmitGameObjectBroadcast(gameObject);

// Or use local buses for subsystems
var combatBus = new MessageBus();
var combatDamage = new TookDamage(5);
combatDamage.EmitGameObjectBroadcast(gameObject, combatBus);
```

---

## Honest Trade-offs: What You Give Up, What You Gain

DxMessaging involves trade-offs like any architectural choice. This section describes what you gain and what you sacrifice when adopting it.

Choose from the communication and ownership requirements below. Project age, line count and team
size do not establish a performance result or an adoption payback period.

### Learning Curve

Try one complete route before adopting the bus across a project: define a message, choose its bus,
register and release a listener, and inspect a delivery. Include a scene or scope transition if the
application uses one. The [getting started guide](../getting-started/getting-started.md) and
[ownership patterns](../concepts/listening-patterns.md) provide call sites to evaluate.

The team needs to understand targeted versus sourced broadcasts, registration lifetime and callback
ordering. Typed message names make emitters and listeners searchable, but a message type alone does
not explain the application's state ownership or scene dependencies. Evaluate those costs in the
code the team maintains; this guide has no measured learning-time estimate.

### Boilerplate

#### What You Give Up

- [ ] "One-liners" - C# events can be `public event Action OnClick;` done
- [ ] Quick and dirty - Need to define message struct, attributes, handler registration

#### What You Gain

- [x] Explicit contracts - Messages are discoverable types, not hidden delegates
- [x] Auto-generated code - `[DxAutoConstructor]` reduces boilerplate
- [x] Typed contracts - The compiler checks typed message and handler usages; string-based reflexive calls still resolve at runtime.

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

A direct event may need less setup when a known publisher and callback already meet the contract.
Use the bus when its routing, interception, ownership or diagnostics remove work the application
would otherwise implement.

### Performance

#### What You Give Up

- [ ] Routing work - Message delivery resolves the configured route and stages.
- [ ] Integration work - Choose a message contract, bus and subscription owner.
- [ ] Profiling scope - Separate bus dispatch from application callback work.

#### What You Gain

- [x] Supported steady-state dispatch paths have zero-allocation regression contracts.
- [x] Benchmarks separate dispatch work from allocation probes.
- [x] Message Monitor, Flow Graph and token diagnostics can inspect the configured routes.

#### Hard Numbers

- C# events and DxMessaging: see the
  [current CI measurements](./performance.md#latest-ci-performance-results)
- Allocation scope: the contract covers the promised steady-state dispatch path. Registration,
  payload construction, boxing, callbacks and diagnostic recording need separate measurement.

Measure the target workload and backend before choosing an implementation for performance. A
message count alone does not establish frame cost. The linked results describe their named routes,
handler work and build profiles; they do not measure an application's UI or gameplay logic.

### Flexibility

#### Contracts to Check

- Emit methods return `void` and dispatch synchronously. Use an explicit query/service contract when
  a caller needs a result. Interceptors can mutate a `ref` payload or routing context and cancel
  delivery; that is different from collecting return values from multiple subscribers.
- Registration methods accept inline callbacks. Keep their deregistration token, lease or component
  owner, and measure any registration/closure work separately from dispatch.
- Targeted messages carry a target and broadcasts carry an explicit source. Untargeted messages do
  not infer a sender identity.
- Priorities and stages order callback invocation. They do not wait for asynchronous operations
  started inside a callback to finish.

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

For a query, define how a caller obtains a result and handles failure. A direct service method or a
returning delegate is one option. For notifications, choose the routing and lifetime contract the
application needs.

### Debuggability

#### What You Give Up

- [ ] Simplicity - Stack traces show message bus internals
- [ ] Step-through - Can't F11 directly from emit to handler (need breakpoints)

#### What You Gain

- [x] Message history - Inspect recent emissions in Message Monitor
- [x] Registration view - Inspect loaded-scene component listeners and routes in Flow Graph
- [x] Bus observability - Inspect emissions on the selected bus within its configured capture scope.
- [x] Filtering - Configure routes and capture filters for the messages under investigation.

#### Example: Finding Who Fired a Message

```csharp
// C# events: Set breakpoint on every possible Invoke(), or add logging everywhere
OnDamage?.Invoke(5); // Where did this come from??

// DxMessaging: Check Message Monitor or add a global logger
_ = debugToken.RegisterBroadcastWithoutSource<TookDamage>(
    (src, msg) => Debug.Log($"Damage from {src}: {msg.amount}")
);
```

Use [Message Monitor](../guides/diagnostics.md#message-monitor) to find the
emission and its call site. For a loaded-scene `MessagingComponent`, use
[Flow Graph](../guides/diagnostics.md#flow-graph) to inspect the matching receiver
route; use bus logs or counters for direct registrations outside those
components.

**Verdict:** Initial debugging adds a routing layer, while the built-in tools
make system-wide message flow inspectable without adding log statements.

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

A bus can remove direct publisher references, but it still introduces shared message contracts,
route choices and lifecycle dependencies. Review those dependencies when adding or changing a
message; line count does not determine whether the tradeoff is useful.

### Testing

#### What You Give Up

- [ ] Simplicity - Can't just mock an event subscription

#### What You Gain

- [x] Isolated routing - Pass a local bus to each test participant and dispose its tokens
- [x] Observability - Count messages, inspect payloads easily
- [x] Explicit ordering - Priorities determine handler order

##### Example

See the [complete local-bus test in the README](https://github.com/Ambiguous-Interactive/DxMessaging#local-bus-islands-for-testing)
for a subscription that enables its token, verifies delivery, and disposes the token even when an
assertion fails. Separate buses isolate routing; runtime settings and pools remain shared.

**Verdict:** DxMessaging makes integration testing easier, unit testing slightly more verbose.

## Feature-by-Feature Comparison Matrix

### Unity Messaging Frameworks Comparison

| Aspect                   | DxMessaging                                                          | UniRx                                                                | MessagePipe                                                          | Zenject Signals                                                      |
| ------------------------ | -------------------------------------------------------------------- | -------------------------------------------------------------------- | -------------------------------------------------------------------- | -------------------------------------------------------------------- |
| **Primary Use Case**     | Pub/sub messaging                                                    | Stream transformations                                               | High-perf DI messaging                                               | DI-integrated messaging                                              |
| **Unity Compatibility**  | Built for Unity                                                      | Built for Unity                                                      | Built for Unity                                                      | Built for Unity                                                      |
| **Performance**          | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) | [Current CI results](./performance.md#latest-ci-performance-results) |
| **Zero Allocations**     | (structs)                                                            | Can allocate                                                         | (structs)                                                            | Can allocate                                                         |
| **Unity Integration**    | Deep (lifecycle)                                                     | Good (UI/async)                                                      | Basic (no lifecycle)                                                 | Good (DI-managed)                                                    |
| **Visual Diagnostics**   | Monitor + graph                                                      | No                                                                   | No                                                                   | No                                                                   |
| **Execution Order**      | Priority-based                                                       | Not built-in                                                         | Subscription order                                                   | Not built-in                                                         |
| **Lifecycle Management** | Tokens, leases or component hooks                                    | Manual dispose                                                       | Manual dispose                                                       | DI-managed                                                           |
| **Learning Curve**       | Moderate                                                             | Steep (Rx paradigm)                                                  | Moderate (DI)                                                        | Steep (DI+Signals)                                                   |
| **Setup Complexity**     | Plug-and-play                                                        | Low                                                                  | DI setup required                                                    | Installers required                                                  |
| **DI Integration**       | Optional                                                             | Optional                                                             | First-class                                                          | Required (Zenject)                                                   |
| **Async/Await**          | Manual                                                               | Native (observables)                                                 | Native                                                               | Yes                                                                  |
| **Message Validation**   | Interceptor pipeline                                                 | Not built-in                                                         | Filters (middleware)                                                 | Not built-in                                                         |
| **GameObject Targeting** | Built-in                                                             | Not designed for                                                     | Not built-in                                                         | Not built-in                                                         |
| **Global Observers**     | Listen to all sources                                                | Not built-in                                                         | Not built-in                                                         | Not built-in                                                         |
| **Post-Processing**      | Dedicated stage                                                      | Not built-in                                                         | Via filters                                                          | Not built-in                                                         |
| **Stream Operators**     | Not built-in                                                         | Extensive (LINQ)                                                     | Not built-in                                                         | With UniRx                                                           |
| **Testability**          | Local buses                                                          | Good                                                                 | DI mocking                                                           | DI mocking                                                           |
| **Decoupling**           | Excellent                                                            | Excellent                                                            | Excellent                                                            | Excellent                                                            |
| **Type Safety**          | Strong                                                               | Strong                                                               | Strong                                                               | Strong                                                               |
| **Dependencies**         | None                                                                 | None                                                                 | MessagePipe package                                                  | Zenject required                                                     |

### Traditional Approaches Comparison

| Responsibility       | C# Events                                                                   | UnityEvents                                                                 | SOA (GameEvent)                                   | Static Bus                        | DxMessaging                                                                 |
| -------------------- | --------------------------------------------------------------------------- | --------------------------------------------------------------------------- | ------------------------------------------------- | --------------------------------- | --------------------------------------------------------------------------- |
| Wiring               | Publisher reference and event subscription                                  | Inspector or runtime listeners                                              | Shared asset reference and channel implementation | Implementation-specific registry  | Message type, bus and registration                                          |
| Payload              | Delegate signature                                                          | UnityEvent signature and serialized arguments                               | Defined by the channel                            | Defined by the implementation     | Typed message interface and handler                                         |
| Subscription owner   | Publisher/subscriber agreement                                              | Serialized and runtime listener lifetimes                                   | Asset listeners and any bridge owner              | Defined by the implementation     | Token, lease or component hooks                                             |
| Current state        | Application-owned                                                           | Application-owned                                                           | Asset or service can own state                    | Application-owned                 | Application-owned; messages report changes                                  |
| Ordering             | Multicast invocation-list order                                             | Check the required callback contract                                        | Defined by the channel                            | Defined by the implementation     | Priority and dispatch stages                                                |
| Diagnostics          | Debugger and project tooling                                                | Inspector, debugger and project tooling                                     | Asset Inspector and project tooling               | Defined by the implementation     | Message Monitor, Flow Graph and token diagnostics                           |
| Performance evidence | [Named benchmark scenarios](./performance.md#latest-ci-performance-results) | [Named benchmark scenarios](./performance.md#latest-ci-performance-results) | The benchmark's tested bridge                     | Measure the actual implementation | [Named benchmark scenarios](./performance.md#latest-ci-performance-results) |

The [C# specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/delegates#216-delegate-invocation)
defines synchronous multicast invocation-list order. That order and DxMessaging priorities describe
callback invocation, not completion of asynchronous work started by callbacks. A custom event
accessor, channel or static bus needs its own contract.

DxMessaging's Unity component helpers require their documented base lifecycle calls. Plain tokens
and leases need an owner that releases them. These helpers do not make arbitrary handler closures,
shared assets or application state leak-free. For a host outside Unity, use the separate
[.NET source-build contract](../reference/compatibility.md#plain-net-runtime), including its declared
support-package dependency; Unity-only components and diagnostics are not part of that target.

<a id="overall-verdict-by-use-case"></a>

### Choices by Use Case

- **Known publisher and local callback:** Start with a direct method, event or UnityEvent if it
  already provides the needed contract.
- **Typed routing across owners:** Evaluate DxMessaging's targeted, broadcast and untargeted paths.
- **Existing asset workflow:** Preserve state ownership and asset references; add a bridge only where
  bus listeners are needed.
- **Stream transformations:** Evaluate the required reactive operators and subscription lifetime.
- **Existing DI services:** Compare the container's event facilities with the optional bus
  integrations using the actual setup and cleanup call sites.
- **Diagnostics or interception:** Check whether priorities, cancellation, post-processors and the
  inspection tools address the application's concrete problem.
- **Performance requirement:** Measure the complete topology, callback work, allocations and target
  backend. Preserve the [published results' scope](./performance.md#how-to-read-these-tables).

<a id="when-each-approach-actually-wins"></a>

## Requirements by Approach

### DxMessaging Wins When

- [x] Unity-first projects (MonoBehaviour lifecycle integration)
- [x] Multiple owners need pub/sub communication without direct publisher references
- [x] Observability essential (Message Monitor and Flow Graph)
- [x] Owners need explicit registration teardown through components, leases or tokens
- [x] Cross-team development (clear message contracts)
- [x] The team needs to inspect and maintain message contracts and routes
- [x] GameObject/Component targeting needed (Unity-specific patterns)
- [x] Execution order control essential (priority-based handlers)
- [x] Message validation/transformation needed (interceptor pipeline)
- [x] Observation of all matching message instances on the selected bus is needed
- [x] Post-processing needed (analytics, logging after handlers)
- [x] Caller-controlled emission timing fits the application; frame scheduling remains application-owned.
- [x] Teams without DI experience (no framework dependencies)
- [x] The Unity package's default or explicit bus meets the required setup and ownership contract

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

- [x] DI-first throughput is the priority and its supported scenarios match the workload
- [x] Already using DI (VContainer, Zenject, etc.)
- [x] Cross-platform .NET projects (not Unity-only)
- [x] Need native async/await support
- [x] Large-scale projects with DI architecture
- [x] Want compile-time leak detection (Roslyn analyzer)
- [x] Measured throughput, fan-out and callback work meet the target frame budget

### Zenject Signals Win When

- [x] Already using Zenject for dependency injection
- [x] Testability through DI is critical
- [x] Need subscriber validation (ensure handlers exist)
- [x] Team experienced with Zenject
- [x] Want DI-managed lifecycle
- [x] Integration with existing Zenject architecture

### C# Events Win When

- [x] You need return values or out parameters
- [x] A language-level event is sufficient for the required host and callback contract
- [x] Subscribers can keep an explicit reference to a stable event publisher
- [x] Team is C# experts, Unity beginners

### UnityEvents Win When

- [x] Designers need to wire logic without code
- [x] Rapid prototyping with prefabs
- [x] Very simple games (mobile casual, hyper-casual)

### SOA (GameEvent/Variables) Wins When

- [x] Designers must create and wire events without touching code
- [x] Team is already heavily invested in SOA with many existing assets
- [x] Shared assets have explicit state ownership, reset and subscription cleanup.
- Consider migration and ongoing maintenance costs alongside the existing authoring workflow.
- **Alternative:** Keep configuration or runtime state in an asset/service and use messages for notifications ([SOA integration choices](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa)).

### Static Event Bus Wins When

- [x] You've already built one and it works
- [x] Very simple use cases (just need globals)

## Cost-Benefit Summary

Adoption adds message declarations, registration ownership and bus selection. Source generation and
ownership helpers reduce some repeated setup, while diagnostics expose routes and selected message
history. Compare those costs with the publisher references, cleanup and inspection code the
application would otherwise maintain. This guide does not establish a payback time or a project-size
threshold.

<a id="making-the-decision-be-honest-with-yourself"></a>

## Evaluate One Real Notification

### Check the Required Behavior

| Question                             | What to verify                                                             |
| ------------------------------------ | -------------------------------------------------------------------------- |
| Who receives a notification?         | Target, source filtering, fan-out and bus scope                            |
| Who owns current state?              | Query path, initial snapshot, reset and persistence                        |
| Who owns each subscription?          | Enable, disable, disposal and scene/scope transitions                      |
| Does delivery modify or cancel work? | Interceptors, payload/context mutation and exception behavior              |
| Does a caller need a result?         | Explicit query/service contract and failure handling                       |
| Does work continue asynchronously?   | Completion, cancellation and lifetime beyond synchronous dispatch          |
| What needs inspection?               | Available capture scope, diagnostics overhead and retention                |
| Which host must run it?              | Supported Unity version/backend or the separate .NET source-build target   |
| What cost is acceptable?             | Measured dispatch, registration, callbacks, diagnostics and retained state |

### Try a Bounded Integration

Choose one real notification with a known owner. Implement the sender and listener, exercise a
lifecycle transition, and inspect the delivered payload and cleanup. Keep the existing design when
a bus does not remove enough application work to justify the change. Migrate incrementally when
routing or inspection needs make the bus useful; preserve existing state/query contracts.

Project duration, team size, message count and lines of code can describe a workload. They do not
prove that a particular framework is required, faster, easier to learn or cheaper to maintain.

See also

- [Message Types](../concepts/message-types.md)
- [Diagnostics (Editor inspector)](../guides/diagnostics.md)
- [Migration Guide](../guides/migration-guide.md) - How to adopt gradually
