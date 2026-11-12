namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Checks explicit current-state publication and callback-owned replay.</summary>
    [TestFixture]
    public sealed partial class StickyUntargetedSpecificTests
    {
        [Test]
        public void OrdinaryLateRegistrationDoesNotReplay()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(981), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            using LeakWatcher leaks = new LeakWatcher(bus);
            ValueMessage message = new ValueMessage(17);
            bus.UntargetedBroadcast(ref message);
            int calls = 0;
            token.RegisterUntargeted<ValueMessage>(_ => ++calls);
            token.Enable();
            Assert.That(calls, Is.Zero, "Ordinary delivery has no retained value.");
            bus.UntargetedBroadcast(ref message);
            Assert.That(calls, Is.EqualTo(1), "The next ordinary publication still delivers.");
            token.Dispose();
        }

        [Test]
        public void StickyPublicationDistinguishesAbsenceFromDefault()
        {
            MessageBus bus = new MessageBus();
            Assert.That(bus.TryGetLatest<ValueMessage>(out _), Is.False, "Nothing was published.");
            ValueMessage message = default;
            bus.EmitSticky(ref message);
            Assert.That(
                bus.TryGetLatest(out ValueMessage latest),
                Is.True,
                "Default was published."
            );
            Assert.That(latest.Value, Is.Zero, "The default value is real retained state.");
            Assert.That(bus.ClearLatest<ValueMessage>(), Is.True, "Clear removes that state.");
            Assert.That(bus.TryGetLatest<ValueMessage>(out _), Is.False, "Clear restores absence.");
        }

        [Test]
        public void LateOptInReplaysOnlyNewHandler()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(982), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            using LeakWatcher leaks = new LeakWatcher(bus);
            List<int> values = new List<int>();
            ValueMessage message = new ValueMessage(17);
            bus.EmitSticky(ref message);
            token.RegisterUntargeted<ValueMessage>(m => values.Add(m.Value), replayLatest: true);
            Assert.That(values, Is.Empty, "Disabled staging must wait for activation.");
            token.Enable();
            Assert.That(values, Is.EqualTo(new[] { 17 }), "Activation replays one current value.");
            token.Enable();
            Assert.That(values.Count, Is.EqualTo(1), "Repeated Enable is inert.");
            token.Dispose();
        }

        [Test]
        public void LookupAndClearAreIsolatedAndOrdinaryEmissionDoesNotOverwrite(
            [Values(false, true)] bool throughInterface
        )
        {
            using CaseState state = new CaseState();
            MessageBus other = new MessageBus();
            IMessageBus bus = state.Bus;
            ValueMessage first = new ValueMessage(int.MinValue);
            if (throughInterface)
            {
                bus.EmitSticky(ref first);
            }
            else
            {
                state.Bus.EmitSticky(ref first);
            }
            ValueMessage ordinary = new ValueMessage(99);
            bus.UntargetedBroadcast(ref ordinary);
            state.Bus.Trim(force: true);
            Assert.That(
                bus.TryGetLatest(out ValueMessage latest),
                Is.True,
                "Trim keeps current state."
            );
            Assert.That(
                latest.Value,
                Is.EqualTo(int.MinValue),
                "Ordinary emits do not overwrite state."
            );
            Assert.That(other.TryGetLatest<ValueMessage>(out _), Is.False, "Buses are isolated.");
            Assert.That(
                bus.TryGetLatest<OtherMessage>(out _),
                Is.False,
                "Message types are isolated."
            );
            ValueMessage next = new ValueMessage(int.MaxValue);
            bus.EmitSticky(ref next);
            Assert.That(bus.TryGetLatest(out latest), Is.True, "The replacement remains present.");
            Assert.That(latest.Value, Is.EqualTo(int.MaxValue), "Latest publication wins.");
            Assert.That(bus.ClearLatest<ValueMessage>(), Is.True, "Clear removes the value.");
            Assert.That(bus.ClearLatest<ValueMessage>(), Is.False, "Repeated clear is inert.");
            Assert.That(bus.TryGetLatest(out latest), Is.False, "Clear restores absence.");
            Assert.That(latest.Value, Is.Zero, "A missing lookup sets the output to default.");
        }

        [Test]
        public void ReferencePayloadAndPublishedNullKeepTheirNormalMeaning()
        {
            using CaseState state = new CaseState();
            Payload payload = new Payload { Value = 17 };
            ReferenceMessage message = new ReferenceMessage(payload);
            state.Bus.EmitSticky(ref message);
            payload.Value = 23;
            message = new ReferenceMessage(new Payload { Value = 99 });
            Assert.That(
                state.Bus.TryGetLatest(out ReferenceMessage latest),
                Is.True,
                "A value exists."
            );
            Assert.That(latest.Payload, Is.SameAs(payload), "Referenced payloads are not cloned.");
            Assert.That(
                latest.Payload.Value,
                Is.EqualTo(23),
                "Shared object mutation remains visible."
            );
            ReferenceMessage empty = null;
            state.Bus.EmitSticky(ref empty);
            Assert.That(
                state.Bus.TryGetLatest(out latest),
                Is.True,
                "Published null differs from absence."
            );
            Assert.That(latest, Is.Null, "The retained null is returned unchanged.");
            state.Bus.ClearLatest<ReferenceMessage>();
            Assert.That(
                state.Bus.TryGetLatest<ReferenceMessage>(out _),
                Is.False,
                "Clear removes null too."
            );
        }

        [Test]
        public void OfferedSnapshotPrecedesInterceptorsAndReplayBypassesPipeline(
            [Values(false, true)] bool cancel,
            [Values(false, true)] bool callbackThrows
        )
        {
            using CaseState state = new CaseState();
            int interceptions = 0;
            int deliveries = 0;
            int posts = 0;
            state.Token.RegisterUntargetedInterceptor<ValueMessage>(
                (ref ValueMessage message) =>
                {
                    ++interceptions;
                    message = new ValueMessage(message.Value + 10);
                    return !cancel;
                }
            );
            state.Token.RegisterUntargeted<ValueMessage>(_ =>
            {
                ++deliveries;
                if (callbackThrows)
                {
                    throw new InvalidOperationException("Injected normal-delivery failure.");
                }
            });
            state.Token.RegisterUntargetedPostProcessor<ValueMessage>(
                (in ValueMessage _) => ++posts
            );
            state.Token.Enable();
            ValueMessage offered = new ValueMessage(7);
            if (!cancel && callbackThrows)
            {
                Assert.Throws<InvalidOperationException>(() => state.Bus.EmitSticky(ref offered));
            }
            else
            {
                state.Bus.EmitSticky(ref offered);
            }
            Assert.That(
                offered.Value,
                Is.EqualTo(17),
                "Normal interceptor mutation reaches the caller."
            );
            Assert.That(
                state.Bus.TryGetLatest(out ValueMessage latest),
                Is.True,
                "The offer is retained."
            );
            Assert.That(
                latest.Value,
                Is.EqualTo(7),
                "Cancellation and exceptions do not rewrite the offer."
            );
            int calls = 0;
            long emission = state.Bus.EmissionId;
            state.Token.RegisterUntargeted<ValueMessage>(m => calls = m.Value, replayLatest: true);
            Assert.That(
                calls,
                Is.EqualTo(7),
                "Only the new callback receives the offered snapshot."
            );
            Assert.That(interceptions, Is.EqualTo(1), "Replay bypasses interceptors.");
            Assert.That(deliveries, Is.EqualTo(cancel ? 0 : 1), "Replay does not rebroadcast.");
            Assert.That(
                posts,
                Is.EqualTo(cancel || callbackThrows ? 0 : 1),
                "Replay bypasses post-processors."
            );
            Assert.That(
                state.Bus.EmissionId,
                Is.EqualTo(emission),
                "Replay is not a new bus emission."
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void NestedPublicationClearAndResetTakePrecedence(int mutation)
        {
            using CaseState state = new CaseState();
            state.Token.RegisterUntargeted<ValueMessage>(m =>
            {
                if (m.Value != 1)
                {
                    return;
                }
                ValueMessage nested = new ValueMessage(2);
                state.Bus.EmitSticky(ref nested);
                if (mutation == 1)
                {
                    state.Bus.ClearLatest<ValueMessage>();
                }
                else if (mutation == 2)
                {
                    state.Bus.ResetState();
                }
            });
            state.Token.Enable();
            ValueMessage outer = new ValueMessage(1);
            state.Bus.EmitSticky(ref outer);
            Assert.That(
                state.Bus.TryGetLatest(out ValueMessage latest),
                Is.EqualTo(mutation == 0),
                "Outer completion must not restore older state."
            );
            Assert.That(
                latest.Value,
                Is.EqualTo(mutation == 0 ? 2 : 0),
                "The nested operation owns final state."
            );
        }

        [Test]
        public void EveryCallbackFormReplaysOncePerActivation(
            [Values(0, 1, 2)] int form,
            [Values(false, true)] bool enabled
        )
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            if (enabled)
            {
                state.Bus.EmitSticky(ref message);
                state.Token.Enable();
            }
            List<int> values = new List<int>();
            MessageRegistrationHandle handle = Register(state.Token, form, values);
            if (!enabled)
            {
                state.Bus.EmitSticky(ref message);
            }
            Assert.That(
                values.Count,
                Is.EqualTo(enabled ? 1 : 0),
                "Disabled staging defers replay."
            );
            state.Token.Enable();
            state.Token.Enable();
            Assert.That(values, Is.EqualTo(new[] { 17 }), "One replay follows initial activation.");
            message = new ValueMessage(23);
            state.Bus.EmitSticky(ref message);
            Assert.That(
                values,
                Is.EqualTo(new[] { 17, 23 }),
                "Future publication delivers normally."
            );
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(
                values,
                Is.EqualTo(new[] { 17, 23, 23 }),
                "Reactivation reads current state once."
            );
            state.Token.RemoveRegistration(handle);
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(values.Count, Is.EqualTo(3), "Removed handles never replay again.");
        }

        [Test]
        public void ReplayFailurePreservesOwnershipAndCanBeRetried(
            [Values(false, true)] bool enabled
        )
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            bool fail = true;
            int calls = 0;
            Action<ValueMessage> callback = _ =>
            {
                ++calls;
                if (fail)
                {
                    throw new InvalidOperationException("Injected replay failure.");
                }
            };
            if (enabled)
            {
                state.Token.Enable();
                Assert.Throws<InvalidOperationException>(() =>
                    state.Token.RegisterUntargeted(callback, replayLatest: true)
                );
                Assert.That(
                    state.Token._metadata.Count,
                    Is.Zero,
                    "An unreturned failed handle is retired."
                );
            }
            else
            {
                state.Token.RegisterUntargeted(callback, replayLatest: true);
                Assert.Throws<InvalidOperationException>(() => state.Token.Enable());
                Assert.That(state.Token.Enabled, Is.False, "Failed activation rolls back cleanup.");
                Assert.That(
                    state.Token._metadata.Count,
                    Is.EqualTo(1),
                    "Staging remains retryable."
                );
            }
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.Zero,
                "The throwing callback leaves no active subscription."
            );
            fail = false;
            if (enabled)
            {
                state.Token.RegisterUntargeted(callback, replayLatest: true);
            }
            else
            {
                state.Token.Enable();
            }
            Assert.That(calls, Is.EqualTo(2), "A recovered activation replays once.");
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.EqualTo(1),
                "Recovery creates exactly one subscription."
            );
        }

        [Test]
        public void ReplayCanRemoveOrCancelItsOwnActivation([Values(0, 1, 2, 3, 4)] int operation)
        {
            using CaseState state = new CaseState();
            using MessageRegistrationGroup group = state.Token.CreateGroup();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            MessageRegistrationHandle handle = default;
            int self = 0;
            int later = 0;
            handle = state.Token.RegisterUntargeted<ValueMessage>(
                _ =>
                {
                    ++self;
                    switch (operation)
                    {
                        case 0:
                            state.Token.RemoveRegistration(handle);
                            break;
                        case 1:
                            state.Token.Dispose();
                            break;
                        case 2:
                            state.Token.Disable();
                            break;
                        case 3:
                            state.Token.UnregisterAll();
                            break;
                        case 4:
                            group.Dispose();
                            break;
                    }
                },
                replayLatest: true
            );
            group.Add(handle);
            state.Token.RegisterUntargeted<ValueMessage>(_ => ++later, replayLatest: true);
            state.Token.Enable();
            Assert.That(self, Is.EqualTo(1), "Cleanup ownership precedes the user callback.");
            bool preservesLater = operation == 0 || operation == 4;
            Assert.That(
                later,
                Is.EqualTo(preservesLater ? 1 : 0),
                "Cancellation stops later queued activation."
            );
            Assert.That(
                state.Token.Enabled,
                Is.EqualTo(preservesLater),
                "Disable and disposal remain effective after replay returns."
            );
            state.Bus.UntargetedBroadcast(ref message);
            Assert.That(self, Is.EqualTo(1), "The self-removed subscription is no longer live.");
            Assert.That(
                later,
                Is.EqualTo(preservesLater ? 2 : 0),
                "Only a preserved later subscription delivers."
            );
        }

        [Test]
        public void FalseReplayOptionAndAbsentStateDoNotInventCallbacks(
            [Values(0, 1, 2)] int form,
            [Values(false, true)] bool published
        )
        {
            using CaseState state = new CaseState();
            if (published)
            {
                ValueMessage stored = new ValueMessage(17);
                state.Bus.EmitSticky(ref stored);
            }
            List<int> values = new List<int>();
            Register(state.Token, form, values, replayLatest: !published);
            state.Token.Enable();
            Assert.That(
                values,
                Is.Empty,
                "Absent state and replayLatest=false must not invoke callbacks."
            );
            ValueMessage next = new ValueMessage(23);
            state.Bus.UntargetedBroadcast(ref next);
            Assert.That(
                values,
                Is.EqualTo(new[] { 23 }),
                "Every form still accepts future ordinary delivery."
            );
        }

        [Test]
        public void UnsupportedCapabilityAndNullBusAreExplicit([Values(false, true)] bool enabled)
        {
            IMessageBus missing = null;
            ValueMessage message = default;
            Assert.Throws<ArgumentNullException>(() => missing.EmitSticky(ref message));
            Assert.Throws<ArgumentNullException>(() => missing.TryGetLatest<ValueMessage>(out _));
            Assert.Throws<ArgumentNullException>(() => missing.ClearLatest<ValueMessage>());
            MessageBus inner = new MessageBus();
            IMessageBus unsupported = new DelegatingMessageBus(inner);
            Assert.Throws<NotSupportedException>(() => unsupported.EmitSticky(ref message));
            Assert.Throws<NotSupportedException>(() =>
                unsupported.TryGetLatest<ValueMessage>(out _)
            );
            Assert.Throws<NotSupportedException>(() => unsupported.ClearLatest<ValueMessage>());
            using CaseState state = new CaseState(unsupported);
            if (enabled)
            {
                state.Token.Enable();
                Assert.Throws<NotSupportedException>(() =>
                    state.Token.RegisterUntargeted<ValueMessage>(_ => { }, replayLatest: true)
                );
                Assert.That(
                    state.Token._metadata.Count,
                    Is.Zero,
                    "Rejected immediate registration has no inaccessible staging."
                );
            }
            else
            {
                state.Token.RegisterUntargeted<ValueMessage>(_ => { }, replayLatest: true);
                Assert.Throws<NotSupportedException>(() => state.Token.Enable());
                state.Token.UnregisterAll();
            }
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(_ => ++calls, replayLatest: false);
            state.Token.Enable();
            unsupported.UntargetedBroadcast(ref message);
            Assert.That(calls, Is.EqualTo(1), "A custom bus still supports ordinary registration.");
        }

        [Test]
        public void ReplayUsesActualAcceptedBusWhenRegistrationChangesFutureOverride(
            [Values(false, true)] bool enabled
        )
        {
            MessageBus first = new MessageBus();
            MessageBus second = new MessageBus();
            StickyBus wrapper = new StickyBus(first);
            using CaseState state = new CaseState(wrapper);
            using LeakWatcher secondLeaks = new LeakWatcher(second);
            ValueMessage old = new ValueMessage(17);
            ValueMessage next = new ValueMessage(23);
            first.EmitSticky(ref old);
            second.EmitSticky(ref next);
            if (enabled)
            {
                state.Token.Enable();
            }
            wrapper.OnRegistration = () =>
                state.Token.RetargetMessageBus(second, MessageBusRebindMode.PreserveRegistrations);
            List<int> values = new List<int>();
            state.Token.RegisterUntargeted<ValueMessage>(
                m => values.Add(m.Value),
                replayLatest: true
            );
            state.Token.Enable();
            Assert.That(
                values,
                Is.EqualTo(new[] { 17 }),
                "The accepted wrapper owns replay, despite the new future override."
            );
            second.UntargetedBroadcast(ref next);
            first.UntargetedBroadcast(ref old);
            Assert.That(
                values,
                Is.EqualTo(new[] { 17, 17 }),
                "The original subscription remains bound to its accepted bus."
            );
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(
                values,
                Is.EqualTo(new[] { 17, 17, 23 }),
                "A later activation uses the selected future bus."
            );
            state.Token.Dispose();
        }

        [Test]
        public void RebindingOutsideReplayReadsOnlyTheSelectedState(
            [Values(false, true)] bool active
        )
        {
            using CaseState state = new CaseState();
            MessageBus replacement = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(replacement);
            ValueMessage first = new ValueMessage(17);
            ValueMessage next = new ValueMessage(23);
            state.Bus.EmitSticky(ref first);
            replacement.EmitSticky(ref next);
            List<int> values = new List<int>();
            Register(state.Token, 1, values);
            state.Token.Enable();
            state.Token.RetargetMessageBus(
                replacement,
                active
                    ? MessageBusRebindMode.RebindActive
                    : MessageBusRebindMode.PreserveRegistrations
            );
            Assert.That(
                values.Count,
                Is.EqualTo(active ? 2 : 1),
                "Only active rebinding immediately activates again."
            );
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(
                values[values.Count - 1],
                Is.EqualTo(23),
                "The selected bus supplies state on the next activation."
            );
            Assert.That(
                values.Count,
                Is.EqualTo(active ? 3 : 2),
                "Each activation replays exactly once."
            );
            state.Token.Dispose();
        }

        [Test]
        public void ActiveRebindingInsideReplayRejectsBeforeMutation(
            [Values(false, true)] bool enabled
        )
        {
            using CaseState state = new CaseState();
            MessageBus replacement = new MessageBus();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            if (enabled)
            {
                state.Token.Enable();
            }
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(
                _ =>
                {
                    ++calls;
                    if (calls == 1)
                    {
                        Assert.Throws<InvalidOperationException>(() =>
                            state.Token.RetargetMessageBus(
                                replacement,
                                MessageBusRebindMode.RebindActive
                            )
                        );
                    }
                },
                replayLatest: true
            );
            state.Token.Enable();
            state.Bus.UntargetedBroadcast(ref message);
            replacement.UntargetedBroadcast(ref message);
            Assert.That(
                calls,
                Is.EqualTo(2),
                "Rejected nested rebinding preserves the accepted original subscription."
            );
        }

        [Test]
        public void ThrowingCleanupAfterReplayRemainsTokenOwned([Values(false, true)] bool enabled)
        {
            MessageBus inner = new MessageBus();
            StickyBus bus = new StickyBus(inner) { RejectCleanup = 1 };
            using CaseState state = new CaseState(bus);
            ValueMessage message = new ValueMessage(17);
            inner.EmitSticky(ref message);
            if (enabled)
            {
                state.Token.Enable();
            }
            Action<ValueMessage> fail = _ =>
                throw new InvalidOperationException("Injected replay failure.");
            if (enabled)
            {
                AggregateException failure = Assert.Throws<AggregateException>(() =>
                    state.Token.RegisterUntargeted(fail, replayLatest: true)
                );
                Assert.That(
                    failure.InnerExceptions.Count,
                    Is.EqualTo(2),
                    "Both replay and cleanup failures remain observable."
                );
            }
            else
            {
                state.Token.RegisterUntargeted(fail, replayLatest: true);
                Assert.Throws<InvalidOperationException>(() => state.Token.Enable());
            }
            Assert.That(state.Token.Enabled, Is.True, "Pending cleanup keeps ownership visible.");
            Assert.That(
                inner.RegisteredUntargeted,
                Is.EqualTo(1),
                "The rejected cleanup leaves one live subscription."
            );
            state.Token.Disable();
            Assert.That(
                state.Token.Enabled,
                Is.False,
                "A later cleanup retries the failed obligation."
            );
            Assert.That(
                inner.RegisteredUntargeted,
                Is.Zero,
                "Retry actually removes the subscription."
            );
            state.Token.UnregisterAll();
            state.Token.Enable();
            Assert.That(
                inner.RegisteredUntargeted,
                Is.Zero,
                "Removed staging cannot silently reactivate."
            );
        }

        [Test]
        public void ReplayRecordsTokenDiagnosticsWithoutCreatingAnotherBusEmission()
        {
            using DiagnosticsScope diagnostics = new DiagnosticsScope(
                diagnosticsTargets: DiagnosticsTarget.Off
            );
            using CaseState state = new CaseState();
            state.Token.DiagnosticMode = true;
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            long emission = state.Bus.EmissionId;
            int calls = 0;
            MessageRegistrationHandle handle = state.Token.RegisterUntargeted<ValueMessage>(
                _ => ++calls,
                replayLatest: true
            );
            state.Token.Enable();
            Assert.That(
                state.Token._callCounts[handle],
                Is.EqualTo(1),
                "The accepted callback's diagnostic count includes replay."
            );
            Assert.That(calls, Is.EqualTo(1), "The actual user callback was invoked.");
            Assert.That(
                state.Bus.EmissionId,
                Is.EqualTo(emission),
                "Replay does not change the bus emission sequence."
            );
        }

        [Test]
        public void ReplacementDuringReplaySurvivesTheOldHandleAndItsFailure(
            [Values(false, true)] bool enabled,
            [Values(false, true)] bool replayThrows
        )
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            if (enabled)
            {
                state.Token.Enable();
            }
            int replacementCalls = 0;
            MessageRegistrationHandle old = default;
            Action<ValueMessage> replace = _ =>
            {
                state.Token.UnregisterAll();
                state.Token.RegisterUntargeted<ValueMessage>(
                    _ => ++replacementCalls,
                    replayLatest: true
                );
                if (replayThrows)
                {
                    throw new InvalidOperationException("Injected retired-callback failure.");
                }
            };
            Action activate;
            if (enabled)
            {
                activate = () => old = state.Token.RegisterUntargeted(replace, replayLatest: true);
            }
            else
            {
                old = state.Token.RegisterUntargeted(replace, replayLatest: true);
                activate = state.Token.Enable;
            }
            if (replayThrows)
            {
                Assert.Throws<InvalidOperationException>(() => activate());
            }
            else
            {
                activate();
            }
            state.Token.RemoveRegistration(old);
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(1),
                "A stale or unreturned identity must not retire its replacement."
            );
            Assert.That(
                replacementCalls,
                Is.Zero,
                "The replacement is staged after UnregisterAll cancels this activation."
            );
            state.Token.Enable();
            Assert.That(
                replacementCalls,
                Is.EqualTo(1),
                "A later activation accepts the replacement exactly once."
            );
            state.Bus.UntargetedBroadcast(ref message);
            Assert.That(replacementCalls, Is.EqualTo(2), "The surviving replacement remains live.");
        }

        [Test]
        public void NestedPublicationFromReplayBecomesTheLatestState()
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(1);
            state.Bus.EmitSticky(ref message);
            List<int> values = new List<int>();
            state.Token.RegisterUntargeted<ValueMessage>(
                m =>
                {
                    values.Add(m.Value);
                    if (m.Value == 1)
                    {
                        ValueMessage nested = new ValueMessage(2);
                        state.Bus.EmitSticky(ref nested);
                    }
                },
                replayLatest: true
            );
            state.Token.Enable();
            Assert.That(
                values,
                Is.EqualTo(new[] { 1, 2 }),
                "Replay has already accepted the callback before nested normal delivery."
            );
            Assert.That(
                state.Bus.TryGetLatest(out ValueMessage latest),
                Is.True,
                "Nested state remains present."
            );
            Assert.That(
                latest.Value,
                Is.EqualTo(2),
                "Returning from replay cannot restore the old snapshot."
            );
        }

        [Test]
        public void ThrowingInterceptorDoesNotUndoTheOfferedSnapshot()
        {
            using CaseState state = new CaseState();
            state.Token.RegisterUntargetedInterceptor<ValueMessage>(
                (ref ValueMessage _) =>
                    throw new InvalidOperationException("Injected interceptor failure.")
            );
            state.Token.Enable();
            ValueMessage message = new ValueMessage(17);
            Assert.Throws<InvalidOperationException>(() => state.Bus.EmitSticky(ref message));
            Assert.That(
                state.Bus.TryGetLatest(out ValueMessage latest),
                Is.True,
                "An offered snapshot precedes the throwing interceptor."
            );
            Assert.That(
                latest.Value,
                Is.EqualTo(17),
                "The offered value survives pipeline failure."
            );
        }

        [Test]
        public void InactiveHandlerDoesNotReceiveReplayUntilAnActiveActivation()
        {
            using CaseState state = new CaseState();
            state.Handler.active = false;
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(_ => ++calls, replayLatest: true);
            state.Token.Enable();
            Assert.That(
                calls,
                Is.Zero,
                "An inactive handler must retain its normal delivery gate during replay."
            );
            state.Handler.active = true;
            state.Bus.UntargetedBroadcast(ref message);
            Assert.That(calls, Is.EqualTo(1), "An active handler accepts future normal delivery.");
            state.Token.Disable();
            state.Token.Enable();
            Assert.That(
                calls,
                Is.EqualTo(2),
                "A later active activation replays current state once."
            );
        }

        [Test]
        public void FailedRebindRecoveryPreservesDisableRequestedByReplay()
        {
            using CaseState state = new CaseState();
            MessageBus replacement = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(replacement);
            ValueMessage original = new ValueMessage(17);
            ValueMessage next = new ValueMessage(23);
            state.Bus.EmitSticky(ref original);
            replacement.EmitSticky(ref next);
            bool recovering = false;
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(
                message =>
                {
                    ++calls;
                    if (message.Value == 23)
                    {
                        throw new InvalidOperationException("Injected new-bus replay failure.");
                    }
                    if (recovering)
                    {
                        state.Token.Disable();
                    }
                },
                replayLatest: true
            );
            state.Token.Enable();
            recovering = true;
            Assert.Throws<InvalidOperationException>(() =>
                state.Token.RetargetMessageBus(replacement, MessageBusRebindMode.RebindActive)
            );
            Assert.That(
                state.Token.Enabled,
                Is.False,
                "Old-bus recovery must preserve lifecycle cancellation from its replay callback."
            );
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.Zero,
                "Recovery honored original-bus cleanup."
            );
            Assert.That(
                replacement.RegisteredUntargeted,
                Is.Zero,
                "The failed replacement remains rolled back."
            );
            recovering = false;
            state.Token.Enable();
            Assert.That(
                calls,
                Is.EqualTo(4),
                "A later activation retries the original staging once."
            );
            state.Token.Dispose();
        }

        [Test]
        public void CustomLookupCannotReplayAfterItsOwnerStopsDelivery(
            [Values(false, true)] bool enabled,
            [Values(0, 1, 2)] int cancellation
        )
        {
            MessageBus inner = new MessageBus();
            StickyBus bus = new StickyBus(inner);
            using CaseState state = new CaseState(bus);
            ValueMessage message = new ValueMessage(17);
            inner.EmitSticky(ref message);
            if (enabled)
            {
                state.Token.Enable();
            }
            int calls = 0;
            bus.OnLookup = () =>
            {
                if (cancellation == 0)
                {
                    state.Token.Disable();
                }
                else if (cancellation == 1)
                {
                    state.Token.UnregisterAll();
                }
                else
                {
                    state.Handler.active = false;
                }
            };
            state.Token.RegisterUntargeted<ValueMessage>(_ => ++calls, replayLatest: true);
            if (!enabled)
            {
                state.Token.Enable();
            }
            Assert.That(
                calls,
                Is.Zero,
                "Recheck actual ownership and the active gate after a custom bus lookup returns."
            );
        }

        /// <summary>Retarget recovery must preserve cancellation from the failed new-bus callback.</summary>
        /// <remarks>2026-11-12: Capturing cancellation only after rollback revived disabled staging.</remarks>
        [Test]
        public void FailedNewBusReplayPreservesItsCancellation(
            [Values(false, true)] bool unregister
        )
        {
            using CaseState state = new CaseState();
            MessageBus replacement = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(replacement);
            ValueMessage original = new ValueMessage(17);
            ValueMessage next = new ValueMessage(23);
            state.Bus.EmitSticky(ref original);
            replacement.EmitSticky(ref next);
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(
                message =>
                {
                    ++calls;
                    if (message.Value == 23)
                    {
                        if (unregister)
                        {
                            state.Token.UnregisterAll();
                        }
                        else
                        {
                            state.Token.Disable();
                        }
                        throw new InvalidOperationException("Injected failure after cancellation.");
                    }
                },
                replayLatest: true
            );
            state.Token.Enable();
            Assert.Throws<InvalidOperationException>(() =>
                state.Token.RetargetMessageBus(replacement, MessageBusRebindMode.RebindActive)
            );
            Assert.That(
                state.Token.Enabled,
                Is.False,
                $"unregister={unregister}: cancellation must survive recovery."
            );
            Assert.That(
                calls,
                Is.EqualTo(2),
                "Recovery must not invoke old-bus staging after cancellation."
            );
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.Zero,
                "Old-bus subscriptions stay removed."
            );
            Assert.That(
                replacement.RegisteredUntargeted,
                Is.Zero,
                "New-bus subscriptions stay removed."
            );
            state.Token.Enable();
            Assert.That(
                calls,
                Is.EqualTo(unregister ? 2 : 3),
                "Only retained staging can be explicitly enabled later."
            );
            state.Token.Dispose();
        }

        /// <summary>Callbacks added by replay join the owning activation in insertion order.</summary>
        /// <remarks>2026-11-12: A fixed queue snapshot left newly staged callbacks inactive on an enabled token.</remarks>
        [Test]
        public void ReplayActivatesNewCallbacksInOrder([Values(false, true)] bool sticky)
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            List<int> order = new List<int>();
            bool added = false;
            state.Token.RegisterUntargeted<ValueMessage>(
                _ =>
                {
                    order.Add(1);
                    if (!added)
                    {
                        added = true;
                        state.Token.RegisterUntargeted<ValueMessage>(
                            m => order.Add(3),
                            replayLatest: sticky
                        );
                    }
                },
                replayLatest: true
            );
            state.Token.RegisterUntargeted<ValueMessage>(_ => order.Add(2), replayLatest: true);
            state.Token.Enable();
            Assert.That(
                order,
                Is.EqualTo(sticky ? new[] { 1, 2, 3 } : new[] { 1, 2 }),
                "New replay follows previously staged callbacks."
            );
            order.Clear();
            state.Bus.UntargetedBroadcast(ref message);
            Assert.That(
                order,
                Is.EqualTo(new[] { 1, 2, 3 }),
                "Every accepted callback receives the next publication."
            );
        }

        /// <summary>Removing or cancelling newly staged replay work prevents its activation.</summary>
        [Test]
        public void ReplayAddedCallbacksRespectOwnership([Values(0, 1, 2)] int cancellation)
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            int calls = 0;
            bool added = false;
            state.Token.RegisterUntargeted<ValueMessage>(
                _ =>
                {
                    if (added)
                    {
                        return;
                    }
                    added = true;
                    MessageRegistrationHandle handle = state.Token.RegisterUntargeted<ValueMessage>(
                        m => ++calls,
                        replayLatest: true
                    );
                    if (cancellation == 0)
                    {
                        state.Token.RemoveRegistration(handle);
                    }
                    else if (cancellation == 1)
                    {
                        state.Token.Disable();
                    }
                    else
                    {
                        state.Token.UnregisterAll();
                    }
                },
                replayLatest: true
            );
            state.Token.Enable();
            Assert.That(
                calls,
                Is.Zero,
                $"cancellation={cancellation}: cancelled work must not replay."
            );
            Assert.That(
                state.Token.Enabled,
                Is.EqualTo(cancellation == 0),
                "Lifecycle cancellation controls the owning activation."
            );
            state.Token.Enable();
            Assert.That(
                calls,
                Is.EqualTo(cancellation == 1 ? 1 : 0),
                "Only disabled staging can be activated later."
            );
        }

        /// <summary>Replay additions can themselves add callbacks without losing activation ownership.</summary>
        [Test]
        public void ReplayActivationDrainsNestedAdditions([Values(1, 4)] int depth)
        {
            using CaseState state = new CaseState();
            ValueMessage message = new ValueMessage(17);
            state.Bus.EmitSticky(ref message);
            List<int> order = new List<int>();
            Add(0);
            state.Token.Enable();
            Assert.That(
                order.Count,
                Is.EqualTo(depth + 1),
                "Activation drains every nested addition."
            );
            for (int index = 0; index <= depth; ++index)
            {
                Assert.That(
                    order[index],
                    Is.EqualTo(index),
                    "Nested additions retain insertion order."
                );
            }
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.EqualTo(1),
                "Callbacks share one handler subscription for this message type."
            );

            void Add(int index)
            {
                bool added = false;
                state.Token.RegisterUntargeted<ValueMessage>(
                    _ =>
                    {
                        order.Add(index);
                        if (!added && index < depth)
                        {
                            added = true;
                            Add(index + 1);
                            state.Token.Enable();
                        }
                    },
                    replayLatest: true
                );
            }
        }

        /// <summary>Failure in an appended callback rolls back its activation and retains retryable staging.</summary>
        [Test]
        public void ReplayAddedCallbackFailureRetainsRecovery([Values(false, true)] bool retarget)
        {
            using CaseState state = new CaseState();
            MessageBus replacement = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(replacement);
            ValueMessage original = new ValueMessage(17);
            ValueMessage next = new ValueMessage(23);
            state.Bus.EmitSticky(ref original);
            replacement.EmitSticky(ref next);
            bool add = !retarget;
            bool added = false;
            bool fail = true;
            int calls = 0;
            state.Token.RegisterUntargeted<ValueMessage>(
                _ =>
                {
                    if (!add || added)
                    {
                        return;
                    }
                    added = true;
                    state.Token.RegisterUntargeted<ValueMessage>(
                        m =>
                        {
                            ++calls;
                            if (fail)
                            {
                                fail = false;
                                throw new InvalidOperationException(
                                    "Injected appended replay failure."
                                );
                            }
                        },
                        replayLatest: true
                    );
                },
                replayLatest: true
            );
            if (retarget)
            {
                state.Token.Enable();
                add = true;
                Assert.Throws<InvalidOperationException>(() =>
                    state.Token.RetargetMessageBus(replacement, MessageBusRebindMode.RebindActive)
                );
                Assert.That(
                    state.Token.Enabled,
                    Is.True,
                    "Successful recovery restores old-bus ownership."
                );
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => state.Token.Enable());
                Assert.That(
                    state.Token.Enabled,
                    Is.False,
                    "A failed Enable rolls back all accepted subscriptions."
                );
                Assert.That(
                    state.Bus.RegisteredUntargeted,
                    Is.Zero,
                    "Rollback owns and removes the appended subscription."
                );
                state.Token.Enable();
            }
            Assert.That(
                calls,
                Is.EqualTo(2),
                "Recovery or an explicit retry activates the appended callback once."
            );
            Assert.That(
                state.Bus.RegisteredUntargeted,
                Is.EqualTo(1),
                "Both callbacks use the original handler subscription."
            );
            Assert.That(
                replacement.RegisteredUntargeted,
                Is.Zero,
                "Failed retarget leaves no new-bus subscription."
            );
            state.Bus.UntargetedBroadcast(ref original);
            Assert.That(
                calls,
                Is.EqualTo(3),
                "The recovered callback receives future original-bus delivery."
            );
            state.Token.Dispose();
        }

        private sealed class StickyBus : DelegatingMessageBus, IStickyMessageBus
        {
            internal Action OnRegistration;
            internal Action OnLookup;
            internal int RejectCleanup;

            internal StickyBus(IMessageBus inner)
                : base(inner) { }

            public void EmitSticky<TMessage>(ref TMessage message)
                where TMessage : IUntargetedMessage => Inner.EmitSticky(ref message);

            public bool TryGetLatest<TMessage>(out TMessage message)
                where TMessage : IUntargetedMessage
            {
                Action callback = OnLookup;
                OnLookup = null;
                callback?.Invoke();
                return Inner.TryGetLatest(out message);
            }

            public bool ClearLatest<TMessage>()
                where TMessage : IUntargetedMessage => Inner.ClearLatest<TMessage>();

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Action callback = OnRegistration;
                OnRegistration = null;
                callback?.Invoke();
                return base.RegisterUntargeted<T>(handler, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (0 < RejectCleanup)
                {
                    --RejectCleanup;
                    throw new InvalidOperationException("Injected teardown failure.");
                }
                base.Deregister<T>(in registration);
            }
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            int form,
            List<int> values,
            bool replayLatest = true
        )
        {
            switch (form)
            {
                case 0:
                    return token.RegisterUntargeted<ValueMessage>(
                        m => values.Add(m.Value),
                        replayLatest: replayLatest
                    );
                case 1:
                    return token.RegisterUntargeted<ValueMessage>(
                        (in ValueMessage m) => values.Add(m.Value),
                        replayLatest: replayLatest
                    );
                default:
                    return token.RegisterUntargeted<ValueMessage, List<int>>(
                        values,
                        static (List<int> s, in ValueMessage m) => s.Add(m.Value),
                        replayLatest: replayLatest
                    );
            }
        }

        private sealed class CaseState : IDisposable
        {
            internal readonly MessageBus Bus = new MessageBus();
            internal readonly MessageHandler Handler;
            internal readonly MessageRegistrationToken Token;
            private readonly LeakWatcher _leaks;

            internal CaseState(IMessageBus selected = null)
            {
                IMessageBus bus = selected ?? Bus;
                Handler = new MessageHandler(new InstanceId(983), bus) { active = true };
                Token = MessageRegistrationToken.Create(Handler, bus);
                Token.DiagnosticMode = false;
                _leaks = new LeakWatcher(bus);
            }

            public void Dispose()
            {
                Token.Dispose();
                _leaks.Dispose();
            }
        }

        private sealed class Payload
        {
            internal int Value;
        }

        [DxUntargetedMessage]
        [DxAutoConstructor]
        private sealed partial class ReferenceMessage
        {
            public readonly Payload Payload;
        }

        [DxUntargetedMessage]
        private readonly partial struct OtherMessage { }

        [DxUntargetedMessage]
        [DxAutoConstructor]
        private readonly partial struct ValueMessage
        {
            public readonly int Value;
        }
    }
}
