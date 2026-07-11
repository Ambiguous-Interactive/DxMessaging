#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Deterministically pins the lazy-diagnostics allocation win on
    /// <see cref="MessageRegistrationToken"/>: token creation dropped from 11 to 7
    /// managed-allocation calls once the diagnostics-only <c>_callCounts</c> dictionary and
    /// <c>_emissionBuffer</c> cyclic buffer (plus the buffer's two backing lists) became
    /// lazily allocated -- exposed as the <c>_callCounts</c>/<c>_emissionBuffer</c>
    /// properties that <c>??=</c>-materialize their <c>_callCountsBacking</c>/
    /// <c>_emissionBufferBacking</c> fields on first use -- rather than eager
    /// <c>= new()</c> per-token fields. A token whose owner never enables diagnostics (the
    /// default, <see cref="DiagnosticsTarget.Off"/>, and the common player case) therefore
    /// pays nothing for them.
    /// <para>
    /// WHY THIS IS A STATE ASSERTION, NOT A GC-COUNT BUDGET. The original guard for this win
    /// (<c>TokenCreateAllocationCountIsWithinBudget</c>, in the Allocation-category perf leg)
    /// asserted an absolute <c>GC.Alloc</c> recorder count against a budget of 10. Measured
    /// on the host editor (6000.4), a warm, long-lived editor domain reads a MINIMUM of ~19
    /// allocations for that 7-allocation operation across 64 windows (median ~51, p90 ~73) --
    /// the recorder attributes background-editor allocations to whatever window is open, and
    /// even <c>MeasureMin</c> cannot push the floor below that ambient noise. The budget of 10
    /// is therefore BELOW the achievable warm-editor measurement floor, so the test
    /// false-failed run-to-run (the recurring "TokenCreate alloc-count flake"). A measure-first
    /// probe also DISPROVED the standing hypothesis that the swing came from
    /// <c>DxPools</c> rental: the steady refcount registration path does not rent from the
    /// typed-handler pools at all (hits = misses = 0), so a deterministic pool pre-warm cannot
    /// remove the noise. The noise is pure background-editor <c>GC.Alloc</c> pollution; only a
    /// deterministic STATE assertion (this) or a paired DIFFERENTIAL measurement (where the
    /// noise cancels) survives it.
    /// </para>
    /// <para>
    /// This guard reads the lazy backing fields directly: after
    /// <see cref="MessageRegistrationToken.Create"/>, both backing fields must still be
    /// <c>null</c> -- proof that <c>Create</c> allocated neither collection. The true contract
    /// is that the diagnostics collections materialize only at first DISPATCH (inside the
    /// <c>_diagnosticMode</c>-guarded augmented-handler bodies), NEVER at <c>Create</c> --
    /// REGARDLESS of the diagnostics setting -- so the test runs the assertion under BOTH
    /// <see cref="DiagnosticsTarget.Off"/> (the common player case the win targets) AND
    /// <see cref="DiagnosticsTarget.All"/> (proving the laziness is a Create-time property, not
    /// merely a consequence of diagnostics being off). Reverting either property to an eager
    /// <c>= new()</c> field makes the corresponding backing field non-<c>null</c> after
    /// <c>Create</c> (or removes it entirely), tripping this test. It uses no allocation probe,
    /// so it is deterministic and backend-independent and runs in the EditMode correctness leg
    /// on EVERY PR -- not only in the dedicated, perf-gated Allocation scope.
    /// </para>
    /// <para>
    /// SCOPE (honest): this pins the DIAGNOSTICS-specific lazy win only. It deliberately does
    /// not set an absolute allocation budget for the token object, its initial slot arena, or
    /// other non-diagnostics construction state because warm-editor ambient allocation makes
    /// that floor unenforceable. Non-diagnostics construction remains covered by the cold CI
    /// benchmark legs in a fresh process.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class RegistrationDiagnosticsLazyAllocationTests
    {
        private static readonly InstanceId Owner = new InstanceId(0x7A7A_7A7A);

        private DiagnosticsTarget _savedDiagnostics;
        private int _savedBufferSize;

        [SetUp]
        public void SaveDiagnostics()
        {
            _savedDiagnostics = IMessageBus.GlobalDiagnosticsTargets;
            _savedBufferSize = IMessageBus.GlobalMessageBufferSize;
        }

        [TearDown]
        public void RestoreDiagnostics()
        {
            IMessageBus.GlobalDiagnosticsTargets = _savedDiagnostics;
            IMessageBus.GlobalMessageBufferSize = _savedBufferSize;
        }

        // Both the common player default (Off) and the diagnostics-enabled case (All): the
        // diagnostics collections must materialize only at first dispatch, so Create allocates
        // neither under EITHER setting. Running All too makes the diagnostics state load-bearing
        // -- it proves the null is a Create-time laziness property, not a side effect of Off.
        [Test]
        public void TokenCreateDoesNotEagerlyAllocateDiagnosticsCollections(
            [Values(DiagnosticsTarget.Off, DiagnosticsTarget.All)] DiagnosticsTarget diagnostics
        )
        {
            IMessageBus.GlobalDiagnosticsTargets = diagnostics;

            FieldInfo callCountsBacking = typeof(MessageRegistrationToken).GetField(
                "_callCountsBacking",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo emissionBufferBacking = typeof(MessageRegistrationToken).GetField(
                "_emissionBufferBacking",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.That(
                callCountsBacking,
                Is.Not.Null,
                "MessageRegistrationToken._callCountsBacking was renamed or removed. The "
                    + "diagnostics _callCounts dictionary must stay a lazily-materialized backing "
                    + "field (the _callCounts property's ??=). If it was renamed, update this guard; "
                    + "if it became an eager `= new()` field, that reverts the token-create "
                    + "allocation win (11 -> 7)."
            );
            Assert.That(
                emissionBufferBacking,
                Is.Not.Null,
                "MessageRegistrationToken._emissionBufferBacking was renamed or removed. The "
                    + "diagnostics _emissionBuffer cyclic buffer must stay a lazily-materialized "
                    + "backing field (the _emissionBuffer property's ??=). If it was renamed, update "
                    + "this guard; if it became an eager `= new()` field, that reverts the "
                    + "token-create allocation win (11 -> 7)."
            );

            MessageHandler handler = new MessageHandler(Owner) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler);

            Assert.That(
                callCountsBacking.GetValue(token),
                Is.Null,
                "MessageRegistrationToken.Create eagerly allocated the diagnostics _callCounts "
                    + $"dictionary (GlobalDiagnosticsTargets = {diagnostics}). It must materialize "
                    + "only at first dispatch (the _callCounts property's ??=), never at Create -- "
                    + "regardless of the diagnostics setting -- so a token whose owner never enables "
                    + "diagnostics pays nothing for it; eager allocation re-adds one managed "
                    + "allocation to every token Create."
            );
            Assert.That(
                emissionBufferBacking.GetValue(token),
                Is.Null,
                "MessageRegistrationToken.Create eagerly allocated the diagnostics _emissionBuffer "
                    + $"cyclic buffer and its two backing lists (GlobalDiagnosticsTargets = {diagnostics}). "
                    + "It must materialize only at first dispatch (the _emissionBuffer property's ??=), "
                    + "never at Create -- regardless of the diagnostics setting -- so a token whose "
                    + "owner never enables diagnostics pays nothing for it; eager allocation re-adds "
                    + "several managed allocations to every token Create."
            );
        }

        [Test]
        public void BusConstructionDoesNotEagerlyAllocateDiagnosticsStorage(
            [Values(DiagnosticsTarget.Off, DiagnosticsTarget.All)] DiagnosticsTarget diagnostics
        )
        {
            IMessageBus.GlobalDiagnosticsTargets = diagnostics;
            FieldInfo logBacking = typeof(MessageBus).GetField(
                "_log",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo emissionBufferBacking = typeof(MessageBus).GetField(
                "_emissionBufferBacking",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo finalizedRegistrations = typeof(RegistrationLog).GetField(
                "_finalizedRegistrations",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.That(logBacking, Is.Not.Null);
            Assert.That(emissionBufferBacking, Is.Not.Null);
            Assert.That(finalizedRegistrations, Is.Not.Null);

            MessageBus bus = new MessageBus();
            Assert.That(
                logBacking.GetValue(bus),
                Is.Null,
                "MessageBus construction must not allocate its disabled registration log."
            );
            Assert.That(
                emissionBufferBacking.GetValue(bus),
                Is.Null,
                "MessageBus construction must not allocate its diagnostics emission buffer."
            );

            RegistrationLog log = bus.Log;
            Assert.That(log, Is.SameAs(bus.Log), "The lazily-created Log must remain stable.");
            Assert.That(
                finalizedRegistrations.GetValue(log),
                Is.Null,
                "Accessing the log object alone must not allocate its registration list."
            );

            IReadOnlyList<MessagingRegistration> registrations = log.Registrations;
            Assert.That(registrations, Is.Empty);
            Assert.That(
                finalizedRegistrations.GetValue(log),
                Is.SameAs(registrations),
                "The first Registrations read must materialize the stable live list."
            );
        }

        [Test]
        public void BusEmissionBufferMaterializesOnlyWhenDiagnosticsRecordAnEmission()
        {
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
            FieldInfo emissionBufferBacking = typeof(MessageBus).GetField(
                "_emissionBufferBacking",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            MessageBus bus = new MessageBus { DiagnosticsMode = false };
            LazyBusDiagnosticsMessage message = default;

            bus.UntargetedBroadcast(ref message);
            Assert.That(
                emissionBufferBacking.GetValue(bus),
                Is.Null,
                "Diagnostics-off dispatch must not materialize emission history."
            );

            bus.DiagnosticsMode = true;
            bus.UntargetedBroadcast(ref message);
            Assert.That(
                emissionBufferBacking.GetValue(bus),
                Is.Not.Null,
                "The first diagnostics-enabled emission must materialize emission history."
            );
        }

        [Test]
        public void LazyEmissionBufferPreservesConstructionTimeCapacity()
        {
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
            IMessageBus.GlobalMessageBufferSize = 2;
            FieldInfo emissionBufferBacking = typeof(MessageBus).GetField(
                "_emissionBufferBacking",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            MessageBus bus = new MessageBus { DiagnosticsMode = true };

            IMessageBus.GlobalMessageBufferSize = 7;
            LazyBusDiagnosticsMessage message = default;
            bus.UntargetedBroadcast(ref message);

            CyclicBuffer<MessageEmissionData> buffer =
                (CyclicBuffer<MessageEmissionData>)emissionBufferBacking.GetValue(bus);
            Assert.That(buffer, Is.Not.Null);
            Assert.That(
                buffer.Capacity,
                Is.EqualTo(2),
                "Lazy materialization must preserve the capacity captured when this bus was constructed."
            );
        }

        private readonly struct LazyBusDiagnosticsMessage : IUntargetedMessage { }
    }
}
#endif
