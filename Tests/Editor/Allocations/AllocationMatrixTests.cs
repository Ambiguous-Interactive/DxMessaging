#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
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
    /// Locks in the zero-GC dispatch contract across the full register / emit /
    /// deregister surface. Each test below is a row in the allocation matrix
    /// that the upcoming GC and performance work depends on; a regression in any
    /// row will surface here before it lands in user-visible benchmarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All tests in this fixture are tagged <c>[Category("Allocation")]</c> so
    /// they can be filtered out of the &lt;1-min default suite. The fixture
    /// intentionally builds emit closures once outside the assertion zone so
    /// the closure-creation cost itself is not measured. Each test owns a
    /// dedicated <see cref="MessageBus"/> instance to keep registrations from
    /// leaking across rows; the global static bus is left untouched.
    /// </para>
    /// <para>
    /// <b>Cross-product reduction.</b> The matrix exercises EACH axis (kind,
    /// interceptor presence, post-processor presence, diagnostics on/off,
    /// multi-priority) independently. The full Cartesian product is intentionally
    /// not tested because: (a) the test count would explode across the canonical
    /// kinds, without-context dispatch surfaces, interceptor, post-processor,
    /// diagnostics, and priority axes; (b) interaction effects are covered by
    /// <see cref="EmitWithFullStackIsZeroAlloc"/>, a single combinatorial test
    /// that exercises the realistic production setup (interceptor +
    /// post-processor + multi-priority handler chain); and (c) any specific
    /// interaction surfaced by Phase D / E adversarial work can be added later
    /// as a focused row without re-running the full Cartesian sweep.
    /// </para>
    /// </remarks>
    [Category("Allocation")]
    public sealed class AllocationMatrixTests : BenchmarkTestBase
    {
        private const int WarmupRegistrationCycles = 100;

        /// <summary>
        /// Number of warm emit cycles run before measurement begins on the
        /// diagnostic emission path. The diagnostic pipeline records each
        /// emission in a fixed-capacity <see cref="DxMessaging.Core.DataStructure.CyclicBuffer{T}"/>
        /// whose underlying <see cref="System.Collections.Generic.List{T}"/>
        /// allocates only while growing toward
        /// <see cref="IMessageBus.GlobalMessageBufferSize"/>. Pre-emitting
        /// twice the buffer size guarantees we are well past the growth phase
        /// and that subsequent <c>Add</c> calls overwrite in place.
        /// </summary>
        private const int DiagnosticsEmitWarmupMultiplier = 2;

        /// <summary>
        /// Cumulative allocation budget for the diagnostics-enabled emit path
        /// across <see cref="AllocationAssertions.DefaultMeasuredIterations"/>
        /// (32) consecutive emissions after the cyclic buffer reaches steady
        /// state. The diagnostics path captures a stack trace per emit (see
        /// <c>MessageEmissionData.GetAccurateStackTrace</c>), which is
        /// fundamentally allocating in the current design - Unity's
        /// <c>StackTraceUtility.ExtractStackTrace</c> returns a fresh string
        /// (typically 1-4 KB), <c>String.Split</c> produces a new array plus
        /// per-line substrings, and the LINQ filter plus <c>String.Join</c>
        /// each materialize additional managed objects. Empirically the steady
        /// state runs ~4-10 KB per emit, so 32 emits land in the 128-320 KB
        /// range. The budget below sets a per-iteration BYTE ceiling
        /// (<see cref="MaxBytesPerDiagnosticsEmit"/> bytes) and multiplies by
        /// the iteration count; a real regression (e.g. an unbounded list
        /// growth or per-frame buffer churn) will breach the ceiling.
        /// </summary>
        /// <remarks>
        /// These byte constants are retained ONLY for documentation: they record
        /// the empirical per-emit cost that motivates the count ceiling below.
        /// The actual assertion uses a managed-allocation CALL count via
        /// <see cref="AllocationProbe"/>, because
        /// <c>GC.GetAllocatedBytesForCurrentThread()</c> returns 0 for every
        /// allocation under Unity's Boehm GC and so a byte delta cannot catch a
        /// regression.
        /// </remarks>
        private const long MaxBytesPerDiagnosticsEmit = 32 * 1024L;
        private const long PerEmitDiagnosticsByteBudget =
            MaxBytesPerDiagnosticsEmit * AllocationAssertions.DefaultMeasuredIterations;

        /// <summary>
        /// Managed-allocation CALL-count budget for the diagnostics-enabled emit
        /// path over the measured window, used in place of the vacuous byte
        /// delta. Derived from <see cref="PerEmitDiagnosticsByteBudget"/> as
        /// <c>ceil(byteBudget / 16)</c> (16 = the minimum managed object size on
        /// 64-bit: an 8-byte object header plus an 8-byte minimum payload), which
        /// is the largest possible number of distinct managed objects that could
        /// fit inside the byte budget. The ceiling is therefore intentionally
        /// generous - it will never false-fail on incidental per-emit allocations
        /// - while still tripping a gross regression that adds an unbounded
        /// allocation per emit.
        /// </summary>
        private const long PerEmitDiagnosticsCountBudget =
            (PerEmitDiagnosticsByteBudget + 15L) / 16L;

        /// <summary>
        /// Per-call allocation budget for a single registration after warm-up.
        /// Estimated cost: a closure object capturing the local function (24-48
        /// bytes including object header and captured fields), the produced
        /// delegate (~64 bytes), and a dictionary entry in the registration
        /// table (~32 bytes). Total expected cost is roughly 120-200 bytes per
        /// registration; we set the budget to a 2-3x ceiling so incidental
        /// runtime behaviour does not flake the test while a genuine
        /// regression (e.g. an extra array allocation) still trips it.
        /// </summary>
        private const long PerRegistrationByteBudget = 512L;

        /// <summary>
        /// Per-call allocation budget for a single deregistration after
        /// warm-up. Deregistration is dictionary-remove plus delegate cleanup
        /// and is expected to be cheaper than registration (no closure
        /// creation). The budget here is half of
        /// <see cref="PerRegistrationByteBudget"/> with the same 2-3x slack
        /// philosophy applied.
        /// </summary>
        private const long PerDeregistrationByteBudget = 256L;

        /// <summary>
        /// Cumulative BYTE allocation budget for 32 trim calls after warm-up.
        /// Trim can perform small fixed bookkeeping work while walking dirty
        /// candidates, but repeated calls must stay bounded and independent of
        /// normal dispatch hot-path allocations. Retained for documentation
        /// only; the assertion uses the count budget below because the Boehm-GC
        /// byte delta is always 0 (see <see cref="AllocationProbe"/>).
        /// </summary>
        private const long TrimAllocBudget = 4 * 1024L;

        /// <summary>
        /// Managed-allocation CALL-count budget for the 32-trim measured window,
        /// derived from <see cref="TrimAllocBudget"/> as <c>ceil(byteBudget / 16)</c>
        /// (16 = minimum managed object size on 64-bit). Generous enough never to
        /// false-fail on incidental bookkeeping yet still trips a gross regression.
        /// </summary>
        private const long TrimAllocCountBudget = (TrimAllocBudget + 15L) / 16L;

        /// <summary>
        /// Per-call allocation budget for a single registration on the
        /// diagnostics-augmented path. The closure inside
        /// <see cref="MessageRegistrationToken"/> (lines ~106-114) wraps the
        /// user handler with diagnostics bookkeeping regardless of the
        /// diagnostics flag, so this budget is the regular registration cost
        /// (<see cref="PerRegistrationByteBudget"/>) plus an extra 50% slack
        /// to cover the augmented closure's captured state.
        /// </summary>
        private const long PerAugmentedRegistrationByteBudget = 768L;
        private const int DirtyTargetPoolRetainedEntryCount = 64;

        // The InstanceId values below are arbitrary 32-bit integers that
        // distinguish the targeted/source/owner participants from each other
        // and from any production-style ids. Tests run on isolated
        // MessageBus instances so collisions with other tests are not
        // possible.
        private static readonly InstanceId StableTarget = new InstanceId(0x5757_5757);
        private static readonly InstanceId StableSource = new InstanceId(0x4242_4242);
        private static readonly InstanceId HandlerOwner = new InstanceId(0x6363_6363);

        private DiagnosticsScope _diagnosticsScope;
        private Action<LogLevel, string> _savedLogFunction;

        protected override bool MessagingDebugEnabled => false;

        [SetUp]
        public void CaptureDiagnosticsState()
        {
            _diagnosticsScope = new DiagnosticsScope();
            _savedLogFunction = MessagingDebug.LogFunction;
            // Stray Debug.Log calls would allocate strings and contaminate the
            // assertion. Mute the messaging logger for the duration of the
            // fixture and restore it in TearDown.
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
        /// Pins zero-allocation emission for the bare register-one-handler-then-emit
        /// path across every dispatch surface. Closure under measurement is built
        /// once with stable captures so its allocation does not pollute the result.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitIsZeroAlloc(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token);
                    AllocationAssertions.AssertNoAllocations($"Emit-{scenario.Kind}", emit);
                }
            );
        }

        /// <summary>
        /// Pins zero-allocation emission across both interceptor-present and
        /// interceptor-absent rows. The scenario flag drives whether an
        /// allowing interceptor is registered, so this single test covers both
        /// halves of the interceptor axis (doubling coverage relative to a
        /// dedicated interceptor-on test) without paying the cost of the full
        /// Cartesian product.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitIsZeroAllocAcrossInterceptorPresence(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutInterceptor)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token);
                    if (scenario.UseInterceptor)
                    {
                        RegisterAllowingInterceptor(scenario, token);
                    }
                    string suffix = scenario.UseInterceptor ? "On" : "Off";
                    AllocationAssertions.AssertNoAllocations(
                        $"Emit+Interceptor{suffix}-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Pins zero-allocation emission across both post-processor-present
        /// and post-processor-absent rows. The scenario flag drives whether a
        /// post-processor is registered, so this single test covers both
        /// halves of the post-processor axis.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitIsZeroAllocAcrossPostProcessorPresence(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token);
                    if (scenario.UsePostProcessor)
                    {
                        RegisterPostProcessor(scenario, token);
                    }
                    string suffix = scenario.UsePostProcessor ? "On" : "Off";
                    AllocationAssertions.AssertNoAllocations(
                        $"Emit+PostProcessor{suffix}-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Pins a bounded-allocation steady state on the diagnostics-enabled
        /// emit path. The cyclic emission buffer's
        /// <see cref="System.Collections.Generic.List{T}"/> backing grows
        /// only while filling toward
        /// <see cref="IMessageBus.GlobalMessageBufferSize"/>, so the per-slot
        /// list churn is one-shot. The unavoidable allocator is the
        /// per-emission stack-trace capture inside
        /// <c>MessageEmissionData.GetAccurateStackTrace</c>: Unity's
        /// <c>StackTraceUtility.ExtractStackTrace</c> returns a fresh string,
        /// <c>String.Split</c> produces a new array, the LINQ filter
        /// materializes another array, and <c>String.Join</c> rebuilds the
        /// string. The contract is therefore "bounded", not "zero": after
        /// the prewarm loop we measure 32 emits as one batch and assert the
        /// observed allocation falls within
        /// <see cref="PerEmitDiagnosticsByteBudget"/>.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitWithDiagnosticsEnabledIsBoundedAlloc(
            [ValueSource(
                typeof(AllocationMatrixTests),
                nameof(DiagnosticsOnScenariosIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token);

                    // Pre-warm the cyclic emission buffer to its capacity so
                    // the underlying List<T> stops growing. After this loop
                    // every subsequent Add overwrites a slot in place. The
                    // 2x multiplier is defensive in case capacity changes in
                    // future or another path also needs to flush.
                    int prewarmCycles =
                        IMessageBus.GlobalMessageBufferSize * DiagnosticsEmitWarmupMultiplier;
                    if (prewarmCycles < 1)
                    {
                        prewarmCycles = 1;
                    }
                    for (int i = 0; i < prewarmCycles; ++i)
                    {
                        emit();
                    }

                    // We count managed allocation CALLS, not bytes:
                    // GC.GetAllocatedBytesForCurrentThread() returns 0 for every
                    // allocation under Unity's Boehm GC (editor Mono and IL2CPP),
                    // so a byte delta is vacuously zero and cannot catch a
                    // regression. The GC.Alloc profiler recorder behind
                    // AllocationProbe counts allocation calls precisely and is
                    // immune to GC timing, so a Gen-0 collection mid-loop cannot
                    // erase the signal the way a live-heap delta could.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    AllocationProbe.Begin();
                    for (int i = 0; i < AllocationAssertions.DefaultMeasuredIterations; ++i)
                    {
                        emit();
                    }
                    long gcAllocations = AllocationProbe.End();
                    if (gcAllocations == AllocationProbe.Unmeasured)
                    {
                        Assert.Ignore(
                            $"EmitDiagnostics-{scenario.Kind}: the GC.Alloc allocation "
                                + "probe is non-functional on this backend, so the "
                                + "diagnostics allocation budget cannot be evaluated."
                        );
                    }
                    Assert.That(
                        gcAllocations,
                        Is.LessThanOrEqualTo(PerEmitDiagnosticsCountBudget),
                        $"EmitDiagnostics-{scenario.Kind} allocated {gcAllocations} GC "
                            + $"allocations across "
                            + $"{AllocationAssertions.DefaultMeasuredIterations} emissions, "
                            + $"exceeding the count budget of {PerEmitDiagnosticsCountBudget}."
                    );
                }
            );
        }

        /// <summary>
        /// Stresses the priority-bucket dispatch path with three handlers at
        /// distinct priorities and pins that emission remains zero-allocation.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitWithMultiplePrioritiesIsZeroAlloc(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token, priority: 0);
                    RegisterHandler(scenario, token, priority: 5);
                    RegisterHandler(scenario, token, priority: 10);
                    AllocationAssertions.AssertNoAllocations(
                        $"Emit+Priorities-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Single combinatorial row that pins zero-allocation emission for the
        /// realistic "production" stack: an allowing interceptor, multiple
        /// handlers at distinct priorities, and multiple post-processors at
        /// distinct priorities. Covers interaction effects between axes that
        /// the per-axis tests above do not exercise. Diagnostics is
        /// intentionally left off here because
        /// <see cref="EmitWithDiagnosticsEnabledIsBoundedAlloc"/> already pins
        /// that axis.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitWithFullStackIsZeroAlloc()
        {
            // Untargeted is the cheapest dispatch and the most common in
            // production code; using a single kind keeps the combinatorial
            // surface small while still exercising the full handler chain.
            MessageScenario scenario = MessageScenario.Untargeted();
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    RegisterHandler(scenario, token, priority: 0);
                    RegisterHandler(scenario, token, priority: 5);
                    RegisterHandler(scenario, token, priority: 10);
                    RegisterAllowingInterceptor(scenario, token);
                    RegisterPostProcessor(scenario, token);
                    RegisterPostProcessor(scenario, token);
                    AllocationAssertions.AssertNoAllocations(
                        $"EmitFullStack-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        /// <summary>
        /// Pins the per-registration allocation cost when diagnostics are enabled.
        /// The diagnostic closure that wraps user handlers is created at
        /// registration time inside
        /// <see cref="MessageRegistrationToken"/> regardless of the diagnostics
        /// flag (the closure body branches on <c>_diagnosticMode</c>), so this
        /// test treats the cost as a budget rather than a hard zero. Expected
        /// cost: a small constant (delegate + closure-state object + dictionary
        /// entry) per registration. Threshold:
        /// <see cref="PerAugmentedRegistrationByteBudget"/> bytes; a regression
        /// past that bound indicates a new per-registration allocation.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void DiagnosticsAugmentedHandlerAllocationCostIsBounded()
        {
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;

            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            try
            {
                token.Enable();

                // Warm: create and tear down a few registrations so the dictionaries
                // and pools used by the registration path are sized for steady state.
                for (int i = 0; i < WarmupRegistrationCycles; ++i)
                {
                    MessageRegistrationHandle warm =
                        token.RegisterUntargeted<SimpleUntargetedMessage>(NoOpUntargeted);
                    token.RemoveRegistration(warm);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                long before = GC.GetTotalMemory(forceFullCollection: false);
                MessageRegistrationHandle measured =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(NoOpUntargeted);
                long after = GC.GetTotalMemory(forceFullCollection: false);
                long delta = after - before;
                token.RemoveRegistration(measured);

                Assert.That(
                    delta,
                    Is.LessThanOrEqualTo(PerAugmentedRegistrationByteBudget),
                    $"Diagnostic registration allocated {delta} bytes; "
                        + $"budget is {PerAugmentedRegistrationByteBudget} bytes. "
                        + "If this assertion regresses, inspect MessageRegistrationToken "
                        + "lines ~106-114 (augmented handler closure) before relaxing the bound."
                );
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        /// <summary>
        /// Pins the per-registration allocation cost in steady state across all
        /// kinds. The registration path uses dictionaries that grow on first
        /// fill; the warm-up cycles below pre-grow them, so the measured
        /// registration only pays for the new entries. The budget is set
        /// generously (<see cref="PerRegistrationByteBudget"/> bytes) to
        /// absorb the expected delegate + closure + dictionary-entry
        /// allocations without flaking on incidental runtime behaviour.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void RegisterIsZeroAllocSteadyState(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    for (int i = 0; i < WarmupRegistrationCycles; ++i)
                    {
                        MessageRegistrationHandle warm = RegisterHandler(scenario, token);
                        token.RemoveRegistration(warm);
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    long before = GC.GetTotalMemory(forceFullCollection: false);
                    MessageRegistrationHandle measured = RegisterHandler(scenario, token);
                    long after = GC.GetTotalMemory(forceFullCollection: false);
                    long delta = after - before;
                    token.RemoveRegistration(measured);

                    Assert.That(
                        delta,
                        Is.LessThanOrEqualTo(PerRegistrationByteBudget),
                        $"Register-{scenario.Kind} allocated {delta} bytes after warm-up; "
                            + $"budget is {PerRegistrationByteBudget} bytes."
                    );
                }
            );
        }

        /// <summary>
        /// Pins the per-deregistration allocation cost in steady state. After
        /// warm-up the deregistration path should not allocate anything beyond
        /// dictionary-remove churn; the budget
        /// (<see cref="PerDeregistrationByteBudget"/> bytes) is half the
        /// registration budget because there is no closure construction on
        /// this path.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void DeregisterIsZeroAllocSteadyState(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    for (int i = 0; i < WarmupRegistrationCycles; ++i)
                    {
                        MessageRegistrationHandle warm = RegisterHandler(scenario, token);
                        token.RemoveRegistration(warm);
                    }

                    MessageRegistrationHandle measured = RegisterHandler(scenario, token);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    long before = GC.GetTotalMemory(forceFullCollection: false);
                    token.RemoveRegistration(measured);
                    long after = GC.GetTotalMemory(forceFullCollection: false);
                    long delta = after - before;

                    Assert.That(
                        delta,
                        Is.LessThanOrEqualTo(PerDeregistrationByteBudget),
                        $"Deregister-{scenario.Kind} allocated {delta} bytes after warm-up; "
                            + $"budget is {PerDeregistrationByteBudget} bytes."
                    );
                }
            );
        }

        /// <summary>
        /// Pins explicit forced trim to a small bounded allocation budget
        /// across the same 32-iteration measurement window used by the emit
        /// allocation assertions. The setup creates a fresh dirty empty slot
        /// for the selected message kind and prewarms one trim call so any
        /// one-time bookkeeping does not contaminate the measured loop.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void TrimIsBoundedAlloc(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    _ = bus.Trim(force: true);
                    CreateFreshTrimCandidate(scenario, token, emit);

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    IMessageBus.TrimResult result = default;
                    int evictedSlots = 0;
                    AllocationProbe.Begin();
                    for (int i = 0; i < AllocationAssertions.DefaultMeasuredIterations; ++i)
                    {
                        result = bus.Trim(force: true);
                        evictedSlots += result.TypeSlotsEvicted + result.TargetSlotsEvicted;
                    }
                    long gcAllocations = AllocationProbe.End();
                    if (gcAllocations == AllocationProbe.Unmeasured)
                    {
                        Assert.Ignore(
                            $"Trim-{scenario.Kind}: the GC.Alloc allocation probe is "
                                + "non-functional on this backend, so the trim allocation "
                                + "budget cannot be evaluated."
                        );
                    }

                    Assert.That(
                        gcAllocations,
                        Is.LessThanOrEqualTo(TrimAllocCountBudget),
                        $"Trim-{scenario.Kind} allocated {gcAllocations} GC allocations "
                            + $"across {AllocationAssertions.DefaultMeasuredIterations} trims, "
                            + $"exceeding the count budget of {TrimAllocCountBudget}. "
                            + $"Result: type evicted={result.TypeSlotsEvicted}, "
                            + $"target evicted={result.TargetSlotsEvicted}, "
                            + $"pooled evicted={result.PooledCollectionsEvicted}, "
                            + $"live type slots={result.LiveTypeSlotsRemaining}."
                    );
                    Assert.Greater(
                        evictedSlots,
                        0,
                        $"Trim-{scenario.Kind} must reclaim at least one slot during the measured loop."
                    );
                }
            );
        }

        /// <summary>
        /// Pins zero-allocation emission after registering several handlers,
        /// deregistering half of them, and running a non-force trim. This
        /// covers the handoff where partial trim bookkeeping observes dirty
        /// candidates while the remaining live routes must still emit on the
        /// hot path without allocating.
        /// </summary>
        [Test]
        [Category("Allocation")]
        public void EmitAfterPartialTrimIsZeroAlloc(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    Action emit = BuildEmitClosure(scenario, bus);
                    List<MessageRegistrationHandle> handles = RegisterManyHandlers(
                        scenario,
                        token,
                        count: 8
                    );
                    for (int i = 0; i < handles.Count / 2; ++i)
                    {
                        token.RemoveRegistration(handles[i]);
                    }

                    _ = bus.Trim(force: false);

                    AllocationAssertions.AssertNoAllocations(
                        $"EmitAfterPartialTrim-{scenario.Kind}",
                        emit
                    );
                }
            );
        }

        [Test]
        [Category("Allocation")]
        public void DirtyTargetTrimReturnsInstanceIdCollectionsToPools(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            RunWithFreshHarness(
                scenario,
                (token, bus) =>
                {
                    int previousListCap = DxPools.InstanceIdLists.MaxRetained;
                    int previousSetCap = DxPools.InstanceIdSets.MaxRetained;
                    bool previousListLru = DxPools.InstanceIdLists.UseLru;
                    bool previousSetLru = DxPools.InstanceIdSets.UseLru;
                    try
                    {
                        _ = DxPools.TrimAll(force: true);
                        DxPools.InstanceIdLists.UseLru = true;
                        DxPools.InstanceIdSets.UseLru = true;
                        DxPools.InstanceIdLists.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                        DxPools.InstanceIdSets.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                        Action emit = BuildEmitClosure(scenario, bus);
                        MessageRegistrationHandle handle = RegisterHandler(scenario, token);
                        emit();
                        token.RemoveRegistration(handle);
                        emit();
                        int listsBefore = DxPools.DescribeAll().InstanceIdLists.Cached;
                        int setsBefore = DxPools.DescribeAll().InstanceIdSets.Cached;

                        IMessageBus.TrimResult result = bus.Trim(force: false);

                        Assert.Greater(
                            result.TargetSlotsEvicted,
                            0,
                            $"Trim-{scenario.Kind} must reclaim a dirty target slot."
                        );
                        Assert.Greater(
                            DxPools.DescribeAll().InstanceIdLists.Cached,
                            listsBefore,
                            $"Trim-{scenario.Kind} must return the dirty-target list to the pool."
                        );
                        Assert.Greater(
                            DxPools.DescribeAll().InstanceIdSets.Cached,
                            setsBefore,
                            $"Trim-{scenario.Kind} must return the dirty-target set to the pool."
                        );

                        long listHitsBeforeReuse = DxPools.DescribeAll().InstanceIdLists.Hits;
                        long setHitsBeforeReuse = DxPools.DescribeAll().InstanceIdSets.Hits;
                        MessageRegistrationHandle reused = RegisterHandler(scenario, token);
                        emit();
                        token.RemoveRegistration(reused);

                        Assert.Greater(
                            DxPools.DescribeAll().InstanceIdLists.Hits,
                            listHitsBeforeReuse,
                            $"Register-{scenario.Kind} must rent a pooled dirty-target list."
                        );
                        Assert.Greater(
                            DxPools.DescribeAll().InstanceIdSets.Hits,
                            setHitsBeforeReuse,
                            $"Register-{scenario.Kind} must rent a pooled dirty-target set."
                        );
                    }
                    finally
                    {
                        _ = DxPools.TrimAll(force: true);
                        DxPools.InstanceIdLists.UseLru = previousListLru;
                        DxPools.InstanceIdSets.UseLru = previousSetLru;
                        DxPools.InstanceIdLists.MaxRetained = previousListCap;
                        DxPools.InstanceIdSets.MaxRetained = previousSetCap;
                    }
                }
            );
        }

        public static IEnumerable<int> RetainedDirtyTargetWarmupCounts
        {
            get
            {
                yield return 1;
                yield return DirtyTargetPoolRetainedEntryCount - 1;
                yield return DirtyTargetPoolRetainedEntryCount;
            }
        }

        public static IEnumerable<int> OversizedDirtyTargetWarmupCounts
        {
            get
            {
                yield return DirtyTargetPoolRetainedEntryCount + 1;
                yield return DirtyTargetPoolRetainedEntryCount * 2;
                yield return 1000;
            }
        }

        [Test]
        [Category("Allocation")]
        public void DirtyTargetTrackingIsAllocationFreeAfterWarmup(
            [ValueSource(nameof(RetainedDirtyTargetWarmupCounts))] int targetCount
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            try
            {
                int previousListCap = DxPools.InstanceIdLists.MaxRetained;
                int previousSetCap = DxPools.InstanceIdSets.MaxRetained;
                bool previousListLru = DxPools.InstanceIdLists.UseLru;
                bool previousSetLru = DxPools.InstanceIdSets.UseLru;
                _ = DxPools.TrimAll(force: true);
                DxPools.InstanceIdLists.UseLru = true;
                DxPools.InstanceIdSets.UseLru = true;
                DxPools.InstanceIdLists.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                DxPools.InstanceIdSets.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                PrimeDirtyTargetMessageTypeIndex(bus);
                _ = DxPools.TrimAll(force: true);
                try
                {
                    Action<InstanceId> markDirtyTarget = CreateDirtyTargetMarker(bus);
                    MarkDirtyTargets(markDirtyTarget, 0x2424_0000, targetCount);
                    _ = bus.Trim(force: false);
                    PoolDiagnosticsSnapshot afterWarmup = DxPools.DescribeAll();

                    Assert.Greater(
                        afterWarmup.InstanceIdLists.Cached,
                        0,
                        "Dirty-target warmup must return a retained InstanceId list to the pool "
                            + $"before measuring reuse. targetCount={targetCount}, "
                            + $"cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"listPool={FormatPoolDiagnostics(afterWarmup.InstanceIdLists)}."
                    );
                    Assert.Greater(
                        afterWarmup.InstanceIdSets.Cached,
                        0,
                        "Dirty-target warmup must return a retained InstanceId set to the pool "
                            + $"before measuring reuse. targetCount={targetCount}, "
                            + $"cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"setPool={FormatPoolDiagnostics(afterWarmup.InstanceIdSets)}."
                    );

                    long listHitsBefore = afterWarmup.InstanceIdLists.Hits;
                    long setHitsBefore = afterWarmup.InstanceIdSets.Hits;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    AllocationProbe.Begin();
                    MarkDirtyTargets(markDirtyTarget, 0x2425_0000, targetCount);
                    long gcAllocations = AllocationProbe.End();
                    PoolDiagnosticsSnapshot afterReuse = DxPools.DescribeAll();

                    // Strict zero-allocation contract: this path reuses warmed
                    // pooled storage and must not allocate at all. We count
                    // managed allocation CALLS via AllocationProbe rather than a
                    // GC.GetAllocatedBytesForCurrentThread() byte delta, which is
                    // vacuously 0 under Unity's Boehm GC and cannot catch a
                    // regression.
                    if (gcAllocations == AllocationProbe.Unmeasured)
                    {
                        Assert.Ignore(
                            "Dirty-target tracking: the GC.Alloc allocation probe is "
                                + "non-functional on this backend, so the zero-allocation "
                                + "contract cannot be evaluated."
                        );
                    }
                    Assert.That(
                        gcAllocations,
                        Is.LessThanOrEqualTo(0L),
                        $"Dirty-target tracking must reuse warmed InstanceId list/set "
                            + $"storage without managed allocations, but allocated "
                            + $"{gcAllocations} GC allocations, exceeding the count budget of 0."
                    );
                    Assert.Greater(
                        afterReuse.InstanceIdLists.Hits,
                        listHitsBefore,
                        "Dirty-target tracking must rent the warmed InstanceId list. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterWarmup.InstanceIdLists)}, "
                            + $"after={FormatPoolDiagnostics(afterReuse.InstanceIdLists)}."
                    );
                    Assert.Greater(
                        afterReuse.InstanceIdSets.Hits,
                        setHitsBefore,
                        "Dirty-target tracking must rent the warmed InstanceId set. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterWarmup.InstanceIdSets)}, "
                            + $"after={FormatPoolDiagnostics(afterReuse.InstanceIdSets)}."
                    );
                }
                finally
                {
                    DxPools.InstanceIdLists.UseLru = previousListLru;
                    DxPools.InstanceIdSets.UseLru = previousSetLru;
                    DxPools.InstanceIdLists.MaxRetained = previousListCap;
                    DxPools.InstanceIdSets.MaxRetained = previousSetCap;
                }
            }
            finally
            {
                _ = bus.Trim(force: false);
                _ = DxPools.TrimAll(force: true);
            }
        }

        [Test]
        [Category("Allocation")]
        public void DirtyTargetTrackingDropsOversizedWarmupCollections(
            [ValueSource(nameof(OversizedDirtyTargetWarmupCounts))] int targetCount
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            try
            {
                int previousListCap = DxPools.InstanceIdLists.MaxRetained;
                int previousSetCap = DxPools.InstanceIdSets.MaxRetained;
                bool previousListLru = DxPools.InstanceIdLists.UseLru;
                bool previousSetLru = DxPools.InstanceIdSets.UseLru;
                _ = DxPools.TrimAll(force: true);
                DxPools.InstanceIdLists.UseLru = true;
                DxPools.InstanceIdSets.UseLru = true;
                DxPools.InstanceIdLists.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                DxPools.InstanceIdSets.MaxRetained = DirtyTargetPoolRetainedEntryCount;
                PrimeDirtyTargetMessageTypeIndex(bus);
                _ = DxPools.TrimAll(force: true);
                try
                {
                    Action<InstanceId> markDirtyTarget = CreateDirtyTargetMarker(bus);
                    MarkDirtyTargets(markDirtyTarget, 0x2525_0000, targetCount);
                    _ = bus.Trim(force: false);
                    PoolDiagnosticsSnapshot afterOversizedTrim = DxPools.DescribeAll();

                    Assert.AreEqual(
                        0,
                        afterOversizedTrim.InstanceIdLists.Cached,
                        "Oversized dirty-target warmup must drop its InstanceId list instead of caching it. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"listPool={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdLists)}."
                    );
                    Assert.AreEqual(
                        0,
                        afterOversizedTrim.InstanceIdSets.Cached,
                        "Oversized dirty-target warmup must drop its InstanceId set instead of caching it. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"setPool={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdSets)}."
                    );

                    MarkDirtyTargets(markDirtyTarget, 0x2526_0000, 1);
                    PoolDiagnosticsSnapshot afterFreshRent = DxPools.DescribeAll();

                    Assert.AreEqual(
                        afterOversizedTrim.InstanceIdLists.Hits,
                        afterFreshRent.InstanceIdLists.Hits,
                        "Renting after an oversized dirty-target drop must not report a pooled list hit. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdLists)}, "
                            + $"after={FormatPoolDiagnostics(afterFreshRent.InstanceIdLists)}."
                    );
                    Assert.AreEqual(
                        afterOversizedTrim.InstanceIdSets.Hits,
                        afterFreshRent.InstanceIdSets.Hits,
                        "Renting after an oversized dirty-target drop must not report a pooled set hit. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdSets)}, "
                            + $"after={FormatPoolDiagnostics(afterFreshRent.InstanceIdSets)}."
                    );
                    Assert.Greater(
                        afterFreshRent.InstanceIdLists.Misses,
                        afterOversizedTrim.InstanceIdLists.Misses,
                        "Renting after an oversized dirty-target drop must allocate a fresh list. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdLists)}, "
                            + $"after={FormatPoolDiagnostics(afterFreshRent.InstanceIdLists)}."
                    );
                    Assert.Greater(
                        afterFreshRent.InstanceIdSets.Misses,
                        afterOversizedTrim.InstanceIdSets.Misses,
                        "Renting after an oversized dirty-target drop must allocate a fresh set. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterOversizedTrim.InstanceIdSets)}, "
                            + $"after={FormatPoolDiagnostics(afterFreshRent.InstanceIdSets)}."
                    );

                    _ = bus.Trim(force: false);
                    PoolDiagnosticsSnapshot afterSmallTrim = DxPools.DescribeAll();

                    Assert.Greater(
                        afterSmallTrim.InstanceIdLists.Cached,
                        0,
                        "A small dirty-target cycle after an oversized drop must return its list to the pool. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"listPool={FormatPoolDiagnostics(afterSmallTrim.InstanceIdLists)}."
                    );
                    Assert.Greater(
                        afterSmallTrim.InstanceIdSets.Cached,
                        0,
                        "A small dirty-target cycle after an oversized drop must return its set to the pool. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"setPool={FormatPoolDiagnostics(afterSmallTrim.InstanceIdSets)}."
                    );

                    MarkDirtyTargets(markDirtyTarget, 0x2527_0000, 1);
                    PoolDiagnosticsSnapshot afterSmallReuse = DxPools.DescribeAll();

                    Assert.Greater(
                        afterSmallReuse.InstanceIdLists.Hits,
                        afterSmallTrim.InstanceIdLists.Hits,
                        "A small dirty-target cycle after an oversized drop must rent the recovered pooled list. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterSmallTrim.InstanceIdLists)}, "
                            + $"after={FormatPoolDiagnostics(afterSmallReuse.InstanceIdLists)}."
                    );
                    Assert.Greater(
                        afterSmallReuse.InstanceIdSets.Hits,
                        afterSmallTrim.InstanceIdSets.Hits,
                        "A small dirty-target cycle after an oversized drop must rent the recovered pooled set. "
                            + $"targetCount={targetCount}, cap={DirtyTargetPoolRetainedEntryCount}, "
                            + $"before={FormatPoolDiagnostics(afterSmallTrim.InstanceIdSets)}, "
                            + $"after={FormatPoolDiagnostics(afterSmallReuse.InstanceIdSets)}."
                    );
                }
                finally
                {
                    DxPools.InstanceIdLists.UseLru = previousListLru;
                    DxPools.InstanceIdSets.UseLru = previousSetLru;
                    DxPools.InstanceIdLists.MaxRetained = previousListCap;
                    DxPools.InstanceIdSets.MaxRetained = previousSetCap;
                }
            }
            finally
            {
                _ = bus.Trim(force: false);
                _ = DxPools.TrimAll(force: true);
            }
        }

        public static IEnumerable<MessageScenario> DiagnosticsOnScenariosIncludingWithoutContext
        {
            get
            {
                foreach (
                    MessageScenario scenario in MessageScenarios.WithDiagnosticsToggleIncludingWithoutContext
                )
                {
                    if (scenario.DiagnosticsEnabled)
                    {
                        yield return scenario;
                    }
                }
            }
        }

        private static void NoOpUntargeted(ref SimpleUntargetedMessage message) { }

        private static void NoOpTargeted(ref SimpleTargetedMessage message) { }

        private static void NoOpBroadcast(ref SimpleBroadcastMessage message) { }

        private static void NoOpTargetedWithoutTargeting(
            ref InstanceId target,
            ref SimpleTargetedMessage message
        ) { }

        private static void NoOpBroadcastWithoutSource(
            ref InstanceId source,
            ref SimpleBroadcastMessage message
        ) { }

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

            MessageBus bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
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

        private static void CreateFreshTrimCandidate(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action emit
        )
        {
            MessageRegistrationHandle handle = RegisterHandler(scenario, token);
            emit();
            token.RemoveRegistration(handle);
        }

        private static List<MessageRegistrationHandle> RegisterManyHandlers(
            MessageScenario scenario,
            MessageRegistrationToken token,
            int count
        )
        {
            List<MessageRegistrationHandle> handles = new List<MessageRegistrationHandle>(count);
            for (int i = 0; i < count; ++i)
            {
                handles.Add(RegisterHandler(scenario, token, priority: i));
            }

            return handles;
        }

        private static MessageRegistrationHandle RegisterHandler(
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
                case MessageKind.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        NoOpTargetedWithoutTargeting,
                        priority: priority
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        NoOpBroadcastWithoutSource,
                        priority: priority
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

        private static Action<InstanceId> CreateDirtyTargetMarker(MessageBus bus)
        {
            MethodInfo method = typeof(MessageBus)
                .GetMethod("MarkDirtyTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(SimpleTargetedMessage));
            return (Action<InstanceId>)
                Delegate.CreateDelegate(typeof(Action<InstanceId>), bus, method);
        }

        private static void PrimeDirtyTargetMessageTypeIndex(MessageBus bus)
        {
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            try
            {
                token.Enable();
                MessageRegistrationHandle handle =
                    ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        MessageScenario.Targeted(),
                        token,
                        StableTarget,
                        NoOpTargeted
                    );
                token.RemoveRegistration(handle);
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
                _ = bus.Trim(force: true);
            }
        }

        private static void MarkDirtyTargets(
            Action<InstanceId> markDirtyTarget,
            int baseValue,
            int targetCount
        )
        {
            for (int i = 0; i < targetCount; ++i)
            {
                markDirtyTarget(new InstanceId(baseValue + i));
            }
        }

        private static string FormatPoolDiagnostics(CollectionPoolDiagnostics diagnostics)
        {
            return "Cached="
                + diagnostics.Cached
                + ", Hits="
                + diagnostics.Hits
                + ", Misses="
                + diagnostics.Misses
                + ", Evictions="
                + diagnostics.Evictions;
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
                case MessageKind.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        NoOpTargetedWithoutTargeting
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        NoOpBroadcastWithoutSource
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
                case MessageKind.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage targeted = new SimpleTargetedMessage();
                    InstanceId target = StableTarget;
                    return () => targeted.EmitTargeted(target, bus);
                }
                case MessageKind.BroadcastWithoutSource:
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
    }
}
#endif
