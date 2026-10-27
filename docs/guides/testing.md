# Testing with DxMessaging

[Back to Index](../getting-started/index.md) | [Patterns](patterns.md) | [runtime configuration](../advanced/runtime-configuration.md)

Test message delivery and registration ownership separately. Use a local bus for plain C# owners,
and a scoped bus override when a Unity component resolves the global bus. Run Unity messaging tests
on the main thread; separate buses still share runtime settings and pools.

## Plain C# owners

This synchronous NUnit test uses no Unity object. Save it as `PlainMessageTests.cs` in a test assembly.
The token starts disabled, so enable it after staging the handler. The `using` block disposes the
registration even when an assertion fails.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Messages;
using NUnit.Framework;

public readonly struct TestPing : IUntargetedMessage<TestPing> { }

public sealed class PlainMessageTests
{
    [Test]
    public void LocalSubscriptionDeliversAndCleansUp()
    {
        MessageBus bus = new MessageBus();
        MessageHandler handler = new MessageHandler(new InstanceId(1), bus) { active = true };
        int received = 0;
        TestPing message = new TestPing();

        using (MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus))
        {
            _ = token.RegisterUntargeted<TestPing>((in TestPing ping) => received++);
            token.Enable();
            message.Emit(bus);
            Assert.That(received, Is.EqualTo(1));
        }

        Assert.That(bus.RegisteredUntargeted, Is.Zero);
        message.Emit(bus);
        Assert.That(received, Is.EqualTo(1), "Disposal must remove the subscription.");
    }
}
```

## Unity component lifecycle

Put these scripts and `TestPing` above in the same PlayMode test assembly. The receiver registers
through its inherited token and keeps the required base call. Save it as `TestPingReceiver.cs`:

```csharp
using DxMessaging.Unity;

public sealed class TestPingReceiver : MessageAwareComponent
{
    public int Received { get; private set; }

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<TestPing>(OnPing);
    }

    private void OnPing(in TestPing message)
    {
        Received++;
    }
}
```

Save the fixture as `ComponentLifecycleTests.cs`. The synchronous test checks enabled, disabled,
and re-enabled delivery. The receiver uses the default `MessageRegistrationTiedToEnableStatus = true`.
The fixture restores the previous global bus after destroying its GameObject:

```csharp
using System;
using System.Collections;
using DxMessaging.Core;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class ComponentLifecycleTests
{
    private MessageBus _bus;
    private IDisposable _busScope;
    private GameObject _host;

    [SetUp]
    public void SetUp()
    {
        _bus = new MessageBus();
        _busScope = MessageHandler.OverrideGlobalMessageBus(_bus);
    }

    [Test]
    public void ReceiverTracksEnabledState()
    {
        _host = new GameObject("TestPingReceiver");
        TestPingReceiver receiver = _host.AddComponent<TestPingReceiver>();
        TestPing message = new TestPing();

        message.Emit(_bus);
        Assert.That(receiver.Received, Is.EqualTo(1));

        receiver.enabled = false;
        message.Emit(_bus);
        Assert.That(receiver.Received, Is.EqualTo(1));

        receiver.enabled = true;
        message.Emit(_bus);
        Assert.That(receiver.Received, Is.EqualTo(2));
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        try
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
            yield return null;
            _host = null;

            Assert.That(_bus.RegisteredUntargeted, Is.Zero);
            Assert.That(_bus.RegisteredTargeted, Is.Zero);
            Assert.That(_bus.RegisteredBroadcast, Is.Zero);
            Assert.That(_bus.RegisteredInterceptors, Is.Zero);
            Assert.That(_bus.RegisteredPostProcessors, Is.Zero);
            Assert.That(_bus.RegisteredGlobalAcceptAll, Is.Zero);
        }
        finally
        {
            _busScope.Dispose();
        }
    }
}
```

Unity's [Destroy contract](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Object.Destroy.html)
defers destruction until after the current Update loop. The
[Unity teardown](https://docs.unity3d.com/Packages/com.unity.test-framework@1.4/api/UnityEngine.TestTools.UnityTearDownAttribute.html)
yields once for that destruction,
then asserts the six registration counters. It does not poll until counts reach zero or reset the bus
to hide live registrations. A failed cleanup assertion still runs the `finally` block that restores
the previous bus.

Use `[Test]` for synchronous registration and delivery checks. Use `[UnityTest]` only when the test
must observe a later frame, scene transition, coroutine, or deferred destruction. Track every owned
GameObject and token so teardown runs even after an assertion fails.

## Extend the checks

- For [targeted messages](patterns.md#2-directed-commands-targeted), verify both the intended
  receiver and a different target that must not receive the message.
- For [broadcast messages](patterns.md#3-observability-broadcast), verify the emitter's source and
  the intended source-bound and all-source listeners.
- For [interceptors](patterns.md#4-validation-and-normalization-interceptors), test accepted,
  cancelled, and modified messages, including whether handlers and post-processors run.
- For cleanup and reentrancy, assert observable delivery and the registrations owned by the test.
  Avoid sleeps, retries, and unbounded waits for another fixture's state.
- Restore any diagnostics or runtime settings changed by a fixture. A local bus isolates routing;
  it does not isolate those shared settings.

## See Also

- [Subscription ownership](../concepts/listening-patterns.md)
- [Unity integration and lifecycle](unity-integration.md)
- [runtime configuration](../advanced/runtime-configuration.md)
