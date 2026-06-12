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
        private GlobalBusScope _globalBusScope;

        [SetUp]
        public void CaptureOriginalBus()
        {
            _globalBusScope = GlobalBusScope.Capture();
        }

        [TearDown]
        public void RestoreOriginalBus()
        {
            _globalBusScope?.Dispose();
            _globalBusScope = null;
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
            DelegatingMessageBus wrapper = new DelegatingMessageBus(new GlobalMessageBus());
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
            DelegatingMessageBus secondary = new DelegatingMessageBus(new GlobalMessageBus());

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
            _ = ScenarioCallbacks.RegisterCountingHandler(
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
            _ = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                oldBusToken,
                context,
                () => ++trailingCount,
                priority: 1
            );
            _ = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                newBusToken,
                context,
                () => ++newBusCount,
                priority: 0
            );

            // First global-routed emission resolves the old bus at emit time.
            Assert.DoesNotThrow(
                () => ScenarioCallbacks.EmitForKind(scenario, context),
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
            ScenarioCallbacks.EmitForKind(scenario, context);
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
#endif

        private sealed class CountingTrimMessageBus : DelegatingMessageBus
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
        private sealed class SentinelTrimMessageBus : DelegatingMessageBus
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
