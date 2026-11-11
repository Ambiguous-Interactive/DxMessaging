namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Checks stable source-bound handles, immutable delivery origins, and recoverable moves.</summary>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class RetargetSourceSpecificTests
    {
        private static readonly InstanceId OldSource = new InstanceId(972);
        private static readonly InstanceId NewSource = new InstanceId(973);

        [Test]
        public void MovePreservesHandlePriorityAndReplay(
            [Values(false, true)] bool post,
            [Values(false, true)] bool action,
            [Values(false, true)] bool enabled,
            [Values(false, true)] bool diagnostics
        )
        {
            string label =
                $"post={post},action={action},enabled={enabled},diagnostics={diagnostics}";
            using CaseState state = new CaseState(diagnostics);
            int calls = 0;
            MessageRegistrationHandle handle = Register(
                state.Token,
                post,
                action,
                () => ++calls,
                17
            );
            if (enabled)
            {
                state.Token.Enable();
            }
            Emit(state.Bus, OldSource);
            int prior = enabled ? 1 : 0;
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.True,
                label + ": valid source-bound handle moves."
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(1),
                label + ": move keeps one staged identity."
            );
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(NewSource),
                label + ": metadata names the new source."
            );
            Assert.That(
                Metadata(state.Token, handle).priority,
                Is.EqualTo(17),
                label + ": priority survives the move."
            );
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Assert.That(calls, Is.EqualTo(prior), label + ": old source no longer delivers.");
            Emit(state.Bus, NewSource);
            state.Token.Disable();
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(prior + 2),
                label + ": disable/enable replays only the new source."
            );
            if (diagnostics)
            {
                Assert.That(
                    state.Token._callCounts[handle],
                    Is.EqualTo(prior + 2),
                    label + ": counts stay under the original handle."
                );
                Assert.That(
                    state.Token._emissionBuffer.Count,
                    Is.EqualTo(prior + 2),
                    label + ": completed deliveries retain their origins."
                );
                for (int i = 0; i < prior + 2; ++i)
                {
                    Assert.That(
                        state.Token._emissionBuffer[i].context,
                        Is.EqualTo(enabled && i == 0 ? OldSource : NewSource),
                        label + $": delivery {i} keeps its actual source."
                    );
                }
            }
            state.Token.RemoveRegistration(handle);
            Assert.That(
                state.Token._metadata.Count,
                Is.Zero,
                label + ": original handle still removes staging."
            );
            Assert.That(
                state.Token._callCounts.Count,
                Is.Zero,
                label + ": cleanup clears call counts."
            );
            Assert.That(
                state.Token._emissionBuffer.Count,
                Is.Zero,
                label + ": last removal clears diagnostic history."
            );
        }

        [Test]
        public void SameSourceAndInvalidHandlesDoNotTouchTheBus()
        {
            using CaseState state = new CaseState(false);
            using CaseState foreign = new CaseState(false);
            state.Token.Enable();
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => { });
            MessageRegistrationHandle other = Register(foreign.Token, false, false, () => { });
            MessageRegistrationHandle scalar = state.Token.RegisterUntargeted<ProbeMessage>(
                (in ProbeMessage _) => { }
            );
            MessageRegistrationHandle targeted = state.Token.RegisterTargeted<ProbeMessage>(
                OldSource,
                (in ProbeMessage _) => { }
            );
            MessageRegistrationHandle unfiltered =
                state.Token.RegisterBroadcastWithoutSource<ProbeMessage>(
                    (in InstanceId _, in ProbeMessage __) => { }
                );
            MessageRegistrationHandle retired = Register(state.Token, false, false, () => { }, 1);
            state.Token.RemoveRegistration(retired);
            state.Bus.RejectRegistrations = 1;
            state.Bus.RejectCleanup = 1;
            Assert.That(
                state.Token.RetargetSource(handle, OldSource),
                Is.True,
                "Same-source move is a true no-op."
            );
            foreach (
                MessageRegistrationHandle invalid in new[]
                {
                    default,
                    other,
                    scalar,
                    targeted,
                    unfiltered,
                    retired,
                }
            )
            {
                Assert.That(
                    state.Token.RetargetSource(invalid, NewSource),
                    Is.False,
                    $"Unsupported handle {invalid} must leave registrations unchanged."
                );
            }
            Assert.That(
                state.Bus.RejectRegistrations,
                Is.EqualTo(1),
                "No-op and rejected moves must not register."
            );
            Assert.That(
                state.Bus.RejectCleanup,
                Is.EqualTo(1),
                "No-op and rejected moves must not deregister."
            );
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(OldSource),
                "Valid staging is unchanged."
            );
        }

        [Test]
        public void RejectedNewSourceRestoresOldStagingAndRetainsRecoveryFailures(
            [Values(1, 2)] int rejections
        )
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.RejectRegistrations = rejections;
            if (rejections == 1)
            {
                Assert.That(
                    Assert.Throws<InvalidOperationException>(() =>
                        state.Token.RetargetSource(handle, NewSource)
                    ),
                    Is.SameAs(state.Bus.Failure),
                    "Failed new registration reports its original failure after old-source recovery."
                );
            }
            else
            {
                AggregateException error = Assert.Throws<AggregateException>(() =>
                    state.Token.RetargetSource(handle, NewSource)
                );
                Assert.That(
                    error.InnerExceptions.Count,
                    Is.EqualTo(2),
                    "Both the move and recovery failures must be visible."
                );
            }
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(OldSource),
                "Failed move restores the old staged source."
            );
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.Zero, "Rejected source must not remain active.");
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "The original handle recovers through the public Enable API."
            );
        }

        [Test]
        public void CleanupFailureKeepsOldSourceAndCanBeRetried()
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.RejectCleanup = 1;
            Assert.That(
                Assert.Throws<InvalidOperationException>(() =>
                    state.Token.RetargetSource(handle, NewSource)
                ),
                Is.SameAs(state.Bus.Failure),
                "A failed old-source cleanup stays retryable."
            );
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(OldSource),
                "Failed cleanup cannot change staged source."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Only the original source remains active after cleanup fails."
            );
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.True,
                "Retry moves the same handle after the bus recovers."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.EqualTo(2), "Retry cannot create an old/new duplicate.");
        }

        [Test]
        public void CleanupCallbacksCanCancelActivationOrRemoveTheHandle(
            [Values(false, true)] bool remove
        )
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.OnCleanup = () =>
            {
                if (remove)
                {
                    state.Token.RemoveRegistration(handle);
                }
                else
                {
                    state.Token.Disable();
                }
            };
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.EqualTo(!remove),
                $"remove={remove}: removal wins; disable permits a staged move only."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.Zero,
                $"remove={remove}: canceled activation must not reappear immediately."
            );
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(remove ? 0 : 1),
                $"remove={remove}: only retained staging can activate later."
            );
        }

        [Test]
        public void ReplayCanMoveALaterHandleButRejectsMovingTheInFlightRegistration()
        {
            using CaseState state = new CaseState(false);
            int firstCalls = 0;
            int laterCalls = 0;
            MessageRegistrationHandle first = Register(
                state.Token,
                false,
                false,
                () => ++firstCalls,
                -1
            );
            MessageRegistrationHandle later = Register(
                state.Token,
                false,
                false,
                () => ++laterCalls,
                1
            );
            state.Bus.OnRegistration = () =>
            {
                Assert.Throws<InvalidOperationException>(
                    () => state.Token.RetargetSource(first, NewSource),
                    "An in-flight bus registration cannot be moved before its cleanup handle exists."
                );
                Assert.That(
                    state.Token.RetargetSource(later, NewSource),
                    Is.True,
                    "A future replay entry can change its staged source."
                );
            };
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Assert.That(
                firstCalls,
                Is.EqualTo(1),
                "In-flight registration retains the source already accepted by the bus."
            );
            Assert.That(laterCalls, Is.Zero, "Later replay must not use its old source.");
            Emit(state.Bus, NewSource);
            Assert.That(laterCalls, Is.EqualTo(1), "Later replay reads its updated source.");
        }

        [Test]
        public void FrozenCallbacksRetainTheirSourceAfterASelfMove(
            [Values(false, true)] bool post,
            [Values(false, true)] bool action
        )
        {
            using CaseState state = new CaseState(true);
            state.Token.Enable();
            List<int> calls = new List<int>();
            bool nested = false;
            MessageRegistrationHandle moving = default;
            moving = Register(
                state.Token,
                post,
                action,
                () =>
                {
                    calls.Add(nested ? 2 : 1);
                    if (!nested)
                    {
                        nested = true;
                        Assert.That(
                            state.Token.RetargetSource(moving, NewSource),
                            Is.True,
                            "Self move preserves the executing handle."
                        );
                        Emit(state.Bus, NewSource);
                    }
                },
                -1
            );
            MessageRegistrationHandle peer = Register(
                state.Token,
                post,
                action,
                () => calls.Add(3),
                1
            );
            Emit(state.Bus, OldSource);
            Assert.That(
                calls,
                Is.EqualTo(new[] { 1, 2, 3 }),
                $"post={post},action={action}: nested new-source delivery must not erase the frozen old-source peer."
            );
            List<InstanceId?> origins = new List<InstanceId?>();
            for (int i = 0; i < state.Token._emissionBuffer.Count; ++i)
            {
                if (state.Token._emissionBuffer[i].registrationHandle == moving)
                {
                    origins.Add(state.Token._emissionBuffer[i].context);
                }
            }
            Assert.That(
                origins,
                Is.EqualTo(new InstanceId?[] { NewSource, OldSource }),
                $"post={post},action={action}: each completed callback records its immutable origin."
            );
            Assert.That(
                state.Token._callCounts[moving],
                Is.EqualTo(2),
                "Both deliveries keep the same staged handle."
            );
            Assert.That(
                state.Token._callCounts[peer],
                Is.EqualTo(1),
                "Frozen peer completes once."
            );
            state.Token.UnregisterAll();
            Assert.That(
                state.Token._metadata.Count,
                Is.Zero,
                "Cleanup removes moved and unchanged staging."
            );
            Assert.That(state.Token._callCounts.Count, Is.Zero, "Cleanup clears all call counts.");
            Assert.That(state.Token._emissionBuffer.Count, Is.Zero, "Cleanup clears all origins.");
        }

        [Test]
        public void ActivationCallbackCanDisposeTheOwningGroupWithoutLosingLateCleanup()
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            int calls = 0;
            MessageRegistrationHandle handle = group.Add(
                Register(state.Token, false, false, () => ++calls)
            );
            state.Bus.OnRegistration = group.Dispose;
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.False,
                "Group removal wins over retaining source staging."
            );
            Assert.That(
                group.Count,
                Is.EqualTo(1),
                "Cleanup accepted after the callback remains owned by its group."
            );
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Late acceptance remains active until its retained cleanup is drained."
            );
            group.Dispose();
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Group retry removes the late accepted source registration."
            );
            Assert.That(group.Count, Is.Zero, "Group completes after the source move returns.");
        }

        [Test]
        public void DisableDuringActivationRetainsAcceptedCleanupForTheNextPublicPass()
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.OnRegistration = state.Token.Disable;
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.True,
                "The handle still owns the accepted source registration."
            );
            Assert.That(
                state.Token.Enabled,
                Is.True,
                "Cleanup accepted after Disable must remain visible as pending work."
            );
            state.Token.Disable();
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.Zero, "Retrying the owning cleanup API drains late acceptance.");
            state.Token.Enable();
            Emit(state.Bus, NewSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "The staged new source remains available for an intentional replay."
            );
        }

        [Test]
        public void ReactivationDuringOldCleanupDoesNotCauseDuplicateSourceActivation()
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.OnCleanup = () =>
            {
                state.Token.Disable();
                state.Token.Enable();
            };
            Assert.Throws<InvalidOperationException>(
                () => state.Token.RetargetSource(handle, NewSource),
                "An old-source registration accepted by a callback must be cleaned before another activation."
            );
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(OldSource),
                "Incomplete cleanup preserves old staging."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.EqualTo(1), "Only the accepted old-source registration is live.");
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(
                state.Token.RetargetSource(handle, NewSource),
                Is.True,
                "A later explicit cleanup permits the same handle to move."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.EqualTo(2), "Retry must not add a second live source binding.");
        }

        [Test]
        public void FailedActivationPreservesDisableRequestedByItsCallback()
        {
            using CaseState state = new CaseState(false);
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, false, false, () => ++calls);
            state.Bus.OnRegistration = state.Token.Disable;
            state.Bus.RejectRegistrations = 1;
            Assert.Throws<InvalidOperationException>(
                () => state.Token.RetargetSource(handle, NewSource),
                "Rejected activation still reports the original failure."
            );
            Assert.That(
                state.Token.Enabled,
                Is.False,
                "Old-source recovery must not undo a reentrant Disable."
            );
            Assert.That(
                Metadata(state.Token, handle).context,
                Is.EqualTo(OldSource),
                "Failed activation restores the old staged source."
            );
            Emit(state.Bus, OldSource);
            Emit(state.Bus, NewSource);
            Assert.That(calls, Is.Zero, "Both sources stay inactive after cancellation.");
            state.Token.Enable();
            Emit(state.Bus, OldSource);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Only an intentional replay restores the old source."
            );
        }

        private static MessageRegistrationMetadata Metadata(
            MessageRegistrationToken token,
            MessageRegistrationHandle handle
        )
        {
            foreach (
                KeyValuePair<
                    MessageRegistrationHandle,
                    MessageRegistrationMetadata
                > entry in token._metadata
            )
            {
                if (entry.Key == handle)
                {
                    return entry.Value;
                }
            }
            Assert.Fail($"Handle {handle} must still have staged metadata.");
            return default;
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            bool post,
            bool action,
            Action record,
            int priority = 0
        )
        {
            if (action)
            {
                Action<ProbeMessage> callback = _ => record();
                return post
                    ? token.RegisterBroadcastPostProcessor(OldSource, callback, priority)
                    : token.RegisterBroadcast(OldSource, callback, priority);
            }
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => record();
            return post
                ? token.RegisterBroadcastPostProcessor(OldSource, fast, priority)
                : token.RegisterBroadcast(OldSource, fast, priority);
        }

        private static void Emit(IMessageBus bus, InstanceId source)
        {
            ProbeMessage message = default;
            bus.SourcedBroadcast(ref source, ref message);
        }

        private sealed class CaseState : IDisposable
        {
            internal readonly MessageBus Inner = new MessageBus();
            internal readonly MovingBus Bus;
            internal readonly MessageRegistrationToken Token;
            private readonly LeakWatcher _leaks;
            private readonly DiagnosticsScope _diagnostics = new DiagnosticsScope(
                messageBufferSize: 8,
                diagnosticsStackTraces: false
            );

            internal CaseState(bool diagnostics)
            {
                _leaks = new LeakWatcher(Inner);
                Bus = new MovingBus(Inner);
                MessageHandler handler = new MessageHandler(new InstanceId(971), Bus)
                {
                    active = true,
                };
                Token = MessageRegistrationToken.Create(handler, Bus);
                Token.DiagnosticMode = diagnostics;
            }

            public void Dispose()
            {
                Bus.RejectCleanup = 0;
                Bus.RejectRegistrations = 0;
                Bus.OnRegistration = null;
                Bus.OnCleanup = null;
                try
                {
                    Token.Dispose();
                    Inner.Trim(true);
                    _leaks.Dispose();
                }
                finally
                {
                    _diagnostics.Dispose();
                }
            }
        }

        private sealed class MovingBus : DelegatingMessageBus
        {
            internal readonly InvalidOperationException Failure = new InvalidOperationException(
                "Source move rejected before forwarding."
            );
            internal int RejectRegistrations;
            internal int RejectCleanup;
            internal Action OnRegistration;
            internal Action OnCleanup;

            internal MovingBus(IMessageBus inner)
                : base(inner) { }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterSourcedBroadcast<T>(source, handler, priority);
            }

            public override MessageBusRegistration RegisterBroadcastPostProcessor<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterBroadcastPostProcessor<T>(source, handler, priority);
            }

            private void BeforeRegistration()
            {
                Action callback = OnRegistration;
                OnRegistration = null;
                callback?.Invoke();
                if (0 < RejectRegistrations)
                {
                    --RejectRegistrations;
                    throw Failure;
                }
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnCleanup;
                OnCleanup = null;
                callback?.Invoke();
                if (0 < RejectCleanup)
                {
                    --RejectCleanup;
                    throw Failure;
                }
                base.Deregister<T>(in registration);
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
