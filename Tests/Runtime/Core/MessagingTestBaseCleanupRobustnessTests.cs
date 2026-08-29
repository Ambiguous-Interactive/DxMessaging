#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Object = UnityEngine.Object;

    public sealed class MessagingTestBaseCleanupRobustnessTests : MessagingTestBase
    {
        private static readonly CleanupScenario[] CleanupScenarios =
        {
            new(
                "sync-zero",
                useUnityCleanup: false,
                trackedObjectCount: 0,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "sync-one",
                useUnityCleanup: false,
                trackedObjectCount: 1,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "sync-one-pre-destroy",
                useUnityCleanup: false,
                trackedObjectCount: 1,
                preDestroyFirstTrackedObject: true
            ),
            new(
                "sync-three",
                useUnityCleanup: false,
                trackedObjectCount: 3,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "sync-three-pre-destroy",
                useUnityCleanup: false,
                trackedObjectCount: 3,
                preDestroyFirstTrackedObject: true
            ),
            new(
                "unity-zero",
                useUnityCleanup: true,
                trackedObjectCount: 0,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "unity-one",
                useUnityCleanup: true,
                trackedObjectCount: 1,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "unity-one-pre-destroy",
                useUnityCleanup: true,
                trackedObjectCount: 1,
                preDestroyFirstTrackedObject: true
            ),
            new(
                "unity-three",
                useUnityCleanup: true,
                trackedObjectCount: 3,
                preDestroyFirstTrackedObject: false
            ),
            new(
                "unity-three-pre-destroy",
                useUnityCleanup: true,
                trackedObjectCount: 3,
                preDestroyFirstTrackedObject: true
            ),
        };

        // Unity Test Framework supports parameterization for UnityTest via ValueSource.
        [UnityTest]
        public IEnumerator CleanupVariantsDestroyTrackedObjectsAndClearRegistrations(
            [ValueSource(nameof(CleanupScenarios))] CleanupScenario scenario
        )
        {
            string scenarioLabel = scenario.ToString();
            TestContext.WriteLine(
                $"Running cleanup scenario: {scenarioLabel}. isPlaying={Application.isPlaying}."
            );

            List<GameObject> created = new(scenario.TrackedObjectCount);
            List<string> names = new(scenario.TrackedObjectCount);

            for (int i = 0; i < scenario.TrackedObjectCount; ++i)
            {
                GameObject go = new(
                    $"MessagingTestBaseCleanup_{scenario.Name}_{scenario.TrackedObjectCount}_{i}",
                    typeof(SimpleMessageAwareComponent)
                );
                _spawned.Add(go);
                created.Add(go);
                names.Add(go.name);
            }

            if (scenario.PreDestroyFirstTrackedObject)
            {
                Assert.Greater(
                    scenario.TrackedObjectCount,
                    0,
                    $"Pre-destroy requires at least one tracked object. {scenarioLabel}"
                );

                DestroyForCleanupScenario(created[0]);
                if (Application.isPlaying)
                {
                    yield return null;
                }

                Assert.IsTrue(
                    created[0] == null,
                    $"Pre-destroyed object should report as destroyed before cleanup runs. {scenarioLabel}"
                );
            }

            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus, $"Message bus must exist before cleanup. {scenarioLabel}");

            int totalBeforeCleanup =
                messageBus.RegisteredUntargeted
                + messageBus.RegisteredTargeted
                + messageBus.RegisteredBroadcast;

            int aliveBeforeCleanup = created.Count(go => go != null);
            if (scenario.TrackedObjectCount == 0)
            {
                Assert.Zero(
                    totalBeforeCleanup,
                    $"Zero tracked objects should not register handlers before cleanup. {scenarioLabel} {DescribeMessageBusState(messageBus, includeLog: true)}"
                );
            }
            else if (aliveBeforeCleanup > 0)
            {
                Assert.Greater(
                    totalBeforeCleanup,
                    0,
                    $"Expected at least one registration before cleanup when tracked objects are alive. {scenarioLabel} {DescribeMessageBusState(messageBus, includeLog: true)}"
                );
            }
            else
            {
                Assert.Zero(
                    totalBeforeCleanup,
                    $"No alive tracked objects should leave no active registrations before cleanup. {scenarioLabel} {DescribeMessageBusState(messageBus, includeLog: true)}"
                );
                TestContext.WriteLine(
                    $"All tracked objects were already destroyed before cleanup. Registrations before cleanup={totalBeforeCleanup}. {scenarioLabel}."
                );
            }

            if (scenario.UseUnityCleanup)
            {
                yield return UnityCleanup();
            }
            else
            {
                Cleanup();
                if (Application.isPlaying)
                {
                    yield return null;
                }
            }

            for (int i = 0; i < created.Count; ++i)
            {
                Assert.IsTrue(
                    created[i] == null,
                    $"Tracked object '{names[i]}' should be destroyed by cleanup. index={i}. {scenarioLabel}"
                );
            }

            Assert.Zero(
                _spawned.Count,
                $"Cleanup should clear tracked spawned objects. {scenarioLabel}"
            );
            yield return WaitUntilMessageHandlerIsFresh();

            IMessageBus finalMessageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(
                finalMessageBus,
                $"Message bus must remain available after cleanup. {scenarioLabel}"
            );

            int totalAfterCleanup =
                finalMessageBus.RegisteredUntargeted
                + finalMessageBus.RegisteredTargeted
                + finalMessageBus.RegisteredBroadcast;
            Assert.Zero(
                totalAfterCleanup,
                $"Cleanup should leave message bus fresh. {scenarioLabel} {DescribeMessageBusState(finalMessageBus, includeLog: true)}"
            );
        }

        /// <summary>
        /// Regression test for the destroy-then-Reset race that previously
        /// flooded TearDown with spurious over-deregistration errors.
        /// <para>
        /// Spawns a <see cref="MessageAwareComponent"/>, calls
        /// <see cref="Object.Destroy"/> synchronously (Unity defers the
        /// destruction to end-of-frame), wipes bus state via
        /// <see cref="DxMessagingStaticState.Reset"/>, then yields a frame to
        /// let Unity flush the destroy queue. The deferred
        /// <c>OnDisable</c>/<c>OnDestroy</c> callbacks must not log any
        /// over-deregistration errors -- the production hardening installs a
        /// reset-generation guard on every cached deregister closure to make
        /// this safe by design.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator DestroyThenResetDoesNotLogOverDeregistrationErrors(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Force a clean baseline so any pre-existing logs from earlier
            // setup do not contaminate the assertion below.
            DxMessagingStaticState.Reset();
            yield return WaitUntilMessageHandlerIsFresh();

            GameObject host = new(
                $"{nameof(DestroyThenResetDoesNotLogOverDeregistrationErrors)}_{scenario.Kind}",
                typeof(SimpleMessageAwareComponent)
            );

            // Layer a kind-specific registration on top of the auto-registered
            // StringMessage handlers so the regression is exercised across all
            // dispatch shapes.
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _ = ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (in SimpleUntargetedMessage _) => { }
                    );
                    break;
                case MessageKind.Targeted:
                    _ = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        hostId,
                        (in SimpleTargetedMessage _) => { }
                    );
                    break;
                case MessageKind.Broadcast:
                    _ = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        hostId,
                        (in SimpleBroadcastMessage _) => { }
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported scenario kind."
                    );
            }

            // Capture every log emitted by MessagingDebug across the destroy/
            // reset window. We swap the function (instead of using LogAssert)
            // so this test does not rely on Unity console plumbing or the
            // test runner's expected-log matching, both of which interact
            // badly with deferred destroys.
            //
            // The log function is installed *after* the Reset() below because
            // DxMessagingStaticState.Reset restores MessagingDebug.LogFunction
            // to the captured baseline; setting it before Reset would lose
            // the override exactly when we need it.
            Action<LogLevel, string> previousLogFunction = MessagingDebug.LogFunction;
            bool previousEnabled = MessagingDebug.enabled;
            List<string> capturedErrors = new();
            try
            {
                Object.Destroy(host);
                DxMessagingStaticState.Reset();

                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (level, message) =>
                {
                    if (level == LogLevel.Error)
                    {
                        capturedErrors.Add(message);
                    }
                };

                // Yield a frame so Unity flushes the deferred destroy queue.
                // Pre-fix this is when OnDisable/OnDestroy would fire against
                // the wiped bus and log over-deregistration errors.
                yield return null;
                yield return null;

                Assert.IsTrue(
                    host == null,
                    $"Destroy should have completed by now. scenario={scenario.Kind}."
                );

                Assert.IsEmpty(
                    capturedErrors,
                    "Expected no MessagingDebug error logs after destroy+reset, got: "
                        + string.Join(" | ", capturedErrors)
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previousLogFunction;
                MessagingDebug.enabled = previousEnabled;
            }
        }

        /// <summary>
        /// Companion to <see cref="DestroyThenResetDoesNotLogOverDeregistrationErrors"/>
        /// that pins the same race-safety guarantee for user-installed custom global buses.
        /// <para>
        /// The component's deregister closures live on the custom bus (because
        /// <see cref="MessageHandler.MessageBus"/> returned the custom bus when the
        /// component awoke). <see cref="DxMessagingStaticState.Reset"/> wipes the default
        /// bus and propagates the reset-generation bump to the active custom bus so the
        /// deferred destroy callbacks silently no-op against the custom bus instead of
        /// either logging spurious over-deregistration errors or undoing registrations
        /// the user wished to preserve.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator DestroyThenResetWithCustomBusDoesNotLogOverDeregistrationErrors(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Force a clean baseline so any pre-existing logs from earlier setup do not
            // contaminate the assertion below.
            DxMessagingStaticState.Reset();
            yield return WaitUntilMessageHandlerIsFresh();

            IMessageBus previousBus = MessageHandler.MessageBus;
            MessageBus customBus = new();
            MessageHandler.SetGlobalMessageBus(customBus);

            try
            {
                GameObject host = new(
                    $"{nameof(DestroyThenResetWithCustomBusDoesNotLogOverDeregistrationErrors)}_{scenario.Kind}",
                    typeof(SimpleMessageAwareComponent)
                );

                SimpleMessageAwareComponent component =
                    host.GetComponent<SimpleMessageAwareComponent>();
                MessageRegistrationToken token = GetToken(component);
                InstanceId hostId = host;
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        _ = ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                            scenario,
                            token,
                            (in SimpleUntargetedMessage _) => { }
                        );
                        break;
                    case MessageKind.Targeted:
                        _ = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                            scenario,
                            token,
                            hostId,
                            (in SimpleTargetedMessage _) => { }
                        );
                        break;
                    case MessageKind.Broadcast:
                        _ = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                            scenario,
                            token,
                            hostId,
                            (in SimpleBroadcastMessage _) => { }
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(scenario),
                            scenario.Kind,
                            "Unsupported scenario kind."
                        );
                }

                Assert.AreSame(
                    customBus,
                    MessageHandler.MessageBus,
                    "Component must be registered against the custom bus for this regression to be meaningful."
                );

                Action<LogLevel, string> previousLogFunction = MessagingDebug.LogFunction;
                bool previousEnabled = MessagingDebug.enabled;
                List<string> capturedErrors = new();
                try
                {
                    Object.Destroy(host);
                    DxMessagingStaticState.Reset();

                    MessagingDebug.enabled = true;
                    MessagingDebug.LogFunction = (level, message) =>
                    {
                        if (level == LogLevel.Error)
                        {
                            capturedErrors.Add(message);
                        }
                    };

                    yield return null;
                    yield return null;

                    Assert.IsTrue(
                        host == null,
                        $"Destroy should have completed by now. scenario={scenario.Kind}."
                    );

                    Assert.IsEmpty(
                        capturedErrors,
                        "Expected no MessagingDebug error logs after destroy+reset against a custom bus, got: "
                            + string.Join(" | ", capturedErrors)
                    );
                }
                finally
                {
                    MessagingDebug.LogFunction = previousLogFunction;
                    MessagingDebug.enabled = previousEnabled;
                }
            }
            finally
            {
                // Wipe the custom bus before restoring the previous global so the
                // generation guard cannot leak entries into the next test's bus
                // observation.
                customBus.ResetState();
                if (previousBus is MessageBus previousConcrete)
                {
                    MessageHandler.SetGlobalMessageBus(previousConcrete);
                }
                else if (previousBus != null)
                {
                    MessageHandler.SetGlobalMessageBus(previousBus);
                }
                else
                {
                    MessageHandler.ResetGlobalMessageBus();
                }
            }
        }

        private static void DestroyForCleanupScenario(GameObject spawned)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(spawned);
                return;
            }

            Object.DestroyImmediate(spawned);
        }

        public sealed class CleanupScenario
        {
            public CleanupScenario(
                string name,
                bool useUnityCleanup,
                int trackedObjectCount,
                bool preDestroyFirstTrackedObject
            )
            {
                Name = name;
                UseUnityCleanup = useUnityCleanup;
                TrackedObjectCount = trackedObjectCount;
                PreDestroyFirstTrackedObject = preDestroyFirstTrackedObject;
            }

            public string Name { get; }

            public bool UseUnityCleanup { get; }

            public int TrackedObjectCount { get; }

            public bool PreDestroyFirstTrackedObject { get; }

            public override string ToString()
            {
                return $"Name={Name},UseUnityCleanup={UseUnityCleanup},TrackedObjectCount={TrackedObjectCount},PreDestroyFirstTrackedObject={PreDestroyFirstTrackedObject}";
            }
        }
    }
}

#endif
