#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>
    /// Pins the post-<see cref="MessageRegistrationToken.Dispose"/> contract of
    /// <see cref="MessageRegistrationToken"/>. Dispose delegates to
    /// <see cref="MessageRegistrationToken.UnregisterAll"/>: every live handler is
    /// deregistered, all staged registrations are cleared, and the token is left
    /// disabled. The token carries no disposed flag, so it remains technically
    /// reusable: new registrations after Dispose are accepted without throwing and
    /// a subsequent <see cref="MessageRegistrationToken.Enable"/> activates them.
    /// These tests codify that reusable-after-Dispose behavior; if a hard "throw
    /// after Dispose" contract is ever introduced deliberately, update these pins
    /// alongside the source change.
    /// </summary>
    [TestFixture]
    public sealed class TokenDisposeReuseTests
    {
        private const int OwnerInstanceId = 11;
        private const int ContextInstanceId = 17;

        [SetUp]
        public void ResetBeforeTest()
        {
            DxMessagingStaticState.Reset();
        }

        [TearDown]
        public void ResetAfterTest()
        {
            DxMessagingStaticState.Reset();
        }

        [Test]
        public void ReplayRemovalAndSlotReuseRetainsOrphanTeardownUntilDisable()
        {
            BusType innerBus = new();
            ReentrantReplayRemovalBus bus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int oldCalls = 0;
            int replacementCalls = 0;
            MessageRegistrationHandle oldHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => ++oldCalls
            );
            bus.OnRegistration = () =>
            {
                bus.OnRegistration = null;
                token.RemoveRegistration(oldHandle);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++replacementCalls
                );
            };

            token.Enable();
            SimpleUntargetedMessage message = new();
            innerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, oldCalls, "The snapshotted registration completed on the bus.");
            Assert.AreEqual(0, replacementCalls, "The replacement was not in the replay snapshot.");

            token.Disable();
            innerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, oldCalls, "Disable must consume the retained orphan teardown.");

            token.Enable();
            innerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, replacementCalls, "The reused slot must retain its replacement.");
            token.Dispose();
        }

        [Test]
        public void ThrowingOrphanTeardownRemainsRetryableAfterReplaySlotReuse()
        {
            BusType innerBus = new();
            ReentrantReplayRemovalBus bus = new(innerBus) { ThrowOnDeregistration = true };
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            MessageRegistrationHandle oldHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => { }
            );
            bus.OnRegistration = () =>
            {
                bus.OnRegistration = null;
                token.RemoveRegistration(oldHandle);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );
            };

            token.Enable();
            Assert.Throws<InvalidOperationException>(token.Disable);
            Assert.IsTrue(token.Enabled, "A failed orphan teardown must keep the token retryable.");
            Assert.AreEqual(1, bus.DeregistrationAttempts);

            bus.ThrowOnDeregistration = false;
            Assert.DoesNotThrow(token.Disable);
            Assert.AreEqual(2, bus.DeregistrationAttempts);
            Assert.IsFalse(token.Enabled);
            token.Dispose();
        }

        [Test]
        public void RetargetReplaySlotReuseKeepsNewBusOrphanTeardownScopedToOriginalHandle()
        {
            BusType oldBus = new();
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int oldCalls = 0;
            int replacementCalls = 0;
            MessageRegistrationHandle oldHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => ++oldCalls
            );
            token.Enable();

            BusType newInnerBus = new();
            ReentrantReplayRemovalBus newBus = new(newInnerBus);
            newBus.OnRegistration = () =>
            {
                newBus.OnRegistration = null;
                token.RemoveRegistration(oldHandle);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++replacementCalls
                );
            };

            token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);
            SimpleUntargetedMessage message = new();
            oldBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(0, oldCalls, "Retarget must drain the old bus.");
            newInnerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, oldCalls, "The retarget snapshot completed on the new bus.");
            Assert.AreEqual(0, replacementCalls);

            token.Disable();
            newInnerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, oldCalls, "Disable must drain the retarget orphan.");
            token.Enable();
            newInnerBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, replacementCalls, "The replacement must replay on the new bus.");
            token.Dispose();
        }

        [TestCase(0)]
        [TestCase(1)]
        public void UnifiedTeardownSnapshotPreservesOrderWithFirstOrMiddleOrphan(int orphanIndex)
        {
            BusType innerBus = new();
            ReentrantReplayRemovalBus bus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int[] priorities = { 10, 20, 30 };
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[priorities.Length];
            for (int i = 0; i < priorities.Length; ++i)
            {
                handles[i] = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { },
                    priorities[i]
                );
            }

            bus.OnRegistrationWithPriority = priority =>
            {
                if (priority != priorities[orphanIndex])
                {
                    return;
                }

                bus.OnRegistrationWithPriority = null;
                token.RemoveRegistration(handles[orphanIndex]);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { },
                    priority: 40
                );
            };

            token.Enable();
            bus.DeregistrationOrder.Clear();
            token.Disable();
            CollectionAssert.AreEqual(
                priorities,
                bus.DeregistrationOrder,
                "Live slots and orphan teardowns must share original registration order."
            );
            token.Dispose();
        }

        [TestCase(0)]
        [TestCase(1)]
        public void MultipleTeardownFailuresReportFirstRegistrationOrderExceptionWithOrphan(
            int orphanIndex
        )
        {
            BusType innerBus = new();
            ReentrantReplayRemovalBus bus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int[] priorities = { 10, 20, 30 };
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[priorities.Length];
            for (int i = 0; i < priorities.Length; ++i)
            {
                handles[i] = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { },
                    priorities[i]
                );
            }

            bus.OnRegistrationWithPriority = priority =>
            {
                if (priority != priorities[orphanIndex])
                {
                    return;
                }

                bus.OnRegistrationWithPriority = null;
                token.RemoveRegistration(handles[orphanIndex]);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { },
                    priority: 40
                );
            };
            token.Enable();
            foreach (int priority in priorities)
            {
                bus.ThrowPriorities.Add(priority);
            }

            bus.DeregistrationOrder.Clear();
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                token.Disable
            );
            Assert.That(exception.Message, Does.Contain("10"));
            CollectionAssert.AreEqual(priorities, bus.DeregistrationOrder);

            bus.ThrowPriorities.Clear();
            bus.DeregistrationOrder.Clear();
            Assert.DoesNotThrow(token.Disable);
            CollectionAssert.AreEqual(
                priorities,
                bus.DeregistrationOrder,
                "Every failed teardown must remain retryable in original order."
            );
            token.Dispose();
        }

        [Test]
        public void OrphanIdentityAddedDuringTeardownIsDeferredToNextPass()
        {
            BusType innerBus = new();
            ReentrantReplayRemovalBus bus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => { },
                priority: 10
            );
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => { },
                priority: 20
            );
            token.Enable();

            bus.OnDeregistration = priority =>
            {
                if (priority != 10)
                {
                    return;
                }

                bus.OnDeregistration = null;
                bus.OnRegistrationWithPriority = addedPriority =>
                {
                    if (addedPriority == 40)
                    {
                        bus.OnRegistrationWithPriority = null;
                        MessageRegistrationHandle addedHandle = token._metadata.Last().Key;
                        token.RemoveRegistration(addedHandle);
                    }
                };
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { },
                    priority: 40
                );
            };

            bus.DeregistrationOrder.Clear();
            token.Disable();
            CollectionAssert.AreEqual(new[] { 10, 20 }, bus.DeregistrationOrder);
            Assert.IsTrue(token.Enabled, "The deferred orphan keeps a retryable teardown pending.");

            bus.DeregistrationOrder.Clear();
            token.Disable();
            CollectionAssert.AreEqual(new[] { 40 }, bus.DeregistrationOrder);
            Assert.IsFalse(token.Enabled);
            token.Dispose();
        }

        [Test]
        public void DisposeUnregistersAllHandlersAndDisablesToken(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DisposeUnregistersAllHandlersAndDisablesToken)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                _ = RegisterHandler(scenario, scope.Token, context, () => ++handled);
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Control failed: handler must fire before Dispose for scenario {0}.",
                    scenario
                );

                scope.Token.Dispose();
                Assert.IsFalse(
                    scope.Token.Enabled,
                    "Dispose must leave the token disabled (UnregisterAll semantics)."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Handlers must not fire after Dispose for scenario {0}.",
                    scenario
                );
            }
        }

        [Test]
        public void UnregisterAllClearsDiagnosticState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            using TokenScope scope = TokenScope.Create();
            scope.Token.DiagnosticMode = true;
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(UnregisterAllClearsDiagnosticState) + "_" + scenario.DisplayName
                )
            )
            {
                MessageRegistrationHandle handle = RegisterHandler(
                    scenario,
                    scope.Token,
                    context,
                    () => ++handled
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Control failed: handler must fire before UnregisterAll for scenario {0}.",
                    scenario
                );
                AssertTokenDiagnosticsPopulated(scope.Token, handle, scenario.DisplayName);

                scope.Token.UnregisterAll();

                Assert.IsFalse(scope.Token.Enabled, "UnregisterAll must disable the token.");
                AssertTokenDiagnosticsEmpty(
                    scope.Token,
                    nameof(UnregisterAllClearsDiagnosticState),
                    scenario.DisplayName
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Handlers must not fire after UnregisterAll for scenario {0}.",
                    scenario
                );
            }
        }

        [Test]
        public void DisposeClearsDiagnosticsBeforeReuse(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            using TokenScope scope = TokenScope.Create();
            scope.Token.DiagnosticMode = true;
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DisposeClearsDiagnosticsBeforeReuse) + "_" + scenario.DisplayName
                )
            )
            {
                MessageRegistrationHandle firstHandle = RegisterHandler(
                    scenario,
                    scope.Token,
                    context,
                    () => ++handled
                );

                Emit(scenario, context, scope.Bus);
                AssertTokenDiagnosticsPopulated(scope.Token, firstHandle, scenario.DisplayName);

                scope.Token.Dispose();
                AssertTokenDiagnosticsEmpty(
                    scope.Token,
                    nameof(DisposeClearsDiagnosticsBeforeReuse),
                    scenario.DisplayName
                );

                MessageRegistrationHandle secondHandle = RegisterHandler(
                    scenario,
                    scope.Token,
                    context,
                    () => ++handled
                );
                Assert.AreEqual(
                    1,
                    scope.Token._metadata.Count,
                    "A reused token must contain only the newly staged registration."
                );
                Assert.IsTrue(
                    scope.Token._metadata.ContainsKey(secondHandle),
                    "A reused token must not keep metadata for the disposed registration."
                );
                Assert.AreEqual(
                    0,
                    scope.Token._callCounts.Count,
                    "A newly staged registration must not inherit old call counts."
                );
                Assert.AreEqual(
                    0,
                    scope.Token._emissionBuffer.Count,
                    "A reused token must not expose old emission history before it is emitted."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "A reused token registration must stay inactive until Enable for scenario {0}.",
                    scenario
                );

                scope.Token.Enable();
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    2,
                    handled,
                    "Enable must activate the reused token registration for scenario {0}.",
                    scenario
                );
                AssertTokenDiagnosticsPopulated(scope.Token, secondHandle, scenario.DisplayName);

                scope.Token.Dispose();
                AssertTokenDiagnosticsEmpty(
                    scope.Token,
                    nameof(DisposeClearsDiagnosticsBeforeReuse),
                    scenario.DisplayName
                );
            }
        }

        [Test]
        public void RemoveFinalRegistrationClearsDiagnosticState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            using TokenScope scope = TokenScope.Create();
            scope.Token.DiagnosticMode = true;
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(RemoveFinalRegistrationClearsDiagnosticState)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                MessageRegistrationHandle handle = RegisterHandler(
                    scenario,
                    scope.Token,
                    context,
                    () => ++handled
                );

                Emit(scenario, context, scope.Bus);
                AssertTokenDiagnosticsPopulated(scope.Token, handle, scenario.DisplayName);

                scope.Token.RemoveRegistration(handle);
                AssertTokenDiagnosticsEmpty(
                    scope.Token,
                    nameof(RemoveFinalRegistrationClearsDiagnosticState),
                    scenario.DisplayName
                );
            }
        }

        [Test]
        public void RemoveRegistrationLeavesFailedDeregistrationRetryable()
        {
            BusType innerBus = new();
            FailingDeregistrationBus throwingBus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), throwingBus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, throwingBus);
            token.DiagnosticMode = true;
            token.Enable();
            int handled = 0;

            try
            {
                using (
                    LeakWatcher watcher = new(
                        bus: throwingBus,
                        throwOnLeak: true,
                        label: nameof(RemoveRegistrationLeavesFailedDeregistrationRetryable)
                    )
                )
                {
                    MessageRegistrationHandle handle =
                        token.RegisterUntargeted<SimpleUntargetedMessage>(
                            (ref SimpleUntargetedMessage _) => ++handled
                        );
                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(1, handled, "Control failed: handler must fire.");
                    AssertTokenDiagnosticsPopulated(
                        token,
                        handle,
                        MessageKind.Untargeted.ToString()
                    );

                    Assert.Throws<InvalidOperationException>(
                        () => token.RemoveRegistration(handle),
                        "The throwing bus must surface the deregistration failure."
                    );
                    Assert.AreEqual(
                        1,
                        throwingBus.RegisteredUntargeted,
                        "A deregistration that fails before cleanup must leave the handler live."
                    );
                    Assert.IsTrue(
                        token._metadata.ContainsKey(handle),
                        "The failed removal must keep token metadata retryable."
                    );

                    throwingBus.AllowDeregistrations();
                    token.RemoveRegistration(handle);
                    Assert.AreEqual(0, throwingBus.RegisteredUntargeted, "Retry must deregister.");
                    AssertTokenDiagnosticsEmpty(
                        token,
                        nameof(RemoveRegistrationLeavesFailedDeregistrationRetryable),
                        MessageKind.Untargeted.ToString()
                    );

                    token.Enable();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        1,
                        handled,
                        "The removed staged registration must not replay after a throwing removal."
                    );
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void DisableThenDisposeDoesNotOverDeregister(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            using MessagingDebugCapture debug = new();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DisableThenDisposeDoesNotOverDeregister)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                _ = RegisterHandler(scenario, scope.Token, context, () => ++handled);
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(1, handled, "Control failed: handler must fire.");

                scope.Token.Disable();
                scope.Token.Dispose();
                debug.AssertNoOverDeregistration();
            }
        }

        [Test]
        public void DisableThenRemoveRegistrationDoesNotOverDeregister(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            using MessagingDebugCapture debug = new();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DisableThenRemoveRegistrationDoesNotOverDeregister)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                MessageRegistrationHandle handle = RegisterHandler(
                    scenario,
                    scope.Token,
                    context,
                    () => ++handled
                );
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(1, handled, "Control failed: handler must fire.");

                scope.Token.Disable();
                scope.Token.RemoveRegistration(handle);
                debug.AssertNoOverDeregistration();

                scope.Token.Enable();
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "RemoveRegistration after Disable must remove the staged handler."
                );
            }
        }

        [Test]
        public void DisableThenDisposeDoesNotOverDeregisterInterceptorOrGlobalAcceptAll()
        {
            using TokenScope scope = TokenScope.Create();
            using MessagingDebugCapture debug = new();

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(
                        DisableThenDisposeDoesNotOverDeregisterInterceptorOrGlobalAcceptAll
                    )
                )
            )
            {
                _ = scope.Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => true
                );
                _ = scope.Token.RegisterGlobalAcceptAll(
                    (ref IUntargetedMessage _) => { },
                    (ref InstanceId _, ref ITargetedMessage __) => { },
                    (ref InstanceId _, ref IBroadcastMessage __) => { }
                );

                scope.Token.Disable();
                scope.Token.Dispose();
                debug.AssertNoOverDeregistration();
            }
        }

        [Test]
        public void GlobalAcceptAllDuplicateRefcountSurvivesRemoveDisableAndReenable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            using LeakWatcher watcher = new(
                scope.Bus,
                label: nameof(GlobalAcceptAllDuplicateRefcountSurvivesRemoveDisableAndReenable)
                    + "_"
                    + scenario.DisplayName
            );
            int calls = 0;
            MessageHandler.FastHandler<IUntargetedMessage> untargeted = (
                ref IUntargetedMessage _
            ) => ++calls;
            MessageHandler.FastHandlerWithContext<ITargetedMessage> targeted = (
                ref InstanceId _,
                ref ITargetedMessage __
            ) => ++calls;
            MessageHandler.FastHandlerWithContext<IBroadcastMessage> broadcast = (
                ref InstanceId _,
                ref IBroadcastMessage __
            ) => ++calls;

            MessageRegistrationHandle first = scope.Token.RegisterGlobalAcceptAll(
                untargeted,
                targeted,
                broadcast
            );
            MessageRegistrationHandle second = scope.Token.RegisterGlobalAcceptAll(
                untargeted,
                targeted,
                broadcast
            );

            InstanceId context = new(ContextInstanceId);
            Emit(scenario, context, scope.Bus);
            Assert.AreEqual(1, calls);

            scope.Token.RemoveRegistration(first);
            Emit(scenario, context, scope.Bus);
            Assert.AreEqual(2, calls);

            scope.Token.Disable();
            Emit(scenario, context, scope.Bus);
            Assert.AreEqual(2, calls);

            scope.Token.Enable();
            Emit(scenario, context, scope.Bus);
            Assert.AreEqual(3, calls);

            scope.Token.RemoveRegistration(second);
            Emit(scenario, context, scope.Bus);
            Assert.AreEqual(3, calls);
        }

        [Test]
        public void GlobalAcceptAllFailedBusTeardownRemainsRetryable()
        {
            BusType innerBus = new();
            FailingGlobalDeregistrationBus bus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);

            try
            {
                using LeakWatcher watcher = new(
                    bus,
                    label: nameof(GlobalAcceptAllFailedBusTeardownRemainsRetryable)
                );
                _ = token.RegisterGlobalAcceptAll(
                    (ref IUntargetedMessage _) => { },
                    (ref InstanceId _, ref ITargetedMessage __) => { },
                    (ref InstanceId _, ref IBroadcastMessage __) => { }
                );
                token.Enable();

                Assert.Throws<InvalidOperationException>(token.Disable);
                Assert.IsTrue(token.Enabled, "A failed global teardown must remain retryable.");
                Assert.AreEqual(1, innerBus.RegisteredGlobalAcceptAll);

                bus.AllowDeregistrations();
                Assert.DoesNotThrow(token.Disable);
                Assert.IsFalse(token.Enabled);
                Assert.AreEqual(0, innerBus.RegisteredGlobalAcceptAll);

                token.Enable();
                Assert.AreEqual(1, innerBus.RegisteredGlobalAcceptAll);
                token.Disable();
                Assert.AreEqual(0, innerBus.RegisteredGlobalAcceptAll);
                Assert.AreEqual(
                    3,
                    handler.ResetEmptyTypedSlotsForSweep(bus),
                    "The successful retry and final disable must drain all three embedded "
                        + "global typed-slot teardown states, not only the bus registration."
                );
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void EnableFailureRollsBackPartialRegistrations()
        {
            BusType innerBus = new();
            ThrowingUntargetedRegistrationBus throwingBus = new(
                innerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), throwingBus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, throwingBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher watcher = new(
                        bus: throwingBus,
                        throwOnLeak: true,
                        label: nameof(EnableFailureRollsBackPartialRegistrations)
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );

                    Assert.Throws<InvalidOperationException>(
                        token.Enable,
                        "The throwing bus must fail during registration replay."
                    );
                    Assert.IsFalse(token.Enabled, "A failed Enable must leave the token disabled.");
                    Assert.AreEqual(
                        0,
                        throwingBus.RegisteredUntargeted,
                        "A failed Enable must roll back registrations replayed before the throw."
                    );

                    throwingBus.AllowRegistrations();
                    token.Enable();
                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        2,
                        handled,
                        "Retrying Enable after rollback must register each staged handler once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RetargetFailureRollsBackPartialRegistrations()
        {
            BusType oldBus = new();
            BusType newInnerBus = new();
            ThrowingUntargetedRegistrationBus throwingNewBus = new(
                newInnerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher oldWatcher = new(
                        bus: oldBus,
                        throwOnLeak: true,
                        label: nameof(RetargetFailureRollsBackPartialRegistrations) + "_Old"
                    )
                )
                using (
                    LeakWatcher newWatcher = new(
                        bus: throwingNewBus,
                        throwOnLeak: true,
                        label: nameof(RetargetFailureRollsBackPartialRegistrations) + "_New"
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                    token.Enable();
                    Assert.AreEqual(2, oldBus.RegisteredUntargeted, "Control failed.");

                    Assert.Throws<InvalidOperationException>(
                        () =>
                            token.RetargetMessageBus(
                                throwingNewBus,
                                MessageBusRebindMode.RebindActive
                            ),
                        "The throwing bus must fail during active retarget replay."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "A failed active retarget must restore the token to its previous enabled state."
                    );
                    Assert.AreEqual(
                        2,
                        oldBus.RegisteredUntargeted,
                        "A failed active retarget must restore old-bus registrations."
                    );
                    Assert.AreEqual(
                        0,
                        throwingNewBus.RegisteredUntargeted,
                        "A failed retarget must roll back new-bus registrations replayed before the throw."
                    );

                    throwingNewBus.AllowRegistrations();
                    token.RetargetMessageBus(throwingNewBus, MessageBusRebindMode.RebindActive);
                    Assert.IsTrue(token.Enabled, "Retrying retarget must leave the token enabled.");
                    Assert.AreEqual(
                        0,
                        oldBus.RegisteredUntargeted,
                        "Retrying retarget must remove restored old-bus registrations."
                    );
                    Assert.AreEqual(
                        2,
                        throwingNewBus.RegisteredUntargeted,
                        "Retrying retarget must register each staged handler on the new bus."
                    );
                    SimpleUntargetedMessage message = new();
                    throwingNewBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        2,
                        handled,
                        "Retrying Enable after retarget rollback must register each staged handler once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RetargetOldDeregistrationFailureRestoresSuccessfulOldDeregistrations()
        {
            BusType oldInnerBus = new();
            PartiallyFailingDeregistrationBus oldBus = new(
                oldInnerBus,
                successfulDeregistrationsBeforeThrow: 1
            );
            BusType newBus = new();
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher oldWatcher = new(
                        bus: oldBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetOldDeregistrationFailureRestoresSuccessfulOldDeregistrations
                        ) + "_Old"
                    )
                )
                using (
                    LeakWatcher newWatcher = new(
                        bus: newBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetOldDeregistrationFailureRestoresSuccessfulOldDeregistrations
                        ) + "_New"
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                    token.Enable();
                    int oldRegistrationCount = oldBus.RegisteredUntargeted;
                    Assert.Greater(oldRegistrationCount, 0, "Control failed.");

                    Assert.Throws<InvalidOperationException>(
                        () => token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive),
                        "The old bus must fail during active retarget teardown."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "A failed old-bus teardown must leave the token retryable and active."
                    );
                    Assert.AreEqual(
                        oldRegistrationCount,
                        oldBus.RegisteredUntargeted,
                        "A failed old-bus teardown must restore registrations already removed."
                    );
                    Assert.AreEqual(
                        0,
                        newBus.RegisteredUntargeted,
                        "Retarget must not register on the new bus when old-bus teardown failed."
                    );

                    oldBus.AllowDeregistrations();
                    token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);
                    Assert.AreEqual(0, oldBus.RegisteredUntargeted, "Retry must clear old bus.");
                    Assert.AreEqual(
                        oldRegistrationCount,
                        newBus.RegisteredUntargeted,
                        "Retry must move the active registrations to the new bus."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RetargetOldDeregistrationFailureWithRestoreFailureKeepsFailedHandleRetryable()
        {
            BusType oldInnerBus = new();
            PartiallyFailingDeregistrationWithRestoreFailureBus oldBus = new(
                oldInnerBus,
                successfulDeregistrationsBeforeThrow: 1
            );
            BusType newBus = new();
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher oldWatcher = new(
                        bus: oldBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetOldDeregistrationFailureWithRestoreFailureKeepsFailedHandleRetryable
                        ) + "_Old"
                    )
                )
                using (
                    LeakWatcher newWatcher = new(
                        bus: newBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetOldDeregistrationFailureWithRestoreFailureKeepsFailedHandleRetryable
                        ) + "_New"
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                    token.Enable();
                    Assert.AreEqual(2, oldBus.RegisteredUntargeted, "Control failed.");

                    Assert.Throws<InvalidOperationException>(
                        () => token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive),
                        "The old bus must fail during teardown and then fail to restore the removed handler."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "The failed old-bus deregistration must keep the token active for cleanup retry."
                    );
                    Assert.AreEqual(
                        1,
                        oldBus.RegisteredUntargeted,
                        "The handler whose deregistration failed must remain live."
                    );
                    Assert.AreEqual(
                        0,
                        newBus.RegisteredUntargeted,
                        "Retarget must not register on the new bus when old-bus teardown failed."
                    );
                    Assert.AreEqual(
                        2,
                        oldBus.DeregistrationAttempts,
                        "Restore rollback must not invoke the unrelated failed old-bus cleanup again."
                    );

                    SimpleUntargetedMessage message = new();
                    oldBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        1,
                        handled,
                        "The originally failed old-bus registration must still dispatch."
                    );

                    oldBus.AllowDeregistrations();
                    oldBus.AllowRegistrations();
                    token.Disable();
                    Assert.IsFalse(token.Enabled, "Retrying cleanup must disable the token.");
                    Assert.AreEqual(0, oldBus.RegisteredUntargeted, "Retry must clear old bus.");

                    token.Enable();
                    oldBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        3,
                        handled,
                        "Re-enabling after cleanup retry must restore both staged handlers once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                oldBus.AllowDeregistrations();
                oldBus.AllowRegistrations();
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RetargetReplayFailureWithRollbackFailureKeepsCleanupRetryable()
        {
            BusType oldBus = new();
            BusType newInnerBus = new();
            ThrowingRegistrationWithFailingRollbackBus throwingNewBus = new(
                newInnerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher oldWatcher = new(
                        bus: oldBus,
                        throwOnLeak: true,
                        label: nameof(RetargetReplayFailureWithRollbackFailureKeepsCleanupRetryable)
                            + "_Old"
                    )
                )
                using (
                    LeakWatcher newWatcher = new(
                        bus: throwingNewBus,
                        throwOnLeak: true,
                        label: nameof(RetargetReplayFailureWithRollbackFailureKeepsCleanupRetryable)
                            + "_New"
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                    token.Enable();

                    Assert.Throws<InvalidOperationException>(
                        () =>
                            token.RetargetMessageBus(
                                throwingNewBus,
                                MessageBusRebindMode.RebindActive
                            ),
                        "The throwing bus must fail during active retarget replay."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "A rollback cleanup failure leaves live registrations and must stay retryable."
                    );
                    Assert.AreEqual(
                        1,
                        oldBus.RegisteredUntargeted,
                        "The old bus must restore only handlers not still live on the failed new bus."
                    );
                    Assert.AreEqual(
                        1,
                        throwingNewBus.RegisteredUntargeted,
                        "The failed rollback registration must remain live and retryable on the new bus."
                    );

                    throwingNewBus.AllowDeregistrations();
                    token.Disable();
                    Assert.IsFalse(token.Enabled, "Retrying cleanup must disable the token.");
                    Assert.AreEqual(0, oldBus.RegisteredUntargeted, "Retry must clear old bus.");
                    Assert.AreEqual(
                        0,
                        throwingNewBus.RegisteredUntargeted,
                        "Retry must clear new bus."
                    );

                    throwingNewBus.AllowRegistrations();
                    token.RetargetMessageBus(
                        throwingNewBus,
                        MessageBusRebindMode.PreserveRegistrations
                    );
                    token.Enable();
                    SimpleUntargetedMessage message = new();
                    throwingNewBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        2,
                        handled,
                        "Retrying Enable must register both handlers once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RetargetReplayFailureWithRestoreFailureDoesNotConsumeNewBusRetryableCleanup()
        {
            BusType oldInnerBus = new();
            ThrowingRestoreRegistrationBus oldBus = new(oldInnerBus);
            BusType newInnerBus = new();
            ThrowingRegistrationWithSingleFailingRollbackBus throwingNewBus = new(
                newInnerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), oldBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher oldWatcher = new(
                        bus: oldBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetReplayFailureWithRestoreFailureDoesNotConsumeNewBusRetryableCleanup
                        ) + "_Old"
                    )
                )
                using (
                    LeakWatcher newWatcher = new(
                        bus: throwingNewBus,
                        throwOnLeak: true,
                        label: nameof(
                            RetargetReplayFailureWithRestoreFailureDoesNotConsumeNewBusRetryableCleanup
                        ) + "_New"
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                    token.Enable();
                    Assert.AreEqual(2, oldBus.RegisteredUntargeted, "Control failed.");

                    Assert.Throws<InvalidOperationException>(
                        () =>
                            token.RetargetMessageBus(
                                throwingNewBus,
                                MessageBusRebindMode.RebindActive
                            ),
                        "The new bus must fail during replay and the old bus must fail during restore."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "The failed new-bus cleanup must keep the token active for retry."
                    );
                    Assert.AreEqual(
                        0,
                        oldBus.RegisteredUntargeted,
                        "The old-bus restore throws before re-registering handlers."
                    );
                    Assert.AreEqual(
                        1,
                        throwingNewBus.RegisteredUntargeted,
                        "The failed rollback registration must remain live on the new bus."
                    );
                    Assert.AreEqual(
                        1,
                        throwingNewBus.DeregistrationAttempts,
                        "Old-bus restore rollback must not consume new-bus retryable cleanup."
                    );
                    Assert.AreEqual(
                        1,
                        oldBus.RegistrationFailures,
                        "Control failed: old-bus restore registration must throw once."
                    );

                    throwingNewBus.AllowDeregistrations();
                    token.Disable();
                    Assert.IsFalse(token.Enabled, "Retrying cleanup must disable the token.");
                    Assert.AreEqual(
                        0,
                        oldBus.RegisteredUntargeted,
                        "Retry must leave old bus clear."
                    );
                    Assert.AreEqual(
                        0,
                        throwingNewBus.RegisteredUntargeted,
                        "Retry must clear new bus."
                    );
                    Assert.AreEqual(
                        2,
                        throwingNewBus.DeregistrationAttempts,
                        "Disable retry must be the second new-bus cleanup attempt."
                    );

                    oldBus.AllowRegistrations();
                    token.Enable();
                    SimpleUntargetedMessage message = new();
                    oldBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        2,
                        handled,
                        "Re-enabling after cleanup retry must restore both staged handlers once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                oldBus.AllowRegistrations();
                throwingNewBus.AllowDeregistrations();
                throwingNewBus.AllowRegistrations();
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void EnableFailureWithRollbackFailureKeepsPartialRegistrationRetryable()
        {
            BusType innerBus = new();
            ThrowingRegistrationWithFailingRollbackBus throwingBus = new(
                innerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), throwingBus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, throwingBus);
            int handled = 0;

            try
            {
                using (
                    LeakWatcher watcher = new(
                        bus: throwingBus,
                        throwOnLeak: true,
                        label: nameof(
                            EnableFailureWithRollbackFailureKeepsPartialRegistrationRetryable
                        )
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );

                    Assert.Throws<InvalidOperationException>(
                        token.Enable,
                        "The throwing bus must fail during registration replay."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "A rollback failure leaves a live partial registration and must stay retryable."
                    );
                    Assert.AreEqual(
                        1,
                        throwingBus.RegisteredUntargeted,
                        "Rollback failure must not forget the live partial registration."
                    );

                    throwingBus.AllowDeregistrations();
                    token.Disable();
                    Assert.IsFalse(token.Enabled, "Retrying cleanup must disable the token.");
                    Assert.AreEqual(0, throwingBus.RegisteredUntargeted, "Retry must deregister.");

                    throwingBus.AllowRegistrations();
                    token.Enable();
                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        2,
                        handled,
                        "Retrying Enable must register both handlers once."
                    );

                    token.Dispose();
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        [Test]
        public void RegistrationAfterDisposeIsAcceptedAndEnableActivatesIt(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using TokenScope scope = TokenScope.Create();
            InstanceId context = new(ContextInstanceId);
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(RegistrationAfterDisposeIsAcceptedAndEnableActivatesIt)
                        + "_"
                        + scenario.DisplayName
                )
            )
            {
                scope.Token.Dispose();
                Assert.IsFalse(scope.Token.Enabled, "Dispose must disable the token.");

                // Pinned: the token has no disposed flag, so registration after
                // Dispose is accepted (staged, not active) rather than throwing.
                Assert.DoesNotThrow(
                    () => _ = RegisterHandler(scenario, scope.Token, context, () => ++handled),
                    "Pinned behavior: registering on a disposed token must not throw "
                        + "(the token is reusable)."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    0,
                    handled,
                    "A registration staged after Dispose must stay inactive until Enable "
                        + "for scenario {0}.",
                    scenario
                );

                scope.Token.Enable();
                Assert.IsTrue(
                    scope.Token.Enabled,
                    "Enable after Dispose must re-enable the token."
                );

                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Enable after Dispose must activate registrations staged post-Dispose "
                        + "for scenario {0}.",
                    scenario
                );

                scope.Token.Dispose();
                Emit(scenario, context, scope.Bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "The second Dispose must deregister the post-Dispose registration "
                        + "for scenario {0}.",
                    scenario
                );
            }
        }

        [Test]
        public void DoubleDisposeIsHarmless()
        {
            using TokenScope scope = TokenScope.Create();
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(DoubleDisposeIsHarmless)
                )
            )
            {
                _ = scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++handled
                );
                SimpleUntargetedMessage message = new();
                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: handler must fire before Dispose.");

                scope.Token.Dispose();
                Assert.DoesNotThrow(
                    scope.Token.Dispose,
                    "Disposing an already-disposed token must be a harmless no-op."
                );
                Assert.IsFalse(
                    scope.Token.Enabled,
                    "Token must stay disabled after double Dispose."
                );

                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "No handler may fire after double Dispose.");
            }
        }

        [Test]
        public void EnableAfterDisposeWithoutNewRegistrationsRestoresNothing()
        {
            using TokenScope scope = TokenScope.Create();
            int handled = 0;

            using (
                LeakWatcher watcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: nameof(EnableAfterDisposeWithoutNewRegistrationsRestoresNothing)
                )
            )
            {
                _ = scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++handled
                );
                SimpleUntargetedMessage message = new();
                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: handler must fire before Dispose.");

                scope.Token.Dispose();
                scope.Token.Enable();
                Assert.IsTrue(
                    scope.Token.Enabled,
                    "Enable after Dispose must report the token as enabled."
                );

                scope.Bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(
                    1,
                    handled,
                    "Dispose clears staged registrations; a bare Enable must not resurrect "
                        + "the pre-Dispose handler."
                );
            }
        }

        private static MessageRegistrationHandle RegisterHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked
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
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked()
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

        [Test]
        public void DisableFailureLeavesDeregistrationRetryable()
        {
            BusType innerBus = new();
            FailingDeregistrationBus throwingBus = new(innerBus);
            MessageHandler handler = new(new InstanceId(OwnerInstanceId), throwingBus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, throwingBus);
            token.Enable();
            int handled = 0;

            try
            {
                using (
                    LeakWatcher watcher = new(
                        bus: throwingBus,
                        throwOnLeak: true,
                        label: nameof(DisableFailureLeavesDeregistrationRetryable)
                    )
                )
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(1, handled, "Control failed: handler must fire.");

                    Assert.Throws<InvalidOperationException>(
                        token.Disable,
                        "The throwing bus must surface the deregistration failure."
                    );
                    Assert.IsTrue(
                        token.Enabled,
                        "A failed Disable must leave the token in a retryable active state."
                    );
                    Assert.AreEqual(
                        1,
                        throwingBus.RegisteredUntargeted,
                        "A failed Disable must not forget a live registration."
                    );

                    throwingBus.AllowDeregistrations();
                    token.Disable();
                    Assert.IsFalse(token.Enabled, "Retrying Disable must disable the token.");
                    Assert.AreEqual(0, throwingBus.RegisteredUntargeted, "Retry must deregister.");
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
            }
        }

        private static void Emit(MessageScenario scenario, InstanceId context, BusType bus)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context, bus);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context, bus);
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

        private static void AssertTokenDiagnosticsPopulated(
            MessageRegistrationToken token,
            MessageRegistrationHandle handle,
            string scenarioName
        )
        {
            Assert.AreEqual(
                1,
                token._metadata.Count,
                "Metadata control failed for scenario {0}.",
                scenarioName
            );
            Assert.IsTrue(
                token._metadata.ContainsKey(handle),
                "Metadata must include the registered handle for scenario {0}.",
                scenarioName
            );
            Assert.IsTrue(
                token._callCounts.TryGetValue(handle, out int recordedCount),
                "Call counts must include the registered handle for scenario {0}.",
                scenarioName
            );
            Assert.AreEqual(
                1,
                recordedCount,
                "Call count control failed for scenario {0}.",
                scenarioName
            );
            Assert.AreEqual(
                1,
                token._emissionBuffer.Count,
                "Emission history control failed for scenario {0}.",
                scenarioName
            );
        }

        private static void AssertTokenDiagnosticsEmpty(
            MessageRegistrationToken token,
            string operationName,
            string scenarioName
        )
        {
            Assert.AreEqual(
                0,
                token._metadata.Count,
                "{0} must clear token metadata for scenario {1}.",
                operationName,
                scenarioName
            );
            Assert.AreEqual(
                0,
                token._callCounts.Count,
                "{0} must clear token call counts for scenario {1}.",
                operationName,
                scenarioName
            );
            Assert.AreEqual(
                0,
                token._emissionBuffer.Count,
                "{0} must clear token emission history for scenario {1}.",
                operationName,
                scenarioName
            );
        }

        private sealed class MessagingDebugCapture : IDisposable
        {
            private readonly bool _previousEnabled;
            private readonly Action<LogLevel, string> _previousLogFunction;
            private readonly List<string> _logs = new();
            private bool _disposed;

            internal MessagingDebugCapture()
            {
                _previousEnabled = MessagingDebug.enabled;
                _previousLogFunction = MessagingDebug.LogFunction;
                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (level, message) => _logs.Add(level + ":" + message);
            }

            internal void AssertNoOverDeregistration()
            {
                Assert.IsFalse(
                    _logs.Exists(log => log.Contains("over-deregistration")),
                    "Expected no over-deregistration logs. Got: " + string.Join(" | ", _logs)
                );
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                MessagingDebug.enabled = _previousEnabled;
                MessagingDebug.LogFunction = _previousLogFunction;
            }
        }

        private sealed class ReentrantReplayRemovalBus : DelegatingMessageBus
        {
            private readonly Dictionary<MessageBusRegistration, int> _priorities = new();

            internal Action OnRegistration { get; set; }

            internal Action<int> OnRegistrationWithPriority { get; set; }

            internal Action<int> OnDeregistration { get; set; }

            internal bool ThrowOnDeregistration { get; set; }

            internal int DeregistrationAttempts { get; private set; }

            internal List<int> DeregistrationOrder { get; } = new();

            internal HashSet<int> ThrowPriorities { get; } = new();

            internal ReentrantReplayRemovalBus(IMessageBus inner)
                : base(inner) { }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                MessageBusRegistration registration = base.RegisterUntargeted<T>(
                    messageHandler,
                    priority
                );
                if (typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    _priorities[registration] = priority;
                    OnRegistration?.Invoke();
                    OnRegistrationWithPriority?.Invoke(priority);
                }

                return registration;
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    ++DeregistrationAttempts;
                    int priority = _priorities[registration];
                    DeregistrationOrder.Add(priority);
                    OnDeregistration?.Invoke(priority);
                    if (ThrowOnDeregistration)
                    {
                        throw new InvalidOperationException("Orphan deregistration failure.");
                    }

                    if (ThrowPriorities.Contains(priority))
                    {
                        throw new InvalidOperationException(
                            $"Deregistration failure at priority {priority}."
                        );
                    }
                }

                base.Deregister<T>(in registration);
                _priorities.Remove(registration);
            }
        }

        private sealed class FailingGlobalDeregistrationBus : DelegatingMessageBus
        {
            private bool _throwOnDeregistration = true;

            internal FailingGlobalDeregistrationBus(IMessageBus inner)
                : base(inner) { }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (_throwOnDeregistration && typeof(T) == typeof(IMessage))
                {
                    throw new InvalidOperationException("Global deregistration failure.");
                }

                base.Deregister<T>(in registration);
            }
        }

        private sealed class ThrowingUntargetedRegistrationBus : DelegatingMessageBus
        {
            private readonly int _successfulRegistrationsBeforeThrow;
            private int _registrationAttempts;
            private bool _throwOnRegistration = true;

            internal ThrowingUntargetedRegistrationBus(
                IMessageBus inner,
                int successfulRegistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulRegistrationsBeforeThrow = successfulRegistrationsBeforeThrow;
            }

            internal void AllowRegistrations()
            {
                _throwOnRegistration = false;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRegistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_registrationAttempts == _successfulRegistrationsBeforeThrow)
                    {
                        throw new InvalidOperationException("Registration replay failure.");
                    }

                    ++_registrationAttempts;
                }

                return base.RegisterUntargeted<T>(messageHandler, priority);
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

        private sealed class PartiallyFailingDeregistrationBus : DelegatingMessageBus
        {
            private int _successfulDeregistrationsBeforeThrow;
            private bool _throwOnDeregistration = true;

            internal PartiallyFailingDeregistrationBus(
                IMessageBus inner,
                int successfulDeregistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulDeregistrationsBeforeThrow = successfulDeregistrationsBeforeThrow;
            }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_successfulDeregistrationsBeforeThrow == 0)
                    {
                        throw new InvalidOperationException("Deregistration failure.");
                    }

                    --_successfulDeregistrationsBeforeThrow;
                }

                base.Deregister<T>(in registration);
            }
        }

        private sealed class PartiallyFailingDeregistrationWithRestoreFailureBus
            : DelegatingMessageBus
        {
            private int _successfulDeregistrationsBeforeThrow;
            private bool _throwOnDeregistration = true;
            private bool _throwOnRestoreRegistration;

            internal PartiallyFailingDeregistrationWithRestoreFailureBus(
                IMessageBus inner,
                int successfulDeregistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulDeregistrationsBeforeThrow = successfulDeregistrationsBeforeThrow;
            }

            internal int DeregistrationAttempts { get; private set; }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            internal void AllowRegistrations()
            {
                _throwOnRestoreRegistration = false;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRestoreRegistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    throw new InvalidOperationException("Restore registration failure.");
                }

                return base.RegisterUntargeted<T>(messageHandler, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    ++DeregistrationAttempts;
                }

                if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_successfulDeregistrationsBeforeThrow == 0)
                    {
                        _throwOnRestoreRegistration = true;
                        throw new InvalidOperationException("Deregistration failure.");
                    }

                    --_successfulDeregistrationsBeforeThrow;
                }

                base.Deregister<T>(in registration);
            }
        }

        private sealed class ThrowingRestoreRegistrationBus : DelegatingMessageBus
        {
            private bool _throwOnRegistrationAfterDrain;

            internal ThrowingRestoreRegistrationBus(IMessageBus inner)
                : base(inner) { }

            internal int RegistrationFailures { get; private set; }

            internal void AllowRegistrations()
            {
                _throwOnRegistrationAfterDrain = false;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRegistrationAfterDrain && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    ++RegistrationFailures;
                    throw new InvalidOperationException("Restore registration failure.");
                }

                return base.RegisterUntargeted<T>(messageHandler, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                base.Deregister<T>(in registration);
                if (typeof(T) == typeof(SimpleUntargetedMessage) && RegisteredUntargeted == 0)
                {
                    _throwOnRegistrationAfterDrain = true;
                }
            }
        }

        private sealed class ThrowingRegistrationWithFailingRollbackBus : DelegatingMessageBus
        {
            private readonly int _successfulRegistrationsBeforeThrow;
            private int _registrationAttempts;
            private bool _throwOnRegistration = true;
            private bool _throwOnDeregistration = true;

            internal ThrowingRegistrationWithFailingRollbackBus(
                IMessageBus inner,
                int successfulRegistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulRegistrationsBeforeThrow = successfulRegistrationsBeforeThrow;
            }

            internal void AllowRegistrations()
            {
                _throwOnRegistration = false;
            }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRegistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_registrationAttempts == _successfulRegistrationsBeforeThrow)
                    {
                        throw new InvalidOperationException("Registration replay failure.");
                    }

                    ++_registrationAttempts;
                }

                return base.RegisterUntargeted<T>(messageHandler, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    throw new InvalidOperationException("Rollback deregistration failure.");
                }

                base.Deregister<T>(in registration);
            }
        }

        private sealed class ThrowingRegistrationWithSingleFailingRollbackBus : DelegatingMessageBus
        {
            private readonly int _successfulRegistrationsBeforeThrow;
            private int _deregistrationFailuresRemaining = 1;
            private int _registrationAttempts;
            private bool _throwOnRegistration = true;

            internal ThrowingRegistrationWithSingleFailingRollbackBus(
                IMessageBus inner,
                int successfulRegistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulRegistrationsBeforeThrow = successfulRegistrationsBeforeThrow;
            }

            internal int DeregistrationAttempts { get; private set; }

            internal void AllowRegistrations()
            {
                _throwOnRegistration = false;
            }

            internal void AllowDeregistrations()
            {
                _deregistrationFailuresRemaining = 0;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRegistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_registrationAttempts == _successfulRegistrationsBeforeThrow)
                    {
                        throw new InvalidOperationException("Registration replay failure.");
                    }

                    ++_registrationAttempts;
                }

                return base.RegisterUntargeted<T>(messageHandler, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    ++DeregistrationAttempts;
                    if (_deregistrationFailuresRemaining > 0)
                    {
                        --_deregistrationFailuresRemaining;
                        throw new InvalidOperationException("Rollback deregistration failure.");
                    }
                }

                base.Deregister<T>(in registration);
            }
        }

        /// <summary>
        /// Pairs a fresh isolated <see cref="BusType"/>, an active
        /// <see cref="MessageHandler"/>, and an enabled
        /// <see cref="MessageRegistrationToken"/> so each test starts from the same
        /// clean state without touching the global bus.
        /// </summary>
        private sealed class TokenScope : IDisposable
        {
            private bool _disposed;

            internal BusType Bus { get; }

            internal MessageHandler Handler { get; }

            internal MessageRegistrationToken Token { get; }

            private TokenScope(BusType bus, MessageHandler handler, MessageRegistrationToken token)
            {
                Bus = bus;
                Handler = handler;
                Token = token;
            }

            internal static TokenScope Create()
            {
                BusType bus = new();
                MessageHandler handler = new(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();
                return new TokenScope(bus, handler, token);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Token.Dispose();
                Handler.active = false;
            }
        }
    }
}
#endif
