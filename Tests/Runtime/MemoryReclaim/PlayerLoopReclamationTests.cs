#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime.Core;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.LowLevel;
    using UnityEngine.TestTools;

    /// <summary>Player startup and shared-pool controls in the default Unity player scope.</summary>
    [TestFixture]
    [Category("UnityRuntime")]
    public sealed class PlayerLoopReclamationTests : MessagingTestBase
    {
        private static readonly InstanceId DefaultContext = new InstanceId(0x5A19_0001);

        /// <summary>Checks runtime defaults without relying on Editor validation callbacks.</summary>
        [Test]
        public void FreshRuntimeSettingsEnableReclamationWithoutEditorValidation()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            using IDisposable owner = new CleanupScope(() =>
                UnityEngine.Object.DestroyImmediate(settings)
            );
            Assert.IsTrue(
                settings.EvictionEnabled,
                "Fresh runtime settings must enable automatic reclamation without Editor validation."
            );
            Assert.IsTrue(
                settings.EnableTrimApi,
                "Fresh runtime settings must enable explicit reclamation without Editor validation."
            );
            Assert.AreEqual(
                DxMessagingRuntimeSettings.DefaultBufferMaxDistinctEntries,
                settings.BufferMaxDistinctEntries,
                "Fresh runtime settings must apply the declared pool entry cap without Editor validation."
            );
        }

        /// <summary>Checks synchronous pool entry caps and cleared contents in both retention modes.</summary>
        /// <param name="useLru">Whether the pool retains the most recently returned entries.</param>
        /// <param name="cap">Maximum number of retained collection objects.</param>
        [Test]
        public void RuntimePoolCapsEntriesAndClearsRecycledElements(
            [Values(false, true)] bool useLru,
            [Values(0, 1, 3)] int cap
        )
        {
            CollectionPool<List<object>> pool = DxPools.ObjectLists;
            int previousCap = pool.MaxRetained;
            bool previousUseLru = pool.UseLru;
            try
            {
                _ = pool.Trim(0);
                pool.UseLru = useLru;
                pool.MaxRetained = cap;
                List<object>[] rented = new List<object>[cap + 2];
                for (int i = 0; i < rented.Length; ++i)
                {
                    rented[i] = pool.Rent();
                    rented[i].Capacity = 128;
                    rented[i].Add(new object());
                }
                foreach (List<object> list in rented)
                {
                    pool.Return(list);
                    Assert.LessOrEqual(
                        pool.Count,
                        cap,
                        "LRU={0}, cap={1}: every return must enforce the entry cap synchronously.",
                        useLru,
                        cap
                    );
                }
                Assert.AreEqual(
                    cap,
                    pool.Count,
                    "LRU={0}, cap={1}: only the configured number of collection objects may be retained.",
                    useLru,
                    cap
                );
                for (int i = 0; i < cap; ++i)
                {
                    List<object> retained = pool.Rent();
                    Assert.AreEqual(
                        0,
                        retained.Count,
                        "LRU={0}, cap={1}: accepted pooled lists must release their elements.",
                        useLru,
                        cap
                    );
                    TestContext.WriteLine(
                        "LRU={0}, cap={1}: retained list {2} has Count={3}, Capacity={4}.",
                        useLru,
                        cap,
                        i,
                        retained.Count,
                        retained.Capacity
                    );
                }
                Assert.AreEqual(
                    0,
                    pool.Count,
                    "LRU={0}, cap={1}: renting retained lists must drain the pool.",
                    useLru,
                    cap
                );
            }
            finally
            {
                _ = pool.Trim(0);
                pool.MaxRetained = previousCap;
                pool.UseLru = previousUseLru;
            }
        }

        /// <summary>Exercises startup-installed reclamation through frame updates and provider settings.</summary>
        /// <param name="scenario">Message route and optional post-processor under test.</param>
        /// <returns>Frames before and after the injected sweep cadence.</returns>
        [UnityTest]
        public IEnumerator AutomaticPlayerLoopReclaimsIdleSlotsWithoutMessages(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessor)
            )]
                MessageScenario scenario
        )
        {
            Assert.IsTrue(
                EvictionPlayerLoopHook.ContainsHook(PlayerLoop.GetCurrentPlayerLoop()),
                "[{0}] runtime startup must install idle reclamation before the test runs.",
                scenario.Kind
            );
            FakeClock clock = new FakeClock();
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            using IDisposable settingsOwner = new CleanupScope(() =>
                UnityEngine.Object.DestroyImmediate(settings)
            );
            settings._idleEvictionSeconds = 0f;
            settings._evictionTickIntervalSeconds = 1f;
            settings._evictionEnabled = true;
            settings._enableTrimApi = true;
            using IDisposable settingsOverride = RuntimeSettingsFixture.Override(settings);
            MessageBus bus = MessageBus.CreateForInternalUse(clock);
            MessageBus disabledBus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 1d,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                bus,
                label: scenario.DisplayName
            );
            using LeakWatcher disabledWatcher = LeakWatcher.WatchWithSlots(
                disabledBus,
                label: scenario.DisplayName + " disabled"
            );
            using IDisposable cleanup = ForceTrimCleanup(bus);
            using IDisposable disabledCleanup = ForceTrimCleanup(disabledBus);
            using MessageRegistrationToken token = CreateToken(bus);
            using MessageRegistrationToken disabledToken = CreateToken(disabledBus);
            MessageRegistrationHandle handle = Register(scenario, token, () => { });
            MessageRegistrationHandle disabledHandle = Register(scenario, disabledToken, () => { });
            float previousTimeScale = Time.timeScale;
            try
            {
                token.RemoveRegistration(handle);
                disabledToken.RemoveRegistration(disabledHandle);
                int occupied = bus.OccupiedTypeSlots + bus.OccupiedTargetSlots;
                int disabledOccupied =
                    disabledBus.OccupiedTypeSlots + disabledBus.OccupiedTargetSlots;
                Assert.Greater(
                    occupied,
                    0,
                    "[{0}] removal must leave idle storage to reclaim.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    occupied,
                    disabledOccupied,
                    "[{0}] controls must start with equal idle storage.",
                    scenario.Kind
                );

                Time.timeScale = 0f;
                yield return null;

                Assert.AreEqual(
                    occupied,
                    bus.OccupiedTypeSlots + bus.OccupiedTargetSlots,
                    "[{0}] a frame before cadence must preserve idle slots.",
                    scenario.Kind
                );
                clock.Advance(1d);
                yield return null;

                Assert.AreEqual(
                    0,
                    bus.OccupiedTypeSlots,
                    "[{0}] an automatic frame must reclaim idle type slots without another message.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    bus.OccupiedTargetSlots,
                    "[{0}] an automatic frame must reclaim idle context slots without another message.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    disabledOccupied,
                    disabledBus.OccupiedTypeSlots + disabledBus.OccupiedTargetSlots,
                    "[{0}] disabled eviction must preserve idle slots through the same frames.",
                    scenario.Kind
                );

                int calls = 0;
                MessageRegistrationHandle replacement = Register(scenario, token, () => ++calls);
                try
                {
                    token.RemoveRegistration(handle);
                    clock.Advance(1d);
                    yield return null;
                    ScenarioCallbacks.EmitForKind(scenario, bus, DefaultContext);
                    Assert.AreEqual(
                        1,
                        calls,
                        "[{0}] a stale teardown and automatic sweep must preserve the live replacement.",
                        scenario.Kind
                    );
                }
                finally
                {
                    token.RemoveRegistration(replacement);
                }
            }
            finally
            {
                Time.timeScale = previousTimeScale;
                token.RemoveRegistration(handle);
                disabledToken.RemoveRegistration(disabledHandle);
            }
        }

        /// <summary>Creates an enabled token on a live handler for the supplied bus.</summary>
        /// <param name="bus">Bus that owns the test registrations.</param>
        /// <returns>The token owned by the test.</returns>
        private static MessageRegistrationToken CreateToken(MessageBus bus)
        {
            MessageHandler handler = new MessageHandler(new InstanceId(0x5A19_0002), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();
            return token;
        }

        /// <summary>Registers the selected handler or post-processor on the fixture's route.</summary>
        /// <param name="scenario">Message route and callback shape to register.</param>
        /// <param name="token">Token that owns the registration.</param>
        /// <param name="onMessage">Callback invoked when the selected message arrives.</param>
        /// <returns>The handle used to remove this registration.</returns>
        private static MessageRegistrationHandle Register(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onMessage
        )
        {
            return scenario.UsePostProcessor
                ? ScenarioCallbacks.RegisterCountingPostProcessor(
                    scenario,
                    token,
                    DefaultContext,
                    onMessage
                )
                : ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    DefaultContext,
                    onMessage
                );
        }

        /// <summary>Creates a scope that force-trims the test bus and shared pools.</summary>
        /// <param name="bus">Bus whose empty storage belongs to the test.</param>
        /// <returns>The owned cleanup action.</returns>
        private static IDisposable ForceTrimCleanup(MessageBus bus)
        {
            return new CleanupScope(() =>
            {
                _ = bus.Trim(force: true);
                _ = DxPools.TrimAll(force: true);
            });
        }
    }
}
#endif
