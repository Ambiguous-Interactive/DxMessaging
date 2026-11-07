#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT && DXM_505_MATHEMATICS_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using global::Unity.Burst;
    using global::Unity.Collections.LowLevel.Unsafe;
    using NUnit.Framework;

    [Category("NativeSdkCpu")]
    public sealed unsafe class VectorPureKernelTests
    {
        private readonly List<VectorBufferOwner> _owned = new();
        private FunctionPointer<VectorBatchDelegate> _scalar;
        private FunctionPointer<VectorBatchDelegate> _vector;
        private VectorBatchDelegate _scalarInvoke;
        private VectorBatchDelegate _vectorInvoke;

        [OneTimeSetUp]
        public void CompilePrimitiveFunctions()
        {
            Assert.That(
                BurstCompiler.IsEnabled,
                Is.True,
                "Current SDK lane must execute Burst, not silently fall back."
            );
            _scalar = BurstCompiler.CompileFunctionPointer<VectorBatchDelegate>(
                VectorPureKernels.ScalarPointer
            );
            _vector = BurstCompiler.CompileFunctionPointer<VectorBatchDelegate>(
                VectorPureKernels.VectorPointer
            );
            _scalarInvoke = _scalar.Invoke;
            _vectorInvoke = _vector.Invoke;
            Assert.That(
                _scalar.IsCreated && _vector.IsCreated,
                Is.True,
                "Both primitive ABI entry points compile."
            );
        }

        [OneTimeTearDown]
        public void RetireManagedDelegates()
        {
            _scalarInvoke = _vectorInvoke = null;
            _scalar = _vector = default;
            Assert.That(_scalarInvoke, Is.Null);
            Assert.That(_vectorInvoke, Is.Null);
            Assert.That(
                _scalar.IsCreated || _vector.IsCreated,
                Is.False,
                "Compiled code is Burst-owned; cached managed references retire here."
            );
        }

        [TearDown]
        public void ReleaseKnownOwners()
        {
            List<Exception> failures = new();
            foreach (VectorBufferOwner owner in _owned)
            {
                try
                {
                    owner.Dispose();
                    owner.Dispose();
                    Assert.That(owner.HasPending, Is.False, "Known jobs complete before release.");
                    Assert.That(
                        owner.Input.IsCreated || owner.Output.IsCreated || owner.Metrics.IsCreated,
                        Is.False,
                        "All owned root containers release."
                    );
                    Assert.That(
                        owner.ReleaseCount,
                        Is.EqualTo(3),
                        "Each of the three root containers releases once."
                    );
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            TestContext.WriteLine(
                $"Vector cleanup: owners={_owned.Count},rootReleases={_owned.Count * 3}; known jobs completed; each root released once."
            );
            _owned.Clear();
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }

        private object _allocationSink;

        [Test]
        [Category("Allocation")]
        public void AllocationRecorderDistinguishesForcedAllocationFromEmptyOperation(
            [Values(false, true)] bool allocate
        )
        {
            Assert.That(
                AllocationProbe.IsFunctional,
                Is.True,
                "This screen requires a real recorder."
            );
            Action operation = allocate ? () => _allocationSink = new byte[128] : () => { };
            operation();
            long count = AllocationProbe.MeasureMin(64, null, operation);
            _allocationSink = null;
            TestContext.WriteLine($"Vector allocation control: allocate={allocate},count={count}.");
            if (allocate)
                Assert.That(
                    count,
                    Is.GreaterThanOrEqualTo(1),
                    "Forced allocation remains visible."
                );
            else
                Assert.That(count, Is.Zero, "An actual empty operation measures zero.");
        }

        /// <remarks>
        /// October 8, 2026: Enum.IsDefined(Type, object) boxed the path on each run.
        /// Typed membership validation removes that measured owner allocation.
        /// This warm minimum assay keeps preparation outside the window and does
        /// not claim every independent window or retained native memory is zero.
        /// </remarks>
        [Test]
        [Category("Allocation")]
        public void WarmVectorRunHasZeroManagedAllocations(
            [Values] VectorBatchPath path,
            [Values(0, 257)] int count,
            [Values(1, 64, 256)] int batch,
            [Values(0, 3)] int rounds
        )
        {
            Assert.That(
                AllocationProbe.IsFunctional,
                Is.True,
                "Unmeasured cannot pass this screen."
            );
            using VectorBufferOwner owner = Create(count);
            NativeNumericPayload[] original = owner.Input.ToArray();
            long[] expected = original
                .Skip(1)
                .Take(count)
                .Select(p => PureBatchKernels.Compute(p, rounds))
                .ToArray();
            Action prepare = owner.Reset;
            Func<int> operation = () =>
            {
                Run(owner, path, batch, rounds);
                return owner.Entries;
            };
            for (int i = 0; i < 8; ++i)
            {
                prepare();
                operation();
            }
            if (path == VectorBatchPath.ManagedVector && count == 257 && batch == 64 && rounds == 3)
            {
                long[] windows = new long[50];
                Action measured = () => operation();
                for (int i = 0; i < windows.Length; ++i)
                {
                    prepare();
                    windows[i] = AllocationProbe.Measure(measured);
                }
                TestContext.WriteLine(
                    "Vector allocation independent windows: " + string.Join(",", windows)
                );
            }
            var minimum = AllocationProbe.MeasureMinWithDiagnostics(64, prepare, operation);
            CollectionAssert.AreEqual(
                original,
                owner.Input.ToArray(),
                "Measured operations preserve input."
            );
            CollectionAssert.AreEqual(
                expected,
                owner.Output.ToArray().Skip(1).Take(count),
                "Every measured output matches the retained scalar oracle."
            );
            AssertRecords(owner, path, batch);
            AssertGuards(owner);
            Assert.That(
                owner.HasPending,
                Is.False,
                "Actual known job completes before observation."
            );
            int width = path == VectorBatchPath.PointerVectorOne ? 1 : batch;
            Assert.That(
                minimum.Diagnostics,
                Is.EqualTo((count + width - 1) / width),
                "Entry diagnostics come from the selected allocation window."
            );
            TestContext.WriteLine(
                $"Vector allocation row: path={path},count={count},batch={batch},rounds={rounds},allocations={minimum.GcAllocations},bytes={minimum.GcAllocatedBytes},attempt={minimum.AttemptIndex},entries={minimum.Diagnostics}; untimed managed allocation screen only."
            );
            Assert.That(
                minimum.GcAllocations,
                Is.Zero,
                "Warm wrapper calls must allocate no managed objects."
            );
        }

        [Test]
        public void UnknownVectorPathRejectsBeforeNativeMutation(
            [Values(-1, int.MinValue, int.MaxValue, 10, 999)] int invalid
        )
        {
            using VectorBufferOwner owner = Create(4);
            long[] output = owner.Output.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Run(owner, (VectorBatchPath)invalid, 4, 3)
            );
            CollectionAssert.AreEqual(output, owner.Output.ToArray());
            Assert.That(owner.Entries, Is.Zero);
            Assert.That(owner.HasPending, Is.False);
        }

        [Test]
        public void FullOddBatchMatchesOriginalScalarInEveryPath(
            [Values] VectorBatchPath path,
            [Values(1, 4, 16, 64, 128, 256)] int batch,
            [Values(0, 3)] int rounds
        ) => Compare(path, 1027, batch, rounds);

        [Test]
        public void EmptySmallAndTailBatchesPreserveEveryOutputAndGuard(
            [Values] VectorBatchPath path,
            [Values(0, 1, 3, 4, 5, 257)] int count,
            [Values(4, 256)] int batch,
            [Values(0, 3)] int rounds
        ) => Compare(path, count, batch, rounds);

        [Test]
        public void IndependentGoldenPacketsExerciseAllFourLanePositions(
            [Values(0, 3)] int rounds,
            [Values(0, 1, 2, 3, 4, 5)] int packet
        )
        {
            int[] producer = { 0, 1, -1, int.MinValue, int.MaxValue, 17 };
            int[] sequence = { 0, 1, -1, int.MaxValue, int.MinValue, 29 };
            long[] value = { 0, 1, -1, long.MinValue, long.MaxValue, 81985529216486895L };
            long[] simple =
            {
                0,
                99,
                -99,
                9223371830696345599L,
                -9223371830696345698L,
                81985529216488573L,
            };
            long[] mixed =
            {
                6392512781046991133L,
                6133562355747626131L,
                297763689130802543L,
                -8091143960558176489L,
                -362853752901266012L,
                9008981355231998747L,
            };
            foreach (VectorBatchPath path in Enum.GetValues(typeof(VectorBatchPath)))
            {
                using VectorBufferOwner owner = Create(4);
                long[] expected = new long[4];
                for (int lane = 0; lane < 4; ++lane)
                {
                    int edge = (packet + lane) % 6;
                    owner.Input[lane + 1] = new NativeNumericPayload
                    {
                        Producer = producer[edge],
                        Sequence = sequence[edge],
                        Value = value[edge],
                    };
                    expected[lane] = rounds == 0 ? simple[edge] : mixed[edge];
                }
                NativeNumericPayload[] original = owner.Input.ToArray();
                Run(owner, path, 4, rounds);
                CollectionAssert.AreEqual(
                    expected,
                    owner.Output.ToArray().Skip(1).Take(4),
                    $"{path}/{rounds}/packet{packet}: all actual four-lane outputs match independent bits."
                );
                CollectionAssert.AreEqual(
                    original,
                    owner.Input.ToArray(),
                    "Golden inputs remain immutable."
                );
                AssertRecords(owner, path, 4);
                AssertGuards(owner);
                TestContext.WriteLine(
                    $"Vector golden: path={path},rounds={rounds},packet={packet},values={string.Join(",", owner.Output.ToArray().Skip(1).Take(4))}"
                );
            }
        }

        [Test]
        public void InvalidArgumentsRejectBeforeNativeWrites()
        {
            using VectorBufferOwner owner = Create(4);
            long[] outputs = owner.Output.ToArray();
            int[] metrics = owner.Metrics.ToArray();
            foreach (int batch in new[] { 0, -1, 257 })
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => Run(owner, VectorBatchPath.ManagedVector, batch, 3),
                    "Invalid batch rejects before pointers."
                );
            foreach (int rounds in new[] { -1, 65 })
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => Run(owner, VectorBatchPath.ManagedVector, 4, rounds),
                    "Invalid rounds reject before pointers."
                );
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Run(owner, (VectorBatchPath)999, 4, 3),
                "Invalid path rejects."
            );
            Assert.Throws<ArgumentException>(
                () =>
                    owner.Run(
                        VectorBatchPath.PointerScalarBatch,
                        4,
                        3,
                        default,
                        _vector,
                        _scalarInvoke,
                        _vectorInvoke
                    ),
                "Missing scalar function rejects."
            );
            Assert.Throws<ArgumentException>(
                () =>
                    owner.Run(
                        VectorBatchPath.PointerVectorBatch,
                        4,
                        3,
                        _scalar,
                        default,
                        _scalarInvoke,
                        _vectorInvoke
                    ),
                "Missing vector function rejects."
            );
            Assert.Throws<ArgumentNullException>(
                () =>
                    owner.Run(
                        VectorBatchPath.PointerScalarBatch,
                        4,
                        3,
                        _scalar,
                        _vector,
                        null,
                        _vectorInvoke
                    ),
                "Missing cached scalar delegate rejects."
            );
            Assert.Throws<ArgumentNullException>(
                () =>
                    owner.Run(
                        VectorBatchPath.PointerVectorOne,
                        4,
                        3,
                        _scalar,
                        _vector,
                        _scalarInvoke,
                        null
                    ),
                "Missing cached vector delegate rejects."
            );
            Assert.Throws<ArgumentException>(
                () => owner.Schedule(VectorBatchPath.ManagedVector, 4, 3, _vector),
                "Only explicit job paths may schedule."
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VectorBufferOwner(-1),
                "Negative capacity rejects before allocation."
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VectorBufferOwner(4097),
                "Over-capacity rejects before allocation."
            );
            CollectionAssert.AreEqual(
                outputs,
                owner.Output.ToArray(),
                "All invalid calls preserve native output."
            );
            CollectionAssert.AreEqual(
                metrics,
                owner.Metrics.ToArray(),
                "All invalid calls preserve metric records."
            );
            owner.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => Run(owner, VectorBatchPath.ManagedVector, 4, 3),
                "Disposed owner cannot borrow freed arrays."
            );
        }

        [Test]
        public void DisposeCompletesKnownJobBeforeReleasingItsBorrowedPointers()
        {
            using VectorBufferOwner owner = Create(257);
            long expected = 0;
            for (int i = 1; i <= owner.Count; ++i)
                expected = unchecked(expected + PureBatchKernels.Compute(owner.Input[i], 3));
            owner.Schedule(VectorBatchPath.JobPointerVector, 256, 3, _vector);
            Assert.That(
                owner.HasPending,
                Is.True,
                "A concrete scheduled handle is tracked even if it already completed."
            );
            Assert.Throws<InvalidOperationException>(
                () => owner.Reset(),
                "Reset cannot mutate arrays with an outstanding known handle."
            );
            owner.Dispose();
            Assert.That(
                owner.CompletedJob,
                Is.True,
                "Known handle was completed before native roots release."
            );
            Assert.That(
                owner.LastCompletedChecksum,
                Is.EqualTo(expected),
                "All scheduled writes became observable before release."
            );
            Assert.That(
                owner.ReleaseCount,
                Is.EqualTo(3),
                "Disposal releases exactly three roots after completion."
            );
        }

        [Test]
        public void ForeignThreadRejectsBeforeBorrowingOrScheduling()
        {
            using VectorBufferOwner owner = Create(4);
            List<Exception> errors = new();
            long[] original = owner.Output.ToArray();
            Thread worker = new(() =>
            {
                Action[] actions =
                {
                    owner.Reset,
                    () => Run(owner, VectorBatchPath.ManagedVector, 4, 3),
                    () => owner.Schedule(VectorBatchPath.JobVector, 4, 3, _vector),
                    owner.Complete,
                    owner.Dispose,
                };
                foreach (Action action in actions)
                    try
                    {
                        action();
                    }
                    catch (Exception failure)
                    {
                        errors.Add(failure);
                    }
            });
            worker.Start();
            worker.Join();
            Assert.That(errors.Count, Is.EqualTo(5), "Every foreign owner operation rejects.");
            Assert.That(
                errors.All(e => e.GetType() == typeof(InvalidOperationException)),
                Is.True,
                "Affinity rejects before phase or pointer access."
            );
            Assert.That(owner.HasPending, Is.False, "Foreign thread scheduled no job.");
            CollectionAssert.AreEqual(
                original,
                owner.Output.ToArray(),
                "Foreign thread performed no native writes."
            );
        }

        [Test]
        public void PrimitiveAbiAndPayloadLayoutRejectManagedReferences()
        {
            ManagedPayload reference = new() { Text = "managed" };
            Assert.That(
                reference.Text,
                Is.EqualTo("managed"),
                "Reference rejection checks a real populated field."
            );
            Assert.That(
                UnsafeUtility.IsBlittable<NativeNumericPayload>(),
                Is.True,
                "Numeric message contains no managed references."
            );
            Assert.That(
                UnsafeUtility.IsBlittable<ManagedPayload>(),
                Is.False,
                "Reference-bearing payload is ineligible for this native tier."
            );
            Assert.That(
                UnsafeUtility.SizeOf<NativeNumericPayload>(),
                Is.EqualTo(16),
                "Actual scalar payload stride is16 bytes."
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Producer))
                    .ToInt32(),
                Is.Zero
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Sequence))
                    .ToInt32(),
                Is.EqualTo(4)
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Value))
                    .ToInt32(),
                Is.EqualTo(8)
            );
            var method = typeof(VectorBatchDelegate).GetMethod("Invoke");
            var parameters = method.GetParameters();
            Assert.That(
                method.ReturnType,
                Is.EqualTo(typeof(void)),
                "Primitive native ABI has void result."
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    typeof(int),
                    typeof(NativeNumericPayload*),
                    typeof(long*),
                    typeof(int*),
                    typeof(int),
                },
                parameters.Select(p => p.ParameterType),
                "Only primitive scalars and pointers cross the boundary."
            );
        }

        private void Compare(VectorBatchPath path, int count, int batch, int rounds)
        {
            using VectorBufferOwner owner = Create(count);
            NativeNumericPayload[] original = owner.Input.ToArray();
            long[] expected = original
                .Skip(1)
                .Take(count)
                .Select(p => PureBatchKernels.Compute(p, rounds))
                .ToArray();
            Run(owner, path, batch, rounds);
            CollectionAssert.AreEqual(
                expected,
                owner.Output.ToArray().Skip(1).Take(count),
                $"{path}/{count}/{batch}/{rounds}: every output matches the unchanged scalar64-bit handler."
            );
            CollectionAssert.AreEqual(
                original,
                owner.Input.ToArray(),
                "Borrowed payloads and guards remain immutable."
            );
            (int groups, int tails) = AssertRecords(owner, path, batch);
            AssertGuards(owner);
            long checksum = 0;
            for (int i = 1; i <= count; ++i)
                checksum = unchecked(checksum + owner.Output[i]);
            TestContext.WriteLine(
                $"Vector row: path={path},count={count},batch={batch},rounds={rounds},entries={owner.Entries},groups={groups},tails={tails},mode={(count == 0 ? -1 : IsManaged(path) ? 1 : 0)},checksum={checksum}; timing/allocation unmeasured."
            );
            TestContext.WriteLine(
                "Vector values: " + string.Join(",", owner.Output.ToArray().Skip(1).Take(count))
            );
        }

        private static (int Groups, int Tails) AssertRecords(
            VectorBufferOwner owner,
            VectorBatchPath path,
            int batch
        )
        {
            int width = path == VectorBatchPath.PointerVectorOne ? 1 : batch;
            int expectedEntries = (owner.Count + width - 1) / width;
            Assert.That(
                owner.Entries,
                Is.EqualTo(expectedEntries),
                "Actual entry count matches bounded batch partition."
            );
            int totalGroups = 0,
                totalTails = 0;
            for (int entry = 0; entry < expectedEntries; ++entry)
            {
                int count = Math.Min(width, owner.Count - entry * width);
                int groups = IsVector(path) ? count / 4 : 0;
                int tails = count - groups * 4;
                Assert.That(
                    owner.Metrics[1 + entry * 3],
                    Is.EqualTo(IsManaged(path) ? 1 : 0),
                    $"Entry{entry}: actual managed/Burst marker."
                );
                Assert.That(
                    owner.Metrics[2 + entry * 3],
                    Is.EqualTo(groups),
                    $"Entry{entry}: actual four-lane group count."
                );
                Assert.That(
                    owner.Metrics[3 + entry * 3],
                    Is.EqualTo(tails),
                    $"Entry{entry}: actual scalar tail count."
                );
                totalGroups += groups;
                totalTails += tails;
            }
            for (int i = 1 + expectedEntries * 3; i < owner.Metrics.Length - 1; ++i)
                Assert.That(owner.Metrics[i], Is.EqualTo(-1), "No out-of-partition metric write.");
            Assert.That(
                totalGroups * 4 + totalTails,
                Is.EqualTo(owner.Count),
                "Groups and tails account for every input exactly once."
            );
            if (path == VectorBatchPath.PointerVectorOne)
                Assert.That(
                    totalGroups,
                    Is.Zero,
                    "One pointer call per item is an actual all-tail negative control."
                );
            return (totalGroups, totalTails);
        }

        private static bool IsManaged(VectorBatchPath path) =>
            path == VectorBatchPath.ManagedScalar || path == VectorBatchPath.ManagedVector;

        private static bool IsVector(VectorBatchPath path) =>
            path == VectorBatchPath.ManagedVector
            || path == VectorBatchPath.BurstVector
            || path == VectorBatchPath.PointerVectorBatch
            || path == VectorBatchPath.PointerVectorOne
            || path == VectorBatchPath.JobVector
            || path == VectorBatchPath.JobPointerVector;

        private static void AssertGuards(VectorBufferOwner owner)
        {
            Assert.That(
                owner.Output[0],
                Is.EqualTo(VectorBufferOwner.Guard),
                "Leading output guard survives."
            );
            Assert.That(
                owner.Output[owner.Count + 1],
                Is.EqualTo(VectorBufferOwner.Guard),
                "Trailing output guard survives."
            );
            Assert.That(
                owner.Metrics[0],
                Is.EqualTo(VectorBufferOwner.MetricGuard),
                "Leading metric guard survives."
            );
            Assert.That(
                owner.Metrics[owner.Metrics.Length - 1],
                Is.EqualTo(VectorBufferOwner.MetricGuard),
                "Trailing metric guard survives."
            );
        }

        private VectorBufferOwner Create(int count)
        {
            VectorBufferOwner owner = new(count);
            _owned.Add(owner);
            return owner;
        }

        private void Run(VectorBufferOwner owner, VectorBatchPath path, int batch, int rounds) =>
            owner.Run(path, batch, rounds, _scalar, _vector, _scalarInvoke, _vectorInvoke);

        private struct ManagedPayload
        {
            public string Text;
        }
    }
}
#endif
