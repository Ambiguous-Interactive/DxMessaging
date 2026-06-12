#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;
    using static DxMessaging.Tests.Runtime.RegistrationCountAssertions;

    /// <summary>
    /// Pins the runtime consequence of forgetting a <c>base.X()</c> call when
    /// subclassing <see cref="DxMessaging.Unity.MessageAwareComponent"/>.
    /// Complements the compile-time analyzer (DXMSG006) and edit-time IL
    /// scanner by asserting the actual user-visible failure mode at runtime,
    /// so future refactors of the base class cannot silently change what
    /// happens when a subclass omits the chain call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most tests parameterized over <see cref="MessageScenarios.AllKinds"/>
    /// drive the dispatch portion of the assertion through the canonical
    /// <see cref="ScenarioHarness"/> entry points so the same body covers
    /// untargeted, targeted, and broadcast registration paths. A few tests
    /// (the ones that exercise base-class default handlers directly) run
    /// once without scenario parameterization. The breadcrumb log assertion
    /// is gated on <c>UNITY_EDITOR || DEBUG</c> on the runtime side, so
    /// standalone Release players assert the token and dispatch consequences
    /// without expecting a log branch that was compiled out.
    /// </para>
    /// <para>
    /// The leak tests
    /// (<see cref="OmitBaseOnDisableAndOnDestroyLeaksRegistration"/> and
    /// <see cref="OmitBaseOnDisableAndOnDestroyLeaksDefaultHandlersToo"/>)
    /// intentionally produce registrations that survive component
    /// destruction. The leaked registrations are cleaned up by the global
    /// bus reset that <see cref="MessagingTestBase.UnitySetup"/> performs at
    /// the start of every test, so they cannot bleed into subsequent tests;
    /// both tests additionally invoke <c>DxMessagingStaticState.Reset</c>
    /// after observing the leak so the bus is drained before
    /// <see cref="MessagingTestBase.UnityCleanup"/> asserts the bus is fresh.
    /// </para>
    /// </remarks>
    public sealed class BaseCallContractTests : MessagingTestBase
    {
        private static readonly Regex MissingBaseAwakeBreadcrumbPattern = new(
            @"\[DxMessaging\].*missing a base\.Awake\(\) call",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        /// <summary>
        /// Number of default handlers
        /// <see cref="DxMessaging.Unity.MessageAwareComponent.RegisterMessageHandlers"/>
        /// installs on a freshly-spawned subclass when
        /// <c>RegisterForStringMessages</c> is left at its default <c>true</c>.
        /// Two go to <c>RegisteredTargeted</c>
        /// (<c>RegisterGameObjectTargeted&lt;StringMessage&gt;</c> and
        /// <c>RegisterComponentTargeted&lt;StringMessage&gt;</c>), one to
        /// <c>RegisteredUntargeted</c>
        /// (<c>RegisterUntargeted&lt;GlobalStringMessage&gt;</c>).
        /// </summary>
        /// <remarks>
        /// This number is load-bearing for the leak math in
        /// <see cref="OmitBaseOnDisableAndOnDestroyLeaksRegistration"/> and
        /// <see cref="OnDisableDuringDestroyMasksOnDestroyLeak"/>: both tests
        /// observe the bus across a spawn-then-destroy round trip and must
        /// know how many handlers the framework adds on its own. If the base
        /// class adds or removes a default handler, update this constant in
        /// lock-step.
        /// </remarks>
        private const int DefaultStringMessageHandlerCount = 3;

        /// <summary>
        /// Skipping <c>base.Awake()</c> means the framework never creates the
        /// registration token. Asserts that the runtime self-check breadcrumb
        /// fires once, the token is null, attempting to register through it
        /// throws (instead of failing silently), and emitted messages do not
        /// produce handler invocations.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseAwakeYieldsNoTokenAndNoDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Expect the self-check breadcrumb BEFORE the action that triggers it
            // in builds that compile the runtime breadcrumb branch.
            ExpectMissingBaseAwakeBreadcrumbIfCompiled();

            GameObject host = new(
                nameof(OmitBaseAwakeYieldsNoTokenAndNoDispatch) + scenario.Kind,
                typeof(MissingBaseAwakeComponent)
            );
            _spawned.Add(host);

            MissingBaseAwakeComponent component = host.GetComponent<MissingBaseAwakeComponent>();
            Assert.IsNotNull(component, "[{0}] Component should be present.", scenario.Kind);
            Assert.IsNull(
                component.Token,
                "[{0}] Token must remain null when base.Awake() is skipped.",
                scenario.Kind
            );

            // Calling through a null token throws NullReferenceException.
            // The exact exception type is not the contract; the contract is
            // "the call fails in a defined way rather than silently dropping
            // the registration", which a thrown exception satisfies.
            Assert.Throws<System.NullReferenceException>(
                () =>
                    _ = component.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => { }
                    ),
                "[{0}] Registering through a null token must throw, not silently no-op.",
                scenario.Kind
            );

            IEnumerator fresh = WaitUntilMessageHandlerIsFresh();
            while (fresh.MoveNext())
            {
                yield return fresh.Current;
            }

            // No handler is registered (the registration above threw); emit
            // anyway and confirm dispatch is a no-op. The bus must remain
            // fresh because the broken component never installed a handler.
            EmitDirectly(scenario, host);

            IMessageBus bus = MessageHandler.MessageBus;
            AssertRegistrationCounts(
                bus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: $"OmitBaseAwake[{scenario.Kind}] no registrations should exist"
            );

            yield break;
        }

        /// <summary>
        /// Skipping <c>base.OnEnable()</c> prevents the registration token from
        /// transitioning to the enabled state, so even though the token exists
        /// the registered handler does not fire.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseOnEnableLeavesHandlerDisabled(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(OmitBaseOnEnableLeavesHandlerDisabled) + scenario.Kind,
                typeof(MissingBaseOnEnableComponent)
            );
            _spawned.Add(host);

            MissingBaseOnEnableComponent component =
                host.GetComponent<MissingBaseOnEnableComponent>();
            MessageRegistrationToken token = GetToken(component);
            Assert.IsNotNull(
                token,
                "[{0}] Token must be created because base.Awake() still runs.",
                scenario.Kind
            );

            int handlerInvocations = 0;
            MessageRegistrationHandle handle = RegisterCounter(
                scenario,
                token,
                host,
                () => handlerInvocations++
            );
            try
            {
                EmitDirectly(scenario, host);

                Assert.AreEqual(
                    0,
                    handlerInvocations,
                    "[{0}] Handler must not fire while the token is never enabled.",
                    scenario.Kind
                );
            }
            finally
            {
                token.RemoveRegistration(handle);
            }

            yield break;
        }

        /// <summary>
        /// Skipping <c>base.OnDisable()</c> means the registration token is
        /// never disabled, so the handler keeps firing while the component is
        /// ostensibly off.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseOnDisableLeavesHandlerLive(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(OmitBaseOnDisableLeavesHandlerLive) + scenario.Kind,
                typeof(MissingBaseOnDisableComponent)
            );
            _spawned.Add(host);

            MissingBaseOnDisableComponent component =
                host.GetComponent<MissingBaseOnDisableComponent>();
            MessageRegistrationToken token = GetToken(component);
            Assert.IsNotNull(
                token,
                "[{0}] Token must be created because base.Awake() still runs.",
                scenario.Kind
            );

            int handlerInvocations = 0;
            MessageRegistrationHandle handle = RegisterCounter(
                scenario,
                token,
                host,
                () => handlerInvocations++
            );
            try
            {
                // Disable the component; because the override skips
                // base.OnDisable(), the token stays enabled.
                component.enabled = false;
                EmitDirectly(scenario, host);

                Assert.AreEqual(
                    1,
                    handlerInvocations,
                    "[{0}] Handler must still fire because the token was never disabled.",
                    scenario.Kind
                );
            }
            finally
            {
                token.RemoveRegistration(handle);
            }

            yield break;
        }

        /// <summary>
        /// Skipping BOTH <c>base.OnDisable()</c> and <c>base.OnDestroy()</c>
        /// means the framework never releases the messaging component or
        /// disables the token, so the registration outlives the GameObject
        /// and the bus's registration counter does not return to the baseline
        /// captured by <see cref="LeakWatcher"/>. The fixture
        /// <see cref="MissingBaseOnDestroyComponent"/> intentionally skips
        /// both base calls because Unity's destroy lifecycle fires
        /// <c>OnDisable</c> before <c>OnDestroy</c>; if only <c>OnDestroy</c>
        /// were skipped, the inherited <c>OnDisable</c> would deregister the
        /// handlers during destruction and the leak would be masked. The
        /// companion test
        /// <see cref="OnDisableDuringDestroyMasksOnDestroyLeak"/> pins that
        /// masking behavior explicitly.
        /// Cleanup choice: the test calls
        /// <see cref="DxMessagingStaticState.Reset"/> after observing the leak
        /// so the bus returns to a clean state before
        /// <see cref="MessagingTestBase.UnityCleanup"/> asserts the bus is
        /// fresh; this is the simplest deterministic way to drop an orphaned
        /// registration whose owning GameObject no longer exists.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseOnDisableAndOnDestroyLeaksRegistration(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Construct the watcher BEFORE spawning the host so the baseline
            // is the truly-fresh bus (0) that MessagingTestBase.UnitySetup
            // guarantees. Capturing the baseline after spawn would understate
            // the leak: the 3 default StringMessage handlers added by the
            // inherited RegisterMessageHandlers would already be in the
            // baseline, so a leak that includes them would only show as the
            // user counter (1) instead of the full failure surface (4).
            // Watching from before spawn lets the assertion pin the EXACT
            // leak count (defaults + counter) and proves that skipping both
            // base.OnDisable and base.OnDestroy strands every handler the
            // framework added on Awake, not just the user-added one.
            LeakWatcher watcher = new(
                bus: MessageHandler.MessageBus,
                throwOnLeak: false,
                label: scenario.DisplayName
            );

            int observedLeak;
            string deltaDescription;
            try
            {
                GameObject host = new(
                    nameof(OmitBaseOnDisableAndOnDestroyLeaksRegistration) + scenario.Kind,
                    typeof(MissingBaseOnDestroyComponent)
                );
                _spawned.Add(host);

                MissingBaseOnDestroyComponent component =
                    host.GetComponent<MissingBaseOnDestroyComponent>();
                MessageRegistrationToken token = GetToken(component);
                Assert.IsNotNull(
                    token,
                    "[{0}] Token must be created because base.Awake() still runs.",
                    scenario.Kind
                );

                int snapshotAfterSpawn = watcher.Snapshot;
                Assert.AreEqual(
                    DefaultStringMessageHandlerCount,
                    snapshotAfterSpawn,
                    "[{0}] Spawning a MessageAwareComponent subclass that does not "
                        + "override RegisterForStringMessages must add exactly the "
                        + "default StringMessage handler count to the bus. {1}",
                    scenario.Kind,
                    watcher.DescribeDelta()
                );

                _ = RegisterCounter(scenario, token, host, () => { });
                Assert.AreEqual(
                    snapshotAfterSpawn + 1,
                    watcher.Snapshot,
                    "[{0}] Bus must reflect the new registration before destroy. {1}",
                    scenario.Kind,
                    watcher.DescribeDelta()
                );

                // Destroy the component / GameObject; because the override skips
                // BOTH base.OnDisable() and base.OnDestroy(), neither the token's
                // handler list nor the framework's MessagingComponent are torn
                // down, and every handler installed during Awake leaks. The
                // expected leak is therefore the default StringMessage handlers
                // PLUS the counter handler, not just the counter.
                UnityEngine.Object.Destroy(host);
                _spawned.Remove(host);

                if (Application.isPlaying)
                {
                    yield return null;
                }

                // Capture the live leak BEFORE Dispose/Reset so the assertion
                // sees the actual orphaned count even if a later step throws.
                observedLeak = watcher.LeakedRegistrations;
                deltaDescription = watcher.DescribeDelta();
            }
            finally
            {
                // Idempotent: protects the watcher from being left undisposed
                // if any earlier Assert in the try block throws. The values
                // captured into observedLeak/deltaDescription above are taken
                // from the live bus, so the assertion below remains correct
                // regardless of when Dispose runs.
                watcher.Dispose();
            }

            // Drop the orphaned registrations so they cannot bleed into the
            // next test; UnityCleanup's WaitUntilMessageHandlerIsFresh would
            // otherwise time out asserting bus staleness.
            DxMessagingStaticState.Reset();

            // Exact equality: the leak surface must be the default handlers
            // PLUS the user counter. Asserting >= 1 (the prior behaviour)
            // would silently allow a future regression that loses one or more
            // of the default handlers but still leaks the counter.
            const int expectedLeak = DefaultStringMessageHandlerCount + 1;
            Assert.AreEqual(
                expectedLeak,
                observedLeak,
                "[{0}] Skipping base.OnDisable() and base.OnDestroy() must leak "
                    + "exactly {1} registrations ({2} default StringMessage "
                    + "handlers + 1 counter), proving that NEITHER user nor "
                    + "default handlers are deregistered when both base calls "
                    + "are absent. {3}",
                scenario.Kind,
                expectedLeak,
                DefaultStringMessageHandlerCount,
                deltaDescription
            );

            yield break;
        }

        /// <summary>
        /// Pins Unity's destroy lifecycle interaction: omitting only
        /// <c>base.OnDestroy()</c> while leaving the inherited
        /// <c>base.OnDisable()</c> intact does NOT leak, because Unity fires
        /// <c>OnDisable</c> before <c>OnDestroy</c> during destruction and
        /// the inherited <c>OnDisable</c> calls
        /// <c>_messageRegistrationToken?.Disable()</c>, deregistering every
        /// active registration before the broken <c>OnDestroy</c> runs. This
        /// test is the negative control for
        /// <see cref="OmitBaseOnDisableAndOnDestroyLeaksRegistration"/> and
        /// documents why that test's fixture must skip both base calls.
        /// </summary>
        [UnityTest]
        public IEnumerator OnDisableDuringDestroyMasksOnDestroyLeak(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Construct the watcher BEFORE spawning the host so the baseline
            // is the truly-fresh bus (0) that MessagingTestBase.UnitySetup
            // guarantees. Capturing the baseline AFTER spawn would fold the
            // 3 default StringMessage handlers (registered by the inherited
            // MessageAwareComponent.RegisterMessageHandlers) into the
            // baseline, so the "leak" delta would be the negative of those
            // 3 handlers when base.OnDisable() drains them at destroy time.
            // Watching from before spawn pins the FULL round-trip: every
            // handler the framework added on Awake (defaults + counter) must
            // be removed by the inherited OnDisable during the destroy
            // lifecycle, so the final delta is exactly 0.
            using LeakWatcher watcher = new(
                bus: MessageHandler.MessageBus,
                throwOnLeak: false,
                label: scenario.DisplayName
            );

            GameObject host = new(
                nameof(OnDisableDuringDestroyMasksOnDestroyLeak) + scenario.Kind,
                typeof(MissingBaseOnDestroyOnlyComponent)
            );
            _spawned.Add(host);

            MissingBaseOnDestroyOnlyComponent component =
                host.GetComponent<MissingBaseOnDestroyOnlyComponent>();
            MessageRegistrationToken token = GetToken(component);
            Assert.IsNotNull(
                token,
                "[{0}] Token must be created because base.Awake() still runs.",
                scenario.Kind
            );

            int snapshotAfterSpawn = watcher.Snapshot;
            Assert.AreEqual(
                DefaultStringMessageHandlerCount,
                snapshotAfterSpawn,
                "[{0}] Spawning a MessageAwareComponent subclass that does not "
                    + "override RegisterForStringMessages must add exactly the "
                    + "default StringMessage handler count to the bus. {1}",
                scenario.Kind,
                watcher.DescribeDelta()
            );

            _ = RegisterCounter(scenario, token, host, () => { });
            Assert.AreEqual(
                snapshotAfterSpawn + 1,
                watcher.Snapshot,
                "[{0}] Bus must reflect the new registration before destroy. {1}",
                scenario.Kind,
                watcher.DescribeDelta()
            );

            // Destroy the GameObject. Unity fires OnDisable then OnDestroy;
            // the inherited base.OnDisable() runs (the override is absent on
            // this fixture) and disables the token before the broken
            // OnDestroy runs, so no registration leaks - including the
            // default StringMessage handlers, which is what makes the masking
            // observable end-to-end.
            UnityEngine.Object.Destroy(host);
            _spawned.Remove(host);

            if (Application.isPlaying)
            {
                yield return null;
            }

            Assert.AreEqual(
                0,
                watcher.LeakedRegistrations,
                "[{0}] Inherited base.OnDisable() must deregister ALL handlers "
                    + "during destroy (counter + {1} default StringMessage "
                    + "handlers), masking the broken OnDestroy. {2}",
                scenario.Kind,
                DefaultStringMessageHandlerCount,
                watcher.DescribeDelta()
            );

            // Belt-and-braces: the live bus counters must each be 0 after the
            // host is gone, not just the aggregate. Guards against a future
            // refactor that nets to zero by accidentally deregistering
            // unrelated registrations along with the user counter.
            IMessageBus bus = MessageHandler.MessageBus;
            AssertRegistrationCounts(
                bus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: $"InheritedOnDisableMasksBrokenOnDestroy[{scenario.Kind}] "
                    + $"after destroy. {watcher.DescribeDelta()}"
            );

            yield break;
        }

        /// <summary>
        /// Pins the per-counter shape of the leak when both
        /// <c>base.OnDisable()</c> and <c>base.OnDestroy()</c> are skipped.
        /// Distinct from
        /// <see cref="OmitBaseOnDisableAndOnDestroyLeaksRegistration"/>, which
        /// asserts the aggregate count: this test reads each registration kind
        /// individually so a future "fix" that accidentally only deregisters
        /// user handlers from one path (or that loses one of the default
        /// handlers but keeps another) cannot pass while still masking the
        /// regression. The expected per-counter shape after destroy is:
        /// Targeted == 2 (the two default StringMessage handlers) plus 1 if
        /// the scenario registers a targeted/broadcast counter,
        /// Untargeted == 1 (the default GlobalStringMessage handler) plus 1
        /// if the scenario registers an untargeted counter, Broadcast == 1
        /// only when the scenario registers a broadcast counter.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseOnDisableAndOnDestroyLeaksDefaultHandlersToo(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Watch from before spawn so the per-counter accounting is
            // anchored to a fresh bus.
            using LeakWatcher watcher = new(
                bus: MessageHandler.MessageBus,
                throwOnLeak: false,
                label: scenario.DisplayName
            );

            GameObject host = new(
                nameof(OmitBaseOnDisableAndOnDestroyLeaksDefaultHandlersToo) + scenario.Kind,
                typeof(MissingBaseOnDestroyComponent)
            );
            _spawned.Add(host);

            MissingBaseOnDestroyComponent component =
                host.GetComponent<MissingBaseOnDestroyComponent>();
            MessageRegistrationToken token = GetToken(component);
            Assert.IsNotNull(
                token,
                "[{0}] Token must be created because base.Awake() still runs.",
                scenario.Kind
            );

            _ = RegisterCounter(scenario, token, host, () => { });

            UnityEngine.Object.Destroy(host);
            _spawned.Remove(host);

            if (Application.isPlaying)
            {
                yield return null;
            }

            IMessageBus bus = MessageHandler.MessageBus;

            // The two default StringMessage handlers ALWAYS land on Targeted
            // regardless of scenario, and the default GlobalStringMessage
            // handler ALWAYS lands on Untargeted. The counter handler lands
            // on the counter that matches the scenario kind.
            int expectedTargeted = 2 + (scenario.Kind == MessageKind.Targeted ? 1 : 0);
            int expectedUntargeted = 1 + (scenario.Kind == MessageKind.Untargeted ? 1 : 0);
            int expectedBroadcast = scenario.Kind == MessageKind.Broadcast ? 1 : 0;

            string deltaDescription = watcher.DescribeDelta();

            // Per-counter shape: the two default StringMessage handlers land on
            // Targeted regardless of scenario, the default GlobalStringMessage
            // handler lands on Untargeted, and the user counter lands on the
            // bucket that matches scenario.Kind. Failure messages surface the
            // diverging bucket(s) directly.
            AssertRegistrationCounts(
                bus,
                untargeted: expectedUntargeted,
                targeted: expectedTargeted,
                broadcast: expectedBroadcast,
                context: $"OmitBaseOnDisableAndOnDestroyLeaksDefaultHandlersToo[{scenario.Kind}] "
                    + $"after destroy. {deltaDescription}"
            );

            // Drop the orphaned registrations so they cannot bleed into the
            // next test; UnityCleanup's WaitUntilMessageHandlerIsFresh would
            // otherwise time out asserting bus staleness.
            DxMessagingStaticState.Reset();

            yield break;
        }

        /// <summary>
        /// Pins that the masking observed in
        /// <see cref="OnDisableDuringDestroyMasksOnDestroyLeak"/> covers the
        /// default <c>StringMessage</c> / <c>GlobalStringMessage</c> handlers
        /// the base class registers, not just user-added handlers. After
        /// destroy, emitting both default-handler triggers is a no-op because
        /// every default handler was deregistered by the inherited
        /// <c>OnDisable</c> during the destroy lifecycle. This guards against
        /// a future regression where the framework only deregisters user
        /// handlers in some code path, leaving default handlers stranded
        /// against a destroyed host.
        /// </summary>
        [UnityTest]
        public IEnumerator OnDisableDuringDestroyDeregistersDefaultStringHandlers()
        {
            using LeakWatcher watcher = new(
                bus: MessageHandler.MessageBus,
                throwOnLeak: false,
                label: nameof(OnDisableDuringDestroyDeregistersDefaultStringHandlers)
            );

            GameObject host = new(
                nameof(OnDisableDuringDestroyDeregistersDefaultStringHandlers),
                typeof(MissingBaseOnDestroyOnlyComponent)
            );
            _spawned.Add(host);

            MissingBaseOnDestroyOnlyComponent component =
                host.GetComponent<MissingBaseOnDestroyOnlyComponent>();
            Assert.IsNotNull(GetToken(component), "Token must be created.");

            // Capture the InstanceId BEFORE destroy so the post-destroy
            // emission targets the same id without dereferencing a
            // fake-null Unity wrapper.
            InstanceId hostId = host;

            // Sanity: spawning installs exactly the default handler count.
            Assert.AreEqual(
                DefaultStringMessageHandlerCount,
                watcher.Snapshot,
                "Spawn must add exactly the default handler count. {0}",
                watcher.DescribeDelta()
            );

            UnityEngine.Object.Destroy(host);
            _spawned.Remove(host);

            if (Application.isPlaying)
            {
                yield return null;
            }

            IMessageBus bus = MessageHandler.MessageBus;
            Assert.Zero(
                bus.RegisteredTargeted,
                "Default StringMessage Targeted handlers must be removed by "
                    + "the inherited base.OnDisable() during destroy. {0}",
                watcher.DescribeDelta()
            );
            Assert.Zero(
                bus.RegisteredUntargeted,
                "Default GlobalStringMessage Untargeted handler must be removed "
                    + "by the inherited base.OnDisable() during destroy. {0}",
                watcher.DescribeDelta()
            );

            // Emit the default-handler triggers against the captured id; with
            // every default handler deregistered the bus has no work to do
            // and no listener to dispatch to. This pins the user-observable
            // consequence of the masking: not just zero counters, but also
            // zero reachable handlers for the messages the framework would
            // normally route by default.
            // Use the untyped overload because Assert.DoesNotThrow takes a
            // delegate and the typed TargetedBroadcast takes a ref parameter,
            // which lambdas cannot capture.
            Assert.DoesNotThrow(
                () =>
                {
                    StringMessage stringMessage = new("after-destroy");
                    bus.UntypedTargetedBroadcast(hostId, stringMessage);
                },
                "Targeted broadcast against the destroyed host's id must not "
                    + "throw and must dispatch to nobody."
            );
            Assert.DoesNotThrow(
                () =>
                {
                    GlobalStringMessage globalMessage = new("after-destroy-global");
                    globalMessage.EmitUntargeted();
                },
                "Emitting GlobalStringMessage after the host is destroyed must not throw."
            );

            Assert.AreEqual(
                0,
                watcher.LeakedRegistrations,
                "No registrations may remain after the inherited base.OnDisable "
                    + "drains the token during destroy. {0}",
                watcher.DescribeDelta()
            );

            yield break;
        }

        /// <summary>
        /// Skipping <c>base.RegisterMessageHandlers()</c> means the default
        /// <c>StringMessage</c> / <c>GlobalStringMessage</c> registrations
        /// the base class normally adds are never installed, while user-added
        /// registrations in the override still apply because the token itself
        /// was created by the untouched <c>Awake</c>.
        /// </summary>
        [UnityTest]
        public IEnumerator OmitBaseRegisterMessageHandlersDoesNotRegisterDefaultStringHandlers()
        {
            GameObject host = new(
                nameof(OmitBaseRegisterMessageHandlersDoesNotRegisterDefaultStringHandlers),
                typeof(MissingBaseRegisterMessageHandlersComponent)
            );
            _spawned.Add(host);

            MissingBaseRegisterMessageHandlersComponent component =
                host.GetComponent<MissingBaseRegisterMessageHandlersComponent>();
            Assert.IsNotNull(
                GetToken(component),
                "Token must exist because base.Awake() still runs."
            );

            // Emit the default-handler messages: a component-targeted StringMessage
            // and an untargeted GlobalStringMessage. Without the base call, the
            // handlers the base would normally register for these are absent.
            StringMessage stringMessage = new("payload");
            stringMessage.EmitComponentTargeted(component);
            GlobalStringMessage globalMessage = new("global-payload");
            globalMessage.EmitUntargeted();

            Assert.AreEqual(
                0,
                component.defaultHandlerInvocations,
                "Default base-class string handlers must not fire when base.RegisterMessageHandlers() is skipped."
            );

            // Confirm the user's own registration (added inside the override)
            // does fire, proving the token itself is operational.
            SimpleUntargetedMessage userMessage = new();
            userMessage.EmitUntargeted();

            Assert.AreEqual(
                1,
                component.userHandlerInvocations,
                "User-registered handler in the override must still fire because the token is created."
            );

            yield break;
        }

        /// <summary>
        /// Positive control: a subclass that correctly chains <c>base</c> on
        /// every guarded lifecycle method passes every check the failure
        /// fixtures use - the handler fires while enabled, stops firing while
        /// disabled, and the bus returns to baseline on destroy.
        /// </summary>
        [UnityTest]
        public IEnumerator CorrectSubclassingPassesAllChecks(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Construct the watcher BEFORE spawning the host so the baseline is
            // the truly-fresh bus (0). The fixture
            // CorrectBaseCallContractComponent overrides
            // RegisterForStringMessages => false today, so this happens to
            // match the post-spawn count - but anchoring to the pre-spawn bus
            // removes the hidden coupling: if a future maintainer flips that
            // override to true, a "leak" of the default handlers would be
            // folded into a post-spawn baseline and silently masked. Watching
            // from before spawn pins the full round-trip (baseline=0,
            // after-spawn=0 with the override in place, after-register=1,
            // after-destroy=0, leaked=0) regardless of the override's value.
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                GameObject host = new(
                    nameof(CorrectSubclassingPassesAllChecks) + scenario.Kind,
                    typeof(CorrectBaseCallContractComponent)
                );
                _spawned.Add(host);

                CorrectBaseCallContractComponent component =
                    host.GetComponent<CorrectBaseCallContractComponent>();
                MessageRegistrationToken token = GetToken(component);
                Assert.IsNotNull(token, "[{0}] Token must be created.", scenario.Kind);

                int handlerInvocations = 0;
                MessageRegistrationHandle handle = RegisterCounter(
                    scenario,
                    token,
                    host,
                    () => handlerInvocations++
                );

                EmitDirectly(scenario, host);
                Assert.AreEqual(
                    1,
                    handlerInvocations,
                    "[{0}] Handler must fire while the component is enabled.",
                    scenario.Kind
                );

                component.enabled = false;
                EmitDirectly(scenario, host);
                Assert.AreEqual(
                    1,
                    handlerInvocations,
                    "[{0}] Handler must not fire after the component is disabled.",
                    scenario.Kind
                );

                token.RemoveRegistration(handle);
                UnityEngine.Object.Destroy(host);
                _spawned.Remove(host);
                if (Application.isPlaying)
                {
                    yield return null;
                }

                Assert.AreEqual(
                    0,
                    watcher.LeakedRegistrations,
                    "[{0}] Correct base-call chaining must leave no leaked registrations.",
                    scenario.Kind
                );
            }

            yield break;
        }

        /// <summary>
        /// Spawns a correct subclass and a broken subclass on different
        /// GameObjects and confirms the bus delivers messages only to the
        /// correct one. Pins that a single broken component does not
        /// suppress dispatch to its siblings.
        /// </summary>
        [UnityTest]
        public IEnumerator MultipleSubclassesDoNotCrossContaminate()
        {
            // A broken Awake emits one breadcrumb when the broken host enables
            // in builds that compile the runtime breadcrumb branch; declare
            // the expectation up front.
            ExpectMissingBaseAwakeBreadcrumbIfCompiled();

            GameObject correctHost = new(
                nameof(MultipleSubclassesDoNotCrossContaminate) + "_Correct",
                typeof(CorrectBaseCallContractComponent)
            );
            _spawned.Add(correctHost);

            GameObject brokenHost = new(
                nameof(MultipleSubclassesDoNotCrossContaminate) + "_Broken",
                typeof(MissingBaseAwakeComponent)
            );
            _spawned.Add(brokenHost);

            CorrectBaseCallContractComponent correct =
                correctHost.GetComponent<CorrectBaseCallContractComponent>();
            MissingBaseAwakeComponent broken = brokenHost.GetComponent<MissingBaseAwakeComponent>();

            Assert.IsNotNull(GetToken(correct), "Correct host must have a token.");
            Assert.IsNull(broken.Token, "Broken host must not have a token.");

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();

            Assert.AreEqual(
                1,
                correct.userHandlerInvocations,
                "Correct host must receive the message."
            );
            // The broken host has no token and no registration, so it cannot
            // observe a counter increment; assert via the only public surface
            // it exposes (the null token and a fresh emit-with-no-effect).
            Assert.IsNull(broken.Token, "Broken host must remain unable to register handlers.");

            yield break;
        }

        private static MessageRegistrationHandle RegisterCounter(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            System.Action onInvoked
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
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked()
                    );
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void ExpectMissingBaseAwakeBreadcrumbIfCompiled()
        {
#if UNITY_EDITOR || DEBUG
            LogAssert.Expect(LogType.Error, MissingBaseAwakeBreadcrumbPattern);
#endif
        }

        private static void EmitDirectly(MessageScenario scenario, InstanceId target)
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
                    throw new System.ArgumentOutOfRangeException(
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
