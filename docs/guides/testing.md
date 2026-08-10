# Testing with DxMessaging

[<- Back to Index](../getting-started/index.md) | [Patterns](patterns.md) | [Runtime Configuration](../advanced/runtime-configuration.md)

---

**You're here because:** You want to write reliable, isolated tests for systems that use DxMessaging.

## Key Testing Principles

1. **Use isolated MessageBus instances** - Avoid global state pollution between tests
1. **Manage token lifecycles explicitly** - Enable/disable tokens to control when handlers are active
1. **Clean up spawned objects** - Destroy test GameObjects to prevent registration leaks

---

## 1) Isolated MessageBus for Unit Tests

The global `MessageHandler.MessageBus` is shared across your application. For unit tests, create a dedicated `MessageBus` instance to ensure complete isolation.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using NUnit.Framework;
using UnityEngine;

[Test]
public void IsolatedBusDoesNotAffectGlobalBus()
{
    // Create isolated bus and handler
    MessageBus testBus = new();
    GameObject testObject = new("TestObject");
    MessageHandler handler = new(testObject) { active = true };
    MessageRegistrationToken token = MessageRegistrationToken.Create(handler, testBus);
    token.Enable();

    int localCount = 0;
    _ = token.RegisterUntargeted<MyMessage>(_ => localCount++);

    // Emit to isolated bus only
    MyMessage msg = new();
    msg.EmitUntargeted(testBus);

    Assert.AreEqual(1, localCount, "Local handler should receive message.");

    // Cleanup
    token.UnregisterAll();
    UnityEngine.Object.DestroyImmediate(testObject);
}
```

---

## 2) Test Base Class Pattern

Create a reusable base class for common setup/teardown.

```csharp
public abstract class MessagingTestBase
{
    protected readonly List<GameObject> _spawned = new();

    [TearDown]
    public virtual void Cleanup()
    {
        foreach (GameObject spawned in _spawned)
            if (spawned != null) UnityEngine.Object.Destroy(spawned);
        _spawned.Clear();
    }

    [UnitySetUp]
    public virtual IEnumerator UnitySetup()
    {
        IMessageBus bus = MessageHandler.MessageBus;
        while (bus.RegisteredUntargeted != 0 || bus.RegisteredTargeted != 0)
            yield return null;
    }

    protected MessageRegistrationToken GetToken(MessageAwareComponent c) => c.Token;
}
```

---

## 3) Verifying Message Emission and Handling

Use counters or captured values to verify messages are emitted and handled correctly.

```csharp
[UnityTest]
public IEnumerator TargetedMessageReachesCorrectTarget()
{
    GameObject target = new("Target", typeof(EmptyMessageAwareComponent));
    GameObject nonTarget = new("NonTarget", typeof(EmptyMessageAwareComponent));
    _spawned.Add(target);
    _spawned.Add(nonTarget);

    MessageRegistrationToken targetToken = GetToken(target.GetComponent<EmptyMessageAwareComponent>());
    MessageRegistrationToken nonTargetToken = GetToken(nonTarget.GetComponent<EmptyMessageAwareComponent>());

    int targetReceived = 0;
    int nonTargetReceived = 0;

    _ = targetToken.RegisterGameObjectTargeted<DamageMessage>(target, _ => targetReceived++);
    _ = nonTargetToken.RegisterGameObjectTargeted<DamageMessage>(nonTarget, _ => nonTargetReceived++);

    DamageMessage msg = new() { amount = 10 };
    msg.EmitGameObjectTargeted(target);

    Assert.AreEqual(1, targetReceived, "Target should receive message.");
    Assert.AreEqual(0, nonTargetReceived, "Non-target should NOT receive message.");
    yield break;
}
```

---

## 4) Testing Interceptors

Interceptors can cancel or modify messages. Test both scenarios.

```csharp
[UnityTest]
public IEnumerator InterceptorCanCancelMessage()
{
    GameObject host = new("Host", typeof(EmptyMessageAwareComponent));
    _spawned.Add(host);
    MessageRegistrationToken token = GetToken(host.GetComponent<EmptyMessageAwareComponent>());

    int handlerCalled = 0;
    _ = token.RegisterUntargeted<TestMessage>(_ => handlerCalled++);
    _ = token.RegisterUntargetedInterceptor((ref TestMessage _) => false); // Cancel

    TestMessage msg = new();
    msg.EmitUntargeted();

    Assert.AreEqual(0, handlerCalled, "Handler must not run when interceptor cancels.");
    yield break;
}

[UnityTest]
public IEnumerator InterceptorCanModifyMessage()
{
    GameObject host = new("Host", typeof(EmptyMessageAwareComponent));
    _spawned.Add(host);
    MessageRegistrationToken token = GetToken(host.GetComponent<EmptyMessageAwareComponent>());

    int receivedAmount = 0;
    _ = token.RegisterUntargeted<DamageMessage>(msg => receivedAmount = msg.amount);
    _ = token.RegisterUntargetedInterceptor((ref DamageMessage msg) =>
    {
        if (msg.amount > 100) msg = new DamageMessage { amount = 100 };
        return true;
    });

    DamageMessage msg = new() { amount = 999 };
    msg.EmitUntargeted();

    Assert.AreEqual(100, receivedAmount, "Interceptor should clamp damage to 100.");
    yield break;
}
```

---

## 5) Testing Component Lifecycle

Verify handlers respect Unity's enable/disable lifecycle.

```csharp
[UnityTest]
public IEnumerator DisabledComponentDoesNotReceiveMessages()
{
    GameObject host = new("Host", typeof(EmptyMessageAwareComponent));
    _spawned.Add(host);
    EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
    MessageRegistrationToken token = GetToken(component);

    int count = 0;
    _ = token.RegisterUntargeted<TestMessage>(_ => count++);

    TestMessage msg1 = new();
    msg1.EmitUntargeted();
    Assert.AreEqual(1, count, "Enabled component should receive message.");

    component.enabled = false;
    yield return null;

    TestMessage msg2 = new();
    msg2.EmitUntargeted();
    Assert.AreEqual(1, count, "Disabled component should NOT receive message.");

    component.enabled = true;
    yield return null;

    TestMessage msg3 = new();
    msg3.EmitUntargeted();
    Assert.AreEqual(2, count, "Re-enabled component should receive message.");
}
```

---

## 6) Integration Testing

For integration tests, use the global bus with proper cleanup. Create GameObjects with components, emit messages, and verify system-wide behavior across multiple components.

---

## Best Practices

| Practice                                    | Why                                                           |
| ------------------------------------------- | ------------------------------------------------------------- |
| Use isolated `MessageBus` for unit tests    | Prevents test pollution and flakiness                         |
| Track spawned objects in a list             | Ensures cleanup even if test fails                            |
| Use `yield return null` after state changes | Allows Unity lifecycle methods to execute                     |
| Test both positive and negative cases       | Verify messages reach correct targets AND miss incorrect ones |
| Use expressive assertion messages           | Makes test failures easier to diagnose                        |

---

## Quick Reference: Test Message Setup

```csharp
// Test messages
[DxUntargetedMessage]
public partial struct TestMessage { }

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct DamageMessage { public readonly int amount; }

// Minimal test component - registers no handlers by default
public sealed class EmptyMessageAwareComponent : MessageAwareComponent { }
```

---

## See Also

- **[Patterns](patterns.md)** - Pattern #6 (Local Bus Islands) and #12 (Testing)
- **[Runtime Configuration](../advanced/runtime-configuration.md)** - MessageBus configuration options
- **[Back to Documentation Hub](../getting-started/index.md)** - Browse all docs
