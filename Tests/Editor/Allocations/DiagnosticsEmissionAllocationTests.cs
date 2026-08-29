#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Editor.Benchmarks;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Pins the cost boundary of diagnostic emission-site capture (issue #433).
    ///
    /// <para>
    /// Every diagnostic record used to capture and post-process a full managed stack trace in its
    /// constructor, and a single-subscriber emission writes two records (bus plus token delivery).
    /// Measured in Unity 6000.4.6f1 Editor PlayMode Mono, that cost about 236 microseconds and ~67
    /// allocation calls PER RECORD, which dropped the
    /// <c>Comparison_DxMessaging_GlobalToOne</c> row from tens of millions of emits per second to
    /// about 1,100 -- roughly 305,000x slower than a plain C# event measured in the same session.
    /// </para>
    ///
    /// <para>
    /// The guard is DIFFERENTIAL rather than an absolute budget: absolute allocation counts in a
    /// warm editor domain carry ambient noise, but the ratio between capture-on and capture-off is
    /// dominated by the stack capture and is stable. If capture stops being opt-in, the two windows
    /// converge and this fails.
    /// </para>
    /// </summary>
    [Category("Allocation")]
    public sealed class DiagnosticsEmissionAllocationTests : BenchmarkTestBase
    {
        private const int WarmupEmits = 256;
        private const int MeasuredEmits = 64;
        private const int MinAttempts = 8;

        // Capture-off must remove at least this share of the capture-on allocation calls. Measured
        // ~97% removed (about 134 calls per emit down to a handful of boxes and buffer writes); the
        // threshold leaves a wide margin over backend noise while still failing outright if capture
        // becomes unconditional again (ratio 1.0).
        private const double MinimumRemovedShare = 0.75d;

        protected override bool MessagingDebugEnabled => false;

        [Test]
        public void DisablingEmissionSiteCaptureRemovesMostDiagnosticAllocations()
        {
            long captureOn = MeasureEmitAllocations(captureStackTraces: true);
            long captureOff = MeasureEmitAllocations(captureStackTraces: false);

            if (captureOn == AllocationProbe.Unmeasured || captureOff == AllocationProbe.Unmeasured)
            {
                Assert.Ignore("GC.Alloc allocation probe is non-functional on this backend.");
            }

            Assert.That(
                captureOn,
                Is.GreaterThan(0L),
                "Capture-on emissions must allocate; otherwise the comparison proves nothing."
            );

            double removedShare = 1d - ((double)captureOff / captureOn);
            Assert.That(
                removedShare,
                Is.GreaterThanOrEqualTo(MinimumRemovedShare),
                $"{MeasuredEmits} emissions allocated {captureOn} managed allocation calls with "
                    + $"emission-site capture on and {captureOff} with it off ({removedShare:P1} "
                    + "removed). Emission-site capture must stay opt-in: it captures and trims a "
                    + "full managed stack trace per diagnostic record, and every emission writes at "
                    + "least two records."
            );
        }

        private static long MeasureEmitAllocations(bool captureStackTraces)
        {
            using DiagnosticsScope scope = new(
                DiagnosticsTarget.All,
                diagnosticsStackTraces: captureStackTraces
            );

            MessageBus bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            bus.DiagnosticsMode = true;
            MessageHandler handler = new(Owner, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = true;
            token.Enable();
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(NoOp);

            SimpleUntargetedMessage message = new();
            for (int i = 0; i < WarmupEmits; ++i)
            {
                message.EmitUntargeted(bus);
            }

            long measured = AllocationProbe.MeasureMin(
                MinAttempts,
                prepare: null,
                operation: () =>
                {
                    SimpleUntargetedMessage emitted = new();
                    for (int i = 0; i < MeasuredEmits; ++i)
                    {
                        emitted.EmitUntargeted(bus);
                    }
                }
            );

            token.UnregisterAll();
            token.Dispose();
            handler.active = false;
            return measured;
        }

        private static readonly InstanceId Owner = new InstanceId(0x4433_4433);

        private static void NoOp(in SimpleUntargetedMessage message) { }
    }
}
#endif
