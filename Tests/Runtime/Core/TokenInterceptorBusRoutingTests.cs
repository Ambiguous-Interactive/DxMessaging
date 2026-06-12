#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>
    /// Pins bus routing for interceptors registered through a
    /// <see cref="MessageRegistrationToken"/> created via
    /// <see cref="MessageRegistrationToken.Create(MessageHandler, IMessageBus)"/>
    /// with a custom bus: <see cref="MessageRegistrationToken.RegisterUntargetedInterceptor{T}"/>,
    /// <see cref="MessageRegistrationToken.RegisterTargetedInterceptor{T}"/>, and
    /// <see cref="MessageRegistrationToken.RegisterBroadcastInterceptor{T}"/> must
    /// forward the token's bound bus exactly like the handler/post-processor
    /// registration methods do. An interceptor registered through a custom-bus
    /// token must intercept emissions on the CUSTOM bus and must NOT intercept
    /// emissions on the global bus. The same routing applies to registrations
    /// staged while the token is disabled (they land on the token's bus at
    /// <see cref="MessageRegistrationToken.Enable"/> time) and interacts with
    /// <see cref="MessageRegistrationToken.RetargetMessageBus"/> (staged and
    /// active interceptors follow the retargeted bus).
    /// </summary>
    /// <remarks>
    /// The <see cref="MessageHandler"/> in these tests is deliberately created
    /// WITHOUT a default bus so the token's bus binding is the only thing that
    /// can route registrations to the custom bus. This mirrors the production
    /// defect path: when the token fails to forward its bus, the registration
    /// silently lands on the global bus.
    /// </remarks>
    [TestFixture]
    public sealed class TokenInterceptorBusRoutingTests
    {
        private const int OwnerInstanceId = 23;
        private const int ContextInstanceId = 29;

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
        public void InterceptorRegisteredViaTokenRunsOnTokenBusOnly(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            InstanceId context = new(ContextInstanceId);
            int intercepted = 0;

            using (
                LeakWatcher customWatcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Custom"
                )
            )
            using (
                LeakWatcher globalWatcher = new(
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Global"
                )
            )
            {
                MessageRegistrationHandle handle = RegisterCountingInterceptor(
                    scenario,
                    scope.Token,
                    () => ++intercepted
                );

                EmitForKind(scenario, scope.Bus, context);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "[{0}] An interceptor registered through a custom-bus token must "
                        + "intercept emissions on the token's bus.",
                    scenario.Kind
                );

                EmitForKind(scenario, messageBus: null, context);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "[{0}] An interceptor registered through a custom-bus token must "
                        + "NOT intercept emissions on the global bus.",
                    scenario.Kind
                );

                scope.Token.RemoveRegistration(handle);
            }
        }

        private static MessageRegistrationHandle RegisterCountingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onIntercepted
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onIntercepted();
                            return true;
                        }
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            onIntercepted();
                            return true;
                        }
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            onIntercepted();
                            return true;
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

        private static void EmitForKind(
            MessageScenario scenario,
            IMessageBus messageBus,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    if (messageBus != null)
                    {
                        message.EmitUntargeted(messageBus);
                    }
                    else
                    {
                        message.EmitUntargeted();
                    }
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    if (messageBus != null)
                    {
                        message.EmitTargeted(context, messageBus);
                    }
                    else
                    {
                        message.EmitTargeted(context);
                    }
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    if (messageBus != null)
                    {
                        message.EmitBroadcast(context, messageBus);
                    }
                    else
                    {
                        message.EmitBroadcast(context);
                    }
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

        [Test]
        public void InterceptorsStagedWhileDisabledLandOnTokenBusAtEnable()
        {
            using TokenScope scope = TokenScope.Create(enable: false);
            InstanceId context = new(ContextInstanceId);
            int untargetedIntercepted = 0;
            int targetedIntercepted = 0;
            int broadcastIntercepted = 0;

            using (
                LeakWatcher customWatcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(InterceptorsStagedWhileDisabledLandOnTokenBusAtEnable) + "_Custom"
                )
            )
            using (
                LeakWatcher globalWatcher = new(
                    throwOnLeak: true,
                    label: nameof(InterceptorsStagedWhileDisabledLandOnTokenBusAtEnable) + "_Global"
                )
            )
            {
                _ = scope.Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++untargetedIntercepted;
                        return true;
                    }
                );
                _ = scope.Token.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                    (ref InstanceId _, ref SimpleTargetedMessage _) =>
                    {
                        ++targetedIntercepted;
                        return true;
                    }
                );
                _ = scope.Token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                    (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                    {
                        ++broadcastIntercepted;
                        return true;
                    }
                );

                EmitAllKinds(scope.Bus, context);
                Assert.AreEqual(
                    0,
                    untargetedIntercepted + targetedIntercepted + broadcastIntercepted,
                    "Staged interceptors must stay inert while the token is disabled."
                );

                scope.Token.Enable();

                EmitAllKinds(scope.Bus, context);
                Assert.AreEqual(
                    1,
                    untargetedIntercepted,
                    "An untargeted interceptor staged while disabled must land on the "
                        + "token's bus at Enable() time."
                );
                Assert.AreEqual(
                    1,
                    targetedIntercepted,
                    "A targeted interceptor staged while disabled must land on the "
                        + "token's bus at Enable() time."
                );
                Assert.AreEqual(
                    1,
                    broadcastIntercepted,
                    "A broadcast interceptor staged while disabled must land on the "
                        + "token's bus at Enable() time."
                );

                EmitAllKinds(messageBus: null, context);
                Assert.AreEqual(
                    3,
                    untargetedIntercepted + targetedIntercepted + broadcastIntercepted,
                    "Interceptors activated by Enable() on a custom-bus token must "
                        + "NOT intercept emissions on the global bus."
                );

                scope.Token.UnregisterAll();
            }
        }

        [Test]
        public void RetargetWhileDisabledReroutesStagedInterceptorsToNewBus()
        {
            BusType originalBus = new();
            BusType retargetedBus = new();
            using TokenScope scope = TokenScope.Create(enable: false, bus: originalBus);
            int intercepted = 0;

            using (
                LeakWatcher originalWatcher = new(
                    bus: originalBus,
                    throwOnLeak: true,
                    label: nameof(RetargetWhileDisabledReroutesStagedInterceptorsToNewBus)
                        + "_Original"
                )
            )
            using (
                LeakWatcher retargetedWatcher = new(
                    bus: retargetedBus,
                    throwOnLeak: true,
                    label: nameof(RetargetWhileDisabledReroutesStagedInterceptorsToNewBus)
                        + "_Retargeted"
                )
            )
            {
                _ = scope.Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++intercepted;
                        return true;
                    }
                );

                scope.Token.RetargetMessageBus(retargetedBus, MessageBusRebindMode.RebindActive);
                scope.Token.Enable();

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(retargetedBus);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "A staged interceptor must land on the bus the token is bound to "
                        + "at Enable() time (the retargeted bus)."
                );

                message.EmitUntargeted(originalBus);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "A staged interceptor re-routed by RetargetMessageBus must NOT "
                        + "intercept emissions on the original bus."
                );

                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    intercepted,
                    "A staged interceptor re-routed by RetargetMessageBus must NOT "
                        + "intercept emissions on the global bus."
                );

                scope.Token.UnregisterAll();
            }
        }

        [Test]
        public void RetargetRebindActiveMovesLiveInterceptorToNewBus()
        {
            BusType originalBus = new();
            BusType retargetedBus = new();
            using TokenScope scope = TokenScope.Create(enable: true, bus: originalBus);
            int intercepted = 0;

            using (
                LeakWatcher originalWatcher = new(
                    bus: originalBus,
                    throwOnLeak: true,
                    label: nameof(RetargetRebindActiveMovesLiveInterceptorToNewBus) + "_Original"
                )
            )
            using (
                LeakWatcher retargetedWatcher = new(
                    bus: retargetedBus,
                    throwOnLeak: true,
                    label: nameof(RetargetRebindActiveMovesLiveInterceptorToNewBus) + "_Retargeted"
                )
            )
            {
                _ = scope.Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++intercepted;
                        return true;
                    }
                );

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(originalBus);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "Control failed: a live interceptor registered through the token "
                        + "must intercept emissions on the token's original bus."
                );

                scope.Token.RetargetMessageBus(retargetedBus, MessageBusRebindMode.RebindActive);

                message.EmitUntargeted(originalBus);
                Assert.AreEqual(
                    1,
                    intercepted,
                    "RebindActive must remove the live interceptor from the original bus."
                );

                message.EmitUntargeted(retargetedBus);
                Assert.AreEqual(
                    2,
                    intercepted,
                    "RebindActive must re-register the live interceptor on the new bus."
                );

                scope.Token.UnregisterAll();
            }
        }

        private static void EmitAllKinds(IMessageBus messageBus, InstanceId context)
        {
            SimpleUntargetedMessage untargeted = new();
            untargeted.EmitUntargeted(messageBus);
            SimpleTargetedMessage targeted = new();
            targeted.EmitTargeted(context, messageBus);
            SimpleBroadcastMessage broadcast = new();
            broadcast.EmitBroadcast(context, messageBus);
        }

        /// <summary>
        /// Pairs a fresh isolated <see cref="BusType"/>, an active
        /// <see cref="MessageHandler"/> with NO default bus, and a
        /// <see cref="MessageRegistrationToken"/> bound to that bus so the token's
        /// bus binding is the only route to the custom bus.
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

            internal static TokenScope Create(bool enable = true, BusType bus = null)
            {
                BusType resolvedBus = bus ?? new BusType();
                MessageHandler handler = new(new InstanceId(OwnerInstanceId)) { active = true };
                MessageRegistrationToken token = MessageRegistrationToken.Create(
                    handler,
                    resolvedBus
                );
                if (enable)
                {
                    token.Enable();
                }

                return new TokenScope(resolvedBus, handler, token);
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
