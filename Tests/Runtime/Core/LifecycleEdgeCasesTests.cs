#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.TestTools;

    /// <summary>
    /// Pins lifecycle invariants that arise from interactions between the
    /// DxMessaging bus and Unity GameObject / Token state changes. Tests in
    /// this fixture exercise destruction, enable/disable cycles, and token
    /// disable/re-enable mid-emission. Scene-loading paths are gated behind
    /// the <c>UnityRuntime</c> category because they require the editor's
    /// runtime to be live and add several seconds to the wall clock; the
    /// rest of the fixture stays in the default suite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMessageBus-not-IDisposable rationale: <see cref="IMessageBus"/> does
    /// not extend <see cref="IDisposable"/> in the public surface because the
    /// bus's lifetime is owned by the application (typically the global
    /// singleton or an explicit container scope), not by the consumers that
    /// register handlers on it. There is no "EmitOnDisposedBus" test in this
    /// fixture because the contract is "no handlers means a silent no-op"
    /// (pinned by <see cref="EmitOnEmptyBusIsSilentNoOp"/>) plus "Reset
    /// invalidates handles via the bus's reset generation" (pinned by
    /// <see cref="EmitImmediatelyAfterResetIsSilentNoOp"/>). Adding an
    /// explicit dispose contract would push lifetime control onto handler
    /// authors; the existing reset-generation guard solves the same problem
    /// without that surface area.
    /// </para>
    /// </remarks>
    public sealed class LifecycleEdgeCasesTests : MessagingTestBase
    {
        private const int PrefabPoolingCycleCount = 100;

        [UnityTest]
        public IEnumerator PrefabPoolingEnableDisableCycles(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PrefabPoolingEnableDisableCycles) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                MessageRegistrationHandle handle = RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );

                for (int i = 0; i < PrefabPoolingCycleCount; ++i)
                {
                    host.SetActive(false);
                    host.SetActive(true);
                }

                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Handler must still receive messages after enable/disable churn (cycles={1}).",
                    scenario.Kind,
                    PrefabPoolingCycleCount
                );

                token.RemoveRegistration(handle);
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator TokenDisableMidDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(TokenDisableMidDispatch) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            int bCount = 0;

            // Handler A (priority 0) disables the token, Handler B (priority 1)
            // is registered after A so it sees the same emission's snapshot.
            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++aCount;
                    token.Disable();
                },
                priority: 0
            );
            MessageRegistrationHandle bHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => ++bCount,
                priority: 1
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] Handler A must run on the in-flight emission. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] Snapshot semantics: B must still run on the in-flight emission even after Disable. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] After Disable, the next emission must NOT invoke A. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] After Disable, the next emission must NOT invoke B. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            token.Enable();
            token.RemoveRegistration(aHandle);
            token.RemoveRegistration(bHandle);
            yield break;
        }

        [UnityTest]
        public IEnumerator TokenReEnableMidDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(TokenReEnableMidDispatch) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Use a dedicated component so we can disable its token without
            // affecting the dispatch loop on the host's token.
            GameObject auxHost = new(
                nameof(TokenReEnableMidDispatch) + "Aux" + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(auxHost);
            EmptyMessageAwareComponent auxComponent =
                auxHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken auxToken = GetToken(auxComponent);

            int hostHandlerCount = 0;
            int auxHandlerCount = 0;

            // Pre-register an aux handler then Disable its token before the
            // first emission, simulating a previously-disabled handler that
            // gets re-enabled mid-dispatch on a different bus client.
            MessageRegistrationHandle auxHandle = RegisterCountingHandler(
                scenario,
                auxToken,
                hostId,
                () => ++auxHandlerCount
            );
            auxToken.Disable();

            MessageRegistrationHandle hostHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++hostHandlerCount;
                    auxToken.Enable();
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                hostHandlerCount,
                "[{0}] Host handler must run on the first emission. host={1}, aux={2}.",
                scenario.Kind,
                hostHandlerCount,
                auxHandlerCount
            );
            Assert.AreEqual(
                0,
                auxHandlerCount,
                "[{0}] Aux handler re-enabled during dispatch must NOT run on current emission (snapshot frozen before Enable). host={1}, aux={2}.",
                scenario.Kind,
                hostHandlerCount,
                auxHandlerCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                hostHandlerCount,
                "[{0}] Host handler must run on the second emission. host={1}, aux={2}.",
                scenario.Kind,
                hostHandlerCount,
                auxHandlerCount
            );
            Assert.AreEqual(
                1,
                auxHandlerCount,
                "[{0}] Aux handler must run on the next emission after the re-enable settles. host={1}, aux={2}.",
                scenario.Kind,
                hostHandlerCount,
                auxHandlerCount
            );

            token.RemoveRegistration(hostHandle);
            auxToken.RemoveRegistration(auxHandle);
            yield break;
        }

        /// <summary>
        /// Pins <see cref="MessageRegistrationToken.UnregisterAll"/> called
        /// from INSIDE a handler mid-dispatch. This exercises a different
        /// path from <see cref="MessageRegistrationToken.Disable"/>: in
        /// addition to invoking every live deregistration, UnregisterAll
        /// clears the token's STAGED registration map, so a later
        /// <c>Enable()</c> must NOT resurrect the handlers. Mid-dispatch the
        /// in-flight emission completes against its frozen snapshot (the same
        /// contract as <see cref="TokenDisableMidDispatch"/>), the bus must
        /// not throw or corrupt its counters, and the token must remain
        /// usable for fresh registrations afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator UnregisterAllMidDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // A standalone token (not a MessageAwareComponent token) keeps this
            // test in full control of every registration: UnregisterAll() on a
            // component token would also wipe the component's built-in
            // StringMessage registrations and skew the bus counters.
            GameObject host = new(nameof(UnregisterAllMidDispatch) + scenario.Kind);
            _spawned.Add(host);
            InstanceId hostId = host;
            MessageHandler handler = new(host) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler);
            token.Enable();

            IMessageBus bus = MessageHandler.MessageBus;
            int aCount = 0;
            int bCount = 0;
            int freshCount = 0;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                int initialUntargeted = bus.RegisteredUntargeted;
                int initialTargeted = bus.RegisteredTargeted;
                int initialBroadcast = bus.RegisteredBroadcast;

                // Handler A (priority 0) clears the whole token mid-dispatch;
                // handler B (priority 1) observes the in-flight snapshot.
                _ = RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        ++aCount;
                        token.UnregisterAll();
                    },
                    priority: 0
                );
                _ = RegisterCountingHandler(scenario, token, hostId, () => ++bCount, priority: 1);

                Assert.DoesNotThrow(
                    () => EmitForScenario(scenario, hostId),
                    "[{0}] UnregisterAll from inside a handler must not throw mid-dispatch.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    aCount,
                    "[{0}] Handler A must run on the in-flight emission. aCount={1}, bCount={2}.",
                    scenario.Kind,
                    aCount,
                    bCount
                );
                Assert.AreEqual(
                    1,
                    bCount,
                    "[{0}] Snapshot semantics: B must still run on the in-flight emission even after UnregisterAll. aCount={1}, bCount={2}.",
                    scenario.Kind,
                    aCount,
                    bCount
                );

                // The registrations must be gone from the bus immediately.
                Assert.AreEqual(
                    initialUntargeted,
                    bus.RegisteredUntargeted,
                    "[{0}] Untargeted counter must return to baseline after UnregisterAll.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    initialTargeted,
                    bus.RegisteredTargeted,
                    "[{0}] Targeted counter must return to baseline after UnregisterAll.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    initialBroadcast,
                    bus.RegisteredBroadcast,
                    "[{0}] Broadcast counter must return to baseline after UnregisterAll.",
                    scenario.Kind
                );

                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    aCount,
                    "[{0}] After UnregisterAll, the next emission must NOT invoke A.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    bCount,
                    "[{0}] After UnregisterAll, the next emission must NOT invoke B.",
                    scenario.Kind
                );

                // KEY difference from Disable(): the staged registrations were
                // cleared, so Enable() must NOT resurrect the handlers.
                token.Enable();
                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    aCount,
                    "[{0}] Enable() after UnregisterAll must NOT resurrect A (staged state was cleared).",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    bCount,
                    "[{0}] Enable() after UnregisterAll must NOT resurrect B (staged state was cleared).",
                    scenario.Kind
                );
                Assert.AreEqual(
                    initialUntargeted,
                    bus.RegisteredUntargeted,
                    "[{0}] Untargeted counter must stay at baseline after Enable() post-UnregisterAll.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    initialTargeted,
                    bus.RegisteredTargeted,
                    "[{0}] Targeted counter must stay at baseline after Enable() post-UnregisterAll.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    initialBroadcast,
                    bus.RegisteredBroadcast,
                    "[{0}] Broadcast counter must stay at baseline after Enable() post-UnregisterAll.",
                    scenario.Kind
                );

                // The token (and the bus) must remain fully usable: a fresh
                // registration on the re-enabled token dispatches normally.
                MessageRegistrationHandle freshHandle = RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++freshCount
                );
                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    freshCount,
                    "[{0}] A registration made after UnregisterAll must receive emissions (bus must not be corrupted).",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    aCount,
                    "[{0}] Old handler A must stay dead after the fresh registration's emission.",
                    scenario.Kind
                );

                token.RemoveRegistration(freshHandle);
            }

            token.Dispose();
            yield break;
        }

        /// <summary>
        /// <see cref="IMessageBus"/> does NOT extend <see cref="IDisposable"/>
        /// in the public surface, so an "EmitOnDisposedBus" test does not
        /// translate directly. The closest defined behavior is "no handlers
        /// registered emit is a silent no-op" - emitting on an empty bus
        /// must succeed without throwing. This pins that contract for every
        /// kind.
        /// </summary>
        /// <remarks>
        /// Per-kind differentiating assertions: the test asserts the correct
        /// per-kind counter remains zero AFTER the emit (as opposed to a
        /// kind-agnostic "no exception"), so that a regression in (say) the
        /// untargeted path that accidentally bumps the targeted counter
        /// would surface as a test failure.
        /// </remarks>
        [UnityTest]
        public IEnumerator EmitOnEmptyBusIsSilentNoOp(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(EmitOnEmptyBusIsSilentNoOp) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Disable the host's token so the only registrations on the bus
            // are zero (or the test base's own pristine state). Emitting
            // must not throw.
            token.Disable();

            IMessageBus bus = MessageHandler.MessageBus;
            int initialUntargeted = bus.RegisteredUntargeted;
            int initialTargeted = bus.RegisteredTargeted;
            int initialBroadcast = bus.RegisteredBroadcast;

            Assert.DoesNotThrow(() => EmitForScenario(scenario, hostId));

            // Per-kind assertion: the counter for the emitted kind must
            // remain at its baseline (no spurious registrations introduced
            // by the empty-bus emit), and so must the OTHER two counters
            // (proves the no-op did not leak into another kind's bookkeeping).
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    Assert.AreEqual(
                        initialUntargeted,
                        bus.RegisteredUntargeted,
                        "[Untargeted] Empty-bus emit must leave RegisteredUntargeted unchanged."
                    );
                    break;
                case MessageKind.Targeted:
                    Assert.AreEqual(
                        initialTargeted,
                        bus.RegisteredTargeted,
                        "[Targeted] Empty-bus emit must leave RegisteredTargeted unchanged."
                    );
                    break;
                case MessageKind.Broadcast:
                    Assert.AreEqual(
                        initialBroadcast,
                        bus.RegisteredBroadcast,
                        "[Broadcast] Empty-bus emit must leave RegisteredBroadcast unchanged."
                    );
                    break;
            }

            // No-leak invariants for the other counters.
            Assert.AreEqual(
                initialUntargeted,
                bus.RegisteredUntargeted,
                "[{0}] Untargeted counter must not move after empty-bus emit.",
                scenario.Kind
            );
            Assert.AreEqual(
                initialTargeted,
                bus.RegisteredTargeted,
                "[{0}] Targeted counter must not move after empty-bus emit.",
                scenario.Kind
            );
            Assert.AreEqual(
                initialBroadcast,
                bus.RegisteredBroadcast,
                "[{0}] Broadcast counter must not move after empty-bus emit.",
                scenario.Kind
            );

            token.Enable();
            yield break;
        }

        /// <summary>
        /// Pins the reset-generation guard introduced by the bus-freezing
        /// fix: handlers registered before <see cref="DxMessagingStaticState.Reset"/>
        /// must NOT be invoked by emissions issued after the reset. The
        /// reset increments the bus's internal generation counter; deregister
        /// closures captured before the bump short-circuit silently, and the
        /// post-reset emit must therefore find no handlers to dispatch to.
        /// </summary>
        [UnityTest]
        public IEnumerator EmitImmediatelyAfterResetIsSilentNoOp(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(EmitImmediatelyAfterResetIsSilentNoOp) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            // Register a handler whose callback would bump handlerCount; we
            // never want to see it fire after the reset.
            _ = RegisterCountingHandler(scenario, token, hostId, () => ++handlerCount);

            // Reset wipes every registration AND bumps the reset generation
            // so any deregister closures captured before the reset turn into
            // no-ops. After the reset, emit a fresh message: no handler may
            // run because the prior registration was wiped.
            DxMessagingStaticState.Reset();

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Emitting after Reset must not throw.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                handlerCount,
                "[{0}] Pre-reset handler must NOT fire after Reset (handlerCount={1}).",
                scenario.Kind,
                handlerCount
            );

            // Defensive sanity: the bus must report zero registrations on
            // every counter after Reset, regardless of what the test ran.
            IMessageBus bus = MessageHandler.MessageBus;
            Assert.AreEqual(
                0,
                bus.RegisteredUntargeted,
                "[{0}] Untargeted counter must be zero after Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredTargeted,
                "[{0}] Targeted counter must be zero after Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredBroadcast,
                "[{0}] Broadcast counter must be zero after Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredInterceptors,
                "[{0}] Interceptor counter must be zero after Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredPostProcessors,
                "[{0}] Post-processor counter must be zero after Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredGlobalAcceptAll,
                "[{0}] GlobalAcceptAll counter must be zero after Reset.",
                scenario.Kind
            );

            yield break;
        }

        [UnityTest]
        [Category("UnityRuntime")]
        public IEnumerator SceneTransitionWithDontDestroyOnLoad(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // The active scene at test start is the test runner's scene. We
            // mark a freshly created GameObject as DontDestroyOnLoad so it
            // survives a scene unload, then load an empty scene additively
            // to simulate a transition without touching the test runner
            // scene. This avoids destroying the test runner's GameObjects
            // (which would terminate the test prematurely).
            GameObject host = new(
                nameof(SceneTransitionWithDontDestroyOnLoad) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            UnityEngine.Object.DontDestroyOnLoad(host);
            _spawned.Add(host);

            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            MessageRegistrationHandle handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => ++handlerCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                handlerCount,
                "[{0}] DDOL handler must receive its first emission.",
                scenario.Kind
            );

            // Create + unload an empty scene additively; the host should
            // survive thanks to DontDestroyOnLoad.
            Scene transient = SceneManager.CreateScene(
                nameof(SceneTransitionWithDontDestroyOnLoad) + scenario.Kind + "-Transient"
            );
            yield return null;

            AsyncOperation unload = SceneManager.UnloadSceneAsync(transient);
            while (unload != null && !unload.isDone)
            {
                yield return null;
            }

            // After the additive scene unloads, the DDOL host must still be
            // alive and continue receiving messages.
            Assert.IsTrue(
                host != null,
                "[{0}] DDOL host must survive the additive scene unload.",
                scenario.Kind
            );
            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                handlerCount,
                "[{0}] DDOL handler must continue to receive messages after scene transition.",
                scenario.Kind
            );

            token.RemoveRegistration(handle);
            yield break;
        }

        /// <summary>
        /// Pins that a registration made from inside a closure subscribed to
        /// <see cref="SceneManager.sceneLoaded"/> becomes immediately effective
        /// for subsequent emissions, with no deferred-frame requirement. The
        /// subscribe / unsubscribe pair documents the API surface a user would
        /// touch (the standard delegate pattern for Unity scene events).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test does NOT drive a real scene load. Per Unity's documented
        /// contract, <see cref="SceneManager.sceneLoaded"/> only fires from
        /// <c>LoadScene</c> / <c>LoadSceneAsync</c>, never from
        /// <c>CreateScene</c>; and <c>LoadSceneAsync</c> requires a scene
        /// asset present in BuildSettings, which a package's own unit-test
        /// suite cannot rely on. The closure is therefore invoked manually so
        /// the test is deterministic across EditMode and PlayMode while still
        /// pinning the contract that matters: a registration installed from
        /// inside a user callback flows through the bus's normal dispatch
        /// path on the very next emission.
        /// </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator RegisterFromInsideSceneLoadedClosureBecomesImmediatelyEffective(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RegisterFromInsideSceneLoadedClosureBecomesImmediatelyEffective)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            IMessageBus bus = MessageHandler.MessageBus;
            int initialUntargeted = bus.RegisteredUntargeted;
            int initialTargeted = bus.RegisteredTargeted;
            int initialBroadcast = bus.RegisteredBroadcast;

            int handlerCount = 0;
            MessageRegistrationHandle? deferredHandle = null;

            // Closure stored in a UnityAction so the same delegate instance
            // can be both subscribed and unsubscribed. Lambda parameter
            // discards (Scene _, LoadSceneMode _) follow the project
            // convention; local functions cannot reuse `_` as a parameter
            // name across slots.
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onSceneLoaded = (
                Scene _,
                LoadSceneMode _
            ) =>
            {
                deferredHandle ??= RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );
            };

            // Subscribe / unsubscribe pair documents the API surface users
            // would touch even though the closure is invoked manually below.
            SceneManager.sceneLoaded += onSceneLoaded;
            try
            {
                // Manual invoke: see the XML doc above. SceneManager.CreateScene
                // does not raise sceneLoaded, and LoadSceneAsync needs a scene
                // asset in BuildSettings, so we drive the closure directly to
                // exercise the registration code path deterministically.
                Scene activeScene = SceneManager.GetActiveScene();
                onSceneLoaded(activeScene, LoadSceneMode.Additive);

                Assert.IsTrue(
                    deferredHandle.HasValue,
                    "[{0}] sceneLoaded closure must have installed the handler.",
                    scenario.Kind
                );

                // Defensive: assert the bus's per-kind counter actually moved
                // by exactly 1. Guards against a regression where the closure
                // runs but the registration silently fails to install on the
                // bus (e.g. a future short-circuit path).
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        Assert.AreEqual(
                            initialUntargeted + 1,
                            bus.RegisteredUntargeted,
                            "[Untargeted] Bus RegisteredUntargeted must increase by exactly 1 after the in-closure registration."
                        );
                        break;
                    case MessageKind.Targeted:
                        Assert.AreEqual(
                            initialTargeted + 1,
                            bus.RegisteredTargeted,
                            "[Targeted] Bus RegisteredTargeted must increase by exactly 1 after the in-closure registration."
                        );
                        break;
                    case MessageKind.Broadcast:
                        Assert.AreEqual(
                            initialBroadcast + 1,
                            bus.RegisteredBroadcast,
                            "[Broadcast] Bus RegisteredBroadcast must increase by exactly 1 after the in-closure registration."
                        );
                        break;
                }

                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Handler registered from inside the sceneLoaded closure must receive the next emission via the bus's dispatch path.",
                    scenario.Kind
                );
            }
            finally
            {
                SceneManager.sceneLoaded -= onSceneLoaded;
                if (deferredHandle.HasValue)
                {
                    token.RemoveRegistration(deferredHandle.Value);
                }
            }

            yield break;
        }

        /// <summary>
        /// Pins that destroying the host GameObject mid-emission via
        /// <see cref="UnityEngine.Object.Destroy"/> (deferred destroy) does
        /// not crash the dispatch loop. The current emission completes
        /// against its frozen snapshot; the next emission (after a frame
        /// for the destroy to flush) must skip the destroyed handler.
        /// </summary>
        [UnityTest]
        public IEnumerator HostDestroyMidDispatchDoesNotCrash(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HostDestroyMidDispatchDoesNotCrash) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // A peer host so we can keep dispatching after host destruction.
            GameObject peer = new(
                nameof(HostDestroyMidDispatchDoesNotCrash) + scenario.Kind + "-Peer",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(peer);
            EmptyMessageAwareComponent peerComponent =
                peer.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken peerToken = GetToken(peerComponent);

            int peerCount = 0;
            int destroyedCount = 0;
            MessageRegistrationHandle peerHandle = RegisterCountingHandler(
                scenario,
                peerToken,
                hostId,
                () => ++peerCount,
                priority: 1
            );
            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++destroyedCount;
                    UnityEngine.Object.Destroy(host);
                },
                priority: 0
            );

            Assert.DoesNotThrow(() => EmitForScenario(scenario, hostId));
            Assert.AreEqual(
                1,
                destroyedCount,
                "[{0}] Self-destroying handler must run exactly once.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                peerCount,
                "[{0}] Peer handler must complete on the in-flight snapshot. peerCount={1}.",
                scenario.Kind,
                peerCount
            );

            // Yield a frame so Object.Destroy is processed and the
            // destroyed component's OnDestroy unregisters it.
            yield return null;

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                destroyedCount,
                "[{0}] Destroyed host's handler must NOT fire on subsequent emits.",
                scenario.Kind
            );

            peerToken.RemoveRegistration(peerHandle);
            yield break;
        }

        /// <summary>
        /// Pins the mid-dispatch scene unload contract: a handler firing on an
        /// in-flight emission triggers <see cref="SceneManager.UnloadSceneAsync(Scene)"/>
        /// against the scene that owns the handler's host. The current
        /// emission's remaining handlers (in priority order) must complete
        /// against their frozen snapshot, the bus must not throw, and on the
        /// NEXT emission - after the unload completes - no handler from the
        /// unloaded scene can fire.
        /// </summary>
        [UnityTest]
        [Category("UnityRuntime")]
        public IEnumerator SceneUnloadMidDispatchDrainsInFlightEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Create a transient scene; the handler-host lives there.
            Scene transient = SceneManager.CreateScene(
                nameof(SceneUnloadMidDispatchDrainsInFlightEmission) + scenario.Kind + "-Transient"
            );
            yield return null;

            GameObject host = new(
                nameof(SceneUnloadMidDispatchDrainsInFlightEmission) + scenario.Kind + "-Host",
                typeof(EmptyMessageAwareComponent)
            );
            SceneManager.MoveGameObjectToScene(host, transient);
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Peer host in the test runner's active scene so we can keep
            // dispatching after the transient scene unloads.
            GameObject peer = new(
                nameof(SceneUnloadMidDispatchDrainsInFlightEmission) + scenario.Kind + "-Peer",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(peer);
            EmptyMessageAwareComponent peerComponent =
                peer.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken peerToken = GetToken(peerComponent);

            int peerCount = 0;
            int hostCount = 0;
            AsyncOperation unloadOp = null;
            bool emittedFromHost = false;

            // Peer at priority 1 to assert it still runs on the in-flight
            // emission AFTER the host's priority-0 handler triggers an
            // unload.
            MessageRegistrationHandle peerHandle = RegisterCountingHandler(
                scenario,
                peerToken,
                hostId,
                () => ++peerCount,
                priority: 1
            );
            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++hostCount;
                    if (!emittedFromHost)
                    {
                        emittedFromHost = true;
                        unloadOp = SceneManager.UnloadSceneAsync(transient);
                    }
                },
                priority: 0
            );

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Bus must not throw when a handler triggers UnloadSceneAsync mid-dispatch.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                hostCount,
                "[{0}] Host handler must run on the in-flight emission. host={1}, peer={2}.",
                scenario.Kind,
                hostCount,
                peerCount
            );
            Assert.AreEqual(
                1,
                peerCount,
                "[{0}] Peer handler must complete the in-flight emission's snapshot. host={1}, peer={2}.",
                scenario.Kind,
                hostCount,
                peerCount
            );

            // Wait for the actual scene unload to finish before re-emitting.
            while (unloadOp != null && !unloadOp.isDone)
            {
                yield return null;
            }
            // One more frame so OnDestroy callbacks land.
            yield return null;

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                hostCount,
                "[{0}] After scene unload, the unloaded handler must NOT fire. host={1}, peer={2}.",
                scenario.Kind,
                hostCount,
                peerCount
            );
            Assert.AreEqual(
                2,
                peerCount,
                "[{0}] Peer handler must continue receiving emissions after the unload. host={1}, peer={2}.",
                scenario.Kind,
                hostCount,
                peerCount
            );

            peerToken.RemoveRegistration(peerHandle);
            yield break;
        }

        /// <summary>
        /// Pins that calling <c>OnApplicationQuit</c> on a registered
        /// <see cref="EmptyMessageAwareComponent"/> drains cleanly: no
        /// exceptions thrown, and (under <see cref="LeakWatcher"/>) no
        /// registration leaks remain. Production code overrides
        /// <c>OnApplicationQuit</c> to log/persist on shutdown; the bus
        /// should tolerate the call without surfacing errors.
        /// </summary>
        [UnityTest]
        [Category("UnityRuntime")]
        public IEnumerator OnApplicationQuitDrainsCleanly(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(OnApplicationQuitDrainsCleanly) + scenario.Kind,
                typeof(QuitOnDemandMessageAwareComponent)
            );
            _spawned.Add(host);
            QuitOnDemandMessageAwareComponent component =
                host.GetComponent<QuitOnDemandMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                MessageRegistrationHandle handle = RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );

                EmitForScenario(scenario, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Handler must run before quit.",
                    scenario.Kind
                );

                // Drive OnApplicationQuit explicitly.
                Assert.DoesNotThrow(
                    () => component.RaiseOnApplicationQuit(),
                    "[{0}] OnApplicationQuit must not throw.",
                    scenario.Kind
                );

                token.RemoveRegistration(handle);
            }

            yield break;
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
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
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
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

        private static void EmitForScenario(MessageScenario scenario, InstanceId target)
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
                    ScenarioHarness.EmitTargeted(scenario, ref message, target);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, target);
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
