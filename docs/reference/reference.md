# API Reference (Practical)

This reference provides a practical overview of the DxMessaging API for Unity developers.

---

## Message Registration (Unity-Friendly)

The `MessageRegistrationToken` is your primary interface for subscribing to messages in a managed, lifecycle-aware manner.

### Token Lifecycle Methods

```csharp
// Enable/disable all registrations on this token
token.Enable();
token.Disable();

// Remove all registrations
token.UnregisterAll();

// Remove a specific registration
token.RemoveRegistration(handle);
```

### Untargeted Message Registration

Register handlers for messages that have no specific target -- system-wide events.

```csharp
// Standard handler (allocation-friendly for simple cases)
MessageRegistrationHandle RegisterUntargeted<T>(
    Action<T> handler,
    int priority = 0
)

// Fast handler (zero-allocation, receives message by ref)
MessageRegistrationHandle RegisterUntargeted<T>(
    MessageHandler.FastHandler<T> handler,
    int priority = 0
)

// Post-processor (runs after all handlers)
MessageRegistrationHandle RegisterUntargetedPostProcessor<T>(
    MessageHandler.FastHandler<T> handler,
    int priority = 0
)
```

### Targeted Message Registration

Register handlers for messages directed at specific GameObjects, Components, or InstanceIds.

#### Specific Target

```csharp
// GameObject target
MessageRegistrationHandle RegisterGameObjectTargeted<T>(
    GameObject target,
    Action<T> handler,
    int priority = 0
)

// Component target
MessageRegistrationHandle RegisterComponentTargeted<T>(
    Component target,
    Action<T> handler,
    int priority = 0
)

// InstanceId target (low-level)
MessageRegistrationHandle RegisterTargeted<T>(
    InstanceId target,
    Action<T> handler,
    int priority = 0
)
```

#### All Targets

```csharp
// Receive all targeted messages regardless of target
MessageRegistrationHandle RegisterTargetedWithoutTargeting<T>(
    FastHandlerWithContext<T> handler,
    int priority = 0
)
```

#### Post-Processors

```csharp
// Post-process for specific target
MessageRegistrationHandle RegisterTargetedPostProcessor<T>(
    InstanceId target,
    FastHandler<T> handler,
    int priority = 0
)

// Post-process all targeted messages
MessageRegistrationHandle RegisterTargetedWithoutTargetingPostProcessor<T>(
    FastHandlerWithContext<T> handler,
    int priority = 0
)
```

### Broadcast Message Registration

Register handlers for messages broadcast from specific sources.

#### Specific Source

```csharp
// From specific GameObject
MessageRegistrationHandle RegisterGameObjectBroadcast<T>(
    GameObject source,
    Action<T> handler,
    int priority = 0
)

// From specific Component
MessageRegistrationHandle RegisterComponentBroadcast<T>(
    Component source,
    Action<T> handler,
    int priority = 0
)

// From specific InstanceId
MessageRegistrationHandle RegisterBroadcast<T>(
    InstanceId source,
    Action<T> handler,
    int priority = 0
)
```

#### All Sources

```csharp
// Receive broadcasts from any source
MessageRegistrationHandle RegisterBroadcastWithoutSource<T>(
    FastHandlerWithContext<T> handler,
    int priority = 0
)
```

#### Post-Processors

```csharp
// Post-process for specific source
MessageRegistrationHandle RegisterBroadcastPostProcessor<T>(
    InstanceId source,
    FastHandler<T> handler,
    int priority = 0
)

// Post-process all broadcasts
MessageRegistrationHandle RegisterBroadcastWithoutSourcePostProcessor<T>(
    FastHandlerWithContext<T> handler,
    int priority = 0
)
```

---

## Emit Helpers

The `DxMessaging.Core.Extensions.MessageExtensions` class provides convenient extension methods for emitting messages.

### Untargeted Emission

```csharp
// Emit any message type as untargeted
message.Emit();
message.EmitUntargeted();
```

### Targeted Emission

```csharp
// Emit to specific target (by InstanceId)
message.EmitTargeted(InstanceId target);

// Emit to GameObject target
message.EmitGameObjectTargeted(GameObject target);

// Emit to Component target
message.EmitComponentTargeted(Component target);
```

### Broadcast Emission

```csharp
// Broadcast from specific source (by InstanceId)
message.EmitBroadcast(InstanceId source);

// Broadcast from GameObject source
message.EmitGameObjectBroadcast(GameObject source);

// Broadcast from Component source
message.EmitComponentBroadcast(Component source);
```

### String Message Conveniences

```csharp
// Quick string message emission
"PlayerDied".Emit();
"DamageDealt".Emit(targetInstanceId);
```

---

## Interceptors (Bus-Level)

Interceptors allow you to intercept and potentially modify or cancel messages at the bus level before they reach handlers.

```csharp
// Intercept untargeted messages
Action RegisterUntargetedInterceptor<T>(
    UntargetedInterceptor<T> interceptor,
    int priority = 0
)

// Intercept targeted messages
Action RegisterTargetedInterceptor<T>(
    TargetedInterceptor<T> interceptor,
    int priority = 0
)

// Intercept broadcast messages
Action RegisterBroadcastInterceptor<T>(
    BroadcastInterceptor<T> interceptor,
    int priority = 0
)

// Global observer for all messages
Action RegisterGlobalAcceptAll(MessageHandler handler)
```

---

## Diagnostics

DxMessaging provides diagnostic tools for debugging and monitoring message flow.

### Global Settings

```csharp
// Enable/disable diagnostics globally
IMessageBus.GlobalDiagnosticsMode = true;

// Configure global message buffer size
IMessageBus.GlobalMessageBufferSize = 1024;
```

### Per-Instance Settings

```csharp
// Per-bus diagnostics
messageBus.DiagnosticsMode = true;

// Per-token diagnostics
token.DiagnosticMode = true;
```

### Registration Logging

```csharp
// Enable registration logging
bus.Log.Enabled = true;

// Get log output
string logOutput = bus.Log.ToString();
```

---

## Key Types

| Type                                     | Description                                                           |
| ---------------------------------------- | --------------------------------------------------------------------- |
| `DxMessaging.Core.InstanceId`            | Value type identity for GameObjects, Components, or custom owners     |
| `DxMessaging.Core.MessageHandler`        | Per-owner callback runner that manages message dispatch               |
| `DxMessaging.Core.MessageBus.MessageBus` | Instanced bus; global instance at `MessageHandler.MessageBus`         |
| `DxMessaging.Core.Messages.*`            | Untargeted/Targeted/Broadcast interfaces and built-in string messages |

> 💡 **Tip: String Messages**
>
> For lightweight string-based messaging, see [String Messages](../advanced/string-messages.md).

---

## Unity Bridge Types

### MessagingComponent

Base component for objects that emit messages.

```csharp
public class MessagingComponent : MonoBehaviour
{
    // When true, messages can be emitted even when component is disabled:
    // OnEnable/OnDisable leave the handler untouched while this is set
    public bool emitMessagesWhenDisabled;

    // Create a registration token for a listener on this GameObject
    public MessageRegistrationToken Create(MonoBehaviour listener);

    // Toggle the message handler on/off; explicit calls always win,
    // even while emitMessagesWhenDisabled is true
    public void ToggleMessageHandler(bool newActive);
}
```

### MessageAwareComponent

Base component for objects that both emit and receive messages.

```csharp
// Requires a MessagingComponent on the same GameObject ([RequireComponent]).
public abstract class MessageAwareComponent : MonoBehaviour
{
    // The registration token for this component
    public MessageRegistrationToken Token { get; }

    // When true, registrations are enabled/disabled with OnEnable/OnDisable
    protected virtual bool MessageRegistrationTiedToEnableStatus { get; }

    // When true (default false), enabling after MessagingComponent.Release
    // re-creates the token and replays RegisterMessageHandlers
    protected virtual bool ReregisterOnEnableAfterRelease { get; }

    // When true, registers for string messages automatically
    protected virtual bool RegisterForStringMessages { get; }

    // Override to register your message handlers
    protected virtual void RegisterMessageHandlers() { }
}
```

> **Warning: Inheritance Tip**
>
> If you override any lifecycle hooks (`Awake`, `OnDestroy`, `OnEnable`, `OnDisable`) or `RegisterMessageHandlers`, always call the base method:
>
> ```csharp
> protected override void RegisterMessageHandlers()
> {
>     base.RegisterMessageHandlers();
>     // Your registrations here
> }
>
> protected override void OnEnable()
> {
>     base.OnEnable();
>     // Your logic here
> }
> ```
>
> Skipping base calls may prevent token setup and default string-message registrations.

---

## Source Files

For deeper exploration, browse the source code:

| Component             | Source                                                                                                                                   |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| Message Bus Interface | [IMessageBus.cs](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Runtime/Core/MessageBus/IMessageBus.cs)                |
| Message Bus           | [MessageBus.cs](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Runtime/Core/MessageBus/MessageBus.cs)                  |
| Message Handler       | [MessageHandler.cs](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Runtime/Core/MessageHandler.cs)                     |
| Registration Token    | [MessageRegistrationToken.cs](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Runtime/Core/MessageRegistrationToken.cs) |
| Emit Helpers          | [MessageExtensions.cs](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Runtime/Core/Extensions/MessageExtensions.cs)    |
| Attributes            | [Attributes/](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Runtime/Core/Attributes)                                  |
