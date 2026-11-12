# Current state for late listeners

**Added in 4.0.0.** Explicit sticky publication retains the latest offered untargeted message
on one bus. A new token callback can opt into that state and then receive future messages.
Use it for readiness or current settings when a listener can start after the publisher.
Ordinary emissions keep their existing behavior.

## Publish and read state

Import `DxMessaging.Core.Extensions` when the bus is held as `IMessageBus`.
The built-in `MessageBus` implements the optional `IStickyMessageBus` capability.
Other bus implementations must implement that capability to support these operations.

```csharp
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Messages;

public readonly struct SettingsReady : IUntargetedMessage<SettingsReady>
{
    public readonly Settings Settings;
    public SettingsReady(Settings settings) => Settings = settings;
}

public sealed class Settings
{
    public int Volume;
}

public static class SettingsPublication
{
    public static void Publish(IMessageBus bus, Settings settings)
    {
        SettingsReady message = new SettingsReady(settings);
        bus.EmitSticky(ref message);
    }

    public static bool Read(IMessageBus bus, out SettingsReady message)
    {
        return bus.TryGetLatest(out message);
    }

    public static void EndService(IMessageBus bus)
    {
        bus.ClearLatest<SettingsReady>();
    }
}
```

`TryGetLatest<T>` returns false and sets its output to default when that type has no retained
value. A published default struct or null reference returns true. Retention is separate for
each bus and message type. Ordinary emission does not replace the retained value.

## Replace the readiness branch

The following before/after examples use the same settings and preserve a pending user edit
made before the service becomes ready. The caller owns the token's lifecycle in both examples.

Before, application code reads the service directly or stages a ready callback:

```csharp
using DxMessaging.Core;

public sealed class ReadySettingsView
{
    private Settings _settings;
    private bool _dirty;
    private int _pending;

    public int InitializationCalls { get; private set; }
    public int DisplayedVolume { get; private set; }

    public ReadySettingsView(MessageRegistrationToken token, Settings current)
    {
        if (current != null)
        {
            Initialize(current);
        }
        else
        {
            token.RegisterUntargeted<SettingsReady>(message => Initialize(message.Settings));
        }
    }

    public void SetVolume(int value)
    {
        if (_settings == null)
        {
            _pending = value;
            _dirty = true;
        }
        else
        {
            _settings.Volume = value;
            DisplayedVolume = value;
        }
    }

    private void Initialize(Settings settings)
    {
        ++InitializationCalls;
        _settings = settings;
        if (_dirty)
        {
            _settings.Volume = _pending;
            _dirty = false;
        }
        DisplayedVolume = _settings.Volume;
    }
}
```

After, register once during setup. The publisher uses `EmitSticky` so activation can read
current state. Keep registration out of repeated `OnEnable` calls; let the existing owner
enable and disable the same token.

```csharp
using DxMessaging.Core;

public sealed class ReadySettingsView
{
    private Settings _settings;
    private bool _dirty;
    private int _pending;

    public int InitializationCalls { get; private set; }
    public int DisplayedVolume { get; private set; }

    public ReadySettingsView(MessageRegistrationToken token)
    {
        token.RegisterUntargeted<SettingsReady, ReadySettingsView>(
            this,
            static (ReadySettingsView view, in SettingsReady message) => view.Initialize(message.Settings),
            replayLatest: true);
    }

    public void SetVolume(int value)
    {
        if (_settings == null)
        {
            _pending = value;
            _dirty = true;
        }
        else
        {
            _settings.Volume = value;
            DisplayedVolume = value;
        }
    }

    private void Initialize(Settings settings)
    {
        ++InitializationCalls;
        _settings = settings;
        if (_dirty)
        {
            _settings.Volume = _pending;
            _dirty = false;
        }
        DisplayedVolume = _settings.Volume;
    }
}
```

The same required `replayLatest` argument is available for `Action<T>` and readonly
`MessageHandler.FastHandler<T>` callbacks. Existing overloads and `replayLatest: false`
do not replay.

## Ordering and ownership

- `EmitSticky` stores the offered snapshot before normal dispatch. Interceptor edits,
  cancellation and callback exceptions do not undo it. A nested sticky publication wins.
  Clearing or resetting during delivery takes effect immediately; the outer call does not restore it.
- Replay invokes only the newly accepted callback after its cleanup is attached to the token.
  It does not run interceptors, other listeners or post-processors, and does not create a new bus emission.
  Token callback diagnostics include a successful replay.
- Disabled staging waits for `Enable`. Repeated `Enable` is inert. Disable/enable and active
  rebinding outside a replay callback activate again and can replay the current value.
  `MessageHandler.active` still gates delivery. An inactive handler skips replay; after making
  it active, use a new token activation to replay, or wait for the next normal emission.
- Registrations added by a replay callback join the owning activation after previously staged
  callbacks. Nested additions are activated before `Enable` completes, subject to removal,
  lifecycle cancellation, and the same rollback rules.
- Removal, group disposal, token disposal and disable are allowed inside replay. Disable or
  disposal cancels the remaining activation queue. A throwing immediate replay retires its new
  registration when cleanup succeeds. A failed `Enable` rolls back new active subscriptions while
  retaining staging for retry. Failed cleanup stays token-owned; retry `Disable` or `Dispose`.
- `RebindActive` from inside replay throws before changing the bus. Defer it until the callback
  returns. `PreserveRegistrations` can select a bus for future registrations while the current
  callback stays bound to its actual accepted bus.
- Unsupported custom buses throw `NotSupportedException` for these operations and replay opt-in.
  Disabled-token capability validation occurs at activation. Ordinary custom-bus registration still works.

## Lifetime and cost

Value messages copy their fields; referenced objects remain shared. A message containing a service,
scene object or large graph intentionally retains it until replacement, `ClearLatest<T>`, or reset
of that built-in bus. Clear state when the owning service or scene ends. `Trim`, including forced
trim, preserves present state. Clearing the final type releases the dictionary immediately.
`DxMessagingStaticState.Reset()` clears the built-in default bus and preserves a custom global bus's state.

The first publication for a bus/type allocates typed storage. Registration and replay setup also
allocate. Warmed publication and lookup have separate zero-allocation tests; ordinary dispatch does
not gain a retained-state lookup. These tests do not establish a throughput improvement.

See [subscription ownership](subscription-ownership.md) for groups and lifecycle owners, and
[memory reclamation](memory-reclamation.md) for empty dispatch caches.
