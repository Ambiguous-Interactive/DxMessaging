#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    [TestFixture]
    [Category("MemoryReclaim")]
    public sealed class MemoryReclamationTests : MessagingTestBase
    {
        private const int DistinctTargetCount = 1024;
        private const int RetainedPoolEntryCount = 64;
        private static readonly InstanceId HandlerOwner = new InstanceId(0x5A17_0001);
        private static readonly InstanceId DefaultContext = new InstanceId(0x5A17_0002);

        public static IEnumerable<MessageScenario> ContextDictPoolScenarios
        {
            get
            {
                yield return MessageScenario.Targeted();
                yield return MessageScenario.Broadcast();
                yield return MessageScenario.Targeted().WithPostProcessor(true);
                yield return MessageScenario.Broadcast().WithPostProcessor(true);
            }
        }

        [Test]
        public void TrimEvictsEmptyTypeSlots(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            int baseline = bus.OccupiedTypeSlots;

            MessageRegistrationHandle first = RegisterFirst(scenario, token, DefaultContext);
            MessageRegistrationHandle second = RegisterSecond(scenario, token, DefaultContext);
            MessageRegistrationHandle third = RegisterThird(scenario, token, DefaultContext);

            token.RemoveRegistration(first);
            token.RemoveRegistration(second);
            token.RemoveRegistration(third);

            Assert.GreaterOrEqual(
                bus.OccupiedTypeSlots,
                baseline + 3,
                "[{0}] deregistered distinct message types must remain occupied until trim.",
                scenario.Kind
            );

            IMessageBus.TrimResult result = bus.Trim(force: true);

            Assert.GreaterOrEqual(
                result.TypeSlotsEvicted,
                3,
                "[{0}] trim must evict every empty distinct type slot.",
                scenario.Kind
            );
            Assert.AreEqual(
                baseline,
                bus.OccupiedTypeSlots,
                "[{0}] occupied type slots must return to the pre-test baseline.",
                scenario.Kind
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UntargetedPlanSweepDropsBorrowedEntryArray(bool force)
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                trimApiEnabled: true
            );
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: nameof(UntargetedPlanSweepDropsBorrowedEntryArray)
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            Action deregister = null;
            try
            {
                Action<UntargetedOne> callback = _ => { };
                deregister = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 0,
                    messageBus: bus
                );
                UntargetedOne message = new UntargetedOne();
                bus.UntargetedBroadcast(ref message);

                const BindingFlags declaredInstanceFields =
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly;
                FieldInfo plansField = typeof(MessageBus).GetField(
                    "_untargetedDispatchPlans",
                    declaredInstanceFields
                );
                Assert.That(
                    plansField,
                    Is.Not.Null,
                    "MessageBus must retain the untargeted plan cache field registered for sweep."
                );
                object plan = null;
                foreach (
                    object candidate in (System.Collections.IEnumerable)plansField.GetValue(bus)
                )
                {
                    Assert.That(
                        plan,
                        Is.Null,
                        "The scenario must create exactly one untargeted dispatch plan."
                    );
                    plan = candidate;
                }

                Assert.That(plan, Is.Not.Null, "The first emit must create a dispatch plan.");
                Type planType = plan.GetType();
                FieldInfo entriesField = planType.GetField("handleEntries", declaredInstanceFields);
                FieldInfo countField = planType.GetField(
                    "handleEntryCount",
                    declaredInstanceFields
                );
                Assert.That(
                    entriesField,
                    Is.Not.Null,
                    "The untargeted plan must expose its borrowed entry-array field to this contract."
                );
                Assert.That(
                    countField,
                    Is.Not.Null,
                    "The untargeted plan must expose its borrowed entry-count field to this contract."
                );
                Assert.That(
                    entriesField.GetValue(plan),
                    Is.Not.Null,
                    "The first untargeted fast emit must publish the settled borrowed route."
                );
                Assert.That(
                    (int)countField.GetValue(plan),
                    Is.GreaterThan(0),
                    "The borrowed route must describe the invocable handler entry."
                );

                deregister();
                deregister = null;
                _ = bus.Trim(force);

                Assert.That(
                    entriesField.GetValue(plan),
                    Is.Null,
                    "Sweeping stale untargeted plans must drop the borrowed entry array."
                );
                Assert.That(
                    countField.GetValue(plan),
                    Is.EqualTo(0),
                    "Sweeping stale untargeted plans must reset the cached entry count."
                );
            }
            finally
            {
                deregister?.Invoke();
                handler.active = false;
            }
        }

        [Test]
        public void TrimEvictsEmptyTargetSlots(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);

            List<MessageRegistrationHandle> handles = new List<MessageRegistrationHandle>(
                DistinctTargetCount
            );
            for (int i = 0; i < DistinctTargetCount; ++i)
            {
                handles.Add(RegisterFirst(scenario, token, new InstanceId(0x5A18_0000 + i)));
            }

            foreach (MessageRegistrationHandle handle in handles)
            {
                token.RemoveRegistration(handle);
            }

            Assert.GreaterOrEqual(
                bus.OccupiedTargetSlots,
                DistinctTargetCount,
                "[{0}] every deregistered context must remain visible until trim.",
                scenario.Kind
            );

            IMessageBus.TrimResult result = bus.Trim(force: true);

            Assert.GreaterOrEqual(result.TargetSlotsEvicted, DistinctTargetCount);
            Assert.AreEqual(
                0,
                bus.OccupiedTargetSlots,
                "[{0}] trim must reclaim every empty target/source slot.",
                scenario.Kind
            );
        }

        [Test]
        public void IdleEvictionFiresAfterInterval(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            FakeClock clock = new FakeClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 1d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            MessageRegistrationHandle handle = RegisterFirst(scenario, token, DefaultContext);
            token.RemoveRegistration(handle);
            EmitSweepSampleWindow(bus);

            Assert.GreaterOrEqual(
                bus.OccupiedTypeSlots + bus.OccupiedTargetSlots,
                1,
                "[{0}] the fresh empty slot must not be reclaimed before cadence elapses.",
                scenario.Kind
            );

            clock.Advance(1d);
            EmitSweepSampleWindow(bus);

            Assert.AreEqual(
                0,
                bus.OccupiedTypeSlots,
                "[{0}] idle sweep must reclaim empty type slots after cadence.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.OccupiedTargetSlots,
                "[{0}] idle sweep must reclaim empty target/source slots after cadence.",
                scenario.Kind
            );
        }

        [Test]
        public void IdleEvictionLeavesNonEmptySlotsAlone(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            FakeClock clock = new FakeClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            int calls = 0;

            MessageRegistrationHandle handle = RegisterCountingFirst(
                scenario,
                token,
                DefaultContext,
                () => calls++
            );
            try
            {
                clock.Advance(3600d);
                EmitSweepSampleWindow(bus);
                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(
                    1,
                    calls,
                    "[{0}] idle sweep must not remove live registrations.",
                    scenario.Kind
                );
                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);
            }
            finally
            {
                token.RemoveRegistration(handle);
            }
        }

        [Test]
        public void TrimDoesNotDisturbActiveDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            int trimmingHandlerCalls = 0;
            int trailingHandlerCalls = 0;

            MessageRegistrationHandle trimmingHandle = RegisterCountingFirst(
                scenario,
                token,
                DefaultContext,
                () =>
                {
                    trimmingHandlerCalls++;
                    _ = bus.Trim(force: true);
                },
                priority: 0
            );
            MessageRegistrationHandle trailingHandle = RegisterCountingFirst(
                scenario,
                token,
                DefaultContext,
                () => trailingHandlerCalls++,
                priority: 1
            );
            try
            {
                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(1, trimmingHandlerCalls);
                Assert.AreEqual(
                    1,
                    trailingHandlerCalls,
                    "[{0}] trim during dispatch must not disturb the active snapshot.",
                    scenario.Kind
                );
            }
            finally
            {
                token.RemoveRegistration(trimmingHandle);
                token.RemoveRegistration(trailingHandle);
            }
        }

        [Test]
        public void NonForceTrimDuringActiveContextDispatchKeepsDirtyTargetCandidate(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            int calls = 0;
            int inDispatchTargetEvictions = -1;
            MessageRegistrationHandle handle = default;
            handle = RegisterCountingFirst(
                scenario,
                token,
                DefaultContext,
                () =>
                {
                    calls++;
                    token.RemoveRegistration(handle);
                    // This trim runs INSIDE active dispatch and must short-circuit the dirty
                    // target eviction (HasActiveDispatchSnapshot guard). Capture the eviction
                    // count to assert the in-dispatch contract directly.
                    IMessageBus.TrimResult inDispatch = bus.Trim(force: false);
                    inDispatchTargetEvictions = inDispatch.TargetSlotsEvicted;
                }
            );

            EmitFirst(scenario, bus, DefaultContext);

            Assert.AreEqual(
                1,
                calls,
                "[{0}] the self-removing handler must run exactly once.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                inDispatchTargetEvictions,
                "[{0}] non-force trim called inside active dispatch must NOT evict the empty target slot it could otherwise reclaim.",
                scenario.Kind
            );

            // Advance the bus tick so the dirty target candidate ages past the idle threshold
            // (idleEvictionTicks=0 still requires _tickCounter strictly greater than the slot's
            // lastTouchTicks). Without this probe the post-dispatch trim observes 0 elapsed
            // ticks since deregister and skips eviction. EmitSweepProbe is the canonical pattern
            // shared with TrimAfterDeregisterReclaimsHandlerCache and BusContextDictReturnsToPool.
            EmitSweepProbe(bus);
            IMessageBus.TrimResult afterDispatch = bus.Trim(force: false);

            Assert.GreaterOrEqual(
                afterDispatch.TargetSlotsEvicted,
                1,
                "[{0}] a non-force trim skipped during active dispatch must leave the dirty target candidate for the next trim. afterDispatch={1}",
                scenario.Kind,
                afterDispatch
            );
        }

        /// <summary>
        /// Pins the contract that <c>idleEvictionTicks: 0</c> still requires at least one tick
        /// advancement between the deregister-touch and a non-force trim. The strict
        /// <c>_tickCounter &gt; lastTouchTicks</c> comparison in <c>IsIdleForSweep</c> means a trim
        /// called immediately after deregister observes 0 elapsed ticks and skips eviction; the
        /// next trim, after a single tick-advancing emit, evicts the slot. This test exists so a
        /// regression that flips the comparison to <c>&gt;=</c> (or that omits the tick advance in
        /// dispatch paths) breaks loudly.
        /// </summary>
        [Test]
        public void NonForceTrimRequiresOneTickAdvancementWithZeroIdleBudget(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageRegistrationToken token = CreateEnabledToken(bus);
            MessageRegistrationHandle handle = RegisterFirst(scenario, token, DefaultContext);

            token.RemoveRegistration(handle);
            IMessageBus.TrimResult immediate = bus.Trim(force: false);
            Assert.AreEqual(
                0,
                immediate.TypeSlotsEvicted,
                "[{0}] non-force trim with no elapsed ticks must NOT evict a freshly-deregistered slot. immediate={1}",
                scenario.Kind,
                immediate
            );
            Assert.AreEqual(
                0,
                immediate.TargetSlotsEvicted,
                "[{0}] non-force trim with no elapsed ticks must NOT evict a freshly-deregistered context slot. immediate={1}",
                scenario.Kind,
                immediate
            );

            EmitSweepProbe(bus);
            IMessageBus.TrimResult afterProbe = bus.Trim(force: false);
            int totalEvicted = afterProbe.TypeSlotsEvicted + afterProbe.TargetSlotsEvicted;
            Assert.GreaterOrEqual(
                totalEvicted,
                1,
                "[{0}] one tick advancement must make the slot eligible for non-force trim. afterProbe={1}",
                scenario.Kind,
                afterProbe
            );
        }

        [Test]
        public void RuntimeSettingsHotReloadAppliesCaps()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                settings._bufferMaxDistinctEntries = 4;
                settings._bufferUseLruEviction = true;
                settings._idleEvictionSeconds = 0f;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
                MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
                using IDisposable cleanup = ForceTrimCleanup(bus);

                List<object> pooled = DxPools.ObjectLists.Rent();
                DxPools.ObjectLists.Return(pooled);
                Assert.Greater(DxPools.DescribeAll().ObjectLists.Cached, 0);

                MessageRegistrationToken token = CreateEnabledToken(bus);
                MessageRegistrationHandle handle = RegisterFirst(
                    MessageScenario.Targeted(),
                    token,
                    DefaultContext
                );
                token.RemoveRegistration(handle);
                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);
                Assert.Greater(bus.GetContextDictPoolDiagnosticsForTesting().Cached, 0);

                GameObject outerTarget = new(
                    nameof(RuntimeSettingsHotReloadAppliesCaps) + "_Outer",
                    typeof(SimpleMessageAwareComponent)
                );
                _spawned.Add(outerTarget);
                GameObject nestedTarget = new(
                    nameof(RuntimeSettingsHotReloadAppliesCaps) + "_Nested",
                    typeof(SimpleMessageAwareComponent)
                );
                _spawned.Add(nestedTarget);
                SimpleMessageAwareComponent outerComponent =
                    outerTarget.GetComponent<SimpleMessageAwareComponent>();
                SimpleMessageAwareComponent nestedComponent =
                    nestedTarget.GetComponent<SimpleMessageAwareComponent>();
                InstanceId nestedTargetId = nestedTarget;
                nestedComponent.reflexiveTwoArgumentHandler = () => { };
                outerComponent.reflexiveTwoArgumentHandler = () =>
                {
                    ReflexiveMessage nested = new(
                        nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                        ReflexiveSendMode.Flat,
                        3,
                        4
                    );
                    nested.EmitTargeted(nestedTargetId, bus);
                };
                ReflexiveMessage outer = new(
                    nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                    ReflexiveSendMode.Flat,
                    1,
                    2
                );
                InstanceId outerTargetId = outerTarget;
                outer.EmitTargeted(outerTargetId, bus);

                Assert.Greater(
                    bus.ReflexiveDispatchPoolDiagnostics.Cached,
                    0,
                    "Nested reflexive delivery must warm the per-bus state pool."
                );
                Assert.AreEqual(
                    4,
                    bus.ReflexiveDispatchPoolMaxRetained,
                    "The reflexive state pool must inherit the initial retained-entry cap."
                );
                Assert.IsTrue(
                    bus.ReflexiveDispatchPoolUsesLru,
                    "The reflexive state pool must inherit the initial LRU policy."
                );

                settings._bufferMaxDistinctEntries = 0;
                settings._bufferUseLruEviction = false;
                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);

                Assert.AreEqual(0, DxPools.ObjectLists.MaxRetained);
                Assert.IsFalse(DxPools.ObjectLists.UseLru);
                Assert.AreEqual(0, DxPools.DescribeAll().ObjectLists.Cached);
                Assert.AreEqual(0, bus.GetContextDictPoolDiagnosticsForTesting().Cached);
                Assert.AreEqual(
                    0,
                    bus.ReflexiveDispatchPoolDiagnostics.Cached,
                    "Lowering the cap to zero must drain retained reflexive states."
                );
                Assert.AreEqual(
                    0,
                    bus.ReflexiveDispatchPoolMaxRetained,
                    "Hot reload must apply the zero cap to the reflexive state pool."
                );
                Assert.IsFalse(
                    bus.ReflexiveDispatchPoolUsesLru,
                    "Hot reload must apply the LIFO policy to the reflexive state pool."
                );
                GC.KeepAlive(bus);
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
                _ = DxPools.TrimAll(force: true);
            }
        }

        [Test]
        public void EmptyPriorityLeafIsReusedAcrossEveryHandlerRoute(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);

            Action firstDeregister = RegisterDirect(
                scenario,
                handler,
                bus,
                DefaultContext,
                () => { }
            );
            firstDeregister();
            object firstSpare = bus.RecycledHandlerCacheForTesting;
            Assert.IsNotNull(
                firstSpare,
                "[{0}] removing the sole priority handler must retain one detached empty leaf.",
                scenario.DisplayName
            );

            Action secondDeregister = RegisterDirect(
                scenario,
                handler,
                bus,
                DefaultContext,
                () => { }
            );
            Assert.IsNull(
                bus.RecycledHandlerCacheForTesting,
                "[{0}] the retained leaf must be rented while the replacement route is live.",
                scenario.DisplayName
            );
            secondDeregister();

            Assert.AreSame(
                firstSpare,
                bus.RecycledHandlerCacheForTesting,
                "[{0}] scalar/context and handle/post routes must return the same leaf identity.",
                scenario.DisplayName
            );
            handler.active = false;
        }

        [Test]
        public void EmptyPriorityLeafSpareHasCapacityOneAndResetDrainsIt()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus, label: nameof(MessageBus));
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler firstHandler = new MessageHandler(new InstanceId(0x5A17_0010), bus)
            {
                active = true,
            };
            MessageHandler secondHandler = new MessageHandler(new InstanceId(0x5A17_0011), bus)
            {
                active = true,
            };
            MessageBusRegistration first = bus.RegisterUntargeted<UntargetedOne>(
                firstHandler,
                priority: 0
            );
            MessageBusRegistration second = bus.RegisterUntargeted<UntargetedOne>(
                secondHandler,
                priority: 1
            );

            bus.Deregister<UntargetedOne>(in first);
            object retained = bus.RecycledHandlerCacheForTesting;
            bus.Deregister<UntargetedOne>(in second);

            Assert.AreSame(
                retained,
                bus.RecycledHandlerCacheForTesting,
                "Returning a second empty leaf must not replace or grow the one-entry spare."
            );
            bus.ResetState();
            Assert.IsNull(
                bus.RecycledHandlerCacheForTesting,
                "ResetState must release the detached empty priority leaf."
            );
            firstHandler.active = false;
            secondHandler.active = false;
        }

        [Test]
        public void EmptyPriorityLeafHonorsZeroAndHighWaterCaps()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                settings._bufferMaxDistinctEntries = 2;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
                MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
                using IDisposable cleanup = ForceTrimCleanup(bus);
                MessageHandler firstHandler = new MessageHandler(new InstanceId(0x5A17_0020), bus)
                {
                    active = true,
                };
                MessageHandler secondHandler = new MessageHandler(new InstanceId(0x5A17_0021), bus)
                {
                    active = true,
                };
                MessageBusRegistration first = bus.RegisterUntargeted<UntargetedOne>(
                    firstHandler,
                    priority: 0
                );
                MessageBusRegistration second = bus.RegisterUntargeted<UntargetedOne>(
                    secondHandler,
                    priority: 0
                );
                bus.Deregister<UntargetedOne>(in first);
                bus.Deregister<UntargetedOne>(in second);

                Assert.AreEqual(
                    2,
                    bus.RecycledHandlerCacheHighWaterForTesting,
                    "A leaf at the configured distinct-handler cap must remain eligible."
                );

                settings._bufferMaxDistinctEntries = 1;
                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                Assert.IsNull(
                    bus.RecycledHandlerCacheForTesting,
                    "Lowering the cap below a retained leaf's high-water must drain it immediately."
                );

                first = bus.RegisterUntargeted<UntargetedOne>(firstHandler, priority: 0);
                second = bus.RegisterUntargeted<UntargetedOne>(secondHandler, priority: 0);
                bus.Deregister<UntargetedOne>(in first);
                bus.Deregister<UntargetedOne>(in second);
                Assert.IsNull(
                    bus.RecycledHandlerCacheForTesting,
                    "An active leaf whose high-water exceeds the lowered cap must drop on return."
                );

                MessageBusRegistration small = bus.RegisterUntargeted<UntargetedOne>(
                    firstHandler,
                    priority: 0
                );
                bus.Deregister<UntargetedOne>(in small);
                Assert.AreEqual(
                    1,
                    bus.RecycledHandlerCacheHighWaterForTesting,
                    "A replacement leaf within the cap must be eligible for reuse."
                );

                settings._bufferMaxDistinctEntries = 0;
                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                Assert.IsNull(
                    bus.RecycledHandlerCacheForTesting,
                    "Hot reload to cap zero must drain the retained leaf immediately."
                );
                MessageBusRegistration zeroCap = bus.RegisterUntargeted<UntargetedOne>(
                    firstHandler,
                    priority: 0
                );
                bus.Deregister<UntargetedOne>(in zeroCap);
                Assert.IsNull(
                    bus.RecycledHandlerCacheForTesting,
                    "Cap zero must keep subsequent empty priority leaves out of retention."
                );
                firstHandler.active = false;
                secondHandler.active = false;
                GC.KeepAlive(bus);
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        /// <remarks>
        /// 2026-09-04: Forced trim also counts the bus-owned reflexive scratch state.
        /// Drain the empty bus before registering the measured priority leaf so the exact
        /// eviction count excludes unrelated initial storage and shared pooled collections.
        /// </remarks>
        [Test]
        public void ForcedTrimCountsPriorityLeafOnceAndThenIsIdempotent()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
            MessageHandler handler = CreateActiveHandler(bus);
            _ = bus.Trim(force: true);
            MessageBusRegistration registration = bus.RegisterUntargeted<UntargetedOne>(
                handler,
                priority: 0
            );
            bus.Deregister<UntargetedOne>(in registration);

            IMessageBus.TrimResult first = bus.Trim(force: true);
            IMessageBus.TrimResult second = bus.Trim(force: true);

            Assert.AreEqual(
                1,
                first.PooledCollectionsEvicted,
                "Forced trim must count the one detached priority leaf exactly once."
            );
            Assert.AreEqual(
                0,
                second.PooledCollectionsEvicted,
                "Repeated forced trim must not count an already-drained priority leaf."
            );
            Assert.IsNull(
                bus.RecycledHandlerCacheForTesting,
                "Forced trim must release the detached priority leaf."
            );
            handler.active = false;
        }

        [Test]
        public void RuntimeSettingsHotReloadUpdatesTrimAndIdleGates()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                settings._enableTrimApi = true;
                settings._evictionEnabled = false;
                settings._idleEvictionSeconds = 30f;
                settings._evictionTickIntervalSeconds = 60f;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
                FakeClock clock = new FakeClock();
                MessageBus bus = MessageBus.CreateForInternalUse(clock);
                using IDisposable cleanup = ForceTrimCleanup(bus);
                MessageRegistrationToken token = CreateEnabledToken(bus);
                MessageRegistrationHandle handle = RegisterFirst(
                    MessageScenario.Untargeted(),
                    token,
                    DefaultContext
                );
                token.RemoveRegistration(handle);

                clock.Advance(60d);
                EmitSweepSampleWindow(bus);
                Assert.GreaterOrEqual(
                    bus.OccupiedTypeSlots,
                    1,
                    "Initial disabled idle eviction setting must prevent emit-time sweep."
                );

                settings._enableTrimApi = false;
                settings._evictionEnabled = true;
                settings._idleEvictionSeconds = 0f;
                settings._evictionTickIntervalSeconds = 0f;
                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                Assert.AreEqual(default(IMessageBus.TrimResult), bus.Trim(force: true));

                EmitSweepSampleWindow(bus);
                Assert.AreEqual(
                    0,
                    bus.OccupiedTypeSlots,
                    "Hot-reloaded idle settings must enable emit-time reclamation on an existing bus."
                );
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
                _ = DxPools.TrimAll(force: true);
            }
        }

        [Test]
        public void TrimRespectsEnableTrimApiFlag()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                settings._enableTrimApi = false;
                settings._evictionEnabled = true;
                settings._bufferMaxDistinctEntries = 4;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
                MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock());
                MessageRegistrationToken token = CreateEnabledToken(bus);
                MessageRegistrationHandle handle = RegisterFirst(
                    MessageScenario.Untargeted(),
                    token,
                    DefaultContext
                );
                token.RemoveRegistration(handle);
                object retainedHandlerCache = bus.RecycledHandlerCacheForTesting;
                Assert.IsNotNull(
                    retainedHandlerCache,
                    "Removing the sole handler must create the retained empty priority leaf."
                );
                List<object> pooled = DxPools.ObjectLists.Rent();
                DxPools.ObjectLists.Return(pooled);
                int cachedBefore = DxPools.DescribeAll().ObjectLists.Cached;

                IMessageBus.TrimResult result = bus.Trim(force: true);

                Assert.AreEqual(default(IMessageBus.TrimResult), result);
                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);
                Assert.AreEqual(cachedBefore, DxPools.DescribeAll().ObjectLists.Cached);
                Assert.AreSame(
                    retainedHandlerCache,
                    bus.RecycledHandlerCacheForTesting,
                    "Disabled Trim must preserve the retained empty priority leaf identity."
                );
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
                _ = DxPools.TrimAll(force: true);
            }
        }

        [Test]
        public void TrimAfterDeregisterReclaimsHandlerCache(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                settings._bufferMaxDistinctEntries = 4;
                settings._bufferUseLruEviction = true;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
                _ = DxPools.TrimAll(force: true);
                MessageBus bus = MessageBus.CreateForInternalUse(
                    new FakeClock(),
                    idleEvictionTicks: 0
                );
                using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                    bus,
                    label: scenario.DisplayName
                );
                using IDisposable cleanup = ForceTrimCleanup(bus);
                MessageRegistrationToken token = CreateEnabledToken(bus);
                MessageRegistrationHandle handle = RegisterFirst(scenario, token, DefaultContext);
                int priorityDictionariesBefore = DxPools
                    .DescribeAll()
                    .TypedHandlerPriorityDicts.Cached;
                token.RemoveRegistration(handle);
                Assert.Greater(
                    DxPools.DescribeAll().TypedHandlerPriorityDicts.Cached,
                    priorityDictionariesBefore,
                    "[{0}] deregistration outside dispatch must return the empty priority dictionary to the pool.",
                    scenario.Kind
                );
                EmitSweepProbe(bus);
                int contextDictionariesBefore = DxPools
                    .DescribeAll()
                    .TypedHandlerContextDicts.Cached;

                IMessageBus.TrimResult result = bus.Trim(force: false);

                Assert.GreaterOrEqual(
                    result.TypeSlotsEvicted,
                    1,
                    "[{0}] non-force trim must reclaim idle empty typed-handler slots.",
                    scenario.Kind
                );
                Assert.Greater(
                    DxPools.DescribeAll().TypedHandlerContextDicts.Cached,
                    contextDictionariesBefore,
                    "[{0}] trim must return the typed-handler context dictionary to the pool.",
                    scenario.Kind
                );
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
                _ = DxPools.TrimAll(force: true);
            }
        }

        [Test]
        public void BusContextDictReturnsToPool(
            [ValueSource(nameof(ContextDictPoolScenarios))] MessageScenario scenario
        )
        {
            // Pin the AppDomain-scoped ContextHandlerByTargetDicts pool's MaxRetained to a
            // known >0 value for the duration of this test. A sibling test that drops it to
            // 0 (e.g. RuntimeSettingsHotReloadAppliesCaps) and runs first under a randomized
            // execution order would otherwise make the trim's Return path drop the dict on
            // the floor instead of caching it, breaking the assertion below. Mirrors the
            // pattern in TrimAfterDeregisterReclaimsHandlerCache.
            DxMessagingRuntimeSettings settings = null;
            IDisposable overrideToken = null;
            Action firstDeregister = null;
            Action secondDeregister = null;
            try
            {
                MessageBus bus = CreatePoolRetainingBus(
                    new FakeClock(),
                    out settings,
                    out overrideToken
                );
                using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                    bus,
                    label: scenario.DisplayName
                );
                using IDisposable cleanup = ForceTrimCleanup(bus);
                MessageHandler handler = CreateActiveHandler(bus);

                // The bus's context-dict pool is AppDomain-scoped (shared across all MessageBus
                // instances). Capture the baseline AFTER the registration's rent so the delta
                // cleanly measures the trim's contribution alone, regardless of whatever entries
                // prior fixtures left in the pool. (Capturing before the rent would produce a
                // net-zero delta: the rent decrements Cached, the trim's return increments it back.)
                firstDeregister = RegisterDirect(scenario, handler, bus, DefaultContext, () => { });
                CollectionPoolDiagnostics afterFirstRent =
                    bus.GetContextDictPoolDiagnosticsForTesting();
                firstDeregister();
                firstDeregister = null;
                EmitSweepProbe(bus);

                IMessageBus.TrimResult firstTrim = bus.Trim(force: false);
                CollectionPoolDiagnostics afterReturn =
                    bus.GetContextDictPoolDiagnosticsForTesting();

                Assert.GreaterOrEqual(
                    firstTrim.TargetSlotsEvicted,
                    1,
                    "[{0}] trim must reclaim the empty context dictionary slot. firstTrim={1}",
                    scenario.Kind,
                    firstTrim
                );
                Assert.Greater(
                    afterReturn.Cached,
                    afterFirstRent.Cached,
                    "[{0}] trim must return the bus context dictionary to the pool. afterFirstRent.Cached={1}, afterReturn.Cached={2}",
                    scenario.Kind,
                    afterFirstRent.Cached,
                    afterReturn.Cached
                );

                secondDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => { }
                );
                CollectionPoolDiagnostics afterRent = bus.GetContextDictPoolDiagnosticsForTesting();

                Assert.Greater(
                    afterRent.Hits,
                    afterReturn.Hits,
                    "[{0}] the next context registration must reuse a pooled dictionary. afterReturn.Hits={1}, afterRent.Hits={2}",
                    scenario.Kind,
                    afterReturn.Hits,
                    afterRent.Hits
                );

                secondDeregister();
                secondDeregister = null;
            }
            finally
            {
                firstDeregister?.Invoke();
                secondDeregister?.Invoke();
                overrideToken?.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void StaleBusContextDeregisterAfterPooledDictionaryReuseDoesNotRemoveReplacement(
            [ValueSource(nameof(ContextDictPoolScenarios))] MessageScenario scenario
        )
        {
            DxMessagingRuntimeSettings settings = null;
            IDisposable overrideToken = null;
            MessageBus bus = CreatePoolRetainingBus(
                new FakeClock(),
                out settings,
                out overrideToken
            );
            int staleCalls = 0;
            int currentCalls = 0;
            List<string> logs = new List<string>();
            Action<LogLevel, string> previousLogFunction = MessagingDebug.LogFunction;
            bool previousMessagingDebugEnabled = MessagingDebug.enabled;
            Action currentDeregister = null;

            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            try
            {
                Action staleDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => staleCalls++
                );
                EmitFirst(scenario, bus, DefaultContext);
                staleDeregister();
                EmitSweepProbe(bus);

                IMessageBus.TrimResult trimResult = bus.Trim(force: false);
                CollectionPoolDiagnostics afterReturn =
                    bus.GetContextDictPoolDiagnosticsForTesting();

                Assert.GreaterOrEqual(
                    trimResult.TargetSlotsEvicted,
                    1,
                    "[{0}] trim must reclaim the stale context slot before replacement.",
                    scenario.Kind
                );
                Assert.Greater(
                    afterReturn.Cached,
                    0,
                    "[{0}] trim must return the bus context dictionary to the private pool.",
                    scenario.Kind
                );

                currentDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => currentCalls++
                );
                CollectionPoolDiagnostics afterRent = bus.GetContextDictPoolDiagnosticsForTesting();

                Assert.Greater(
                    afterRent.Hits,
                    afterReturn.Hits,
                    "[{0}] replacement registration must reuse the pooled context dictionary.",
                    scenario.Kind
                );

                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (_, message) => logs.Add(message);
                staleDeregister();
                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(
                    1,
                    staleCalls,
                    "[{0}] stale deregistration must not revive the old registration.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    currentCalls,
                    "[{0}] stale deregistration must not remove the replacement registration.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    logs.Count,
                    "[{0}] stale deregistration after pooled dictionary reuse must not log diagnostics.",
                    scenario.Kind
                );

                currentDeregister();
                currentDeregister = null;
                _ = bus.Trim(force: true);
            }
            finally
            {
                MessagingDebug.enabled = previousMessagingDebugEnabled;
                MessagingDebug.LogFunction = previousLogFunction;
                currentDeregister?.Invoke();
                _ = bus.Trim(force: true);
                overrideToken?.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void OversizedDirtyTargetAndContextPoolsDropHighWaterCollections()
        {
            DxMessagingRuntimeSettings settings = null;
            IDisposable overrideToken = null;
            MessageBus bus = CreatePoolRetainingBus(
                new FakeClock(),
                out settings,
                out overrideToken
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            List<Action> deregistrations = new List<Action>(RetainedPoolEntryCount + 1);
            Action smallDeregister = null;
            try
            {
                _ = DxPools.TrimAll(force: true);
                MessageBus.ResetStaticPools();

                for (int i = 0; i <= RetainedPoolEntryCount; ++i)
                {
                    InstanceId target = new InstanceId(0x5A18_0000 + i);
                    deregistrations.Add(
                        RegisterDirect(MessageScenario.Targeted(), handler, bus, target, () => { })
                    );
                }

                foreach (Action deregister in deregistrations)
                {
                    deregister();
                }
                deregistrations.Clear();

                EmitSweepProbe(bus);
                IMessageBus.TrimResult oversizedTrim = bus.Trim(force: false);
                PoolDiagnosticsSnapshot oversizedPools = DxPools.DescribeAll();
                CollectionPoolDiagnostics oversizedContextPool =
                    bus.GetContextDictPoolDiagnosticsForTesting();

                Assert.GreaterOrEqual(
                    oversizedTrim.TargetSlotsEvicted,
                    RetainedPoolEntryCount + 1,
                    "Trim must reclaim every oversized target slot before evaluating pool retention."
                );
                Assert.AreEqual(
                    0,
                    oversizedPools.InstanceIdLists.Cached,
                    "Dirty-target lists that exceeded the pool cap must be dropped instead of cached."
                );
                Assert.AreEqual(
                    0,
                    oversizedPools.InstanceIdSets.Cached,
                    "Dirty-target sets that exceeded the pool cap must be dropped instead of cached."
                );
                Assert.AreEqual(
                    0,
                    oversizedContextPool.Cached,
                    "Bus context dictionaries that exceeded the pool cap must be dropped instead of cached."
                );

                smallDeregister = RegisterDirect(
                    MessageScenario.Targeted(),
                    handler,
                    bus,
                    DefaultContext,
                    () => { }
                );
                smallDeregister();
                smallDeregister = null;
                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);

                PoolDiagnosticsSnapshot smallPools = DxPools.DescribeAll();
                CollectionPoolDiagnostics smallContextPool =
                    bus.GetContextDictPoolDiagnosticsForTesting();
                Assert.Greater(
                    smallPools.InstanceIdLists.Cached,
                    0,
                    "Small dirty-target lists should still return to the pool."
                );
                Assert.Greater(
                    smallPools.InstanceIdSets.Cached,
                    0,
                    "Small dirty-target sets should still return to the pool."
                );
                Assert.Greater(
                    smallContextPool.Cached,
                    0,
                    "Small bus context dictionaries should still return to the private pool."
                );
            }
            finally
            {
                smallDeregister?.Invoke();
                foreach (Action deregistration in deregistrations)
                {
                    deregistration();
                }
                _ = bus.Trim(force: true);
                overrideToken?.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void StaticResetDrainsBusContextDictionaryPool()
        {
            DxMessagingRuntimeSettings settings = null;
            IDisposable overrideToken = null;
            MessageBus bus = CreatePoolRetainingBus(
                new FakeClock(),
                out settings,
                out overrideToken
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus);
            Action deregister = null;
            try
            {
                MessageBus.ResetStaticPools();
                MessageHandler handler = CreateActiveHandler(bus);
                deregister = RegisterDirect(
                    MessageScenario.Targeted(),
                    handler,
                    bus,
                    DefaultContext,
                    () => { }
                );
                deregister();
                deregister = null;
                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);

                Assert.Greater(
                    bus.GetContextDictPoolDiagnosticsForTesting().Cached,
                    0,
                    "Setup must return at least one bus context dictionary to the static pool."
                );

                DxMessagingStaticState.Reset();

                Assert.AreEqual(
                    0,
                    bus.GetContextDictPoolDiagnosticsForTesting().Cached,
                    "DxMessagingStaticState.Reset must drain the bus-owned static context dictionary pool."
                );
            }
            finally
            {
                deregister?.Invoke();
                _ = bus.Trim(force: true);
                overrideToken?.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void OversizedContextPoolDropsAfterPartialReclaim()
        {
            DxMessagingRuntimeSettings settings = null;
            IDisposable overrideToken = null;
            MessageBus bus = CreatePoolRetainingBus(
                new FakeClock(),
                out settings,
                out overrideToken
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            List<Action> deregistrations = new List<Action>(RetainedPoolEntryCount + 1);
            try
            {
                MessageBus.ResetStaticPools();

                for (int i = 0; i <= RetainedPoolEntryCount; ++i)
                {
                    InstanceId target = new InstanceId(0x5A19_0000 + i);
                    deregistrations.Add(
                        RegisterDirect(MessageScenario.Targeted(), handler, bus, target, () => { })
                    );
                }

                for (int i = 0; i < RetainedPoolEntryCount; ++i)
                {
                    deregistrations[i]();
                    deregistrations[i] = null;
                }

                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);

                Assert.AreEqual(
                    0,
                    bus.GetContextDictPoolDiagnosticsForTesting().Cached,
                    "The oversized context dictionary must remain live while one target is still registered."
                );

                deregistrations[RetainedPoolEntryCount]();
                deregistrations[RetainedPoolEntryCount] = null;
                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);

                Assert.AreEqual(
                    0,
                    bus.GetContextDictPoolDiagnosticsForTesting().Cached,
                    "The oversized context dictionary must be dropped even when it empties across multiple sweeps."
                );
            }
            finally
            {
                foreach (Action deregistration in deregistrations)
                {
                    deregistration?.Invoke();
                }
                _ = bus.Trim(force: true);
                overrideToken?.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void ResetGenerationBumpInvalidatesPostEvictDeregister(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            int currentCalls = 0;
            int staleCalls = 0;
            List<string> logs = new List<string>();
            Action<LogLevel, string> previousLogFunction = MessagingDebug.LogFunction;
            bool previousMessagingDebugEnabled = MessagingDebug.enabled;
            Action currentDeregister = null;

            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            try
            {
                Action staleDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => staleCalls++
                );
                staleDeregister();
                _ = bus.Trim(force: true);

                currentDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => currentCalls++
                );
                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (_, message) => logs.Add(message);
                staleDeregister();
                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(
                    0,
                    staleCalls,
                    "[{0}] stale deregistration must not revive or invoke the old handler.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    currentCalls,
                    "[{0}] stale deregistration must not remove a later registration.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    logs.Count,
                    "[{0}] stale deregistration must not log diagnostics.",
                    scenario.Kind
                );
                currentDeregister();
                currentDeregister = null;
                _ = bus.Trim(force: true);
            }
            finally
            {
                MessagingDebug.enabled = previousMessagingDebugEnabled;
                MessagingDebug.LogFunction = previousLogFunction;
                currentDeregister?.Invoke();
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void HandlerBusCachesTrackOnlyUsedBusesAndReclaimEmptyEntries(
            [Values(false, true)] bool force,
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus firstBus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0
            );
            for (int i = 0; i < 32; i++)
            {
                _ = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            }

            MessageBus secondBus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = CreateActiveHandler(firstBus);
            using LeakWatcher firstWatcher = LeakWatcher.WatchWithSlots(
                firstBus,
                label: scenario.DisplayName
            );
            using LeakWatcher secondWatcher = LeakWatcher.WatchWithSlots(
                secondBus,
                label: scenario.DisplayName
            );
            Action firstDeregister = null;
            Action secondDeregister = null;
            int firstCalls = 0;
            int secondCalls = 0;
            try
            {
                Assert.AreEqual(
                    0,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] a fresh handler must not allocate bus cache entries.",
                    scenario.Kind
                );
                Assert.IsFalse(
                    handler.HasTypedHandlersForBus(secondBus),
                    "[{0}] probing an unused high-index bus must report no handlers.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] probing an unused bus must not populate the bus map.",
                    scenario.Kind
                );

                secondDeregister = RegisterDirect(
                    scenario,
                    handler,
                    secondBus,
                    DefaultContext,
                    () => secondCalls++
                );
                Assert.AreEqual(
                    1,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] one used bus must allocate one cache regardless of earlier bus indices.",
                    scenario.Kind
                );
                firstDeregister = RegisterDirect(
                    scenario,
                    handler,
                    firstBus,
                    DefaultContext,
                    () => firstCalls++
                );
                Assert.AreEqual(
                    2,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] two used buses must retain exactly two cache entries.",
                    scenario.Kind
                );

                EmitFirst(scenario, firstBus, DefaultContext);
                EmitFirst(scenario, secondBus, DefaultContext);
                Assert.AreEqual(
                    1,
                    firstCalls,
                    "[{0}] the first bus must dispatch only its registration.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    secondCalls,
                    "[{0}] the second bus must dispatch only its registration.",
                    scenario.Kind
                );
                Assert.Greater(
                    secondBus.OccupiedTypeSlots,
                    0,
                    "[{0}] registration must populate bus type slots before reclamation.",
                    scenario.Kind
                );

                Action staleDeregister = secondDeregister;
                secondDeregister();
                secondDeregister = null;
                EmitSweepProbe(secondBus);
                _ = secondBus.Trim(force);
                Assert.AreEqual(
                    1,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] trimming an empty bus must reclaim its cache and retain the active bus.",
                    scenario.Kind
                );
                Assert.IsFalse(
                    handler.HasTypedHandlersForBus(secondBus),
                    "[{0}] the trimmed bus must have no typed handlers.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    secondBus.OccupiedTypeSlots,
                    "[{0}] the trimmed bus must reclaim its occupied type slots.",
                    scenario.Kind
                );
                EmitFirst(scenario, firstBus, DefaultContext);
                Assert.AreEqual(
                    2,
                    firstCalls,
                    "[{0}] reclaiming another bus must preserve active registrations.",
                    scenario.Kind
                );

                secondDeregister = RegisterDirect(
                    scenario,
                    handler,
                    secondBus,
                    DefaultContext,
                    () => secondCalls++
                );
                staleDeregister();
                EmitFirst(scenario, secondBus, DefaultContext);
                Assert.AreEqual(
                    2,
                    secondCalls,
                    "[{0}] stale cleanup must not remove the replacement bus cache registration.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    2,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] re-registering the reclaimed bus must create just one replacement cache.",
                    scenario.Kind
                );
            }
            finally
            {
                firstDeregister?.Invoke();
                secondDeregister?.Invoke();
                _ = firstBus.Trim(force: true);
                _ = secondBus.Trim(force: true);
            }

            Assert.AreEqual(
                0,
                handler._handlersByTypeByMessageBus.Count,
                "[{0}] cleanup must reclaim every empty bus cache entry.",
                scenario.Kind
            );
        }

        [Test]
        public void ReentrantBusResetReclaimsCacheAndPreservesReplacementRegistration(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            MessageHandler handler = CreateActiveHandler(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            Action staleDeregister = null;
            Action replacementDeregister = null;
            int originalCalls = 0;
            int replacementCalls = 0;
            try
            {
                staleDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () =>
                    {
                        originalCalls++;
                        bus.ResetState();
                        Assert.AreEqual(
                            0,
                            handler._handlersByTypeByMessageBus.Count,
                            "[{0}] resetting during dispatch must release the old bus cache.",
                            scenario.Kind
                        );
                        replacementDeregister = RegisterDirect(
                            scenario,
                            handler,
                            bus,
                            DefaultContext,
                            () => replacementCalls++
                        );
                    }
                );

                EmitFirst(scenario, bus, DefaultContext);
                Assert.AreEqual(
                    1,
                    originalCalls,
                    "[{0}] the original callback must execute once before resetting its bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    replacementCalls,
                    "[{0}] the replacement must not join the emission that reset the bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] registration after reset must retain one replacement cache.",
                    scenario.Kind
                );

                staleDeregister();
                EmitFirst(scenario, bus, DefaultContext);
                Assert.AreEqual(
                    1,
                    originalCalls,
                    "[{0}] reset must leave the original registration inactive.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    replacementCalls,
                    "[{0}] stale deregistration must preserve the replacement after reset.",
                    scenario.Kind
                );
            }
            finally
            {
                staleDeregister?.Invoke();
                replacementDeregister?.Invoke();
                _ = bus.Trim(force: true);
            }

            Assert.AreEqual(
                0,
                handler._handlersByTypeByMessageBus.Count,
                "[{0}] replacement cleanup must release the last bus cache.",
                scenario.Kind
            );
        }

        [Test]
        public void ContextCleanupPreservesFrozenHandlersAndReusesRetiredDictionary(
            [Values(false, true)] bool forceDuringDispatch,
            [Values(false, true)] bool ageDuringDispatch,
            [ValueSource(nameof(ContextDictPoolScenarios))] MessageScenario scenario
        )
        {
            MessageBus bus = CreatePoolRetainingBus(
                new FakeClock(),
                out DxMessagingRuntimeSettings settings,
                out IDisposable overrideToken
            );
            MessageHandler handler = CreateActiveHandler(bus);
            InstanceId anchorContext = new InstanceId(DefaultContext.Id + 1);
            Action anchorDeregister = null;
            Action firstDeregister = null;
            Action trailingDeregister = null;
            Action replacementDeregister = null;
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName,
                handler: handler
            );
            try
            {
                _ = DxPools.TrimAll(force: true);
                int anchorCalls = 0;
                int firstCalls = 0;
                int trailingCalls = 0;
                int replacementCalls = 0;
                anchorDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    anchorContext,
                    () => anchorCalls++
                );
                firstDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () =>
                    {
                        firstCalls++;
                        firstDeregister();
                        trailingDeregister();
                        Assert.IsTrue(
                            handler.HasPendingContextCleanup(bus),
                            "[{0}] removing the last context handlers during dispatch must queue cleanup.",
                            scenario.DisplayName
                        );
                        if (ageDuringDispatch)
                        {
                            EmitSweepProbe(bus);
                        }
                        _ = bus.Trim(forceDuringDispatch);
                        EmitFirst(scenario, bus, anchorContext);
                    }
                );
                trailingDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => trailingCalls++
                );
                Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> contexts =
                    ReadContextHandlers(scenario, handler, bus);
                Dictionary<int, IHandlerActionCache> retiredPriorities = contexts[DefaultContext];

                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(
                    1,
                    firstCalls,
                    "[{0}] the removing callback must run once.",
                    scenario.DisplayName
                );
                Assert.AreEqual(
                    1,
                    trailingCalls,
                    "[{0}] the frozen trailing callback must survive deregistration and trim.",
                    scenario.DisplayName
                );
                Assert.AreEqual(
                    1,
                    anchorCalls,
                    "[{0}] reentrant dispatch must preserve the other live context.",
                    scenario.DisplayName
                );
                Assert.IsTrue(
                    contexts.ContainsKey(DefaultContext),
                    "[{0}] the retired context must remain pending until a sweep outside dispatch.",
                    scenario.DisplayName
                );

                EmitSweepProbe(bus);
                _ = bus.Trim(force: false);
                Assert.IsFalse(
                    contexts.ContainsKey(DefaultContext),
                    "[{0}] the sweep must reclaim pending context storage even after an in-dispatch trim.",
                    scenario.DisplayName
                );
                Assert.IsFalse(
                    handler.HasPendingContextCleanup(bus),
                    "[{0}] the completed sweep must return pending-context tracking storage.",
                    scenario.DisplayName
                );
                Assert.AreEqual(
                    1,
                    contexts.Count,
                    "[{0}] only the anchor context may remain after cleanup.",
                    scenario.DisplayName
                );

                replacementDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => replacementCalls++
                );
                Assert.AreSame(
                    retiredPriorities,
                    contexts[DefaultContext],
                    "[{0}] the replacement context must reuse the returned priority dictionary.",
                    scenario.DisplayName
                );
                firstDeregister();
                trailingDeregister();
                EmitFirst(scenario, bus, DefaultContext);
                Assert.AreEqual(
                    1,
                    replacementCalls,
                    "[{0}] stale deregistration must preserve the replacement after dictionary reuse.",
                    scenario.DisplayName
                );
                Assert.AreEqual(
                    1,
                    trailingCalls,
                    "[{0}] later dispatch must not revive the retired snapshot.",
                    scenario.DisplayName
                );
            }
            finally
            {
                firstDeregister?.Invoke();
                trailingDeregister?.Invoke();
                replacementDeregister?.Invoke();
                anchorDeregister?.Invoke();
                _ = bus.Trim(force: true);
                overrideToken.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static Dictionary<
            InstanceId,
            Dictionary<int, IHandlerActionCache>
        > ReadContextHandlers(MessageScenario scenario, MessageHandler handler, MessageBus bus)
        {
            if (scenario.Kind == MessageKind.Targeted)
            {
                bool exists = handler
                    ._handlersByTypeByMessageBus[bus.RegisteredGlobalSequentialIndex]
                    .TryGetValue<TargetedOne>(out object untyped);
                Assert.IsTrue(
                    exists,
                    "[{0}] the registered targeted handler must exist.",
                    scenario.DisplayName
                );
                int index = scenario.UsePostProcessor
                    ? TypedSlotIndex.TargetedPostProcessDefault
                    : TypedSlotIndex.TargetedHandleDefault;
                return ((MessageHandler.TypedHandler<TargetedOne>)untyped)._slots[index].byContext;
            }
            if (scenario.Kind == MessageKind.Broadcast)
            {
                bool exists = handler
                    ._handlersByTypeByMessageBus[bus.RegisteredGlobalSequentialIndex]
                    .TryGetValue<BroadcastOne>(out object untyped);
                Assert.IsTrue(
                    exists,
                    "[{0}] the registered broadcast handler must exist.",
                    scenario.DisplayName
                );
                int index = scenario.UsePostProcessor
                    ? TypedSlotIndex.BroadcastPostProcessDefault
                    : TypedSlotIndex.BroadcastHandleDefault;
                return ((MessageHandler.TypedHandler<BroadcastOne>)untyped)._slots[index].byContext;
            }
            throw UnsupportedScenario(scenario);
        }

        [Test]
        public void TypedHandlerOuterWrapperReclaimedAfterTrim(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            Action deregisterFirst = null;
            Action deregisterSecond = null;
            Action deregisterThird = null;
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            try
            {
                deregisterFirst = RegisterDirect(scenario, handler, bus, DefaultContext, () => { });
                deregisterSecond = RegisterDirectSecond(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => { }
                );
                deregisterThird = RegisterDirectThird(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => { }
                );

                Assert.IsTrue(
                    CountHandlerTypeCacheEntries(handler, bus) >= 3,
                    "[{0}] typed-handler wrappers must exist while registrations are live.",
                    scenario.Kind
                );

                deregisterFirst();
                deregisterFirst = null;
                deregisterSecond();
                deregisterSecond = null;
                deregisterThird();
                deregisterThird = null;

                Assert.IsTrue(
                    CountHandlerTypeCacheEntries(handler, bus) >= 3,
                    "[{0}] empty typed-handler wrappers must remain until trim.",
                    scenario.Kind
                );

                _ = bus.Trim(force: true);

                Assert.AreEqual(
                    0,
                    CountHandlerTypeCacheEntries(handler, bus),
                    "[{0}] trim must remove empty typed-handler outer wrappers.",
                    scenario.Kind
                );
            }
            finally
            {
                deregisterFirst?.Invoke();
                deregisterSecond?.Invoke();
                deregisterThird?.Invoke();
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void TypedHandlerOuterWrappersReclaimedAtScaleAfterTrim()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            List<Action> deregistrations = new List<Action>(1024);
            MethodInfo registerMethod = typeof(MemoryReclamationTests).GetMethod(
                nameof(RegisterUntargetedGenericDirect),
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Type[] markerTypes = RegistrationFloodMarkerTypes;

            try
            {
                foreach (Type outerMarker in markerTypes)
                {
                    foreach (Type innerMarker in markerTypes)
                    {
                        Type messageType = typeof(RegistrationFloodMessage<,>).MakeGenericType(
                            outerMarker,
                            innerMarker
                        );
                        deregistrations.Add(
                            (Action)
                                registerMethod
                                    .MakeGenericMethod(messageType)
                                    .Invoke(null, new object[] { handler, bus })
                        );
                    }
                }

                Assert.AreEqual(
                    1024,
                    CountHandlerTypeCacheEntries(handler, bus),
                    "Scale setup must create one typed-handler wrapper per distinct message type."
                );

                foreach (Action deregister in deregistrations)
                {
                    deregister();
                }

                deregistrations.Clear();

                Assert.AreEqual(
                    1024,
                    CountHandlerTypeCacheEntries(handler, bus),
                    "Empty typed-handler wrappers must remain in the sparse cache until trim."
                );

                _ = bus.Trim(force: true);

                Assert.AreEqual(
                    0,
                    CountHandlerTypeCacheEntries(handler, bus),
                    "Trim must remove every empty typed-handler wrapper from the sparse cache."
                );
            }
            finally
            {
                foreach (Action deregistration in deregistrations)
                {
                    deregistration();
                }

                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void DirtyHandlerCompactedAfterTrim()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            Action deregister = RegisterDirect(
                MessageScenario.Untargeted(),
                handler,
                bus,
                DefaultContext,
                () => { }
            );

            deregister();

            Assert.GreaterOrEqual(
                CountDirtyHandlers(bus),
                1,
                "Deregistering the last typed handler must dirty the owning MessageHandler."
            );

            _ = bus.Trim(force: true);

            Assert.AreEqual(
                0,
                CountDirtyHandlers(bus),
                "Trim must compact dirty-handler candidates after their typed wrappers are reclaimed."
            );
        }

        [Test]
        public void EmitAfterOuterReclamationDispatchesReplacementOnly(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            using IDisposable cleanup = ForceTrimCleanup(bus);
            MessageHandler handler = CreateActiveHandler(bus);
            int staleCalls = 0;
            int currentCalls = 0;
            Action staleDeregister = null;
            Action currentDeregister = null;
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            try
            {
                staleDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => staleCalls++
                );

                EmitFirst(scenario, bus, DefaultContext);
                Assert.AreEqual(
                    1,
                    staleCalls,
                    "[{0}] setup emission must prove the stale registration was reachable.",
                    scenario.DisplayName
                );

                staleDeregister();
                staleDeregister = null;
                IMessageBus.TrimResult trimResult = bus.Trim(force: true);
                Assert.GreaterOrEqual(
                    trimResult.TypeSlotsEvicted,
                    1,
                    "[{0}] trim must reclaim the empty typed-handler wrapper before replacement. Result={1}",
                    scenario.DisplayName,
                    trimResult
                );

                currentDeregister = RegisterDirect(
                    scenario,
                    handler,
                    bus,
                    DefaultContext,
                    () => currentCalls++
                );

                EmitFirst(scenario, bus, DefaultContext);

                Assert.AreEqual(
                    1,
                    staleCalls,
                    "[{0}] stale bus dispatch state must not fire the reclaimed registration.",
                    scenario.DisplayName
                );
                Assert.AreEqual(
                    1,
                    currentCalls,
                    "[{0}] replacement registration must dispatch through the production bus path.",
                    scenario.DisplayName
                );
            }
            finally
            {
                staleDeregister?.Invoke();
                currentDeregister?.Invoke();
                _ = bus.Trim(force: true);
            }
        }

        private static MessageRegistrationToken CreateEnabledToken(MessageBus bus)
        {
            MessageHandler handler = CreateActiveHandler(bus);
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();
            return token;
        }

        private static MessageHandler CreateActiveHandler(MessageBus bus)
        {
            return new MessageHandler(HandlerOwner, bus) { active = true };
        }

        private static MessageBus CreatePoolRetainingBus(
            IDxMessagingClock clock,
            out DxMessagingRuntimeSettings settings,
            out IDisposable overrideToken
        )
        {
            settings = ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            settings._bufferMaxDistinctEntries = RetainedPoolEntryCount;
            settings._bufferUseLruEviction = true;
            settings._idleEvictionSeconds = 0f;
            settings._evictionTickIntervalSeconds = 0f;
            settings._enableTrimApi = true;
            settings._evictionEnabled = true;
            overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);
            MessageBus bus = MessageBus.CreateForInternalUse(clock, idleEvictionTicks: 0);
            DxPools.Configure(settings);
            DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
            return bus;
        }

        private static IDisposable ForceTrimCleanup(MessageBus bus)
        {
            return new CleanupScope(() =>
            {
                _ = bus.Trim(force: true);
                _ = DxPools.TrimAll(force: true);
            });
        }

        private static MessageRegistrationHandle RegisterFirst(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context
        )
        {
            return RegisterCountingFirst(scenario, token, context, () => { });
        }

        private static MessageRegistrationHandle RegisterSecond(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<UntargetedTwo>((in UntargetedTwo _) => { });
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<TargetedTwo>(context, (in TargetedTwo _) => { });
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<BroadcastTwo>(
                        context,
                        (in BroadcastTwo _) => { }
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static MessageRegistrationHandle RegisterThird(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<UntargetedThree>((in UntargetedThree _) => { });
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<TargetedThree>(
                        context,
                        (in TargetedThree _) => { }
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<BroadcastThree>(
                        context,
                        (in BroadcastThree _) => { }
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static MessageRegistrationHandle RegisterCountingFirst(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onMessage,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<UntargetedOne>(
                        (in UntargetedOne _) => onMessage(),
                        priority: priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<TargetedOne>(
                        context,
                        (in TargetedOne _) => onMessage(),
                        priority: priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<BroadcastOne>(
                        context,
                        (in BroadcastOne _) => onMessage(),
                        priority: priority
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static Action RegisterDirect(
            MessageScenario scenario,
            MessageHandler handler,
            MessageBus bus,
            InstanceId context,
            Action onMessage
        )
        {
            if (scenario.UsePostProcessor)
            {
                return RegisterDirectPostProcessor(scenario, handler, bus, context, onMessage);
            }

            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    Action<UntargetedOne> callback = _ => onMessage();
                    return handler.RegisterUntargetedMessageHandler(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Targeted:
                {
                    Action<TargetedOne> callback = _ => onMessage();
                    return handler.RegisterTargetedMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Broadcast:
                {
                    Action<BroadcastOne> callback = _ => onMessage();
                    return handler.RegisterSourcedBroadcastMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    Action<InstanceId, TargetedOne> callback = (_, _) => onMessage();
                    return handler.RegisterTargetedWithoutTargeting(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    Action<InstanceId, BroadcastOne> callback = (_, _) => onMessage();
                    return handler.RegisterSourcedBroadcastWithoutSource(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static Action RegisterDirectPostProcessor(
            MessageScenario scenario,
            MessageHandler handler,
            MessageBus bus,
            InstanceId context,
            Action onMessage
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    Action<UntargetedOne> callback = _ => onMessage();
                    return handler.RegisterUntargetedPostProcessor(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Targeted:
                {
                    Action<TargetedOne> callback = _ => onMessage();
                    return handler.RegisterTargetedPostProcessor(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Broadcast:
                {
                    Action<BroadcastOne> callback = _ => onMessage();
                    return handler.RegisterSourcedBroadcastPostProcessor(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    Action<InstanceId, TargetedOne> callback = (_, _) => onMessage();
                    return handler.RegisterTargetedWithoutTargetingPostProcessor(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    Action<InstanceId, BroadcastOne> callback = (_, _) => onMessage();
                    return handler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static Action RegisterDirectSecond(
            MessageScenario scenario,
            MessageHandler handler,
            MessageBus bus,
            InstanceId context,
            Action onMessage
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    Action<UntargetedTwo> callback = _ => onMessage();
                    return handler.RegisterUntargetedMessageHandler(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Targeted:
                {
                    Action<TargetedTwo> callback = _ => onMessage();
                    return handler.RegisterTargetedMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Broadcast:
                {
                    Action<BroadcastTwo> callback = _ => onMessage();
                    return handler.RegisterSourcedBroadcastMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static Action RegisterDirectThird(
            MessageScenario scenario,
            MessageHandler handler,
            MessageBus bus,
            InstanceId context,
            Action onMessage
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    Action<UntargetedThree> callback = _ => onMessage();
                    return handler.RegisterUntargetedMessageHandler(
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Targeted:
                {
                    Action<TargetedThree> callback = _ => onMessage();
                    return handler.RegisterTargetedMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                case MessageKind.Broadcast:
                {
                    Action<BroadcastThree> callback = _ => onMessage();
                    return handler.RegisterSourcedBroadcastMessageHandler(
                        context,
                        callback,
                        callback,
                        priority: 0,
                        messageBus: bus
                    );
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static int CountDirtyHandlers(MessageBus bus)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                "_dirtyHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            List<MessageHandler> dirtyHandlers = (List<MessageHandler>)field.GetValue(bus);
            return dirtyHandlers.Count;
        }

        private static int CountHandlerTypeCacheEntries(MessageHandler handler, MessageBus bus)
        {
            int busIndex = bus.RegisteredGlobalSequentialIndex;
            if (!handler._handlersByTypeByMessageBus.ContainsKey(busIndex))
            {
                return 0;
            }

            int count = 0;
            foreach (object typedHandler in handler._handlersByTypeByMessageBus[busIndex])
            {
                if (typedHandler != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static Action RegisterUntargetedGenericDirect<T>(
            MessageHandler handler,
            MessageBus bus
        )
            where T : IUntargetedMessage<T>
        {
            Action<T> callback = _ => { };
            return handler.RegisterUntargetedMessageHandler(
                callback,
                callback,
                priority: 0,
                messageBus: bus
            );
        }

        private static void EmitFirst(MessageScenario scenario, MessageBus bus, InstanceId context)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    UntargetedOne message = new UntargetedOne();
                    bus.UntargetedBroadcast(ref message);
                    return;
                }
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                {
                    TargetedOne message = new TargetedOne();
                    bus.TargetedBroadcast(ref context, ref message);
                    return;
                }
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                {
                    BroadcastOne message = new BroadcastOne();
                    bus.SourcedBroadcast(ref context, ref message);
                    return;
                }
                default:
                {
                    throw UnsupportedScenario(scenario);
                }
            }
        }

        private static void EmitSweepProbe(MessageBus bus)
        {
            SweepProbeMessage message = new SweepProbeMessage();
            bus.UntargetedBroadcast(ref message);
        }

        private static void EmitSweepSampleWindow(MessageBus bus)
        {
            SweepProbeMessage message = new SweepProbeMessage();
            for (int i = 0; i <= MessageBus.SweepGateSampleSize; i++)
            {
                bus.UntargetedBroadcast(ref message);
            }
        }

        private static ArgumentOutOfRangeException UnsupportedScenario(MessageScenario scenario)
        {
            return new ArgumentOutOfRangeException(
                nameof(scenario),
                scenario?.Kind,
                "Unsupported message kind."
            );
        }

        private sealed class CleanupScope : IDisposable
        {
            private readonly Action _cleanup;
            private bool _disposed;

            public CleanupScope(Action cleanup)
            {
                _cleanup = cleanup;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cleanup();
            }
        }

        private static readonly Type[] RegistrationFloodMarkerTypes =
        {
            typeof(RegistrationFloodMarker00),
            typeof(RegistrationFloodMarker01),
            typeof(RegistrationFloodMarker02),
            typeof(RegistrationFloodMarker03),
            typeof(RegistrationFloodMarker04),
            typeof(RegistrationFloodMarker05),
            typeof(RegistrationFloodMarker06),
            typeof(RegistrationFloodMarker07),
            typeof(RegistrationFloodMarker08),
            typeof(RegistrationFloodMarker09),
            typeof(RegistrationFloodMarker10),
            typeof(RegistrationFloodMarker11),
            typeof(RegistrationFloodMarker12),
            typeof(RegistrationFloodMarker13),
            typeof(RegistrationFloodMarker14),
            typeof(RegistrationFloodMarker15),
            typeof(RegistrationFloodMarker16),
            typeof(RegistrationFloodMarker17),
            typeof(RegistrationFloodMarker18),
            typeof(RegistrationFloodMarker19),
            typeof(RegistrationFloodMarker20),
            typeof(RegistrationFloodMarker21),
            typeof(RegistrationFloodMarker22),
            typeof(RegistrationFloodMarker23),
            typeof(RegistrationFloodMarker24),
            typeof(RegistrationFloodMarker25),
            typeof(RegistrationFloodMarker26),
            typeof(RegistrationFloodMarker27),
            typeof(RegistrationFloodMarker28),
            typeof(RegistrationFloodMarker29),
            typeof(RegistrationFloodMarker30),
            typeof(RegistrationFloodMarker31),
        };

        private readonly struct RegistrationFloodMessage<TOuter, TInner>
            : IUntargetedMessage<RegistrationFloodMessage<TOuter, TInner>> { }

        private readonly struct RegistrationFloodMarker00 { }

        private readonly struct RegistrationFloodMarker01 { }

        private readonly struct RegistrationFloodMarker02 { }

        private readonly struct RegistrationFloodMarker03 { }

        private readonly struct RegistrationFloodMarker04 { }

        private readonly struct RegistrationFloodMarker05 { }

        private readonly struct RegistrationFloodMarker06 { }

        private readonly struct RegistrationFloodMarker07 { }

        private readonly struct RegistrationFloodMarker08 { }

        private readonly struct RegistrationFloodMarker09 { }

        private readonly struct RegistrationFloodMarker10 { }

        private readonly struct RegistrationFloodMarker11 { }

        private readonly struct RegistrationFloodMarker12 { }

        private readonly struct RegistrationFloodMarker13 { }

        private readonly struct RegistrationFloodMarker14 { }

        private readonly struct RegistrationFloodMarker15 { }

        private readonly struct RegistrationFloodMarker16 { }

        private readonly struct RegistrationFloodMarker17 { }

        private readonly struct RegistrationFloodMarker18 { }

        private readonly struct RegistrationFloodMarker19 { }

        private readonly struct RegistrationFloodMarker20 { }

        private readonly struct RegistrationFloodMarker21 { }

        private readonly struct RegistrationFloodMarker22 { }

        private readonly struct RegistrationFloodMarker23 { }

        private readonly struct RegistrationFloodMarker24 { }

        private readonly struct RegistrationFloodMarker25 { }

        private readonly struct RegistrationFloodMarker26 { }

        private readonly struct RegistrationFloodMarker27 { }

        private readonly struct RegistrationFloodMarker28 { }

        private readonly struct RegistrationFloodMarker29 { }

        private readonly struct RegistrationFloodMarker30 { }

        private readonly struct RegistrationFloodMarker31 { }

        private readonly struct UntargetedOne : IUntargetedMessage<UntargetedOne> { }

        private readonly struct UntargetedTwo : IUntargetedMessage<UntargetedTwo> { }

        private readonly struct UntargetedThree : IUntargetedMessage<UntargetedThree> { }

        private readonly struct TargetedOne : ITargetedMessage<TargetedOne> { }

        private readonly struct TargetedTwo : ITargetedMessage<TargetedTwo> { }

        private readonly struct TargetedThree : ITargetedMessage<TargetedThree> { }

        private readonly struct BroadcastOne : IBroadcastMessage<BroadcastOne> { }

        private readonly struct BroadcastTwo : IBroadcastMessage<BroadcastTwo> { }

        private readonly struct BroadcastThree : IBroadcastMessage<BroadcastThree> { }

        private readonly struct SweepProbeMessage : IUntargetedMessage<SweepProbeMessage> { }
    }
}
#endif
