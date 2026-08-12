namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Reflection;
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

        [Test]
        public void DefaultGlobalMessageBusScopeDisposalDoesNothing()
        {
            GlobalMessageBus current = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(current);

            MessageHandler.GlobalMessageBusScope scope = default;
            Assert.DoesNotThrow(
                scope.Dispose,
                "Disposing a default global-bus scope must be a harmless no-op."
            );
            Assert.AreSame(
                current,
                MessageHandler.MessageBus,
                "A default scope must not reset or replace the current global bus."
            );
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CopiedGlobalMessageBusScopeDisposesExactlyOnce(bool disposeCopyFirst)
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            GlobalMessageBus overrideBus = new GlobalMessageBus();
            MessageHandler.GlobalMessageBusScope scope = MessageHandler.OverrideGlobalMessageBus(
                overrideBus
            );
            MessageHandler.GlobalMessageBusScope copy = scope;

            if (disposeCopyFirst)
            {
                copy.Dispose();
            }
            else
            {
                scope.Dispose();
            }

            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "The first disposal of either copy must restore the original bus. copyFirst={0}",
                disposeCopyFirst
            );

            GlobalMessageBus intervening = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(intervening);
            if (disposeCopyFirst)
            {
                scope.Dispose();
            }
            else
            {
                copy.Dispose();
            }

            Assert.AreSame(
                intervening,
                MessageHandler.MessageBus,
                "The stale copy must not restore over a newer global-bus change. copyFirst={0}",
                disposeCopyFirst
            );
        }

        [Test]
        public void InterveningGlobalMessageBusChangeInvalidatesActiveScopeRestore()
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            MessageHandler.GlobalMessageBusScope scope = MessageHandler.OverrideGlobalMessageBus(
                new GlobalMessageBus()
            );
            GlobalMessageBus intervening = new GlobalMessageBus();

            MessageHandler.SetGlobalMessageBus(intervening);
            scope.Dispose();

            Assert.AreSame(
                intervening,
                MessageHandler.MessageBus,
                "Disposal must not restore a snapshot over a newer explicit global-bus change."
            );
        }

        [Test]
        public void ResetGlobalMessageBusInvalidatesActiveScopeRestore()
        {
            MessageHandler.SetGlobalMessageBus(new GlobalMessageBus());
            MessageHandler.GlobalMessageBusScope scope = MessageHandler.OverrideGlobalMessageBus(
                new GlobalMessageBus()
            );

            MessageHandler.ResetGlobalMessageBus();
            IMessageBus resetBus = MessageHandler.MessageBus;
            scope.Dispose();

            Assert.AreSame(
                resetBus,
                MessageHandler.MessageBus,
                "A scope created before ResetGlobalMessageBus must not restore stale state."
            );
        }

        [Test]
        public void StaticResetInvalidatesActiveScopeRestore()
        {
            MessageHandler.SetGlobalMessageBus(new GlobalMessageBus());
            MessageHandler.GlobalMessageBusScope scope = MessageHandler.OverrideGlobalMessageBus(
                new GlobalMessageBus()
            );

            DxMessagingStaticState.Reset();
            IMessageBus resetBus = MessageHandler.MessageBus;
            scope.Dispose();

            Assert.AreSame(
                resetBus,
                MessageHandler.MessageBus,
                "A scope created before DxMessagingStaticState.Reset must not restore stale state."
            );
        }

        [Test]
        public void OverrideGlobalMessageBusRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => MessageHandler.OverrideGlobalMessageBus((IMessageBus)null),
                "A null global-bus override must fail before changing global state."
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecycledOverrideSlotRejectsStaleScopeGeneration(bool useStaticReset)
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            MessageHandler.GlobalMessageBusScope stale = MessageHandler.OverrideGlobalMessageBus(
                new GlobalMessageBus()
            );

            if (useStaticReset)
            {
                DxMessagingStaticState.Reset();
            }
            else
            {
                MessageHandler.SetGlobalMessageBus(original);
            }

            IMessageBus baseline = MessageHandler.MessageBus;
            GlobalMessageBus current = new GlobalMessageBus();
            MessageHandler.GlobalMessageBusScope currentScope =
                MessageHandler.OverrideGlobalMessageBus(current);

            stale.Dispose();
            Assert.AreSame(
                current,
                MessageHandler.MessageBus,
                "A stale generation must not end the scope that recycled its slot. staticReset={0}",
                useStaticReset
            );

            currentScope.Dispose();
            Assert.AreSame(
                baseline,
                MessageHandler.MessageBus,
                "The recycled slot's live scope must still restore its own baseline. staticReset={0}",
                useStaticReset
            );
        }

        [Test]
        public void OverrideSlotTableGrowsBeyondOneBlockAndIsReusableAfterUnwind()
        {
            const int scopeCount = 2049;
            GlobalMessageBus original = new GlobalMessageBus();
            GlobalMessageBus overrideBus = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            MessageHandler.GlobalMessageBusScope[] scopes =
                new MessageHandler.GlobalMessageBusScope[scopeCount];

            for (int i = 0; i < scopes.Length; ++i)
            {
                scopes[i] = MessageHandler.OverrideGlobalMessageBus(overrideBus);
            }

            Assert.AreSame(
                overrideBus,
                MessageHandler.MessageBus,
                "The first scope in a third block must become active without a fixed-block cap."
            );

            for (int i = 0; i < scopes.Length - 1; ++i)
            {
                scopes[i].Dispose();
            }

            Assert.AreSame(
                overrideBus,
                MessageHandler.MessageBus,
                "Out-of-order-disposed ancestors must leave the newest scope active."
            );
            scopes[scopes.Length - 1].Dispose();
            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "Ending the newest scope must unwind every disposed ancestor."
            );

            using (MessageHandler.OverrideGlobalMessageBus(overrideBus))
            {
                Assert.AreSame(
                    overrideBus,
                    MessageHandler.MessageBus,
                    "Unwound slots must be reusable after multi-block recovery."
                );
            }

            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "The recovered scope must restore the pre-growth-test bus."
            );
        }

        [Test]
        public void OverrideSlotStorageUsesFixedSizeSegmentsInsteadOfOneHardCapArray()
        {
            const int expectedBlockSize = 1024;
            const int scopeCount = (expectedBlockSize * 2) + 1;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo blocksField = typeof(MessageHandler).GetField(
                "_globalMessageBusOverrideBlocks",
                flags
            );
            FieldInfo blockSizeField = typeof(MessageHandler).GetField(
                "GlobalMessageBusOverrideBlockSize",
                flags
            );

            Assert.IsNotNull(
                blocksField,
                "Override state must use a segmented directory instead of a fixed monolithic arena."
            );
            Assert.IsNotNull(blockSizeField, "The fixed segment size must remain explicit.");
            Assert.IsTrue(
                blocksField.FieldType.IsArray
                    && blocksField.FieldType.GetElementType()?.IsArray == true,
                "Override storage must be a jagged array so existing state blocks never move when capacity grows."
            );
            Assert.AreEqual(
                expectedBlockSize,
                (int)blockSizeField.GetRawConstantValue(),
                "Override storage must grow in bounded fixed-size segments."
            );

            MessageHandler.GlobalMessageBusScope[] scopes =
                new MessageHandler.GlobalMessageBusScope[scopeCount];
            GlobalMessageBus overrideBus = new GlobalMessageBus();
            try
            {
                for (int i = 0; i < scopes.Length; ++i)
                {
                    scopes[i] = MessageHandler.OverrideGlobalMessageBus(overrideBus);
                }

                Array blocks = (Array)blocksField.GetValue(null);
                int realizedBlockCount = 0;
                foreach (Array block in blocks)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    ++realizedBlockCount;
                    Assert.AreEqual(
                        expectedBlockSize,
                        block.Length,
                        "Every realized override-state segment must use the fixed block size."
                    );
                }

                Assert.GreaterOrEqual(
                    realizedBlockCount,
                    3,
                    "Crossing two boundaries must realize at least three fixed-size blocks."
                );
            }
            finally
            {
                for (int i = scopes.Length - 1; i >= 0; --i)
                {
                    scopes[i].Dispose();
                }
            }
        }

        [Test]
        public void OverrideSlotIndexExhaustionFailsWithoutChangingTheGlobalBus()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo slotCountField = typeof(MessageHandler).GetField(
                "_globalMessageBusOverrideSlotCount",
                flags
            );
            FieldInfo freeHeadField = typeof(MessageHandler).GetField(
                "_globalMessageBusOverrideFreeHead",
                flags
            );
            GlobalMessageBus current = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(current);
            int originalSlotCount = (int)slotCountField.GetValue(null);
            int originalFreeHead = (int)freeHeadField.GetValue(null);

            try
            {
                slotCountField.SetValue(null, int.MaxValue);
                freeHeadField.SetValue(null, -1);

                Assert.Throws<InvalidOperationException>(
                    () => MessageHandler.OverrideGlobalMessageBus(new GlobalMessageBus()),
                    "A fresh slot outside the representable range must fail closed."
                );
                Assert.AreSame(
                    current,
                    MessageHandler.MessageBus,
                    "Slot-index exhaustion must not change the active global bus."
                );
            }
            finally
            {
                slotCountField.SetValue(null, originalSlotCount);
                freeHeadField.SetValue(null, originalFreeHead);
            }
        }

        [Test]
        public void ExhaustedGenerationSlotIsBurnedAndStaleCopyCannotEndReplacement()
        {
            GlobalMessageBus original = new GlobalMessageBus();
            GlobalMessageBus replacementBus = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            MessageHandler.GlobalMessageBusScope bootstrap =
                MessageHandler.OverrideGlobalMessageBus(new GlobalMessageBus());
            int exhaustedSlot = GetScopeSlot(bootstrap);
            bootstrap.Dispose();
            long originalGeneration = GetOverrideStateGeneration(exhaustedSlot);
            SetOverrideStateGeneration(exhaustedSlot, -2);
            MessageHandler.GlobalMessageBusScope exhaustedScope = default;
            MessageHandler.GlobalMessageBusScope replacement = default;

            try
            {
                exhaustedScope = MessageHandler.OverrideGlobalMessageBus(new GlobalMessageBus());
                Assert.AreEqual(
                    exhaustedSlot,
                    GetScopeSlot(exhaustedScope),
                    "The prepared slot must issue its final nonzero generation."
                );
                MessageHandler.GlobalMessageBusScope staleCopy = exhaustedScope;
                exhaustedScope.Dispose();

                replacement = MessageHandler.OverrideGlobalMessageBus(replacementBus);
                Assert.AreNotEqual(
                    exhaustedSlot,
                    GetScopeSlot(replacement),
                    "A slot that issued generation -1 must be burned instead of wrapping."
                );

                staleCopy.Dispose();
                Assert.AreSame(
                    replacementBus,
                    MessageHandler.MessageBus,
                    "The exhausted stale scope must not end the replacement scope."
                );

                replacement.Dispose();
                Assert.AreSame(
                    original,
                    MessageHandler.MessageBus,
                    "The replacement scope must restore the original bus."
                );
            }
            finally
            {
                MessageHandler.SetGlobalMessageBus(original);
                exhaustedScope.Dispose();
                replacement.Dispose();
                RestoreBurnedOverrideSlot(exhaustedSlot, originalGeneration);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void NestedGlobalMessageBusScopeCopiesRestoreNearestActiveBus(
            bool disposeOuterFirst,
            bool disposeCopies
        )
        {
            GlobalMessageBus original = new GlobalMessageBus();
            MessageHandler.SetGlobalMessageBus(original);
            GlobalMessageBus outerBus = new GlobalMessageBus();
            GlobalMessageBus innerBus = new GlobalMessageBus();

            MessageHandler.GlobalMessageBusScope outerScope =
                MessageHandler.OverrideGlobalMessageBus(outerBus);
            MessageHandler.GlobalMessageBusScope innerScope =
                MessageHandler.OverrideGlobalMessageBus(innerBus);
            MessageHandler.GlobalMessageBusScope outerCopy = outerScope;
            MessageHandler.GlobalMessageBusScope innerCopy = innerScope;
            Assert.AreSame(
                innerBus,
                MessageHandler.MessageBus,
                "The inner override must be active before disposal. outerFirst={0}, copies={1}",
                disposeOuterFirst,
                disposeCopies
            );

            MessageHandler.GlobalMessageBusScope first = disposeOuterFirst
                ? (disposeCopies ? outerCopy : outerScope)
                : (disposeCopies ? innerCopy : innerScope);
            MessageHandler.GlobalMessageBusScope second = disposeOuterFirst
                ? (disposeCopies ? innerCopy : innerScope)
                : (disposeCopies ? outerCopy : outerScope);
            first.Dispose();

            IMessageBus expectedAfterFirst = disposeOuterFirst ? innerBus : outerBus;
            Assert.AreSame(
                expectedAfterFirst,
                MessageHandler.MessageBus,
                "The first disposal must preserve the nearest active override. outerFirst={0}, copies={1}",
                disposeOuterFirst,
                disposeCopies
            );

            second.Dispose();
            Assert.AreSame(
                original,
                MessageHandler.MessageBus,
                "The second disposal must restore the original bus. outerFirst={0}, copies={1}",
                disposeOuterFirst,
                disposeCopies
            );
        }

        private static int GetScopeSlot(MessageHandler.GlobalMessageBusScope scope)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo slotField = typeof(MessageHandler.GlobalMessageBusScope).GetField(
                "_slot",
                flags
            );
            return (int)slotField.GetValue(scope) - 1;
        }

        private static long GetOverrideStateGeneration(int slot)
        {
            object state = GetOverrideState(slot, out _, out _);
            return (long)GetOverrideStateField(state, "Generation").GetValue(state);
        }

        private static void SetOverrideStateGeneration(int slot, long generation)
        {
            object state = GetOverrideState(slot, out Array block, out int offset);
            GetOverrideStateField(state, "Generation").SetValue(state, generation);
            block.SetValue(state, offset);
        }

        private static void RestoreBurnedOverrideSlot(int slot, long generation)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo freeHeadField = typeof(MessageHandler).GetField(
                "_globalMessageBusOverrideFreeHead",
                flags
            );
            int freeHead = (int)freeHeadField.GetValue(null);
            object state = GetOverrideState(slot, out Array block, out int offset);
            GetOverrideStateField(state, "PreviousBus").SetValue(state, null);
            GetOverrideStateField(state, "Generation").SetValue(state, generation);
            GetOverrideStateField(state, "PreviousSlot").SetValue(state, -1);
            GetOverrideStateField(state, "NextFree").SetValue(state, freeHead);
            GetOverrideStateField(state, "Disposed").SetValue(state, true);
            block.SetValue(state, offset);
            freeHeadField.SetValue(null, slot);
        }

        private static FieldInfo GetOverrideStateField(object state, string fieldName)
        {
            const BindingFlags flags =
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            return state.GetType().GetField(fieldName, flags);
        }

        private static object GetOverrideState(int slot, out Array block, out int offset)
        {
            const int blockShift = 10;
            const int blockMask = (1 << blockShift) - 1;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo blocksField = typeof(MessageHandler).GetField(
                "_globalMessageBusOverrideBlocks",
                flags
            );
            Array blocks = (Array)blocksField.GetValue(null);
            block = (Array)blocks.GetValue(slot >> blockShift);
            offset = slot & blockMask;
            return block.GetValue(offset);
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
