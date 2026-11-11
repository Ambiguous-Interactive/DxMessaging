# Subscription ownership

> **Added in 4.0.0.** Use registration groups, source retargeting, and explicit state handlers with an existing token.

A token owns its registrations for its whole lifecycle. A registration group owns a selected subset of those handles.
Use a group when a pooled object changes parent or replaces one set of subscriptions while keeping its other subscriptions.

| Existing application code                                              | API to use                                                            |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------- |
| Store handles in a list and remove each handle when the parent changes | Enroll each handle with `group.Add(...)`, then call `group.Dispose()` |
| Remove a source-bound handle and register its callback again           | Call `token.RetargetSource(handle, newSource)`                        |
| Capture an owner or local value in a registration lambda               | Pass the state explicitly and use a static callback                   |

## Keep a selected set together

Call `token.CreateGroup()` and enroll the handles returned by the token. Enrollment does not enable the token.
The token's existing owner still calls `Enable`, `Disable`, or its lifecycle helper.
Disposing a group removes its members from both active subscriptions and future token replays.
It leaves the token's other registrations active.

This complete example replaces a parent's subscriptions, retains their cleanup owner before registration starts,
and uses static callbacks with explicit state:

```csharp
namespace SubscriptionOwnershipExample
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;

    public sealed class ParentSubscriptions : IDisposable
    {
        private readonly MessageRegistrationToken _token;
        private MessageRegistrationGroup _parentGroup;
        private MessageRegistrationHandle _changed;

        public int Revision { get; private set; }
        public int HiddenCount { get; private set; }

        public ParentSubscriptions(MessageRegistrationToken token)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
        }

        public void BindParent(InstanceId parent)
        {
            Dispose();
            _parentGroup = _token.CreateGroup();
            _changed = _parentGroup.Add(
                _token.RegisterBroadcast<ParentChanged, ParentSubscriptions>(
                    parent, this,
                    static (ParentSubscriptions owner, in ParentChanged message) =>
                        owner.Revision = message.Revision));
            _parentGroup.Add(
                _token.RegisterBroadcast<ParentHidden, ParentSubscriptions>(
                    parent, this,
                    static (ParentSubscriptions owner, in ParentHidden message) =>
                        ++owner.HiddenCount));
        }

        public bool MoveChangedHandler(InstanceId source)
        {
            return _token.RetargetSource(_changed, source);
        }

        public void Dispose()
        {
            if (_parentGroup == null)
            {
                return;
            }
            _parentGroup.Dispose();
            if (_parentGroup.Count != 0)
            {
                throw new InvalidOperationException(
                    "Parent cleanup is still pending. Finish it before binding another parent.");
            }
            _parentGroup = null;
        }
    }

    public readonly struct ParentChanged : IBroadcastMessage
    {
        public readonly int Revision;
        public ParentChanged(int revision) => Revision = revision;
        public Type MessageType => typeof(ParentChanged);
    }

    public readonly struct ParentHidden : IBroadcastMessage
    {
        public Type MessageType => typeof(ParentHidden);
    }
}
```

The group field remains reachable if a later registration or cleanup fails.
Call `Dispose` again after resolving a cleanup failure. Replace the group only after its `Count` reaches zero.
Cleanup visits every member even if another removal throws. One failure is rethrown; multiple failures produce an `AggregateException`.
A reentrant callback can accept cleanup after a disposal pass starts, so a pass can return with a nonzero `Count`.
That includes disposal during a registration callback, before the bus returns its cleanup handle.
The next pass removes that retained obligation. Draining the final cleanup clears diagnostics
when no staged registrations or other cleanup remain.

Duplicate enrollment has no effect while the handle belongs to the token.
A handle can belong to multiple groups; removing it from either group cancels that registration.
Other groups then hold a harmless stale handle. A retired handle cannot remove a replacement that reuses its slot.
Enrollment rejects foreign or missing handles. Once disposal starts, the group rejects further enrollment, including after a failed pass.

## Move one source-bound handle

`RetargetSource` moves a broadcast handler or post-processor registered for one source.
It accepts an `InstanceId` or a Unity `GameObject`; `InstanceId` also converts a `Component`.
A null `GameObject` selects the empty identity, following the normal `InstanceId` conversion.
The sample's `MoveChangedHandler` moves only its changed-message handle; the hidden-message handle stays with its current source.
To replace an entire set, finish group cleanup and build a new group.

The move keeps the handle, callback, explicit state, priority, and diagnostic counts.
A disabled registration changes staging only. An active registration moves on its current bus.
It joins the end of the new source's equal-priority registration order.
An outer dispatch finishes its frozen callbacks with their original diagnostic sources;
nested and later emissions observe the new route. Moving to the same source does nothing.

The method returns `false` for an untargeted, targeted, all-source, stale, foreign, or removed handle.
It rejects moving the same registration while a custom bus is still accepting it or while another source move owns it.
A later queued registration can change its staged source during token replay.

Failed old-source cleanup leaves the old source and handle retryable.
Failed new-source activation restores old staging and attempts to register it again.
Recovery joins the old source's current equal-priority order; it cannot restore a removed insertion position.
If recovery also fails, both failures appear in an `AggregateException`.
Reentrant disable or removal takes precedence over activation.
If a callback accepts cleanup after a lifecycle cancellation, finish that cleanup through the token, handle, or group before enabling again.

## Pass state to a static callback

Explicit-state overloads cover untargeted, targeted, source-bound broadcast, all-target, and all-source handlers and post-processors.
Their type arguments are the message type followed by the state type.
Scalar callbacks receive `(state, in message)`. All-target and all-source callbacks receive `(state, in context, in message)`.

Struct state is copied when registering and passed by value on each delivery.
Changing the original struct or a callback's copy does not change the stored value.
Reference state stays shared, including changes made after registration. Null reference state is valid.
A null callback throws at registration time, including on a disabled token.
Separate registrations retain separate bindings even when they use the same static callback.

Registration allocates a state adapter and delegate. Groups also allocate when created and populated.
These APIs do not promise allocation-free registration or source moves.
The state adapter adds no managed allocation to steady-state dispatch with diagnostics disabled.
Prefer a static method or static lambda so the callback does not capture additional application state.
Token replay and bus retargeting reuse the stored state.

## See also

- [Unity lifecycle ownership](unity-integration.md#composable-lifecycle-ownership-400)
- [Registration builders and leases](../advanced/registration-builders.md)
- [Target and source identities](../concepts/targeting-and-context.md)
- [Memory reclamation](memory-reclamation.md)
