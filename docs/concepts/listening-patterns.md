# Listening Patterns

## Targeted across all targets

- Accept every targeted message of a given type regardless of who it's for.

```csharp
using DxMessaging.Core;   // InstanceId
using DxMessaging.Core.Messages;

// Update the combat feed for every requested heal.
_ = token.RegisterTargetedWithoutTargeting<Heal>(ShowRequestedHeal);
void ShowRequestedHeal(InstanceId target, Heal m) => combatFeed.ShowHeal(target, m.amount);

// Record the request only after message dispatch reaches post-processing.
_ = token.RegisterTargetedWithoutTargetingPostProcessor<Heal>(RecordProcessedHealRequest);
void RecordProcessedHealRequest(InstanceId target, Heal m) =>
    metrics.RecordProcessedHealRequest(target, m.amount);
```

## Broadcast across all sources

- Accept every broadcast message of a given type regardless of who emitted it.

```csharp
using DxMessaging.Core;   // InstanceId
using DxMessaging.Core.Messages;

// Spawn presentation feedback for every damage source.
_ = token.RegisterBroadcastWithoutSource<TookDamage>(ShowDamageNumber);
void ShowDamageNumber(InstanceId source, TookDamage m) => damageNumbers.Show(source, m.amount);

// Record the processed message for replay after every gameplay handler has run.
_ = token.RegisterBroadcastWithoutSourcePostProcessor<TookDamage>(RecordProcessedDamage);
void RecordProcessedDamage(InstanceId source, TookDamage m) =>
    replay.RecordProcessedDamageMessage(source, m.amount);
```

The handler owns presentation state by spawning a damage number. The post-processor records
that dispatch completed. It cannot infer the target's final health from the message payload.

## Global accept-all (debug/inspection)

- Receive every message of every type on a handler; useful for tooling.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Messages;
using DxMessaging.Core.MessageBus;

IMessageBus bus = MessageHandler.MessageBus;
MessageHandler handler = new(new InstanceId(1)) { active = true };
MessageBusRegistration registration = bus.RegisterGlobalAcceptAll(handler);
// implement handler callbacks for generic categories on your MessageHandler

// When the owning tool shuts down:
bus.Deregister<IMessage>(in registration);
```

## Real-World Use Cases

### Development Debug Dump

Capture all messages during development for debugging and diagnostics:

```csharp
using System;
using DxMessaging.Core;
using DxMessaging.Core.Messages;
using DxMessaging.Core.MessageBus;
using UnityEngine;

public sealed class DebugMessageLogger : MessageHandler, IDisposable
{
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;

    public DebugMessageLogger(IMessageBus bus) : base(new InstanceId(999))
    {
        _bus = bus;
        active = true;
        _registration = bus.RegisterGlobalAcceptAll(this);
    }

    public override void Handle(ref IUntargetedMessage message)
    {
        Debug.Log($"[Untargeted] {message.GetType().Name}: {message}");
    }

    public override void Handle(ref InstanceId target, ref ITargetedMessage message)
    {
        Debug.Log($"[Targeted -> {target}] {message.GetType().Name}: {message}");
    }

    public override void Handle(ref InstanceId source, ref IBroadcastMessage message)
    {
        Debug.Log($"[Broadcast <- {source}] {message.GetType().Name}: {message}");
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }
        _bus.Deregister<IMessage>(in _registration);
        _registration = MessageBusRegistration.None;
    }
}
```

Keep the owner alive for the intended observation scope:

```csharp
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using DebugMessageLogger logger = new(MessageHandler.MessageBus);
#endif
```

### Attribute-Based Network Replication

Automatically replicate messages marked with custom attributes across the network:

```csharp
using System;
using System.Reflection;
using System.Collections.Generic;
using DxMessaging.Core;
using DxMessaging.Core.Messages;
using DxMessaging.Core.MessageBus;

// Mark messages that should be replicated
[AttributeUsage(AttributeTargets.Struct)]
public class NetworkedAttribute : Attribute { }

[Networked]
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerMoved
{
    public readonly Vector3 position;
}

[Networked]
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct DealDamage
{
    public readonly float amount;
}

// Network replication handler
public sealed class NetworkReplicator : MessageHandler, IDisposable
{
    private readonly INetworkManager _network;
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;
    private readonly HashSet<Type> _networkedTypes = new();

    public NetworkReplicator(INetworkManager network, IMessageBus bus) : base(new InstanceId(1000))
    {
        _network = network;
        _bus = bus;
        CacheNetworkedTypes();
        active = true;
        _registration = bus.RegisterGlobalAcceptAll(this);
    }

    private void CacheNetworkedTypes()
    {
        // Find all message types with [Networked] attribute
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<NetworkedAttribute>() != null)
                {
                    _networkedTypes.Add(type);
                }
            }
        }
    }

    public override void Handle(ref IUntargetedMessage message)
    {
        if (_networkedTypes.Contains(message.GetType()))
        {
            _network.Send(message);  // Serialize and send
        }
    }

    public override void Handle(ref InstanceId target, ref ITargetedMessage message)
    {
        if (_networkedTypes.Contains(message.GetType()))
        {
            _network.Send(target, message);
        }
    }

    public override void Handle(ref InstanceId source, ref IBroadcastMessage message)
    {
        if (_networkedTypes.Contains(message.GetType()))
        {
            _network.Send(source, message);
        }
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }
        _bus.Deregister<IMessage>(in _registration);
        _registration = MessageBusRegistration.None;
    }
}
```

Use the owner for the full replication scope. Messages marked with `[Networked]` then replicate
without explicit per-type registration:

```csharp
using NetworkReplicator replicator = new(networkManager, MessageHandler.MessageBus);

var playerMoved = new PlayerMoved(playerPos);
playerMoved.EmitFrom(gameObject);
var dealDamage = new DealDamage(50f);
dealDamage.EmitTargeted(enemyId);
```

### Message Analytics and Metrics

Track message frequency and performance across your entire game:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DxMessaging.Core;
using DxMessaging.Core.Messages;

public class MessageAnalytics : MessageHandler
{
    private readonly Dictionary<Type, (int count, long totalMs)> _stats = new();
    private readonly Stopwatch _stopwatch = new();

    public MessageAnalytics() : base(new InstanceId(1001)) { }

    public override void Handle(ref IUntargetedMessage message)
    {
        TrackMessage(message.GetType());
    }

    public override void Handle(ref InstanceId target, ref ITargetedMessage message)
    {
        TrackMessage(message.GetType());
    }

    public override void Handle(ref InstanceId source, ref IBroadcastMessage message)
    {
        TrackMessage(message.GetType());
    }

    private void TrackMessage(Type messageType)
    {
        _stopwatch.Restart();
        // Message processing happens here
        _stopwatch.Stop();

        if (!_stats.TryGetValue(messageType, out var stat))
        {
            stat = (0, 0);
        }
        _stats[messageType] = (stat.count + 1, stat.totalMs + _stopwatch.ElapsedMilliseconds);
    }

    public void PrintStats()
    {
        foreach (var kvp in _stats)
        {
            var avg = kvp.Value.totalMs / (double)kvp.Value.count;
            UnityEngine.Debug.Log($"{kvp.Key.Name}: {kvp.Value.count} messages, avg {avg:F2}ms");
        }
    }
}
```

When to Use Global Accept-All

Yes **Good use cases:**

- Development-time debugging and logging
- Cross-cutting concerns (analytics, telemetry, metrics)
- Attribute-based systems (networking, serialization, persistence)
- Testing and diagnostics tools
- Message replay/recording systems

Warning: **Performance consideration:**
Global Accept-All handlers are invoked for **every** message of **every** type. For performance-sensitive gameplay logic, prefer type-specific registrations which use O(1) lookup instead of O(N) iteration.

No **Avoid for:**

- Core gameplay logic that only needs specific message types
- Hot paths with thousands of messages per frame
- Production code that can use specific type registrations instead

Tips

- Use across-all listeners for diagnostics, analytics, or cross-cutting observers.
- Prefer specific (target/source) registrations for gameplay logic.

Related

- [Interceptors & Ordering](interceptors-and-ordering.md)
- [Diagnostics](../guides/diagnostics.md)
