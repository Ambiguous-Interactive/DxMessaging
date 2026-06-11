namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using GlobalMessageBus = DxMessaging.Core.MessageBus.MessageBus;
#if UNITY_2021_3_OR_NEWER
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
#endif

    [TestFixture]
    public sealed class MessageHandlerGlobalBusTests
    {
        private IMessageBus _originalBus;

        [SetUp]
        public void CaptureOriginalBus()
        {
            _originalBus = MessageHandler.MessageBus;
        }

        [TearDown]
        public void RestoreOriginalBus()
        {
            MessageHandler.SetGlobalMessageBus(_originalBus);
        }

        [Test]
        public void SetGlobalMessageBusReplacesGlobalInstance()
        {
            GlobalMessageBus customBus = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(customBus);

            Assert.AreSame(customBus, MessageHandler.MessageBus);
        }

        [Test]
        public void ResetGlobalMessageBusRestoresDefaultInstance()
        {
            MessageHandler.ResetGlobalMessageBus();
            IMessageBus expectedDefault = MessageHandler.MessageBus;

            GlobalMessageBus customBus = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(customBus);
            Assert.AreSame(customBus, MessageHandler.MessageBus);

            MessageHandler.ResetGlobalMessageBus();
            Assert.AreSame(expectedDefault, MessageHandler.MessageBus);
        }

        [Test]
        public void SetGlobalMessageBusAcceptsInterfaceImplementation()
        {
            WrapperMessageBus wrapper = new WrapperMessageBus(new GlobalMessageBus());
            MessageHandler.SetGlobalMessageBus(wrapper);
            Assert.AreSame(wrapper, MessageHandler.MessageBus);
        }

        [Test]
        public void TrimAllUsesCurrentGlobalMessageBus()
        {
            CountingTrimMessageBus wrapper = new CountingTrimMessageBus(new GlobalMessageBus());
            MessageHandler.SetGlobalMessageBus(wrapper);

            IMessageBus.TrimResult result = MessageHandler.TrimAll(force: true);

            Assert.AreEqual(1, wrapper.TrimCallCount);
            Assert.IsTrue(wrapper.LastForce);
            // The wrapped bus has no registrations, so its eviction-side fields are always zero.
            // PooledCollectionsEvicted is intentionally NOT asserted: Trim(force: true) drains
            // AppDomain-scoped static pools (DxPools / ContextHandlerByTargetDicts) shared with
            // other test fixtures, so its value is non-deterministic across test orderings.
            Assert.AreEqual(
                0,
                result.TypeSlotsEvicted,
                "TypeSlotsEvicted should be 0 on a fresh bus."
            );
            Assert.AreEqual(
                0,
                result.TargetSlotsEvicted,
                "TargetSlotsEvicted should be 0 on a fresh bus."
            );
            Assert.AreEqual(
                0,
                result.LiveTypeSlotsRemaining,
                "LiveTypeSlotsRemaining should be 0 on a fresh bus."
            );
        }

        [Test]
        public void TrimAllPropagatesInnerBusResultUnchanged()
        {
            IMessageBus.TrimResult sentinel = new IMessageBus.TrimResult(7, 11, 13, 17);
            SentinelTrimMessageBus wrapper = new SentinelTrimMessageBus(
                new GlobalMessageBus(),
                sentinel
            );
            MessageHandler.SetGlobalMessageBus(wrapper);

            IMessageBus.TrimResult result = MessageHandler.TrimAll(force: false);

            Assert.AreEqual(
                sentinel,
                result,
                "MessageHandler.TrimAll must return the inner bus's TrimResult unchanged. expected={0}, actual={1}",
                sentinel,
                result
            );
        }

        [Test]
        public void OverrideGlobalMessageBusScopeRestoresPreviousBus()
        {
            GlobalMessageBus primary = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(primary);
            WrapperMessageBus secondary = new WrapperMessageBus(new GlobalMessageBus());

            using (MessageHandler.OverrideGlobalMessageBus(secondary))
            {
                Assert.AreSame(secondary, MessageHandler.MessageBus);
            }

            Assert.AreSame(primary, MessageHandler.MessageBus);
        }

        /// <summary>
        /// Pins LIFO disposal of nested
        /// <see cref="MessageHandler.OverrideGlobalMessageBus"/> scopes: each
        /// scope captures the bus active at its construction, so disposing
        /// inner-then-outer walks the chain back to the original bus.
        /// </summary>
        [Test]
        public void OverrideGlobalMessageBusNestedScopesRestoreInLifoOrder()
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            GlobalMessageBus outerBus = new GlobalMessageBus();
            GlobalMessageBus innerBus = new GlobalMessageBus();

            MessageHandler.GlobalMessageBusScope outerScope =
                MessageHandler.OverrideGlobalMessageBus(outerBus);
            Assert.AreSame(
                outerBus,
                MessageHandler.MessageBus,
                "Outer override must take effect immediately."
            );

            MessageHandler.GlobalMessageBusScope innerScope =
                MessageHandler.OverrideGlobalMessageBus(innerBus);
            Assert.AreSame(
                innerBus,
                MessageHandler.MessageBus,
                "Inner override must take effect immediately."
            );

            innerScope.Dispose();
            Assert.AreSame(
                outerBus,
                MessageHandler.MessageBus,
                "Disposing the inner scope must restore the outer override bus."
            );

            outerScope.Dispose();
            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "Disposing the outer scope must restore the original bus."
            );
        }

        /// <summary>
        /// Pins what <see cref="MessageHandler.GlobalMessageBusScope"/>
        /// actually does on OUT-OF-ORDER disposal (outer disposed before
        /// inner). The implementation performs no nesting validation: each
        /// scope independently captures the bus that was active at its own
        /// construction and restores exactly that snapshot when disposed,
        /// regardless of disposal order. "Sane" here means deterministic
        /// per-scope snapshot-restore - the scope neither throws nor tries to
        /// reconcile the stack.
        /// </summary>
        /// <remarks>
        /// CONSEQUENCE (pinned below, and worth flagging to maintainers):
        /// after disposing outer-then-inner, the globally active bus is the
        /// OUTER override bus - the inner scope captured it as its "previous"
        /// - NOT the original bus that was active before either override. A
        /// caller that disposes scopes out of order is silently left on a
        /// stale override. If GlobalMessageBusScope ever grows nesting
        /// validation (e.g. throwing on out-of-order disposal, or restoring
        /// the original), this test must be re-pinned deliberately.
        /// </remarks>
        [Test]
        public void OverrideGlobalMessageBusOutOfOrderDisposalRestoresConstructionSnapshots()
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            GlobalMessageBus outerBus = new GlobalMessageBus();
            GlobalMessageBus innerBus = new GlobalMessageBus();

            MessageHandler.GlobalMessageBusScope outerScope =
                MessageHandler.OverrideGlobalMessageBus(outerBus);
            MessageHandler.GlobalMessageBusScope innerScope =
                MessageHandler.OverrideGlobalMessageBus(innerBus);
            Assert.AreSame(
                innerBus,
                MessageHandler.MessageBus,
                "Sanity: inner override is active before any disposal."
            );

            // Outer disposed FIRST: it restores ITS captured previous (the
            // original bus), even though the inner scope is still open.
            Assert.DoesNotThrow(
                () => outerScope.Dispose(),
                "Out-of-order disposal must not throw (no nesting validation exists)."
            );
            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "Disposing the outer scope restores the outer scope's construction snapshot (the original bus), ignoring the still-open inner scope."
            );

            // Inner disposed SECOND: it restores ITS captured previous - the
            // outer override bus - leaving a stale override active. See the
            // remarks; this is the deterministic consequence of per-scope
            // snapshot-restore without nesting validation.
            Assert.DoesNotThrow(() => innerScope.Dispose());
            Assert.AreSame(
                outerBus,
                MessageHandler.MessageBus,
                "Disposing the inner scope restores the inner scope's construction snapshot (the OUTER override bus), not the original. Out-of-order disposal leaves a stale override active."
            );

            // Recover explicitly so no stale override leaks past this test
            // (the fixture TearDown also restores the captured original).
            MessageHandler.SetGlobalMessageBus(original);
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Pins <see cref="MessageHandler.SetGlobalMessageBus(IMessageBus)"/>
        /// invoked from INSIDE a handler during dispatch. The emission in
        /// flight was resolved against the old global bus when the emit
        /// started, so it must complete on the old bus's frozen snapshot
        /// (later-priority handlers on the old bus still run). The very next
        /// emission through a global-bus-routed API (an emit with no explicit
        /// bus) must resolve to the new global bus.
        /// </summary>
        [Test]
        public void SetGlobalMessageBusFromInsideHandlerAffectsOnlySubsequentEmissions(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GlobalMessageBus oldBus = new GlobalMessageBus();
            GlobalMessageBus newBus = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(oldBus);

            MessageHandler oldBusHandler = new MessageHandler(new InstanceId(101))
            {
                active = true,
            };
            MessageRegistrationToken oldBusToken = MessageRegistrationToken.Create(
                oldBusHandler,
                oldBus
            );
            oldBusToken.Enable();

            MessageHandler newBusHandler = new MessageHandler(new InstanceId(102))
            {
                active = true,
            };
            MessageRegistrationToken newBusToken = MessageRegistrationToken.Create(
                newBusHandler,
                newBus
            );
            newBusToken.Enable();

            InstanceId context = new InstanceId(103);
            int swappingCount = 0;
            int trailingCount = 0;
            int newBusCount = 0;

            // Priority 0 on the old bus swaps the global bus mid-dispatch;
            // priority 1 on the old bus observes the in-flight snapshot.
            _ = RegisterCountingHandler(
                scenario,
                oldBusToken,
                context,
                () =>
                {
                    ++swappingCount;
                    if (swappingCount == 1)
                    {
                        MessageHandler.SetGlobalMessageBus(newBus);
                    }
                },
                priority: 0
            );
            _ = RegisterCountingHandler(
                scenario,
                oldBusToken,
                context,
                () => ++trailingCount,
                priority: 1
            );
            _ = RegisterCountingHandler(
                scenario,
                newBusToken,
                context,
                () => ++newBusCount,
                priority: 0
            );

            // First global-routed emission resolves the old bus at emit time.
            Assert.DoesNotThrow(
                () => EmitForScenarioOnGlobalBus(scenario, context),
                "[{0}] Swapping the global bus from inside a handler must not throw mid-dispatch.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                swappingCount,
                "[{0}] The swapping handler must run on the in-flight emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                trailingCount,
                "[{0}] The in-flight emission must be unaffected by the swap: the old bus's later-priority handler still runs. swapping={1}, trailing={2}, newBus={3}.",
                scenario.Kind,
                swappingCount,
                trailingCount,
                newBusCount
            );
            Assert.AreEqual(
                0,
                newBusCount,
                "[{0}] The in-flight emission must NOT leak onto the new bus.",
                scenario.Kind
            );

            // The next global-routed emission resolves the NEW bus.
            EmitForScenarioOnGlobalBus(scenario, context);
            Assert.AreEqual(
                1,
                newBusCount,
                "[{0}] The next global-routed emission must dispatch on the new global bus. swapping={1}, trailing={2}, newBus={3}.",
                scenario.Kind,
                swappingCount,
                trailingCount,
                newBusCount
            );
            Assert.AreEqual(
                1,
                swappingCount,
                "[{0}] Old-bus handlers must not receive global-routed emissions after the swap.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                trailingCount,
                "[{0}] Old-bus trailing handler must not receive global-routed emissions after the swap.",
                scenario.Kind
            );

            oldBusToken.UnregisterAll();
            newBusToken.UnregisterAll();
            oldBusHandler.active = false;
            newBusHandler.active = false;
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority
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

        /// <summary>
        /// Emits with NO explicit bus so the extension methods resolve
        /// <see cref="MessageHandler.MessageBus"/> at emit time - the
        /// global-bus-routed API surface under test.
        /// </summary>
        private static void EmitForScenarioOnGlobalBus(MessageScenario scenario, InstanceId context)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context);
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
#endif

        private class WrapperMessageBus : IMessageBus
        {
            protected readonly IMessageBus _inner;

            public WrapperMessageBus(IMessageBus inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public bool DiagnosticsMode => _inner.DiagnosticsMode;

            public int RegisteredGlobalSequentialIndex => _inner.RegisteredGlobalSequentialIndex;

            public int OccupiedTypeSlots => _inner.OccupiedTypeSlots;

            public int OccupiedTargetSlots => _inner.OccupiedTargetSlots;

            public int RegisteredBroadcast => _inner.RegisteredBroadcast;

            public int RegisteredTargeted => _inner.RegisteredTargeted;

            public int RegisteredUntargeted => _inner.RegisteredUntargeted;

            public int RegisteredInterceptors => _inner.RegisteredInterceptors;

            public int RegisteredPostProcessors => _inner.RegisteredPostProcessors;

            public int RegisteredGlobalAcceptAll => _inner.RegisteredGlobalAcceptAll;

            public RegistrationLog Log => _inner.Log;

            public long EmissionId => _inner.EmissionId;

            public virtual IMessageBus.TrimResult Trim(bool force = false) => _inner.Trim(force);

            public Action RegisterUntargeted<T>(MessageHandler messageHandler, int priority = 0)
                where T : IUntargetedMessage =>
                _inner.RegisterUntargeted<T>(messageHandler, priority);

            public Action RegisterUntargetedPostProcessor<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : IUntargetedMessage =>
                _inner.RegisterUntargetedPostProcessor<T>(messageHandler, priority);

            public Action RegisterTargeted<T>(
                InstanceId target,
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : ITargetedMessage =>
                _inner.RegisterTargeted<T>(target, messageHandler, priority);

            public Action RegisterTargetedPostProcessor<T>(
                InstanceId target,
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : ITargetedMessage =>
                _inner.RegisterTargetedPostProcessor<T>(target, messageHandler, priority);

            public Action RegisterTargetedWithoutTargeting<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : ITargetedMessage =>
                _inner.RegisterTargetedWithoutTargeting<T>(messageHandler, priority);

            public Action RegisterTargetedWithoutTargetingPostProcessor<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : ITargetedMessage =>
                _inner.RegisterTargetedWithoutTargetingPostProcessor<T>(messageHandler, priority);

            public Action RegisterBroadcastPostProcessor<T>(
                InstanceId source,
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : IBroadcastMessage =>
                _inner.RegisterBroadcastPostProcessor<T>(source, messageHandler, priority);

            public Action RegisterBroadcastWithoutSourcePostProcessor<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : IBroadcastMessage =>
                _inner.RegisterBroadcastWithoutSourcePostProcessor<T>(messageHandler, priority);

            public Action RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : IBroadcastMessage =>
                _inner.RegisterSourcedBroadcast<T>(source, messageHandler, priority);

            public Action RegisterSourcedBroadcastWithoutSource<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
                where T : IBroadcastMessage =>
                _inner.RegisterSourcedBroadcastWithoutSource<T>(messageHandler, priority);

            public Action RegisterGlobalAcceptAll(MessageHandler messageHandler) =>
                _inner.RegisterGlobalAcceptAll(messageHandler);

            public Action RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
                where T : IUntargetedMessage =>
                _inner.RegisterUntargetedInterceptor(interceptor, priority);

            public Action RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
                where T : ITargetedMessage =>
                _inner.RegisterTargetedInterceptor(interceptor, priority);

            public Action RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
                where T : IBroadcastMessage =>
                _inner.RegisterBroadcastInterceptor(interceptor, priority);

            public void UntypedUntargetedBroadcast(IUntargetedMessage typedMessage) =>
                _inner.UntypedUntargetedBroadcast(typedMessage);

            public void UntargetedBroadcast<TMessage>(ref TMessage typedMessage)
                where TMessage : IUntargetedMessage => _inner.UntargetedBroadcast(ref typedMessage);

            public void UntypedTargetedBroadcast(
                InstanceId target,
                ITargetedMessage typedMessage
            ) => _inner.UntypedTargetedBroadcast(target, typedMessage);

            public void TargetedBroadcast<TMessage>(
                ref InstanceId target,
                ref TMessage typedMessage
            )
                where TMessage : ITargetedMessage =>
                _inner.TargetedBroadcast(ref target, ref typedMessage);

            public void UntypedSourcedBroadcast(
                InstanceId source,
                IBroadcastMessage typedMessage
            ) => _inner.UntypedSourcedBroadcast(source, typedMessage);

            public void SourcedBroadcast<TMessage>(ref InstanceId source, ref TMessage typedMessage)
                where TMessage : IBroadcastMessage =>
                _inner.SourcedBroadcast(ref source, ref typedMessage);
        }

        private sealed class CountingTrimMessageBus : WrapperMessageBus
        {
            public CountingTrimMessageBus(IMessageBus inner)
                : base(inner) { }

            public int TrimCallCount { get; private set; }

            public bool LastForce { get; private set; }

            public override IMessageBus.TrimResult Trim(bool force = false)
            {
                TrimCallCount++;
                LastForce = force;
                return base.Trim(force);
            }
        }

        /// <summary>
        /// Wrapper that returns a fixed sentinel <see cref="IMessageBus.TrimResult"/> so the test
        /// can assert field-by-field propagation through <see cref="MessageHandler.TrimAll"/>
        /// without depending on the real bus's pool/eviction state.
        /// </summary>
        private sealed class SentinelTrimMessageBus : WrapperMessageBus
        {
            private readonly IMessageBus.TrimResult _sentinel;

            public SentinelTrimMessageBus(IMessageBus inner, IMessageBus.TrimResult sentinel)
                : base(inner)
            {
                _sentinel = sentinel;
            }

            public override IMessageBus.TrimResult Trim(bool force = false) => _sentinel;
        }
    }
}
