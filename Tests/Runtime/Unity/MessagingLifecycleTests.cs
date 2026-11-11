#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using Bus = DxMessaging.Core.MessageBus.MessageBus;
    using Object = UnityEngine.Object;

    [Category("UnityRuntime")]
    public sealed class MessagingLifecycleTests : MessagingTestBase
    {
        /// <summary>Checks reversible activation and final cleanup through the lifecycle owner.</summary>
        [Test]
        public void EnableDisableAndDisposeOwnExactlyOneToken(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            MessageRegistrationToken token = lifecycle.Token;
            int calls = 0;
            Register(scenario, token, () => ++calls);
            Assert.That(
                token.Enabled,
                Is.False,
                "Creation must leave registration staging disabled."
            );
            lifecycle.Enable();
            Emit(scenario, bus);
            lifecycle.Disable();
            Emit(scenario, bus);
            lifecycle.Enable();
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(2),
                $"[{scenario.Kind}] Disable must be reversible without duplicate callbacks."
            );
            lifecycle.Dispose();
            lifecycle.Dispose();
            lifecycle.Disable();
            Assert.That(
                lifecycle.IsDisposed,
                Is.True,
                "Successful cleanup must mark the owner disposed."
            );
            Assert.That(
                token._metadata.Count,
                Is.Zero,
                "Final cleanup must release staged metadata."
            );
            Assert.That(
                component._registeredListeners.Count,
                Is.Zero,
                "The component must release its cached owner."
            );
            Assert.Throws<ObjectDisposedException>(
                () => lifecycle.Enable(),
                "A disposed lifecycle must reject reactivation."
            );
            Assert.Throws<ObjectDisposedException>(
                () => _ = lifecycle.Token,
                "A disposed lifecycle must release access to its token."
            );
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(2),
                $"[{scenario.Kind}] Final disposal must stop delivery."
            );
        }

        /// <summary>Prevents stale owners from releasing a replacement token for the same listener.</summary>
        [Test]
        public void StaleLifecycleCannotEnableOrDisposeItsReplacement(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            MessagingLifecycle stale = component.CreateLifecycleBound(listener);
            MessageRegistrationToken old = stale.Token;
            Assert.That(
                component.Release(listener),
                Is.True,
                "The original component token must release."
            );
            using MessagingLifecycle current = component.CreateLifecycleBound(listener);
            int calls = 0;
            Register(scenario, current.Token, () => ++calls);
            current.Enable();
            Assert.Throws<InvalidOperationException>(
                () => stale.Enable(),
                "Stale bindings must not reactivate a detached token."
            );
            stale.Dispose();
            Assert.That(
                stale.IsDisposed,
                Is.True,
                "Detached token cleanup must complete independently."
            );
            Assert.That(current.Token, Is.Not.SameAs(old), "A replacement must own a new token.");
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] Stale disposal must preserve the replacement."
            );
        }

        /// <summary>Keeps other listeners on the same GameObject active when one owner ends.</summary>
        [Test]
        public void SharedGameObjectRetainsTheOtherListener(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent first);
            ManualListenerComponent second =
                component.gameObject.AddComponent<ManualListenerComponent>();
            using MessagingLifecycle left = component.CreateLifecycleBound(first);
            using MessagingLifecycle right = component.CreateLifecycleBound(second);
            int leftCalls = 0;
            int rightCalls = 0;
            Register(scenario, left.Token, () => ++leftCalls);
            Register(scenario, right.Token, () => ++rightCalls);
            left.Enable();
            right.Enable();
            left.Dispose();
            Emit(scenario, bus);
            Assert.That(leftCalls, Is.Zero, $"[{scenario.Kind}] The disposed listener must stop.");
            Assert.That(
                rightCalls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] The other listener must survive."
            );
        }

        /// <summary>Preserves cleanup ownership when a custom bus fails or reenters disposal.</summary>
        [Test]
        public void CleanupFailureCanBeRetriedThroughTheLifecycle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool reentrant
        )
        {
            Bus inner = new();
            CallbackBus bus = new(inner);
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            MessageRegistrationToken token = lifecycle.Token;
            Register(scenario, token, () => { });
            lifecycle.Enable();
            if (reentrant)
            {
                bus.OnCleanup = lifecycle.Dispose;
            }
            bus.FailCleanup = true;
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => lifecycle.Dispose(),
                    "A failing bus must report cleanup failure."
                );
                Assert.That(
                    lifecycle.IsDisposed,
                    Is.False,
                    "Failure must preserve the owner for retry."
                );
                Assert.That(
                    lifecycle.Token,
                    Is.SameAs(token),
                    "Retry must use the exact captured token."
                );
                Assert.That(
                    component._registeredListeners[listener],
                    Is.SameAs(token),
                    "The component must retain retry ownership."
                );
                bus.FailCleanup = false;
                lifecycle.Dispose();
                Assert.That(
                    lifecycle.IsDisposed,
                    Is.True,
                    "Retry through the lifecycle must finish cleanup."
                );
                Assert.That(
                    token._metadata.Count,
                    Is.Zero,
                    "Retry must remove all accepted metadata."
                );
            }
            finally
            {
                bus.FailCleanup = false;
                lifecycle.Dispose();
            }
        }

        /// <summary>Retains an owner when a cleanup callback stages fresh accepted work.</summary>
        [Test]
        public void AcceptedWorkDuringCleanupKeepsTheLifecycleRetryable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus inner = new();
            CallbackBus bus = new(inner);
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            MessageRegistrationToken token = lifecycle.Token;
            int calls = 0;
            Register(scenario, token, () => { });
            lifecycle.Enable();
            bus.OnCleanup = () =>
            {
                Register(scenario, token, () => ++calls);
                lifecycle.Enable();
                lifecycle.Dispose();
            };
            lifecycle.Dispose();
            Assert.That(
                lifecycle.IsDisposed,
                Is.False,
                "Accepted callback work must keep the owner retryable."
            );
            Assert.That(
                lifecycle.Token,
                Is.SameAs(token),
                "Reentrancy must not clear the outer disposal owner."
            );
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] Accepted work must remain reachable."
            );
            lifecycle.Dispose();
            Assert.That(lifecycle.IsDisposed, Is.True, "A later call must drain accepted work.");
        }

        /// <summary>Validates listener identity before creating any retained lifecycle state.</summary>
        [Test]
        public void InvalidListenersAreRejected()
        {
            MessagingComponent component = Create(new Bus(), out ManualListenerComponent listener);
            Assert.Throws<ArgumentNullException>(
                () => component.CreateLifecycleBound(null),
                "Null owners must be rejected."
            );
            MessagingComponent other = Create(new Bus(), out ManualListenerComponent otherListener);
            Assert.Throws<ArgumentException>(
                () => component.CreateLifecycleBound(otherListener),
                "A different GameObject cannot own this token."
            );
            Object.DestroyImmediate(otherListener);
            Assert.Throws<ArgumentNullException>(
                () => other.CreateLifecycleBound(otherListener),
                "Destroyed Unity owners must be rejected."
            );
            Assert.That(
                component._registeredListeners.Count,
                Is.Zero,
                "Invalid requests must not create cached tokens."
            );
            Assert.That(
                listener != null,
                Is.True,
                "Validation must not destroy the valid listener."
            );
        }

        /// <summary>Allows explicit cleanup through retained references after Unity destroys a listener.</summary>
        [Test]
        public void DestroyedListenerStillAllowsFinalCleanup()
        {
            MessagingComponent component = Create(new Bus(), out ManualListenerComponent listener);
            MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            Object.DestroyImmediate(listener);
            Assert.Throws<ObjectDisposedException>(
                () => lifecycle.Enable(),
                "A destroyed owner must reject activation."
            );
            lifecycle.Dispose();
            Assert.That(
                lifecycle.IsDisposed,
                Is.True,
                "Retained managed references must still allow final disposal."
            );
            Assert.That(
                component._registeredListeners.Count,
                Is.Zero,
                "Destroyed listeners must not leave cached tokens."
            );
        }

        /// <summary>Uses the existing component bus configuration for active token migration.</summary>
        [Test]
        public void ActiveBusRebindKeepsTheSameLifecycleToken(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus first = new();
            Bus second = new();
            using LeakWatcher firstLeaks = new(bus: first, label: "first");
            using LeakWatcher secondLeaks = new(bus: second, label: "second");
            MessagingComponent component = Create(first, out ManualListenerComponent listener);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            MessageRegistrationToken token = lifecycle.Token;
            int calls = 0;
            Register(scenario, token, () => ++calls);
            lifecycle.Enable();
            component.Configure(second, MessageBusRebindMode.RebindActive);
            Emit(scenario, first);
            Emit(scenario, second);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] Only the replacement bus must deliver."
            );
            Assert.That(
                lifecycle.Token,
                Is.SameAs(token),
                "Component migration must preserve lifecycle identity."
            );
        }

        /// <summary>Keeps inactive and pooled listeners aligned with the shared handler state.</summary>
        [Test]
        public void InitiallyInactiveAndPooledHostRespectsActivation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool emitWhenDisabled
        )
        {
            Bus bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            GameObject host = new("InactiveLifecycle");
            _spawned.Add(host);
            host.SetActive(false);
            MessagingComponent component = host.AddComponent<MessagingComponent>();
            component.emitMessagesWhenDisabled = emitWhenDisabled;
            component.Configure(bus, MessageBusRebindMode.RebindActive);
            ManualListenerComponent listener = host.AddComponent<ManualListenerComponent>();
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            int calls = 0;
            Register(scenario, lifecycle.Token, () => ++calls);
            lifecycle.Enable();
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(emitWhenDisabled ? 1 : 0),
                $"[{scenario.Kind}] Initial handler activity must honor the component option."
            );
            lifecycle.Disable();
            calls = 0;
            for (int cycle = 0; cycle < 10; ++cycle)
            {
                host.SetActive(true);
                lifecycle.Enable();
                Emit(scenario, bus);
                lifecycle.Disable();
                host.SetActive(false);
                Emit(scenario, bus);
            }
            Assert.That(
                calls,
                Is.EqualTo(10),
                $"[{scenario.Kind}] Pool churn must deliver exactly once per active cycle."
            );
        }

        /// <summary>Preserves staged handlers when a custom bus rejects initial activation.</summary>
        [Test]
        public void ActivationFailureRemainsRetryable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus inner = new();
            CallbackBus bus = new(inner) { FailRegistration = true };
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            int calls = 0;
            Register(scenario, lifecycle.Token, () => ++calls);
            Assert.Throws<InvalidOperationException>(
                () => lifecycle.Enable(),
                "Registration failure must reach the caller."
            );
            Assert.That(
                lifecycle.IsDisposed,
                Is.False,
                "Activation failure must preserve the lifecycle owner."
            );
            Assert.That(
                lifecycle.Token._metadata.Count,
                Is.EqualTo(1),
                "The failed staged registration must remain retryable."
            );
            bus.FailRegistration = false;
            lifecycle.Enable();
            Emit(scenario, bus);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] Retried activation must deliver exactly once."
            );
        }

        /// <summary>Uses a provider configured before lifecycle construction.</summary>
        [Test]
        public void ConfiguredProviderSelectsTheLifecycleBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus selected = new();
            Bus original = new();
            using LeakWatcher selectedLeaks = new(bus: selected, label: "provider");
            using LeakWatcher originalLeaks = new(bus: original, label: "original");
            MessagingComponent component = Create(original, out ManualListenerComponent listener);
            component.Configure(new Provider(selected), MessageBusRebindMode.RebindActive);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            int calls = 0;
            Register(scenario, lifecycle.Token, () => ++calls);
            lifecycle.Enable();
            Emit(scenario, original);
            Emit(scenario, selected);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario.Kind}] The configured provider must select exactly one bus."
            );
        }

        /// <summary>Releases references even when Unity already destroyed the component host.</summary>
        [Test]
        public void DestroyedMessagingComponentLeavesFinalDisposalSafe()
        {
            MessagingComponent component = Create(new Bus(), out ManualListenerComponent listener);
            MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            Object.DestroyImmediate(component);
            Assert.Throws<ObjectDisposedException>(
                () => lifecycle.Enable(),
                "A destroyed messaging host must reject activation."
            );
            lifecycle.Dispose();
            Assert.That(
                lifecycle.IsDisposed,
                Is.True,
                "Already completed component cleanup must permit final lifecycle disposal."
            );
        }

        /// <summary>Retains failed deactivation cleanup for retry through the same lifecycle owner.</summary>
        [Test]
        public void FailedDisableRecoversThroughTheOwningLifecycle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool disposeOnRetry
        )
        {
            Bus inner = new();
            CallbackBus bus = new(inner);
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            MessageRegistrationToken token = lifecycle.Token;
            int calls = 0;
            Register(scenario, token, () => ++calls);
            lifecycle.Enable();
            bus.FailCleanup = true;
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => lifecycle.Disable(),
                    "Failed deactivation must report the bus cleanup exception."
                );
                Assert.That(
                    lifecycle.IsDisposed,
                    Is.False,
                    "Disable failure must preserve the owning lifecycle."
                );
                Assert.That(
                    lifecycle.Token,
                    Is.SameAs(token),
                    "Retry must preserve the captured token."
                );
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(1),
                    "Failed deactivation must retain staged handlers for retry."
                );
                bus.FailCleanup = false;
                if (disposeOnRetry)
                {
                    lifecycle.Dispose();
                    Assert.That(
                        lifecycle.IsDisposed,
                        Is.True,
                        "Final disposal must recover from a failed disable."
                    );
                    Assert.That(
                        token._metadata.Count,
                        Is.Zero,
                        "Final retry must release all metadata."
                    );
                }
                else
                {
                    lifecycle.Disable();
                    lifecycle.Enable();
                    Emit(scenario, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"[{scenario.Kind}] Retried disable and enable must deliver exactly once."
                    );
                }
            }
            finally
            {
                bus.FailCleanup = false;
                lifecycle.Dispose();
            }
        }

        /// <summary>Rejects activation after managed owner destruction starts but before Unity deletes its native object.</summary>
        /// <remarks>2026-11-11: Unity's native object stays alive during OnDestroy. Repeat consecutive owners to check cleanup state reuse.</remarks>
        [Test]
        [Repeat(10)]
        public void DestructionRejectsReentrantLifecycleActivation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Bus inner = new();
            CallbackBus bus = new(inner);
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessagingComponent component = Create(bus, out ManualListenerComponent listener);
            using MessagingLifecycle lifecycle = component.CreateLifecycleBound(listener);
            Register(scenario, lifecycle.Token, () => { });
            lifecycle.Enable();
            Exception observed = null;
            bus.OnCleanup = () =>
            {
                try
                {
                    lifecycle.Enable();
                }
                catch (Exception error)
                {
                    observed = error;
                }
            };
            Object.DestroyImmediate(component);
            Assert.That(
                observed,
                Is.TypeOf<ObjectDisposedException>(),
                $"[{scenario.Kind}] Destruction must reject callback reactivation before native deletion completes."
            );
            lifecycle.Dispose();
            Assert.That(
                lifecycle.IsDisposed,
                Is.True,
                "Destroyed managed owners must still allow final cleanup."
            );
        }

        /// <summary>Builds an explicitly configured test host and tracks its destruction.</summary>
        private MessagingComponent Create(IMessageBus bus, out ManualListenerComponent listener)
        {
            GameObject host = new(
                "MessagingLifecycle",
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent component = host.GetComponent<MessagingComponent>();
            listener = host.GetComponent<ManualListenerComponent>();
            component.Configure(bus, MessageBusRebindMode.RebindActive);
            return component;
        }

        /// <summary>Registers the requested route through the shared scenario harness.</summary>
        private static void Register(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action callback
        )
        {
            void Receive(in Payload message)
            {
                callback();
            }
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.RegisterUntargeted<Payload>(scenario, token, Receive);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.RegisterTargeted<Payload>(
                        scenario,
                        token,
                        new InstanceId(123),
                        Receive
                    );
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.RegisterBroadcast<Payload>(
                        scenario,
                        token,
                        new InstanceId(123),
                        Receive
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        /// <summary>Emits through the matching route without a global bus dependency.</summary>
        private static void Emit(MessageScenario scenario, IMessageBus bus)
        {
            Payload message = default;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.EmitTargeted(scenario, ref message, new InstanceId(123), bus);
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.EmitBroadcast(scenario, ref message, new InstanceId(123), bus);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class CallbackBus : DelegatingMessageBus
        {
            internal bool FailCleanup;
            internal bool FailRegistration;

            /// <summary>Applies the controlled activation failure to untargeted registrations.</summary>
            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterUntargeted<T>(handler, priority);
            }

            /// <summary>Applies the controlled activation failure to targeted registrations.</summary>
            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterTargeted<T>(target, handler, priority);
            }

            /// <summary>Applies the controlled activation failure to sourced registrations.</summary>
            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterSourcedBroadcast<T>(source, handler, priority);
            }

            /// <summary>Raises the declared activation failure before the inner bus accepts a handle.</summary>
            private void BeforeRegistration()
            {
                if (FailRegistration)
                {
                    throw new InvalidOperationException(
                        "Intentional lifecycle activation failure."
                    );
                }
            }

            internal Action OnCleanup;

            internal CallbackBus(IMessageBus inner)
                : base(inner) { }

            /// <summary>Invokes the declared cleanup callback and failure before forwarding deregistration.</summary>
            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnCleanup;
                OnCleanup = null;
                callback?.Invoke();
                if (FailCleanup)
                {
                    throw new InvalidOperationException("Intentional lifecycle cleanup failure.");
                }
                base.Deregister<T>(in registration);
            }
        }

        private sealed class Provider : IMessageBusProvider
        {
            private readonly IMessageBus _bus;

            internal Provider(IMessageBus bus)
            {
                _bus = bus;
            }

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }

        private readonly struct Payload
            : IUntargetedMessage<Payload>,
                ITargetedMessage<Payload>,
                IBroadcastMessage<Payload>
        {
            public Type MessageType => typeof(Payload);
        }
    }
}
#endif
