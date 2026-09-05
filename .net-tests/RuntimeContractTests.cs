using System;
using System.Linq;
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Messages;
using DxMessaging.Tests.Runtime;
using NUnit.Framework;

namespace DxMessaging.Tests.Net;

[TestFixture]
public sealed class RuntimeContractTests
{
    [Test]
    public void RuntimeHasNoUnityAssemblyDependency()
    {
        Assert.That(
            typeof(MessageBus).Assembly.GetReferencedAssemblies().Select(a => a.Name),
            Has.None.StartsWith("Unity"),
            "The plain .NET runtime must load without Unity assemblies."
        );
        Assert.That(
            typeof(MessageBus).Assembly.GetType(
                "DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider"
            ),
            Is.Null,
            "The ScriptableObject settings provider is Unity-only."
        );
    }

    [Test]
    public void DispatchAndTokenLifecycleWorkWithoutUnity(
        [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
            MessageScenario scenario,
        [Values(false, true)] bool untyped,
        [Values(false, true)] bool classMessage,
        [Values(false, true)] bool diagnostics
    )
    {
        if (classMessage)
        {
            Exercise(scenario, untyped, new ClassMessage(), diagnostics);
        }
        else
        {
            Exercise(scenario, untyped, new StructMessage(), diagnostics);
        }
    }

    private static void Exercise<T>(
        MessageScenario scenario,
        bool untyped,
        T message,
        bool diagnostics
    )
        where T : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
    {
        MessageBus bus = new MessageBus { DiagnosticsMode = diagnostics };
        MessageHandler handler = new MessageHandler(new InstanceId(1), bus) { active = true };
        using (LeakWatcher.WatchWithSlots(bus, label: scenario.ToString(), handler: handler))
        {
            int calls = 0;
            bool fail = false;
            void Receive(in T value)
            {
                Assert.That(
                    value,
                    Is.EqualTo(message),
                    "Payload fields must survive typed and untyped dispatch."
                );
                if (!typeof(T).IsValueType)
                {
                    Assert.That(
                        value,
                        Is.SameAs(message),
                        "Class-message identity must survive dispatch."
                    );
                }
                calls++;
                if (fail)
                {
                    throw new InvalidOperationException("callback failure");
                }
            }
            using (MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus))
            {
                InstanceId context = new InstanceId(int.MinValue);
                MessageRegistrationHandle registration = scenario.Kind switch
                {
                    MessageKind.Untargeted => ScenarioHarness.RegisterUntargeted<T>(
                        scenario,
                        token,
                        Receive
                    ),
                    MessageKind.Targeted => ScenarioHarness.RegisterTargeted<T>(
                        scenario,
                        token,
                        context,
                        Receive
                    ),
                    MessageKind.Broadcast => ScenarioHarness.RegisterBroadcast<T>(
                        scenario,
                        token,
                        context,
                        Receive
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
                void Emit()
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            if (untyped)
                            {
                                bus.UntypedUntargetedBroadcast(message);
                            }
                            else
                            {
                                bus.UntargetedBroadcast(ref message);
                            }
                            break;
                        case MessageKind.Targeted:
                            if (untyped)
                            {
                                bus.UntypedTargetedBroadcast(context, message);
                            }
                            else
                            {
                                bus.TargetedBroadcast(ref context, ref message);
                            }
                            break;
                        case MessageKind.Broadcast:
                            if (untyped)
                            {
                                bus.UntypedSourcedBroadcast(context, message);
                            }
                            else
                            {
                                bus.SourcedBroadcast(ref context, ref message);
                            }
                            break;
                    }
                }
                Emit();
                Assert.That(calls, Is.Zero, "Staged registrations must not receive messages.");
                token.Enable();
                Emit();
                Assert.That(calls, Is.EqualTo(1), "Enabled registration must dispatch once.");
                token.Disable();
                Emit();
                Assert.That(calls, Is.EqualTo(1), "Disabled registration must remain silent.");
                token.Enable();
                fail = true;
                Assert.Throws<InvalidOperationException>(Emit, "Callback failures must propagate.");
                fail = false;
                Emit();
                Assert.That(calls, Is.EqualTo(3), "Dispatch must recover after a callback throws.");
                token.RemoveRegistration(registration);
                token.RemoveRegistration(registration);
                Emit();
                Assert.That(
                    calls,
                    Is.EqualTo(3),
                    "Repeated removal must not restore a registration."
                );
            }
            bus.Trim(force: true);
        }
    }

    [Test]
    public void StaticResetPreservesPlainNetUsability(
        [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
            MessageScenario scenario
    )
    {
        DxMessagingStaticState.Reset();
        MessageBus bus = (MessageBus)MessageHandler.MessageBus;
        using (LeakWatcher.WatchWithSlots(bus, label: scenario.ToString()))
        {
            MessageHandler owner = new MessageHandler(new InstanceId(12), bus) { active = true };
            using (MessageRegistrationToken stale = MessageRegistrationToken.Create(owner, bus))
            using (
                MessageRegistrationToken replacement = MessageRegistrationToken.Create(owner, bus)
            )
            {
                int staleCalls = 0;
                int replacementCalls = 0;
                InstanceId context = new InstanceId(12);
                StructMessage payload = new StructMessage();
                void Old(in StructMessage value)
                {
                    staleCalls++;
                }
                void Current(in StructMessage value)
                {
                    replacementCalls++;
                }
                void Register(
                    MessageRegistrationToken token,
                    MessageHandler.FastHandler<StructMessage> callback
                )
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            ScenarioHarness.RegisterUntargeted(scenario, token, callback);
                            break;
                        case MessageKind.Targeted:
                            ScenarioHarness.RegisterTargeted(scenario, token, context, callback);
                            break;
                        case MessageKind.Broadcast:
                            ScenarioHarness.RegisterBroadcast(scenario, token, context, callback);
                            break;
                    }
                    token.Enable();
                }
                void Emit()
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            bus.UntargetedBroadcast(ref payload);
                            break;
                        case MessageKind.Targeted:
                            bus.TargetedBroadcast(ref context, ref payload);
                            break;
                        case MessageKind.Broadcast:
                            bus.SourcedBroadcast(ref context, ref payload);
                            break;
                    }
                }
                Register(stale, Old);
                Emit();
                Assert.That(
                    staleCalls,
                    Is.EqualTo(1),
                    "The global registration must be live before reset."
                );
                DxMessagingStaticState.Reset();
                DxMessagingStaticState.Reset();
                Assert.That(
                    MessageHandler.MessageBus,
                    Is.SameAs(bus),
                    "Reset must restore the original global bus."
                );
                Register(replacement, Current);
                stale.Dispose();
                Emit();
                Assert.That(
                    staleCalls,
                    Is.EqualTo(1),
                    "Reset must clear old global registrations."
                );
                Assert.That(
                    replacementCalls,
                    Is.EqualTo(1),
                    "A stale token must not remove the post-reset global registration."
                );
            }
            bus.Trim(force: true);
        }
    }

    private readonly struct StructMessage : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
    {
        public readonly int Number;
        public readonly string Text;

        public StructMessage()
        {
            Number = 173;
            Text = "struct payload";
        }

        public Type MessageType => typeof(StructMessage);
    }

    private sealed class ClassMessage : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
    {
        public int Number = 271;
        public string Text = "class payload";
        public Type MessageType => typeof(ClassMessage);
    }
}
