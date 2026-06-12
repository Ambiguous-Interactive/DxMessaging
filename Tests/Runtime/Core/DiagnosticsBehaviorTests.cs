#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine.TestTools;

    /// <summary>
    /// Pins emission-history semantics of bus diagnostics: the history buffer
    /// records every emission BEFORE interceptors run (record-everything
    /// semantics, so vetoed emissions still appear), and toggling
    /// <see cref="MessageBus.DiagnosticsMode"/> from inside a handler mid-dispatch
    /// never throws and takes effect on the NEXT emission. All tests use isolated
    /// buses so the global bus history is untouched.
    /// </summary>
    public sealed class DiagnosticsBehaviorTests : MessagingTestBase
    {
        private const int OwnerInstanceId = 21;
        private const int ContextInstanceId = 23;
        private const int TestBufferSize = 4;

        [UnityTest]
        public IEnumerator VetoedEmissionStillAppearsInEmissionHistory(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using (new DiagnosticsScope(messageBufferSize: TestBufferSize))
            {
                MessageBus bus = new() { DiagnosticsMode = true };
                MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();
                InstanceId context = new(ContextInstanceId);

                int handled = 0;
                int intercepted = 0;
                _ = RegisterHandler(scenario, token, context, () => ++handled);
                _ = RegisterVetoingInterceptor(scenario, token, () => ++intercepted);

                CyclicBuffer<MessageEmissionData> history = GetEmissionBuffer(bus);
                Assert.AreEqual(0, history.Count, "History must start empty.");

                Emit(scenario, context, bus);

                Assert.AreEqual(
                    1,
                    intercepted,
                    "Control failed: the vetoing interceptor must have run for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    0,
                    handled,
                    "The handler must not run for a vetoed emission for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    1,
                    history.Count,
                    "Record-everything semantics: a vetoed emission must still appear "
                        + "in the emission history (the buffer is appended before "
                        + "interceptors run) for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    ExpectedMessageType(scenario),
                    history[0].message.MessageType,
                    "The recorded emission must describe the vetoed message for " + "scenario {0}.",
                    scenario
                );

                token.Dispose();
                handler.active = false;
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator EnablingDiagnosticsModeInsideHandlerTakesEffectNextEmission()
        {
            using (new DiagnosticsScope(messageBufferSize: TestBufferSize))
            {
                MessageBus bus = new() { DiagnosticsMode = false };
                MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();

                int handled = 0;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++handled;
                        bus.DiagnosticsMode = true;
                    }
                );

                CyclicBuffer<MessageEmissionData> history = GetEmissionBuffer(bus);
                SimpleUntargetedMessage message = new();

                Assert.DoesNotThrow(
                    () => message.EmitUntargeted(bus),
                    "Toggling DiagnosticsMode from inside a handler must not throw."
                );
                Assert.AreEqual(1, handled, "Control failed: handler must have run.");
                Assert.AreEqual(
                    0,
                    history.Count,
                    "The emission that enabled diagnostics must not be recorded: the "
                        + "diagnostics check happens at the start of the emission, before "
                        + "handlers run."
                );

                message.EmitUntargeted(bus);
                Assert.AreEqual(2, handled, "Handler must run for the second emission.");
                Assert.AreEqual(
                    1,
                    history.Count,
                    "Diagnostics enabled mid-dispatch must take effect on the next emission."
                );

                token.Dispose();
                handler.active = false;
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator DisablingDiagnosticsModeInsideHandlerKeepsCurrentEmissionRecorded()
        {
            using (new DiagnosticsScope(messageBufferSize: TestBufferSize))
            {
                MessageBus bus = new() { DiagnosticsMode = true };
                MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();

                int handled = 0;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++handled;
                        bus.DiagnosticsMode = false;
                    }
                );

                CyclicBuffer<MessageEmissionData> history = GetEmissionBuffer(bus);
                SimpleUntargetedMessage message = new();

                Assert.DoesNotThrow(
                    () => message.EmitUntargeted(bus),
                    "Toggling DiagnosticsMode off from inside a handler must not throw."
                );
                Assert.AreEqual(1, handled, "Control failed: handler must have run.");
                Assert.AreEqual(
                    1,
                    history.Count,
                    "The emission that disabled diagnostics is still recorded: the buffer "
                        + "append happens before handlers run."
                );

                message.EmitUntargeted(bus);
                Assert.AreEqual(2, handled, "Handler must run for the second emission.");
                Assert.AreEqual(
                    1,
                    history.Count,
                    "Diagnostics disabled mid-dispatch must suppress recording of the "
                        + "next emission."
                );

                token.Dispose();
                handler.active = false;
            }

            yield break;
        }

        private static CyclicBuffer<MessageEmissionData> GetEmissionBuffer(MessageBus bus)
        {
            return bus._emissionBuffer;
        }

        private static Type ExpectedMessageType(MessageScenario scenario)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return typeof(SimpleUntargetedMessage);
                }
                case MessageKind.Targeted:
                {
                    return typeof(SimpleTargetedMessage);
                }
                case MessageKind.Broadcast:
                {
                    return typeof(SimpleBroadcastMessage);
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked()
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterVetoingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onInvoked();
                            return false;
                        }
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage __) =>
                        {
                            onInvoked();
                            return false;
                        }
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage __) =>
                        {
                            onInvoked();
                            return false;
                        }
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void Emit(MessageScenario scenario, InstanceId context, MessageBus bus)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context, bus);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context, bus);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}
#endif
