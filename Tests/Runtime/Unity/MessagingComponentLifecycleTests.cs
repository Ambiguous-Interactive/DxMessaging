#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Covers <see cref="MessagingComponent"/> lifecycle surface not exercised elsewhere:
    /// direct <see cref="MessagingComponent.ToggleMessageHandler"/> calls, same-host listener
    /// multiplexing (two <see cref="MessageAwareComponent"/> listeners sharing one
    /// <see cref="MessagingComponent"/>), and <see cref="MessagingComponent.Release"/> edge cases.
    /// </summary>
    /// <remarks>
    /// Complements (does not duplicate):
    /// - <c>EdgeCaseTests.MessagingComponentStopsEmittingWhenDisabled</c> /
    ///   <c>MessagingComponentContinuesEmittingWhenConfigured</c> (single-cycle
    ///   <c>MessagingComponent.enabled</c> toggles via the Unity lifecycle).
    /// - <c>BaseCallContractTests.MultipleSubclassesDoNotCrossContaminate</c> (separate-host
    ///   listeners); the same-host variants live here.
    /// - <c>Core.MessagingComponentLifecycleTests</c> (destroy-driven release bookkeeping).
    /// </remarks>
    public sealed class MessagingComponentLifecycleTests : MessagingTestBase
    {
        /// <summary>Retains public cleanup ownership when a release callback accepts new work.</summary>
        /// <remarks>
        /// 2026-10-26: a captured token can finish nested release before the outer call removes its listener key.
        /// Replacement work must remain reachable through the component, regardless of token identity.
        /// </remarks>
        [Test]
        public void ReleaseRetainsWorkAcceptedDuringNestedRelease(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool beforeDeregistration,
            [Values(false, true)] bool interceptor,
            [Values(false, true)] bool replace
        )
        {
            string context =
                $"[{scenario.Kind}, before={beforeDeregistration}, interceptor={interceptor}, replace={replace}]";
            GameObject host = new(
                nameof(ReleaseRetainsWorkAcceptedDuringNestedRelease),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus inner = new();
            ReleaseCallbackBus bus = new(inner, beforeDeregistration);
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            InstanceId route = host;
            MessageRegistrationToken original = messaging.Create(listener);
            MessageRegistrationToken replacement = null;
            MessageRegistrationToken recovery = null;
            int originalCalls = 0;
            int replacementCalls = 0;
            int recoveryCalls = 0;
            bool callbackCompleted = false;

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                try
                {
                    RegisterReleaseProbe(
                        scenario,
                        original,
                        route,
                        interceptor,
                        () => ++originalCalls
                    );
                    original.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        originalCalls,
                        Is.EqualTo(1),
                        $"{context} Initial work must deliver once."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.EqualTo(1),
                        $"{context} Setup must own one registration."
                    );

                    bus.OnDeregister = () =>
                    {
                        _ = messaging.Release(listener);
                        if (replace)
                        {
                            replacement = messaging.Create(listener);
                            RegisterReleaseProbe(
                                scenario,
                                replacement,
                                route,
                                interceptor,
                                () => ++replacementCalls
                            );
                            replacement.Enable();
                        }
                        callbackCompleted = true;
                    };

                    bool released = messaging.Release(listener);
                    Assert.That(
                        callbackCompleted,
                        Is.True,
                        $"{context} The nested public operation must complete."
                    );
                    Assert.That(
                        bus.Callbacks,
                        Is.EqualTo(1),
                        $"{context} Deregistration callback must run exactly once."
                    );
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        originalCalls,
                        Is.EqualTo(1),
                        $"{context} Released original callback must stay retired."
                    );
                    Assert.That(
                        replacementCalls,
                        Is.EqualTo(replace ? 1 : 0),
                        $"{context} Only accepted replacement work may deliver."
                    );
                    Assert.That(
                        released,
                        Is.EqualTo(!original.Enabled),
                        $"{context} Release may report completion only when its token is inactive."
                    );

                    if (replace)
                    {
                        Assert.That(
                            replacement.Enabled,
                            Is.True,
                            $"{context} Accepted replacement must remain active."
                        );
                        Assert.That(
                            messaging.TryGetToken(listener, out MessageRegistrationToken retained)
                                && ReferenceEquals(retained, replacement),
                            Is.True,
                            $"{context} Replacement ownership must remain in the component cache."
                        );
                        Assert.That(
                            messaging.Release(listener),
                            Is.True,
                            $"{context} Public release must reach accepted replacement work."
                        );
                    }
                    Assert.That(
                        messaging.Release(listener),
                        Is.False,
                        $"{context} Completed cleanup must be idempotent."
                    );
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        replacementCalls,
                        Is.EqualTo(replace ? 1 : 0),
                        $"{context} Public cleanup must stop replacement delivery."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.Zero,
                        $"{context} Public cleanup must release all registrations."
                    );

                    recovery = messaging.Create(listener);
                    RegisterReleaseProbe(
                        scenario,
                        recovery,
                        route,
                        interceptor,
                        () => ++recoveryCalls
                    );
                    recovery.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        recoveryCalls,
                        Is.EqualTo(1),
                        $"{context} Independent recovery must deliver once."
                    );
                    Assert.That(
                        messaging.Release(listener),
                        Is.True,
                        $"{context} Independent recovery must remain publicly releasable."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.Zero,
                        $"{context} Independent recovery must finish empty."
                    );
                }
                finally
                {
                    bus.OnDeregister = null;
                    original.Dispose();
                    replacement?.Dispose();
                    recovery?.Dispose();
                    _ = messaging.Release(listener);
                }
            }
        }

        private static void RegisterReleaseProbe(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId route,
            bool interceptor,
            Action callback
        )
        {
            if (interceptor)
            {
                _ = ScenarioCallbacks.RegisterCountingInterceptor(
                    scenario,
                    token,
                    () => true,
                    callback
                );
            }
            else
            {
                _ = ScenarioCallbacks.RegisterCountingHandler(scenario, token, route, callback);
            }
        }

        private sealed class ReleaseCallbackBus : DelegatingMessageBus
        {
            private readonly bool _before;
            internal Action OnDeregister;
            internal int Callbacks;

            internal ReleaseCallbackBus(IMessageBus inner, bool before)
                : base(inner)
            {
                _before = before;
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnDeregister;
                OnDeregister = null;
                if (_before && callback != null)
                {
                    ++Callbacks;
                    callback();
                }
                base.Deregister<T>(in registration);
                if (!_before && callback != null)
                {
                    ++Callbacks;
                    callback();
                }
            }
        }

        /// <summary>Leaves detached tokens and replacement owners intact across configuration callbacks.</summary>
        /// <remarks>
        /// 2026-10-26: a live listener enumeration was invalidated by completed peer release during
        /// retarget cleanup. A captured peer token may also accept independently owned work before
        /// the outer migration resumes, so checking the listener key alone is insufficient.
        /// </remarks>
        [Test]
        public void ConfigureDoesNotRetargetDetachedTokensOrReplacementOwners(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool beforeDeregistration,
            [Values(false, true)] bool interceptor,
            [Values(false, true)] bool replaceOwner,
            [Values(false, true)] bool throwAfterRelease
        )
        {
            string context =
                $"[{scenario.Kind}, before={beforeDeregistration}, interceptor={interceptor}, replace={replaceOwner}, throw={throwAfterRelease}]";
            GameObject host = new(
                nameof(ConfigureDoesNotRetargetDetachedTokensOrReplacementOwners),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent first = host.GetComponent<ManualListenerComponent>();
            ManualListenerComponent second = host.AddComponent<ManualListenerComponent>();
            InstanceId route = host;
            MessageBus original = new();
            MessageBus replacement = new();
            MessageBus independent = new();
            ReleaseCallbackBus bus = new(original, beforeDeregistration);
            using (LeakWatcher originalWatcher = new(bus: bus, label: context + " original"))
            using (
                LeakWatcher replacementWatcher = new(
                    bus: replacement,
                    label: context + " replacement"
                )
            )
            using (
                LeakWatcher independentWatcher = new(
                    bus: independent,
                    label: context + " independent"
                )
            )
            {
                for (int cycle = 0; cycle < 10; ++cycle)
                {
                    string cycleContext = $"{context}, cycle={cycle}";
                    messaging.Configure(bus, MessageBusRebindMode.RebindActive);
                    MessageRegistrationToken firstToken = messaging.Create(first);
                    MessageRegistrationToken secondToken = messaging.Create(second);
                    MessageRegistrationToken replacementOwner = null;
                    int firstCalls = 0;
                    int secondCalls = 0;
                    int independentCalls = 0;
                    int replacementCalls = 0;
                    MonoBehaviour survivor = null;
                    foreach (MonoBehaviour listener in messaging._registeredListeners.Keys)
                    {
                        survivor = listener;
                        break;
                    }
                    Assert.That(survivor, Is.Not.Null, cycleContext);
                    bool firstSurvives = ReferenceEquals(survivor, first);
                    MonoBehaviour peer = firstSurvives ? second : first;
                    MessageRegistrationToken survivorToken = firstSurvives
                        ? firstToken
                        : secondToken;
                    MessageRegistrationToken peerToken = firstSurvives ? secondToken : firstToken;
                    InvalidOperationException failure = new("configuration cleanup failure");
                    try
                    {
                        RegisterReleaseProbe(
                            scenario,
                            firstToken,
                            route,
                            interceptor,
                            () => ++firstCalls
                        );
                        RegisterReleaseProbe(
                            scenario,
                            secondToken,
                            route,
                            interceptor,
                            () => ++secondCalls
                        );
                        firstToken.Enable();
                        secondToken.Enable();
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(firstCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(secondCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(
                            originalWatcher.LeakedRegistrations,
                            Is.EqualTo(2),
                            cycleContext
                        );
                        int callbacksBefore = bus.Callbacks;
                        bool callbackCompleted = false;
                        bus.OnDeregister = () =>
                        {
                            Assert.That(messaging.Release(peer), Is.True, cycleContext);
                            Assert.That(messaging.TryGetToken(peer, out _), Is.False, cycleContext);
                            peerToken.RetargetMessageBus(
                                independent,
                                MessageBusRebindMode.PreserveRegistrations
                            );
                            RegisterReleaseProbe(
                                scenario,
                                peerToken,
                                route,
                                interceptor,
                                () => ++independentCalls
                            );
                            peerToken.Enable();
                            if (replaceOwner)
                            {
                                replacementOwner = messaging.Create(peer);
                                RegisterReleaseProbe(
                                    scenario,
                                    replacementOwner,
                                    route,
                                    interceptor,
                                    () => ++replacementCalls
                                );
                                replacementOwner.Enable();
                            }
                            callbackCompleted = true;
                            if (throwAfterRelease)
                            {
                                throw failure;
                            }
                        };

                        Exception configurationFailure = null;
                        try
                        {
                            messaging.Configure(replacement, MessageBusRebindMode.RebindActive);
                        }
                        catch (Exception exception)
                        {
                            configurationFailure = exception;
                        }
                        Assert.That(callbackCompleted, Is.True, cycleContext);
                        Assert.That(bus.Callbacks, Is.EqualTo(callbacksBefore + 1), cycleContext);
                        if (throwAfterRelease)
                        {
                            Assert.That(configurationFailure, Is.SameAs(failure), cycleContext);
                        }
                        else
                        {
                            Assert.That(configurationFailure, Is.Null, cycleContext);
                        }
                        Assert.That(
                            messaging.TryGetToken(survivor, out MessageRegistrationToken retained),
                            Is.True,
                            cycleContext
                        );
                        Assert.That(retained, Is.SameAs(survivorToken), cycleContext);
                        Assert.That(
                            messaging.TryGetToken(peer, out MessageRegistrationToken cachedPeer),
                            Is.EqualTo(replaceOwner),
                            cycleContext
                        );
                        Assert.That(cachedPeer, Is.SameAs(replacementOwner), cycleContext);
                        Assert.That(peerToken.Enabled, Is.True, cycleContext);
                        Assert.That(
                            messaging._registeredListeners.Count,
                            Is.EqualTo(replaceOwner ? 2 : 1),
                            cycleContext
                        );
                        ScenarioCallbacks.EmitForKind(scenario, independent, route);
                        Assert.That(independentCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(replacementCalls, Is.Zero, cycleContext);
                        if (!throwAfterRelease)
                        {
                            Assert.That(originalWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                            ScenarioCallbacks.EmitForKind(scenario, bus, route);
                            Assert.That(firstCalls, Is.EqualTo(1), cycleContext);
                            Assert.That(secondCalls, Is.EqualTo(1), cycleContext);
                            ScenarioCallbacks.EmitForKind(scenario, replacement, route);
                            Assert.That(
                                firstCalls,
                                Is.EqualTo(firstSurvives ? 2 : 1),
                                cycleContext
                            );
                            Assert.That(
                                secondCalls,
                                Is.EqualTo(firstSurvives ? 1 : 2),
                                cycleContext
                            );
                            Assert.That(
                                replacementCalls,
                                Is.EqualTo(replaceOwner ? 1 : 0),
                                cycleContext
                            );
                        }
                        Assert.That(messaging.Release(survivor), Is.True, cycleContext);
                        Assert.That(
                            messaging.Release(peer),
                            Is.EqualTo(replaceOwner),
                            cycleContext
                        );
                        Assert.That(messaging._registeredListeners.Count, Is.Zero, cycleContext);
                        Assert.That(peerToken.Enabled, Is.True, cycleContext);
                        Assert.That(originalWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                        Assert.That(replacementWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                        Assert.That(
                            independentWatcher.LeakedRegistrations,
                            Is.EqualTo(1),
                            cycleContext
                        );
                        ScenarioCallbacks.EmitForKind(scenario, independent, route);
                        Assert.That(independentCalls, Is.EqualTo(2), cycleContext);
                    }
                    finally
                    {
                        bus.OnDeregister = null;
                        firstToken.Dispose();
                        secondToken.Dispose();
                        replacementOwner?.Dispose();
                        _ = messaging.Release(first);
                        _ = messaging.Release(second);
                    }
                    Assert.That(originalWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                    Assert.That(replacementWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                    Assert.That(independentWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                }
            }
        }

        /// <summary>Retains listener ownership when a bus callback releases a peer during configuration.</summary>
        [Test]
        public void ConfigureRetainsListenerOwnershipWhenBusCallbacksReleasePeers(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool beforeDeregistration,
            [Values(false, true)] bool interceptor,
            [Values(false, true)] bool releasePeer
        )
        {
            string context =
                $"[{scenario.Kind}, before={beforeDeregistration}, interceptor={interceptor}, releasePeer={releasePeer}]";
            GameObject host = new(
                nameof(ConfigureRetainsListenerOwnershipWhenBusCallbacksReleasePeers),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent first = host.GetComponent<ManualListenerComponent>();
            ManualListenerComponent second = host.AddComponent<ManualListenerComponent>();
            InstanceId route = host;
            MessageBus original = new();
            MessageBus replacement = new();
            ReleaseCallbackBus bus = new(original, beforeDeregistration);
            using (LeakWatcher originalWatcher = new(bus: bus, label: context + " original"))
            using (
                LeakWatcher replacementWatcher = new(
                    bus: replacement,
                    label: context + " replacement"
                )
            )
            {
                for (int cycle = 0; cycle < 10; ++cycle)
                {
                    string cycleContext = $"{context}, cycle={cycle}";
                    messaging.Configure(bus, MessageBusRebindMode.RebindActive);
                    MessageRegistrationToken firstToken = messaging.Create(first);
                    MessageRegistrationToken secondToken = messaging.Create(second);
                    int firstCalls = 0;
                    int secondCalls = 0;
                    MonoBehaviour survivor = null;
                    foreach (MonoBehaviour listener in messaging._registeredListeners.Keys)
                    {
                        survivor = listener;
                        break;
                    }
                    Assert.That(survivor, Is.Not.Null, cycleContext);
                    bool firstSurvives = ReferenceEquals(survivor, first);
                    MonoBehaviour peer = firstSurvives ? second : first;
                    MessageRegistrationToken survivorToken = firstSurvives
                        ? firstToken
                        : secondToken;
                    MessageRegistrationToken peerToken = firstSurvives ? secondToken : firstToken;
                    try
                    {
                        RegisterReleaseProbe(
                            scenario,
                            firstToken,
                            route,
                            interceptor,
                            () => ++firstCalls
                        );
                        RegisterReleaseProbe(
                            scenario,
                            secondToken,
                            route,
                            interceptor,
                            () => ++secondCalls
                        );
                        firstToken.Enable();
                        secondToken.Enable();
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(firstCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(secondCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(
                            originalWatcher.LeakedRegistrations,
                            Is.EqualTo(2),
                            cycleContext
                        );
                        int callbacksBefore = bus.Callbacks;
                        bool callbackCompleted = false;
                        bus.OnDeregister = () =>
                        {
                            if (releasePeer)
                            {
                                Assert.That(messaging.Release(peer), Is.True, cycleContext);
                                Assert.That(peerToken.Enabled, Is.False, cycleContext);
                                Assert.That(
                                    messaging.TryGetToken(peer, out _),
                                    Is.False,
                                    cycleContext
                                );
                            }
                            callbackCompleted = true;
                        };

                        Exception configurationFailure = null;
                        try
                        {
                            messaging.Configure(replacement, MessageBusRebindMode.RebindActive);
                        }
                        catch (Exception exception)
                        {
                            configurationFailure = exception;
                        }
                        Assert.That(callbackCompleted, Is.True, cycleContext);
                        Assert.That(bus.Callbacks, Is.EqualTo(callbacksBefore + 1), cycleContext);
                        Assert.That(
                            configurationFailure,
                            Is.Null,
                            cycleContext
                                + " Configuration must survive a completed peer-release callback."
                        );
                        Assert.That(
                            messaging.TryGetToken(survivor, out MessageRegistrationToken retained),
                            Is.True,
                            cycleContext
                        );
                        Assert.That(retained, Is.SameAs(survivorToken), cycleContext);
                        Assert.That(survivorToken.Enabled, Is.True, cycleContext);
                        Assert.That(peerToken.Enabled, Is.EqualTo(!releasePeer), cycleContext);
                        Assert.That(
                            messaging._registeredListeners.Count,
                            Is.EqualTo(releasePeer ? 1 : 2),
                            cycleContext
                        );
                        Assert.That(originalWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                        Assert.That(
                            replacementWatcher.LeakedRegistrations,
                            Is.EqualTo(releasePeer ? 1 : 2),
                            cycleContext
                        );
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(firstCalls, Is.EqualTo(1), cycleContext);
                        Assert.That(secondCalls, Is.EqualTo(1), cycleContext);
                        ScenarioCallbacks.EmitForKind(scenario, replacement, route);
                        Assert.That(
                            firstCalls,
                            Is.EqualTo(!releasePeer || firstSurvives ? 2 : 1),
                            cycleContext
                        );
                        Assert.That(
                            secondCalls,
                            Is.EqualTo(!releasePeer || !firstSurvives ? 2 : 1),
                            cycleContext
                        );
                    }
                    finally
                    {
                        bus.OnDeregister = null;
                        firstToken.Dispose();
                        secondToken.Dispose();
                        _ = messaging.Release(first);
                        _ = messaging.Release(second);
                    }
                    Assert.That(originalWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                    Assert.That(replacementWatcher.LeakedRegistrations, Is.Zero, cycleContext);
                }
            }
        }

        [Test]
        public void ReleasingAnotherListenerPreservesEachInFlightOwner(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool beforeDeregistration,
            [Values(false, true)] bool interceptor
        )
        {
            string context =
                $"[{scenario.Kind}, before={beforeDeregistration}, interceptor={interceptor}]";
            GameObject host = new(
                nameof(ReleasingAnotherListenerPreservesEachInFlightOwner),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent first = host.GetComponent<ManualListenerComponent>();
            ManualListenerComponent second = host.AddComponent<ManualListenerComponent>();
            MessageBus inner = new();
            ReleaseCallbackBus bus = new(inner, beforeDeregistration);
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            InstanceId route = host;
            MessageRegistrationToken firstToken = messaging.Create(first);
            MessageRegistrationToken secondToken = messaging.Create(second);
            int firstCalls = 0;
            int secondCalls = 0;
            int replacementCalls = 0;
            bool secondReleased = false;
            bool firstNestedReleased = true;
            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                try
                {
                    RegisterReleaseProbe(
                        scenario,
                        firstToken,
                        route,
                        interceptor,
                        () => ++firstCalls
                    );
                    RegisterReleaseProbe(
                        scenario,
                        secondToken,
                        route,
                        interceptor,
                        () => ++secondCalls
                    );
                    firstToken.Enable();
                    secondToken.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(firstCalls, Is.EqualTo(1), context);
                    Assert.That(secondCalls, Is.EqualTo(1), context);
                    Assert.That(watcher.LeakedRegistrations, Is.EqualTo(2), context);
                    bus.OnDeregister = () =>
                    {
                        bus.OnDeregister = () =>
                        {
                            firstNestedReleased = messaging.Release(first);
                            RegisterReleaseProbe(
                                scenario,
                                firstToken,
                                route,
                                interceptor,
                                () => ++replacementCalls
                            );
                            firstToken.Enable();
                        };
                        secondReleased = messaging.Release(second);
                    };

                    Assert.That(messaging.Release(first), Is.False, context);
                    Assert.That(secondReleased, Is.True, context);
                    Assert.That(firstNestedReleased, Is.False, context);
                    Assert.That(bus.Callbacks, Is.EqualTo(2), context);
                    Assert.That(
                        messaging.TryGetToken(first, out MessageRegistrationToken retained),
                        Is.True,
                        context
                    );
                    Assert.That(retained, Is.SameAs(firstToken), context);
                    Assert.That(messaging.TryGetToken(second, out _), Is.False, context);
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(firstCalls, Is.EqualTo(1), context);
                    Assert.That(secondCalls, Is.EqualTo(1), context);
                    Assert.That(replacementCalls, Is.EqualTo(1), context);
                    Assert.That(watcher.LeakedRegistrations, Is.EqualTo(1), context);
                    Assert.That(messaging.Release(first), Is.True, context);
                    Assert.That(messaging.Release(first), Is.False, context);
                    Assert.That(watcher.LeakedRegistrations, Is.Zero, context);
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(replacementCalls, Is.EqualTo(1), context);
                }
                finally
                {
                    bus.OnDeregister = null;
                    firstToken.Dispose();
                    secondToken.Dispose();
                    _ = messaging.Release(first);
                    _ = messaging.Release(second);
                }
            }
        }

        [Test]
        public void RejectedReleaseKeepsNestedWorkOwnedForPublicRetry(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool interceptor,
            [Values(false, true)] bool stageReplacement,
            [Values(false, true)] bool nestedRelease
        )
        {
            string context =
                $"[{scenario.Kind}, interceptor={interceptor}, replacement={stageReplacement}, nestedRelease={nestedRelease}]";
            GameObject host = new(
                nameof(RejectedReleaseKeepsNestedWorkOwnedForPublicRetry),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus inner = new();
            RejectedReleaseBus bus = new(inner);
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            InstanceId route = host;
            FieldInfo releaseOwners = typeof(MessagingComponent).GetField(
                "_releasingTokens",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                releaseOwners,
                Is.Not.Null,
                $"{context} Inspect actual transient ownership storage."
            );

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                for (int iteration = 0; iteration < 10; ++iteration)
                {
                    string label = context + ", iteration=" + iteration;
                    MessageRegistrationToken token = messaging.Create(listener);
                    int originalCalls = 0;
                    int replacementCalls = 0;
                    bool callbackCompleted = false;
                    try
                    {
                        RegisterReleaseProbe(
                            scenario,
                            token,
                            route,
                            interceptor,
                            () => ++originalCalls
                        );
                        token.Enable();
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(
                            originalCalls,
                            Is.EqualTo(1),
                            $"{label} Initial callback must deliver once."
                        );
                        Assert.That(
                            watcher.LeakedRegistrations,
                            Is.EqualTo(1),
                            $"{label} Setup must own one registration."
                        );
                        bus.RejectNext = true;
                        bus.OnDeregister = () =>
                        {
                            if (nestedRelease)
                            {
                                _ = messaging.Release(listener);
                            }
                            if (stageReplacement)
                            {
                                RegisterReleaseProbe(
                                    scenario,
                                    token,
                                    route,
                                    interceptor,
                                    () => ++replacementCalls
                                );
                                token.Enable();
                            }
                            callbackCompleted = true;
                        };

                        InvalidOperationException observed =
                            Assert.Throws<InvalidOperationException>(
                                () => messaging.Release(listener),
                                $"{label} Rejected cleanup must propagate."
                            );
                        Assert.That(
                            observed,
                            Is.SameAs(bus.Failure),
                            $"{label} Preserve exact cleanup exception identity."
                        );
                        Assert.That(
                            callbackCompleted,
                            Is.True,
                            $"{label} Complete the admitted one-shot callback."
                        );
                        Assert.That(
                            bus.Callbacks,
                            Is.EqualTo(iteration + 1),
                            $"{label} Invoke each callback once."
                        );
                        Assert.That(
                            token.Enabled,
                            Is.True,
                            $"{label} Failed cleanup must retain live ownership."
                        );
                        Assert.That(
                            watcher.LeakedRegistrations,
                            Is.GreaterThan(0),
                            $"{label} Observe pending work before retry."
                        );
                        Assert.That(
                            messaging.TryGetToken(listener, out MessageRegistrationToken retained)
                                && ReferenceEquals(retained, token),
                            Is.True,
                            $"{label} The component must retain its retryable cleanup owner."
                        );
                        Assert.That(
                            releaseOwners.GetValue(messaging),
                            Is.Null,
                            $"{label} Failed frames must release transient owner storage."
                        );
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(
                            replacementCalls,
                            Is.EqualTo(stageReplacement ? 1 : 0),
                            $"{label} Accepted new work must remain observable."
                        );
                        int oldCallsBeforeRetry = originalCalls;
                        int newCallsBeforeRetry = replacementCalls;

                        Assert.That(
                            messaging.Release(listener),
                            Is.True,
                            $"{label} Public retry must drain original and new work."
                        );
                        Assert.That(
                            messaging.Release(listener),
                            Is.False,
                            $"{label} Completed retry must be idempotent."
                        );
                        Assert.That(
                            token.Enabled,
                            Is.False,
                            $"{label} Complete the token cleanup."
                        );
                        Assert.That(
                            watcher.LeakedRegistrations,
                            Is.Zero,
                            $"{label} Public retry must finish empty."
                        );
                        Assert.That(
                            releaseOwners.GetValue(messaging),
                            Is.Null,
                            $"{label} Completed frames must release their capacity."
                        );
                        ScenarioCallbacks.EmitForKind(scenario, bus, route);
                        Assert.That(
                            originalCalls,
                            Is.EqualTo(oldCallsBeforeRetry),
                            $"{label} Retry must stop original delivery."
                        );
                        Assert.That(
                            replacementCalls,
                            Is.EqualTo(newCallsBeforeRetry),
                            $"{label} Retry must stop replacement delivery."
                        );
                    }
                    finally
                    {
                        bus.OnDeregister = null;
                        bus.RejectNext = false;
                        token.Dispose();
                        _ = messaging.Release(listener);
                    }
                }
            }
        }

        private sealed class RejectedReleaseBus : DelegatingMessageBus
        {
            internal readonly InvalidOperationException Failure = new(
                "Component release rejected before acceptance."
            );
            internal Action OnDeregister;
            internal bool RejectNext;
            internal int Callbacks;

            internal RejectedReleaseBus(IMessageBus inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnDeregister;
                OnDeregister = null;
                if (callback != null)
                {
                    ++Callbacks;
                    callback();
                }
                if (RejectNext)
                {
                    RejectNext = false;
                    throw Failure;
                }
                base.Deregister<T>(in registration);
            }
        }

        [Test]
        public void DestroyingMessagingOwnerDisposesManualTokens(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool emitWhenDisabled,
            [Values(false, true)] bool destroyWholeHost
        )
        {
            string context =
                $"[{scenario.Kind}, emitWhenDisabled={emitWhenDisabled}, destroyWholeHost={destroyWholeHost}]";
            GameObject host = new(
                nameof(DestroyingMessagingOwnerDisposesManualTokens),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus bus = new();
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            messaging.emitMessagesWhenDisabled = emitWhenDisabled;
            InstanceId route = host;

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                try
                {
                    int calls = 0;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        route,
                        () => ++calls
                    );
                    token.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(calls, Is.EqualTo(1), $"{context} Control must deliver once.");

                    if (destroyWholeHost)
                    {
                        _spawned.Remove(host);
                        UnityEngine.Object.DestroyImmediate(host);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(messaging);
                    }

                    Assert.That(
                        messaging == null,
                        Is.True,
                        $"{context} Messaging owner must be destroyed."
                    );
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"{context} Destroyed messaging owners must stop delivery."
                    );
                    Assert.That(
                        token.Enabled,
                        Is.False,
                        $"{context} Destroying the owner must dispose its tokens."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.Zero,
                        $"{context} Destruction must remove bus registrations."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.Zero,
                        $"{context} Destruction must release retained listener references."
                    );
                }
                finally
                {
                    token.Dispose();
                }
            }
        }

        [Test]
        public void DestructionFailureRetainsRetryWithoutBlockingSiblingCleanup(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            AssertCleanupFailureRetainsRetryableListener(scenario, destroyOwner: true);
        }

#if UNITY_EDITOR
        [Test]
        public void EditorResetFailureRetainsRetryWithoutBlockingSiblingCleanup(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            AssertCleanupFailureRetainsRetryableListener(scenario, destroyOwner: false);
        }
#endif

        private void AssertCleanupFailureRetainsRetryableListener(
            MessageScenario scenario,
            bool destroyOwner
        )
        {
            string context = $"[{scenario.Kind}, destroyOwner={destroyOwner}]";
            GameObject host = new(
                nameof(AssertCleanupFailureRetainsRetryableListener),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent first = host.GetComponent<ManualListenerComponent>();
            ManualListenerComponent sibling = host.AddComponent<ManualListenerComponent>();
            MessageBus innerBus = new();
            ThrowOnceDeregistrationBus bus = new(innerBus);
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            messaging.emitMessagesWhenDisabled = true;
            InstanceId route = host;

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                MessageRegistrationToken firstToken = first.RequestToken(messaging);
                MessageRegistrationToken siblingToken = sibling.RequestToken(messaging);
                try
                {
                    int calls = 0;
                    firstToken.DiagnosticMode = true;
                    siblingToken.DiagnosticMode = true;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        firstToken,
                        route,
                        () => ++calls
                    );
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        siblingToken,
                        route,
                        () => ++calls
                    );
                    firstToken.Enable();
                    siblingToken.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(2),
                        $"{context} Both listeners must deliver before cleanup."
                    );

                    LogAssert.Expect(
                        LogType.Warning,
                        new Regex(
                            @"\[DxMessaging\] Disposing listener token failed\. System.InvalidOperationException: Deregistration failure\."
                        )
                    );
                    if (destroyOwner)
                    {
                        UnityEngine.Object.DestroyImmediate(messaging);
                        messaging.ToggleMessageHandler(true);
                        messaging.OnEnable();
                        Assert.Throws<ObjectDisposedException>(
                            () => messaging.Create(first),
                            $"{context} A destroyed owner must reject new tokens."
                        );
                    }
#if UNITY_EDITOR
                    else
                    {
                        Assert.That(
                            messaging.EditorResetRuntimeState(),
                            Is.False,
                            $"{context} A failed reset must not report success."
                        );
                    }
#endif

                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(2),
                        $"{context} Failed cleanup must leave the old handler inactive."
                    );
                    Assert.That(
                        firstToken.Enabled,
                        Is.True,
                        $"{context} Failed token must remain retryable."
                    );
                    Assert.That(
                        siblingToken.Enabled,
                        Is.False,
                        $"{context} Failure must not block sibling disposal."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.EqualTo(1),
                        $"{context} Only failed cleanup may retain a listener."
                    );
                    Assert.That(
                        siblingToken._metadata.Count,
                        Is.Zero,
                        $"{context} Successful disposal must clear metadata."
                    );
                    Assert.That(
                        siblingToken._callCounts.Count,
                        Is.Zero,
                        $"{context} Successful disposal must clear call counts."
                    );
                    Assert.That(
                        siblingToken._emissionBuffer.Count,
                        Is.Zero,
                        $"{context} Successful disposal must clear history."
                    );

                    if (destroyOwner)
                    {
                        Assert.That(
                            messaging.Release(first),
                            Is.True,
                            $"{context} Retained destroyed owner must support explicit cleanup retry."
                        );
                    }
#if UNITY_EDITOR
                    else
                    {
                        Assert.That(
                            messaging.EditorResetRuntimeState(),
                            Is.True,
                            $"{context} Reset retry must complete cleanup."
                        );
                    }
#endif

                    Assert.That(
                        firstToken.Enabled,
                        Is.False,
                        $"{context} Retry must dispose the retained token."
                    );
                    Assert.That(
                        firstToken._metadata.Count,
                        Is.Zero,
                        $"{context} Retry must clear metadata."
                    );
                    Assert.That(
                        firstToken._callCounts.Count,
                        Is.Zero,
                        $"{context} Retry must clear call counts."
                    );
                    Assert.That(
                        firstToken._emissionBuffer.Count,
                        Is.Zero,
                        $"{context} Retry must clear history."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.Zero,
                        $"{context} Retry must release listener references."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.Zero,
                        $"{context} Retry must remove remaining registrations."
                    );
                }
                finally
                {
                    firstToken.Dispose();
                    siblingToken.Dispose();
                }
            }
        }

        [Test]
        public void ExplicitReleaseCleansTokenAfterNeverActivatedHostDestruction(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            string context = $"[{scenario.Kind}]";
            GameObject host = new(
                nameof(ExplicitReleaseCleansTokenAfterNeverActivatedHostDestruction)
            );
            _spawned.Add(host);
            host.SetActive(false);
            MessagingComponent messaging = host.AddComponent<MessagingComponent>();
            ManualListenerComponent listener = host.AddComponent<ManualListenerComponent>();
            MessageBus bus = new();
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            messaging.emitMessagesWhenDisabled = true;
            InstanceId route = host;

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                try
                {
                    int calls = 0;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        route,
                        () => ++calls
                    );
                    token.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"{context} Explicit opt-in must deliver before host activation."
                    );

                    _spawned.Remove(host);
                    UnityEngine.Object.DestroyImmediate(host);
                    Assert.That(
                        host == null,
                        Is.True,
                        $"{context} Never-activated host must be destroyed."
                    );
                    messaging.Release(listener);

                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"{context} Explicit release must stop delivery after destruction."
                    );
                    Assert.That(
                        token.Enabled,
                        Is.False,
                        $"{context} Explicit release must dispose the token."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.Zero,
                        $"{context} Explicit release must drop retained listeners."
                    );
                    Assert.That(
                        watcher.LeakedRegistrations,
                        Is.Zero,
                        $"{context} Explicit release must remove bus registrations."
                    );
                }
                finally
                {
                    token.Dispose();
                }
            }
        }

        [Test]
        public void TokenCreatedBeforeHostActivationRespectsHandlerState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool emitWhenDisabled,
            [Values(false, true)] bool componentEnabled
        )
        {
            string context =
                $"[{scenario.Kind}, emitWhenDisabled={emitWhenDisabled}, componentEnabled={componentEnabled}]";
            GameObject host = new(nameof(TokenCreatedBeforeHostActivationRespectsHandlerState));
            _spawned.Add(host);
            host.SetActive(false);
            MessagingComponent messaging = host.AddComponent<MessagingComponent>();
            ManualListenerComponent listener = host.AddComponent<ManualListenerComponent>();
            messaging.enabled = componentEnabled;
            messaging.emitMessagesWhenDisabled = emitWhenDisabled;
            MessageBus bus = new();
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);
            InstanceId route = host;

            using (LeakWatcher watcher = new(bus: bus, label: context))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                try
                {
                    int calls = 0;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        route,
                        () => ++calls
                    );
                    token.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    int expectedCalls = emitWhenDisabled ? 1 : 0;
                    Assert.That(
                        calls,
                        Is.EqualTo(expectedCalls),
                        $"{context} A new handler must respect the inactive host."
                    );

                    host.SetActive(true);
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    expectedCalls += emitWhenDisabled || componentEnabled ? 1 : 0;
                    Assert.That(
                        calls,
                        Is.EqualTo(expectedCalls),
                        $"{context} Host activation must respect the component state."
                    );

                    messaging.enabled = true;
                    ScenarioCallbacks.EmitForKind(scenario, bus, route);
                    Assert.That(
                        calls,
                        Is.EqualTo(expectedCalls + 1),
                        $"{context} Enabling the host and component must resume delivery."
                    );
                }
                finally
                {
                    messaging.Release(listener);
                }
            }
        }

        [Test]
        public void ToggleMessageHandlerFalseSuspendsDeliveryUntilToggledTrue()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerFalseSuspendsDeliveryUntilToggledTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            /*
                Direct public API call; the component and the listener stay enabled throughout,
                proving the toggle gates delivery independently of Unity's enabled state.
            */
            messaging.ToggleMessageHandler(false);
            Assert.IsTrue(messaging.enabled, "Toggling the handler must not touch enabled state.");

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "ToggleMessageHandler(false) should suspend delivery for the shared handler."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(2, count, "ToggleMessageHandler(true) should resume delivery.");
        }

        [Test]
        public void ToggleMessageHandlerFalseWinsOverEmitMessagesWhenDisabled()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerFalseWinsOverEmitMessagesWhenDisabled),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            messaging.emitMessagesWhenDisabled = true;
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            /*
                emitMessagesWhenDisabled only opts the Unity enable/disable lifecycle out of
                touching the handler. An EXPLICIT ToggleMessageHandler(false) call is a direct
                user decision and must always win, flag or no flag.
            */
            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "An explicit ToggleMessageHandler(false) must suspend delivery even while "
                    + "emitMessagesWhenDisabled is true."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(2, count, "ToggleMessageHandler(true) should resume delivery.");
        }

        [Test]
        public void EnableCycleDoesNotOverrideExplicitToggleWhileEmitMessagesWhenDisabledIsTrue()
        {
            GameObject host = new(
                nameof(EnableCycleDoesNotOverrideExplicitToggleWhileEmitMessagesWhenDisabledIsTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            messaging.emitMessagesWhenDisabled = true;
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Explicit deactivation must suspend delivery.");

            /*
                While emitMessagesWhenDisabled is true the Unity lifecycle must leave the handler
                alone in BOTH directions: OnDisable must not deactivate it, and OnEnable must not
                reactivate it behind the user's back. The explicit choice above survives a full
                enabled=false/true cycle.
            */
            messaging.enabled = false;
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Disabling the MessagingComponent must not disturb the explicitly suspended handler."
            );

            messaging.enabled = true;
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Re-enabling the MessagingComponent must not silently reactivate a handler the "
                    + "user explicitly toggled off while emitMessagesWhenDisabled is true."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "An explicit ToggleMessageHandler(true) remains the way to resume delivery."
            );
        }

        [Test]
        public void ToggleMessageHandlerTrueReactivatesEvenWhenEmitMessagesWhenDisabledIsTrue()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerTrueReactivatesEvenWhenEmitMessagesWhenDisabledIsTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            /*
                Suspend with the flag clear, then set the flag while suspended. Explicit
                toggle calls are never gated by emitMessagesWhenDisabled in either
                direction, so reactivation works with the flag set.
            */
            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Handler should be suspended while the flag is false.");

            messaging.emitMessagesWhenDisabled = true;
            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "ToggleMessageHandler(true) must reactivate regardless of emitMessagesWhenDisabled."
            );
        }

        [Test]
        public void FlagEnabledWhileLifecycleSuspendedRequiresExplicitReactivation()
        {
            GameObject host = new(
                nameof(FlagEnabledWhileLifecycleSuspendedRequiresExplicitReactivation),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();
            listener.enabled = false; // isolate the handler gate from the listener token

            int count = 0;
            listener.untargetedHandler = () => ++count;

            /*
                Pins the documented edge of the lifecycle-skip model: with the flag
                clear, disabling deactivates the handler via the lifecycle. Setting
                emitMessagesWhenDisabled WHILE suspended then re-enabling does NOT
                reactivate (the lifecycle no longer touches the handler once the flag
                is set); an explicit ToggleMessageHandler(true) is the way to resume.
            */
            messaging.enabled = false;
            messaging.emitMessagesWhenDisabled = true;
            messaging.enabled = true;

            listener.enabled = true;
            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(
                0,
                count,
                "Enabling with the flag newly set must not silently reactivate a handler "
                    + "the lifecycle previously deactivated."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "An explicit ToggleMessageHandler(true) resumes delivery.");
        }

        [Test]
        public void ToggleMessageHandlerBeforeAwakeIsSafeNoOp()
        {
            GameObject host = new(nameof(ToggleMessageHandlerBeforeAwakeIsSafeNoOp));
            _spawned.Add(host);
            host.SetActive(false);
            MessagingComponent messaging = host.AddComponent<MessagingComponent>();
            ManualListenerComponent listener = host.AddComponent<ManualListenerComponent>();

            /*
                Awake has not run on the inactive host, so no MessageHandler exists yet. Both
                toggle directions must be safe no-ops on the public API.
            */
            Assert.DoesNotThrow(() => messaging.ToggleMessageHandler(false));
            Assert.DoesNotThrow(() => messaging.ToggleMessageHandler(true));

            host.SetActive(true);

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "PreAwakeToggle"))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                int count = 0;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);
                token.Enable();

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    count,
                    "Handler created on activation should deliver normally after pre-Awake toggles."
                );

                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing the registered listener should succeed."
                );
            }
        }

        [Test]
        public void DisablingOneListenerOnSharedHostLeavesSiblingActive()
        {
            GameObject host = new(
                nameof(DisablingOneListenerOnSharedHostLeavesSiblingActive),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent first = host.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent second = host.AddComponent<SimpleMessageAwareComponent>();

            int firstCount = 0;
            int secondCount = 0;
            first.untargetedHandler = () => ++firstCount;
            second.untargetedHandler = () => ++secondCount;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                firstCount,
                "Positive control: first listener multiplexed over the shared handler should receive."
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Positive control: second listener multiplexed over the shared handler should receive."
            );

            first.enabled = false;
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Disabled listener must stop receiving.");
            Assert.AreEqual(
                2,
                secondCount,
                "Sibling on the same host must keep receiving while the other listener is disabled."
            );

            first.enabled = true;
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                firstCount,
                "Re-enabled listener must resume receiving exactly once per emit."
            );
            Assert.AreEqual(3, secondCount, "Sibling must be unaffected by the re-enable.");
        }

        [Test]
        public void ReleasingOneListenerOnSharedHostLeavesSiblingActive()
        {
            GameObject host = new(
                nameof(ReleasingOneListenerOnSharedHostLeavesSiblingActive),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent first = host.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent second = host.AddComponent<SimpleMessageAwareComponent>();

            int firstCount = 0;
            int secondCount = 0;
            first.untargetedHandler = () => ++firstCount;
            second.untargetedHandler = () => ++secondCount;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Positive control: first listener should receive.");
            Assert.AreEqual(1, secondCount, "Positive control: second listener should receive.");

            Assert.IsTrue(
                messaging.Release(first),
                "Releasing a registered listener should report success."
            );
            Assert.IsFalse(first.Token.Enabled, "Released listener's token must be disabled.");

            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Released listener must stop receiving.");
            Assert.AreEqual(
                2,
                secondCount,
                "Sibling on the same host must keep receiving after the other listener is released."
            );

            Assert.IsFalse(
                messaging.Release(first),
                "Releasing the same listener twice should report failure on the second call."
            );
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Double release must not resurrect the listener.");
            Assert.AreEqual(3, secondCount, "Sibling must be unaffected by the double release.");
        }

        [Test]
        public void ReleaseReturnsFalseForNeverRegisteredListener()
        {
            GameObject host = new(
                nameof(ReleaseReturnsFalseForNeverRegisteredListener),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent registered =
                host.GetComponent<SimpleMessageAwareComponent>();
            ManualListenerComponent neverRegistered = host.GetComponent<ManualListenerComponent>();

            int count = 0;
            registered.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: registered listener should receive.");

            Assert.IsFalse(
                messaging.Release(neverRegistered),
                "Releasing a listener that never requested a token should report failure."
            );
            Assert.IsFalse(
                messaging.Release(null),
                "Releasing a null listener should report failure."
            );
            UnityEngine.Object.DestroyImmediate(neverRegistered);
            Assert.That(
                neverRegistered == null,
                Is.True,
                "Test setup must retain a Unity fake-null listener reference."
            );
            Assert.IsFalse(
                messaging.Release(neverRegistered),
                "Releasing a destroyed listener that never requested a token should report failure."
            );

            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "Failed release calls must have no side effects on registered listeners."
            );
        }

        [Test]
        public void ReleaseDisposesRegisteredDestroyedListener()
        {
            GameObject host = new(
                nameof(ReleaseDisposesRegisteredDestroyedListener),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus bus = new();
            messaging.Configure(bus, MessageBusRebindMode.RebindActive);

            using (
                LeakWatcher watcher = new(
                    bus,
                    label: nameof(ReleaseDisposesRegisteredDestroyedListener)
                )
            )
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => { });
                token.Enable();
                Assert.That(
                    bus.RegisteredUntargeted,
                    Is.EqualTo(1),
                    "Positive control: the manual listener must own one live registration."
                );

                UnityEngine.Object.DestroyImmediate(listener);
                Assert.That(
                    listener == null,
                    Is.True,
                    "Test setup must produce a Unity fake-null listener."
                );
                Assert.That(
                    ReferenceEquals(listener, null),
                    Is.False,
                    "Test setup must retain the destroyed managed listener reference."
                );

                try
                {
                    Assert.That(
                        messaging.Release(listener),
                        Is.True,
                        "Release must find and dispose the token retained under a destroyed listener key."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.Zero,
                        "Successful release must remove the destroyed listener key."
                    );
                    Assert.That(
                        token.Enabled,
                        Is.False,
                        "Successful release must disable the token."
                    );
                    Assert.That(
                        bus.RegisteredUntargeted,
                        Is.Zero,
                        "Successful release must deregister the destroyed listener's handler."
                    );
                }
                finally
                {
                    token.Dispose();
                }
            }
        }

        [Test]
        public void DoubleReleaseReturnsFalseAndLeavesMessagingUsable()
        {
            GameObject host = new(
                nameof(DoubleReleaseReturnsFalseAndLeavesMessagingUsable),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();

            int originalCount = 0;
            SimpleUntargetedMessage message = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "DoubleRelease"))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                token.DiagnosticMode = true;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++originalCount);
                token.Enable();

                message.EmitUntargeted();
                Assert.AreEqual(1, originalCount, "Positive control: listener should receive.");
                Assert.AreEqual(1, token._metadata.Count, "Control failed: metadata must exist.");
                Assert.AreEqual(
                    1,
                    token._callCounts.Count,
                    "Control failed: call count must exist."
                );
                Assert.AreEqual(
                    1,
                    token._emissionBuffer.Count,
                    "Control failed: emission history must exist."
                );

                Assert.IsTrue(messaging.Release(listener), "First release should succeed.");
                Assert.IsFalse(token.Enabled, "First release must disable the token.");
                Assert.AreEqual(0, token._metadata.Count, "Release must clear token metadata.");
                Assert.AreEqual(
                    0,
                    token._callCounts.Count,
                    "Release must clear token call counts."
                );
                Assert.AreEqual(
                    0,
                    token._emissionBuffer.Count,
                    "Release must clear token emission history."
                );

                message.EmitUntargeted();
                Assert.AreEqual(1, originalCount, "Released listener must stop receiving.");

                token.Enable();
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    originalCount,
                    "A released token reference must not resurrect old registrations."
                );

                Assert.IsFalse(
                    messaging.Release(listener),
                    "Second release of the same listener should report failure."
                );
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    originalCount,
                    "Second release must not corrupt or resurrect the registration."
                );
            }

            /*
                No corruption: the same listener can request a fresh, fully functional token
                after the double release, and the old token stays dead.
            */
            MessageRegistrationToken recreated = messaging.Create(listener);
            Assert.IsNotNull(recreated, "Create after release should produce a token.");

            int recreatedCount = 0;
            _ = recreated.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++recreatedCount);
            recreated.Enable();

            message.EmitUntargeted();
            Assert.AreEqual(1, recreatedCount, "Recreated token must deliver messages.");
            Assert.AreEqual(1, originalCount, "Old released token must remain dead.");

            Assert.IsTrue(
                messaging.Release(listener),
                "Release should succeed again after re-creating the token."
            );
        }

        [TestCase(false, TestName = "ReleaseFailureKeepsLiveListenerRegisteredForRetry")]
        [TestCase(true, TestName = "ReleaseFailureKeepsDestroyedListenerRegisteredForRetry")]
        public void ReleaseFailureKeepsListenerRegisteredForRetry(bool destroyBeforeRelease)
        {
            GameObject host = new(
                nameof(ReleaseFailureKeepsListenerRegisteredForRetry),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus innerBus = new();
            FailingDeregistrationBus failingBus = new(innerBus);
            messaging.Configure(failingBus, MessageBusRebindMode.RebindActive);

            int count = 0;
            string caseContext = $"[destroyBeforeRelease={destroyBeforeRelease}]";
            SimpleUntargetedMessage message = new();

            using (
                LeakWatcher watcher = new(
                    bus: failingBus,
                    throwOnLeak: true,
                    label: nameof(ReleaseFailureKeepsListenerRegisteredForRetry) + caseContext
                )
            )
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                try
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);
                    token.Enable();

                    message.EmitUntargeted(failingBus);
                    Assert.AreEqual(
                        1,
                        count,
                        $"{caseContext} Control failed: listener should receive."
                    );

                    if (destroyBeforeRelease)
                    {
                        UnityEngine.Object.DestroyImmediate(listener);
                        Assert.That(
                            listener == null,
                            Is.True,
                            $"{caseContext} Test setup must retain a Unity fake-null listener reference."
                        );
                    }

                    Assert.Throws<InvalidOperationException>(
                        () => messaging.Release(listener),
                        $"{caseContext} The failing bus must surface the token disposal failure."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        $"{caseContext} Failed release must leave the token active for cleanup retry."
                    );
                    Assert.AreEqual(
                        1,
                        failingBus.RegisteredUntargeted,
                        $"{caseContext} Failed release must not forget the live registration."
                    );
                    Assert.That(
                        messaging._registeredListeners.Count,
                        Is.EqualTo(1),
                        $"{caseContext} Failed release must retain the listener key for retry."
                    );

                    failingBus.AllowDeregistrations();
                    Assert.IsTrue(
                        messaging.Release(listener),
                        $"{caseContext} Release retry must succeed."
                    );
                    Assert.IsFalse(
                        token.Enabled,
                        $"{caseContext} Release retry must disable the token."
                    );
                    Assert.AreEqual(
                        0,
                        failingBus.RegisteredUntargeted,
                        $"{caseContext} Retry must deregister."
                    );
                    Assert.IsFalse(
                        messaging.Release(listener),
                        $"{caseContext} A second release after successful retry must report failure."
                    );
                }
                finally
                {
                    failingBus.AllowDeregistrations();
                    token.Dispose();
                }
            }
        }

        private sealed class ThrowOnceDeregistrationBus : DelegatingMessageBus
        {
            private bool _throwOnDeregistration = true;

            internal ThrowOnceDeregistrationBus(IMessageBus inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (_throwOnDeregistration)
                {
                    _throwOnDeregistration = false;
                    throw new InvalidOperationException("Deregistration failure.");
                }

                base.Deregister<T>(in registration);
            }
        }

        private sealed class FailingDeregistrationBus : DelegatingMessageBus
        {
            private bool _throwOnDeregistration = true;

            internal FailingDeregistrationBus(IMessageBus inner)
                : base(inner) { }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    throw new InvalidOperationException("Deregistration failure.");
                }

                base.Deregister<T>(in registration);
            }
        }
    }
}

#endif
