#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Editor.Benchmarks;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Locks in two registration-path allocation reductions:
    /// <list type="bullet">
    /// <item><description>
    /// Token creation dropped from 11 to 7 managed-allocation calls once the
    /// diagnostics-only <c>_callCounts</c> dictionary and <c>_emissionBuffer</c> cyclic
    /// buffer (plus the buffer's two backing lists) became lazily allocated rather than
    /// eager per-token fields. Guarded DETERMINISTICALLY by
    /// <c>RegistrationDiagnosticsLazyAllocationTests.TokenCreateDoesNotEagerlyAllocateDiagnosticsCollections</c>
    /// (in the per-PR EditMode correctness leg): it asserts the lazy backing fields are
    /// <c>null</c> after <c>Create</c> rather than measuring a <c>GC.Alloc</c> count. The
    /// original absolute-count budget for this win was inherently warm-editor-flaky -- the
    /// recorder's ambient-noise floor (~19 measured for a 7-allocation operation) sits above
    /// any budget tight enough to catch the revert -- so it was replaced by the deterministic
    /// state assertion. See that test's remarks for the measured distribution.
    /// </description></item>
    /// <item><description>
    /// Marginal registration dropped by one managed-allocation call per registration
    /// once the per-registration <c>Func&lt;MessageRegistrationMetadata&gt;</c> factory
    /// (which was invoked immediately, so it only cost a closure) became a by-value
    /// struct. Guarded structurally by
    /// <c>RegistrationStorageStructuralGuardTests.InternalRegisterPassesMetadataByValueNotFactory</c>
    /// (deterministic, backend-independent -- in the per-PR EditMode correctness leg) and
    /// bounded by <see cref="MarginalRegistrationAllocationCountIsBounded"/>.
    /// </description></item>
    /// <item><description>
    /// <see cref="Action{T}"/> registration dropped its extra adapter closure once the
    /// token folded diagnostics into a single by-ref <c>FastHandler&lt;T&gt;</c> flat
    /// invoker (rather than an Action wrapper PLUS a separately allocated FastHandler
    /// adapter). Guarded by
    /// <see cref="ActionRegistrationAllocatesNoMoreClosuresThanFastHandler"/>, which
    /// pins the Action path to the FastHandler path's closure count.
    /// </description></item>
    /// <item><description>
    /// <see cref="Action{T}"/> post-processor registration dropped the same extra
    /// adapter closure once the token folded diagnostics into a single by-ref
    /// <c>FastHandler&lt;T&gt;</c> flat invoker for the post-process default slot.
    /// Guarded by
    /// <see cref="ActionPostProcessorAllocatesNoMoreClosuresThanFastHandler"/>, which
    /// pins the Action post-processor path to the FastHandler post-processor path's
    /// closure count.
    /// </description></item>
    /// <item><description>
    /// Every registration kind dropped about two managed-allocation calls once the
    /// token collapsed its per-handle staging <c>Func</c> (display class + delegate) and
    /// its nested <c>AugmentedHandler</c> local-function delegate into a single unified
    /// per-handle <c>Registration</c> object stored in <c>_registrations</c> (paired with
    /// its handle in the replay queue). Guarded structurally by
    /// <c>RegistrationStorageStructuralGuardTests.RegistrationsStoreUnifiedRegistrationObjectNotFuncOrAction</c>
    /// (deterministic, backend-independent -- in the per-PR EditMode correctness leg).
    /// </description></item>
    /// </list>
    /// <para>
    /// The counting rows use <see cref="AllocationProbe"/> (the <c>GC.Alloc</c> profiler
    /// recorder) rather than a <c>GC.GetTotalMemory</c> byte delta, which is vacuously
    /// 0 under Unity's Boehm GC. Diagnostics are forced OFF (the default
    /// <see cref="DiagnosticsTarget.Off"/> and the common player case) so the budgets
    /// reflect production cost. Token creation is a deterministic fixed set of field
    /// allocations, so its budget is tight; the per-registration window also touches the
    /// bus-side flat-dispatch arrays, whose incidental warm-domain churn is absorbed by a
    /// looser bound (the precise per-registration win is pinned by the structural test).
    /// </para>
    /// </summary>
    [Category("Allocation")]
    public sealed class RegistrationAllocationCountTests : BenchmarkTestBase
    {
        private static readonly InstanceId Owner = new InstanceId(0x7A7A_7A7A);
        private static readonly InstanceId PostProcessorTarget = new InstanceId(0x5151_5151);

        // Warm to a count that grows the per-token registration dictionaries AND the
        // bus-side per-type handler arrays past the measured window; the settle batch
        // then absorbs any capacity-boundary resize so the measured window pays only the
        // steady per-registration cost.
        private const int WarmupRegistrations = 512;
        private const int SettleRegistrations = 64;
        private const int MeasuredRegistrations = 16;

        // Attempts for AllocationProbe.MeasureMin: a single allocation window in a warm,
        // long-lived editor domain intermittently spikes above the true cost, so we take
        // the minimum over several attempts (see AllocationProbe.MeasureMin).
        private const int MinAttempts = 8;

        // ~14 measured per registration post-change (224 over 16). The window also pays
        // bus-side flat-array growth whose warm-domain count varies, so this is a
        // gross-regression tripwire (20/registration), not a 1-call-precise bound -- the
        // structural test pins the exact metadata-closure removal. 20 (not 16) leaves
        // margin over the explicitly-varying ~224 floor so a boundary resize landing in
        // every attempt's window cannot false-fail it.
        private const long MarginalRegistrationBudget = MeasuredRegistrations * 20;

        private DiagnosticsTarget _savedDiagnostics;

        protected override bool MessagingDebugEnabled => false;

        [SetUp]
        public void ForceDiagnosticsOff()
        {
            _savedDiagnostics = IMessageBus.GlobalDiagnosticsTargets;
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
        }

        [TearDown]
        public void RestoreDiagnostics()
        {
            IMessageBus.GlobalDiagnosticsTargets = _savedDiagnostics;
        }

        private static void NoOp(ref SimpleUntargetedMessage message) { }

        private static void NoOpAction(SimpleUntargetedMessage message) { }

        private static void NoOpTargetedPostProcessor(ref SimpleTargetedMessage message) { }

        private static void NoOpTargetedActionPostProcessor(SimpleTargetedMessage message) { }

        private static MessageBus NewBus()
        {
            return MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
        }

        /// <summary>
        /// Pins the registration-closure collapse: an <see cref="Action{T}"/>
        /// registration must allocate no more closures than the by-ref
        /// <c>FastHandler&lt;T&gt;</c> registration. The token folds diagnostics into a
        /// single <c>FastHandler</c> flat invoker and hands it to the internal
        /// flat-invoker registration overload, so the default slot no longer pays
        /// for an <see cref="Action{T}"/> wrapper PLUS a separately allocated
        /// FastHandler adapter (the pre-collapse cost was one extra closure per
        /// registration). Both paths register the same handler shape (a static
        /// method group, so neither path allocates the user delegate inside the
        /// window) into independent buses; the difference is therefore exactly the
        /// per-registration closure count.
        /// <para>
        /// Measured as the minimum over attempts of (Action batch - FastHandler
        /// batch): the minimum filters warm-editor spikes (a spike inflates a
        /// single window, never the floor) and the differential cancels the
        /// bus-side flat-array churn shared by both paths, leaving the structural
        /// closure delta. A regression that re-adds the adapter would push the
        /// Action batch a full <see cref="MeasuredRegistrations"/> calls above the
        /// FastHandler batch, well past the half-window tolerance.
        /// </para>
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void ActionRegistrationAllocatesNoMoreClosuresThanFastHandler()
        {
            MessageBus actionBus = NewBus();
            MessageHandler actionHandler = new MessageHandler(Owner, actionBus) { active = true };
            MessageRegistrationToken actionToken = MessageRegistrationToken.Create(
                actionHandler,
                actionBus
            );
            actionToken.Enable();

            MessageBus fastBus = NewBus();
            MessageHandler fastHandler = new MessageHandler(Owner, fastBus) { active = true };
            MessageRegistrationToken fastToken = MessageRegistrationToken.Create(
                fastHandler,
                fastBus
            );
            fastToken.Enable();

            // Warm both paths past their capacity-boundary resizes so the measured
            // batches pay only the steady per-registration cost.
            for (int i = 0; i < WarmupRegistrations + SettleRegistrations; ++i)
            {
                _ = actionToken.RegisterUntargeted<SimpleUntargetedMessage>(NoOpAction);
                _ = fastToken.RegisterUntargeted<SimpleUntargetedMessage>(NoOp);
            }

            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
            }

            long bestDelta = long.MaxValue;
            for (int attempt = 0; attempt < MinAttempts; ++attempt)
            {
                long fastCount = AllocationProbe.Measure(() =>
                {
                    for (int i = 0; i < MeasuredRegistrations; ++i)
                    {
                        _ = fastToken.RegisterUntargeted<SimpleUntargetedMessage>(NoOp);
                    }
                });
                long actionCount = AllocationProbe.Measure(() =>
                {
                    for (int i = 0; i < MeasuredRegistrations; ++i)
                    {
                        _ = actionToken.RegisterUntargeted<SimpleUntargetedMessage>(NoOpAction);
                    }
                });
                if (
                    fastCount == AllocationProbe.Unmeasured
                    || actionCount == AllocationProbe.Unmeasured
                )
                {
                    Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
                }
                bestDelta = Math.Min(bestDelta, actionCount - fastCount);
            }

            // Tolerance is half the window: the collapsed delta is ~0, while a
            // re-introduced adapter would add a full MeasuredRegistrations of extra
            // closures (one per registration), so half the window cleanly separates
            // "collapsed" from "regressed" without tripping on warm-editor noise.
            long tolerance = MeasuredRegistrations / 2;
            Assert.That(
                bestDelta,
                Is.LessThanOrEqualTo(tolerance),
                $"Action<T> registration allocated {bestDelta} more managed objects than the "
                    + $"FastHandler<T> registration over {MeasuredRegistrations} registrations "
                    + $"(tolerance {tolerance}). The token must fold diagnostics into a single "
                    + "FastHandler flat invoker (the internal flat-invoker registration overload) "
                    + "so the default slot does not allocate an Action wrapper plus a separate "
                    + "FastHandler adapter."
            );
        }

        /// <summary>
        /// Pins the post-processor registration-closure collapse: an
        /// <see cref="Action{T}"/> post-processor registration must allocate no more
        /// closures than the by-ref <c>FastHandler&lt;T&gt;</c> post-processor
        /// registration. The token folds diagnostics into a single <c>FastHandler</c>
        /// flat invoker and hands it to the internal flat-invoker post-processor overload
        /// (one per family: targeted, targeted-without-targeting, broadcast,
        /// broadcast-without-source), so the default post-process slot no longer pays for
        /// an <see cref="Action{T}"/> wrapper PLUS a separately allocated FastHandler
        /// adapter. This mirrors
        /// <see cref="ActionRegistrationAllocatesNoMoreClosuresThanFastHandler"/> for the
        /// post-processor paths; the targeted post-processor exercises the
        /// <c>FastHandler&lt;T&gt;</c> collapse and the same internal-overload pattern the
        /// other three families share.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void ActionPostProcessorAllocatesNoMoreClosuresThanFastHandler()
        {
            MessageBus actionBus = NewBus();
            MessageHandler actionHandler = new MessageHandler(Owner, actionBus) { active = true };
            MessageRegistrationToken actionToken = MessageRegistrationToken.Create(
                actionHandler,
                actionBus
            );
            actionToken.Enable();

            MessageBus fastBus = NewBus();
            MessageHandler fastHandler = new MessageHandler(Owner, fastBus) { active = true };
            MessageRegistrationToken fastToken = MessageRegistrationToken.Create(
                fastHandler,
                fastBus
            );
            fastToken.Enable();

            // Warm both paths past their capacity-boundary resizes so the measured
            // batches pay only the steady per-registration cost.
            for (int i = 0; i < WarmupRegistrations + SettleRegistrations; ++i)
            {
                _ = actionToken.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                    PostProcessorTarget,
                    NoOpTargetedActionPostProcessor
                );
                _ = fastToken.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                    PostProcessorTarget,
                    NoOpTargetedPostProcessor
                );
            }

            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
            }

            long bestDelta = long.MaxValue;
            for (int attempt = 0; attempt < MinAttempts; ++attempt)
            {
                long fastCount = AllocationProbe.Measure(() =>
                {
                    for (int i = 0; i < MeasuredRegistrations; ++i)
                    {
                        _ = fastToken.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                            PostProcessorTarget,
                            NoOpTargetedPostProcessor
                        );
                    }
                });
                long actionCount = AllocationProbe.Measure(() =>
                {
                    for (int i = 0; i < MeasuredRegistrations; ++i)
                    {
                        _ = actionToken.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                            PostProcessorTarget,
                            NoOpTargetedActionPostProcessor
                        );
                    }
                });
                if (
                    fastCount == AllocationProbe.Unmeasured
                    || actionCount == AllocationProbe.Unmeasured
                )
                {
                    Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
                }
                bestDelta = Math.Min(bestDelta, actionCount - fastCount);
            }

            long tolerance = MeasuredRegistrations / 2;
            Assert.That(
                bestDelta,
                Is.LessThanOrEqualTo(tolerance),
                $"Action<T> targeted post-processor registration allocated {bestDelta} more managed "
                    + $"objects than the FastHandler<T> post-processor registration over "
                    + $"{MeasuredRegistrations} registrations (tolerance {tolerance}). The token must "
                    + "fold diagnostics into a single FastHandler flat invoker (the internal "
                    + "flat-invoker post-processor overload) so the default post-process slot does not "
                    + "allocate an Action wrapper plus a separate FastHandler adapter."
            );
        }

        [Test]
        [Category("Allocation")]
        public void MarginalRegistrationAllocationCountIsBounded()
        {
            MessageBus bus = NewBus();
            MessageHandler handler = new MessageHandler(Owner, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            for (int i = 0; i < WarmupRegistrations + SettleRegistrations; ++i)
            {
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(NoOp);
            }

            // Each attempt registers another MeasuredRegistrations handlers (they
            // accumulate); the minimum skips the attempts where a bus-array resize lands
            // in the window, leaving the steady per-registration floor.
            long registrationCount = AllocationProbe.MeasureMin(
                MinAttempts,
                prepare: null,
                operation: () =>
                {
                    for (int i = 0; i < MeasuredRegistrations; ++i)
                    {
                        _ = token.RegisterUntargeted<SimpleUntargetedMessage>(NoOp);
                    }
                }
            );

            if (registrationCount == AllocationProbe.Unmeasured)
            {
                Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
            }

            Assert.That(
                registrationCount,
                Is.LessThanOrEqualTo(MarginalRegistrationBudget),
                $"{MeasuredRegistrations} registrations allocated {registrationCount} managed "
                    + $"objects ({registrationCount / (double)MeasuredRegistrations:0.00}/registration); "
                    + $"budget is {MarginalRegistrationBudget}. A gross regression here means a new "
                    + "per-registration allocation; the by-value metadata is pinned separately by "
                    + "RegistrationStorageStructuralGuardTests."
                    + "InternalRegisterPassesMetadataByValueNotFactory (per-PR EditMode leg)."
            );
        }
    }
}
#endif
