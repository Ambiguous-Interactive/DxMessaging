#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>
    /// Pins the post-<see cref="MessageRegistrationToken.Dispose"/> contract of
    /// <see cref="MessageRegistrationToken"/>. Dispose delegates to
    /// <see cref="MessageRegistrationToken.UnregisterAll"/>: every live handler is
    /// deregistered, all staged registrations are cleared, and the token is left
    /// disabled. The token carries no disposed flag, so it remains technically
    /// reusable: new registrations after Dispose are accepted without throwing and
    /// a subsequent <see cref="MessageRegistrationToken.Enable"/> activates them.
    /// These tests codify that reusable-after-Dispose behavior; if a hard "throw
    /// after Dispose" contract is ever introduced deliberately, update these pins
    /// alongside the source change.
    /// </summary>
    [TestFixture]
    public sealed class TokenDisposeReuseTests
    {
        private const int OwnerInstanceId = 11;
        private const int ContextInstanceId = 17;

        [SetUp]
        public void ResetBeforeTest()
        {
            DxMessagingStaticState.Reset();
        }

        [TearDown]
        public void ResetAfterTest()
        {
            DxMessagingStaticState.Reset();
        }

        [Test]
        public void DisposeUnregistersAllHandlersAndDisablesToken(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DisposeUnregistersAllHandlersAndDisablesToken)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                _ = RegisterHandler(scenario, scope.Token, context, () => ++handled);
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Control failed: handler must fire before Dispose for scenario {0}.",
                    scenario
                );

                scope.Token.Dispose();
                Assert.IsFalse(
                    scope.Token.Enabled,
                    "Dispose must leave the token disabled (UnregisterAll semantics)."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Handlers must not fire after Dispose for scenario {0}.",
                    scenario
                );
            }
        }

        [Test]
        public void RegistrationAfterDisposeIsAcceptedAndEnableActivatesIt(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(RegistrationAfterDisposeIsAcceptedAndEnableActivatesIt)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                scope.Token.Dispose();
                Assert.IsFalse(scope.Token.Enabled, "Dispose must disable the token.");

                // Pinned: the token has no disposed flag, so registration after
                // Dispose is accepted (staged, not active) rather than throwing.
                Assert.DoesNotThrow(
                    () => _ = RegisterHandler(scenario, scope.Token, context, () => ++handled),
                    "Pinned behavior: registering on a disposed token must not throw "
                        + "(the token is reusable)."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    0,
                    handled,
                    "A registration staged after Dispose must stay inactive until Enable "
                        + "for scenario {0}.",
                    scenario
                );

                scope.Token.Enable();
                Assert.IsTrue(
                    scope.Token.Enabled,
                    "Enable after Dispose must re-enable the token."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Enable after Dispose must activate registrations staged post-Dispose "
                        + "for scenario {0}.",
                    scenario
                );

                scope.Token.Dispose();
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "The second Dispose must deregister the post-Dispose registration "
                        + "for scenario {0}.",
                    scenario
                );
            }
        }

        [Test]
        public void DoubleDisposeIsHarmless()
        {
            using TokenScope scope = TokenScope.Create();
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DoubleDisposeIsHarmless)
                )
            )
            {
                _ = scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++handled
                );
                SimpleUntargetedMessage message = new();
                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: handler must fire before Dispose.");

                scope.Token.Dispose();
                Assert.DoesNotThrow(
                    scope.Token.Dispose,
                    "Disposing an already-disposed token must be a harmless no-op."
                );
                Assert.IsFalse(
                    scope.Token.Enabled,
                    "Token must stay disabled after double Dispose."
                );

                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "No handler may fire after double Dispose.");
            }
        }

        [Test]
        public void EnableAfterDisposeWithoutNewRegistrationsRestoresNothing()
        {
            using TokenScope scope = TokenScope.Create();
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(EnableAfterDisposeWithoutNewRegistrationsRestoresNothing)
                )
            )
            {
                _ = scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++handled
                );
                SimpleUntargetedMessage message = new();
                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: handler must fire before Dispose.");

                scope.Token.Dispose();
                scope.Token.Enable();
                Assert.IsTrue(
                    scope.Token.Enabled,
                    "Enable after Dispose must report the token as enabled."
                );

                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(
                    1,
                    handled,
                    "Dispose clears staged registrations; a bare Enable must not resurrect "
                        + "the pre-Dispose handler."
                );
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

        private static void Emit(MessageScenario scenario, InstanceId context, BusType bus)
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

        /// <summary>
        /// Pairs a fresh isolated <see cref="BusType"/>, an active
        /// <see cref="MessageHandler"/>, and an enabled
        /// <see cref="MessageRegistrationToken"/> so each test starts from the same
        /// clean state without touching the global bus.
        /// </summary>
        private sealed class TokenScope : IDisposable
        {
            private bool _disposed;

            internal BusType Bus { get; }

            internal MessageHandler Handler { get; }

            internal MessageRegistrationToken Token { get; }

            private TokenScope(BusType bus, MessageHandler handler, MessageRegistrationToken token)
            {
                Bus = bus;
                Handler = handler;
                Token = token;
            }

            internal static TokenScope Create()
            {
                BusType bus = new();
                MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();
                return new TokenScope(bus, handler, token);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Token.Dispose();
                Handler.active = false;
            }
        }
    }
}
#endif
