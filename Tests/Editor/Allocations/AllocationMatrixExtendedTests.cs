#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Editor.Benchmarks;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Extends <see cref="AllocationMatrixTests"/> with rows that the existing
    /// fixture intentionally skips: class-message dispatch (boxed reference
    /// type), long-running registration churn, global accept-all dispatch,
    /// and the cross-product of interceptor/post-processor presence per kind.
    /// All tests are gated behind <c>[Category("Allocation")]</c> so they do
    /// not run in the default suite and the wall-clock budget remains within
    /// the 60-second target.
    /// </summary>
    [Category("Allocation")]
    public sealed class AllocationMatrixExtendedTests : BenchmarkTestBase
    {
        private const int RegistrationChurnCycles = 1_000;

        private static readonly InstanceId StableTarget = new InstanceId(0x4242_5757);
        private static readonly InstanceId StableSource = new InstanceId(0x6464_3232);
        private static readonly InstanceId HandlerOwner = new InstanceId(0x1313_8989);

        private DiagnosticsScope _diagnosticsScope;
        private Action<LogLevel, string> _savedLogFunction;

        protected override bool MessagingDebugEnabled => false;

        [SetUp]
        public void CaptureDiagnosticsState()
        {
            _diagnosticsScope = new DiagnosticsScope(diagnosticsTargets: DiagnosticsTarget.Off);
            _savedLogFunction = MessagingDebug.LogFunction;
            MessagingDebug.LogFunction = null;
        }

        [TearDown]
        public void RestoreDiagnosticsState()
        {
            _diagnosticsScope?.Dispose();
            _diagnosticsScope = null;
            MessagingDebug.LogFunction = _savedLogFunction;
        }

        /// <summary>
        /// Pins zero-allocation emission for class-typed (reference) messages.
        /// Class messages reuse the same emit path as struct messages (the
        /// instance is a long-lived field captured in the closure), so the
        /// dispatch loop must not allocate.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitClassMessageIsZeroAlloc(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildClassEmitClosure(scenario, bus);
                    RegisterClassHandler(scenario, token);
                    AllocationAssertions.AssertNoAllocations($"EmitClass-{scenario.Kind}", emit);
                }
            );
        }

        /// <summary>
        /// Pins zero-allocation emission across the joint distribution of
        /// interceptor presence x post-processor presence x kind, restricted
        /// to combinations where at least one feature is enabled. The
        /// (interceptor=false, post-processor=false) baseline is the bare
        /// emit path already pinned by
        /// <see cref="AllocationMatrixTests.EmitIsZeroAlloc"/>; the existing
        /// per-axis tests in <see cref="AllocationMatrixTests"/> cover each
        /// feature in isolation. This row exists to catch interaction-only
        /// regressions when both features are wired in together.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void AllocationAcrossInterceptorAndPostProcessor(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAtLeastOneFeatureToggle)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterStructHandler(scenario, token);
                    if (scenario.UseInterceptor)
                    {
                        RegisterAllowingInterceptor(scenario, token);
                    }
                    if (scenario.UsePostProcessor)
                    {
                        RegisterPostProcessor(scenario, token);
                    }

                    string interceptorTag = scenario.UseInterceptor ? "I+" : "I-";
                    string postProcessorTag = scenario.UsePostProcessor ? "P+" : "P-";
                    AllocationAssertions.AssertNoAllocations(
                        $"EmitInterceptorPostProcessor-{interceptorTag}{postProcessorTag}-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Long-running registration churn: 1000 register/emit/unregister
        /// cycles. After warm-up the per-cycle allocation budget must scale
        /// linearly with the registration cycles (not super-linearly), so
        /// the total measured budget is bounded.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void LongRunningRegistrationChurn1000Cycles(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Budget: per-cycle worst case is dominated by closure +
            // dictionary churn (a single registration costs on the order of a few
            // hundred bytes -- see the count-based budgets in AllocationMatrixTests).
            // We allow 1.5x that ceiling per cycle to absorb interim
            // dictionary resizing across the longer run. The byte figures are
            // retained for documentation only; the assertion uses a managed
            // allocation CALL count via AllocationProbe because
            // GC.GetAllocatedBytesForCurrentThread() returns 0 for every
            // allocation under Unity's Boehm GC and cannot catch a regression.
            const long PerCycleBudgetBytes = 768L;
            long totalBudget = PerCycleBudgetBytes * RegistrationChurnCycles;

            // Count budget = ceil(byteBudget / 16); 16 = minimum managed object
            // size on 64-bit (8-byte header + 8-byte minimum payload), i.e. the
            // most distinct objects that could fit in the byte budget. Generous
            // by construction so incidental allocations never false-fail, while a
            // gross per-cycle regression still trips it.
            long totalCountBudget = (totalBudget + 15L) / 16L;

            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);

                    // Warm: a few cycles to settle dictionary capacity.
                    for (int i = 0; i < 32; ++i)
                    {
                        MessageRegistrationHandle warm = RegisterStructHandler(scenario, token);
                        emit();
                        token.RemoveRegistration(warm);
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    using AllocationProbe.Window window = AllocationProbe.BeginWindow();

                    for (int i = 0; i < RegistrationChurnCycles; ++i)
                    {
                        MessageRegistrationHandle handle = RegisterStructHandler(scenario, token);
                        emit();
                        token.RemoveRegistration(handle);
                    }

                    long gcAllocations = window.Sample();
                    if (gcAllocations == AllocationProbe.Unmeasured)
                    {
                        Assert.Ignore(
                            $"RegistrationChurn-{scenario.Kind}: the GC.Alloc allocation "
                                + "probe is non-functional on this backend, so the churn "
                                + "allocation budget cannot be evaluated."
                        );
                    }

                    // Always log the per-cycle average so a passing run still
                    // surfaces the baseline (useful when tightening the budget
                    // later). On failure the per-cycle figure is the actionable
                    // signal a maintainer needs to decide whether the regression
                    // is per-cycle or a one-off resize.
                    long perCycleAvg = gcAllocations / RegistrationChurnCycles;
                    UnityEngine.Debug.Log(
                        $"RegistrationChurn-{scenario.Kind}: {gcAllocations} GC allocations / "
                            + $"{RegistrationChurnCycles} cycles = {perCycleAvg} avg/cycle "
                            + $"(count budget total {totalCountBudget})."
                    );

                    Assert.That(
                        gcAllocations,
                        Is.LessThanOrEqualTo(totalCountBudget),
                        $"RegistrationChurn-{scenario.Kind} allocated {gcAllocations} GC "
                            + $"allocations across {RegistrationChurnCycles} cycles "
                            + $"({perCycleAvg} avg/cycle), "
                            + $"exceeding the count budget of {totalCountBudget}."
                    );
                }
            );
        }

        /// <summary>
        /// Pins zero-allocation steady-state emission for the global accept-all
        /// dispatch path. The global accept-all delegate signatures take
        /// <c>ref IUntargetedMessage</c> / <c>ref ITargetedMessage</c> /
        /// <c>ref IBroadcastMessage</c>, so emitting a struct message under
        /// a registered global accept-all forces a struct-to-interface box
        /// at the dispatch site
        /// (<c>MessageBus.cs</c> lines 1290, 1471, 2485). That box is
        /// structural to the API and cannot be eliminated without changing
        /// the global-handler signature, so this test is restricted to
        /// class-typed (reference) messages, where no boxing occurs and the
        /// dispatch loop's own zero-allocation contract is the property
        /// under test. The struct-message budget for the same path is
        /// pinned separately by
        /// <see cref="GlobalAcceptAllStructMessageBudgetIsBounded"/>.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void GlobalAcceptAllAllocationIsZeroSteadyState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    // Class messages are reference types, so the dispatch
                    // path's <c>IUntargetedMessage</c>/<c>ITargetedMessage</c>/
                    // <c>IBroadcastMessage</c> interface upcast is a pointer
                    // copy rather than a box. See the docstring above.
                    Action emit = BuildClassEmitClosure(scenario, bus);
                    RegisterClassHandler(scenario, token);
                    _ = token.RegisterGlobalAcceptAll(
                        AcceptAllUntargeted,
                        AcceptAllTargeted,
                        AcceptAllBroadcast
                    );
                    AllocationAssertions.AssertNoAllocations(
                        $"EmitGlobalAcceptAll-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Pins a per-emit upper bound on the struct-message global accept-all
        /// dispatch path. Emitting a struct message under a registered global
        /// accept-all incurs an unavoidable struct-to-interface box at the
        /// dispatch site (<c>MessageBus.cs</c> lines 1290, 1471, 2485) -- the
        /// box is structural to the API. This test documents that cost as a
        /// bounded budget rather than a zero-allocation contract so a future
        /// regression that adds more allocations on the same path is still
        /// caught.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void GlobalAcceptAllStructMessageBudgetIsBounded(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // 128 bytes/emit covers struct-to-interface boxes up to 64 bytes
            // plus 64 bytes of per-emit overhead for any future bookkeeping;
            // 64 bytes was at the edge of safety relative to current struct
            // sizes, so 128 provides meaningful margin for field growth. These
            // byte figures are retained for documentation only; the assertion
            // uses a managed allocation CALL count via AllocationProbe because
            // GC.GetAllocatedBytesForCurrentThread() returns 0 for every
            // allocation under Unity's Boehm GC and cannot catch a regression.
            const long PerEmitBudgetBytes = 128L;
            long totalBudget = PerEmitBudgetBytes * AllocationAssertions.DefaultMeasuredIterations;

            // Count budget = ceil(byteBudget / 16); 16 = minimum managed object
            // size on 64-bit (8-byte header + 8-byte minimum payload). Generous
            // by construction so incidental allocations never false-fail, while a
            // gross per-emit regression on the boxing path still trips it.
            long totalCountBudget = (totalBudget + 15L) / 16L;

            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterStructHandler(scenario, token);
                    _ = token.RegisterGlobalAcceptAll(
                        AcceptAllUntargeted,
                        AcceptAllTargeted,
                        AcceptAllBroadcast
                    );

                    // Warm: settle any one-shot allocations (delegate caches,
                    // dictionary capacity) before measurement.
                    for (int i = 0; i < AllocationAssertions.DefaultMeasuredIterations; ++i)
                    {
                        emit();
                    }

                    // Take the minimum allocation over several attempts: the boxing count
                    // per emit is real, but a single warm-editor window intermittently
                    // spikes above it (see AllocationProbe.MeasureMin). 8 attempts read the
                    // boxing floor reliably; the operation just re-runs the measured emit
                    // batch.
                    long gcAllocations = AllocationProbe.MeasureMin(
                        8,
                        prepare: null,
                        operation: () =>
                        {
                            for (int i = 0; i < AllocationAssertions.DefaultMeasuredIterations; ++i)
                            {
                                emit();
                            }
                        }
                    );
                    if (gcAllocations == AllocationProbe.Unmeasured)
                    {
                        Assert.Ignore(
                            $"GlobalAcceptAllStruct-{scenario.Kind}: the GC.Alloc "
                                + "allocation probe is non-functional on this backend, so "
                                + "the boxing budget cannot be evaluated."
                        );
                    }
                    long perEmit = gcAllocations / AllocationAssertions.DefaultMeasuredIterations;

                    // Always log the measured per-emit cost so a passing run
                    // still surfaces the baseline. Future maintainers can use
                    // the printed figure to decide whether the budget can be
                    // tightened.
                    UnityEngine.Debug.Log(
                        $"GlobalAcceptAllStruct-{scenario.Kind}: {gcAllocations} GC allocations / "
                            + $"{AllocationAssertions.DefaultMeasuredIterations} emissions = "
                            + $"{perEmit} avg/emit (count budget total {totalCountBudget})."
                    );

                    Assert.That(
                        gcAllocations,
                        Is.LessThanOrEqualTo(totalCountBudget),
                        $"GlobalAcceptAllStruct-{scenario.Kind} allocated {gcAllocations} GC "
                            + $"allocations ({perEmit} avg/emit) across "
                            + $"{AllocationAssertions.DefaultMeasuredIterations} emissions, "
                            + $"exceeding the count budget of {totalCountBudget}."
                    );
                }
            );
        }

        private void RunWithFreshHarness(
            MessageScenario scenario,
            Action<MessageRegistrationToken, MessageBus> body
        )
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            try
            {
                token.Enable();
                body(token, bus);
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        private static MessageRegistrationHandle RegisterStructHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        NoOpUntargeted,
                        priority: priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        StableTarget,
                        NoOpTargeted,
                        priority: priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        StableSource,
                        NoOpBroadcast,
                        priority: priority
                    );
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static MessageRegistrationHandle RegisterClassHandler(
            MessageScenario scenario,
            MessageRegistrationToken token
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<ClassUntargetedMessage>(NoOpClassUntargeted);
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<ClassTargetedMessage>(
                        StableTarget,
                        NoOpClassTargeted
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<ClassBroadcastMessage>(
                        StableSource,
                        NoOpClassBroadcast
                    );
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static MessageRegistrationHandle RegisterAllowingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        AllowUntargeted
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        AllowTargeted
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        AllowBroadcast
                    );
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static MessageRegistrationHandle RegisterPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        NoOpUntargeted
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        StableTarget,
                        NoOpTargeted
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        StableSource,
                        NoOpBroadcast
                    );
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static Action BuildEmitClosure(MessageScenario scenario, IMessageBus bus)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage untargeted = new SimpleUntargetedMessage();
                    return () => untargeted.EmitUntargeted(bus);
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage targeted = new SimpleTargetedMessage();
                    InstanceId target = StableTarget;
                    return () => targeted.EmitTargeted(target, bus);
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage broadcast = new SimpleBroadcastMessage();
                    InstanceId source = StableSource;
                    return () => broadcast.EmitBroadcast(source, bus);
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static Action BuildClassEmitClosure(MessageScenario scenario, IMessageBus bus)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    ClassUntargetedMessage untargeted = new ClassUntargetedMessage("steady");
                    return () => untargeted.EmitUntargeted(bus);
                }
                case MessageKind.Targeted:
                {
                    ClassTargetedMessage targeted = new ClassTargetedMessage("steady");
                    InstanceId target = StableTarget;
                    return () => targeted.EmitTargeted(target, bus);
                }
                case MessageKind.Broadcast:
                {
                    ClassBroadcastMessage broadcast = new ClassBroadcastMessage("steady");
                    InstanceId source = StableSource;
                    return () => broadcast.EmitBroadcast(source, bus);
                }
                default:
                {
                    throw new InvalidOperationException($"Unhandled MessageKind {scenario.Kind}.");
                }
            }
        }

        private static void AcceptAllUntargeted(ref IUntargetedMessage message) { }

        private static void AcceptAllTargeted(
            ref InstanceId target,
            ref ITargetedMessage message
        ) { }

        private static void AcceptAllBroadcast(
            ref InstanceId source,
            ref IBroadcastMessage message
        ) { }

        private static void NoOpUntargeted(ref SimpleUntargetedMessage message) { }

        private static void NoOpTargeted(ref SimpleTargetedMessage message) { }

        private static void NoOpBroadcast(ref SimpleBroadcastMessage message) { }

        private static void NoOpClassUntargeted(ref ClassUntargetedMessage message) { }

        private static void NoOpClassTargeted(ref ClassTargetedMessage message) { }

        private static void NoOpClassBroadcast(ref ClassBroadcastMessage message) { }

        private static bool AllowUntargeted(ref SimpleUntargetedMessage message)
        {
            return true;
        }

        private static bool AllowTargeted(ref InstanceId target, ref SimpleTargetedMessage message)
        {
            return true;
        }

        private static bool AllowBroadcast(
            ref InstanceId source,
            ref SimpleBroadcastMessage message
        )
        {
            return true;
        }
    }
}
#endif
