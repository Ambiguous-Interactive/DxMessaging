#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;
    using NUnit.Framework;

    [Category("NativeSdkCpu")]
    public sealed unsafe class PureBatchKernelTests
    {
        private readonly List<BufferOwner> _owned = new();
        private FunctionPointer<NumericBatchDelegate> _function;
        private NumericBatchDelegate _invoke;

        [OneTimeSetUp]
        public void CompileClosedFunction()
        {
            Assert.That(BurstCompiler.IsEnabled, Is.True);
            _function = BurstCompiler.CompileFunctionPointer<NumericBatchDelegate>(
                PureBatchKernels.Pointer
            );
            _invoke = _function.Invoke;
            Assert.That(_function.IsCreated, Is.True);
        }

        [OneTimeTearDown]
        public void RetireCachedDelegate()
        {
            _invoke = null;
            _function = default;
            Assert.That(_invoke, Is.Null);
            Assert.That(_function.IsCreated, Is.False);
            // Compiled code belongs to Burst; there is no manual code-free operation.
        }

        [TearDown]
        public void TearDown()
        {
            List<Exception> failures = new();
            foreach (BufferOwner owner in _owned)
            {
                try
                {
                    owner.Dispose();
                    owner.Dispose();
                    Assert.That(owner.HasPending, Is.False);
                    Assert.That(owner.Input.IsCreated, Is.False);
                    Assert.That(owner.Output.IsCreated, Is.False);
                    Assert.That(owner.Modes.IsCreated, Is.False);
                    Assert.That(owner.ReleaseCount, Is.EqualTo(3));
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            TestContext.WriteLine(
                $"Pure batch cleanup: owners={_owned.Count}; all three containers released once after known jobs completed."
            );
            _owned.Clear();
            if (failures.Count != 0)
            {
                throw new AggregateException(failures);
            }
        }

        [Test]
        public void AllPathsPreserveEveryOutputAndGuardsWithPartialFinalBatch(
            [Values] PureBatchPath path,
            [Values(1, 4, 16, 64, 128, 256)] int batch,
            [Values(0, 3)] int rounds
        )
        {
            using BufferOwner owner = Create(1027);
            NativeNumericPayload[] original = owner.Input.ToArray();
            owner.Run(PureBatchPath.Managed, batch, rounds, _function, _invoke);
            long[] expected = owner.Output.ToArray();
            owner.AssertModes(PureBatchPath.Managed, batch);
            owner.Reset();
            owner.Run(path, batch, rounds, _function, _invoke);
            CollectionAssert.AreEqual(expected, owner.Output.ToArray());
            CollectionAssert.AreEqual(original, owner.Input.ToArray());
            owner.AssertModes(path, batch);
            owner.AssertGuards();
            long checksum = 0;
            for (int index = 1; index <= owner.Count; ++index)
            {
                checksum = unchecked(checksum + owner.Output[index]);
            }
            int entries =
                path == PureBatchPath.PointerScalar
                    ? owner.Count
                    : (owner.Count + batch - 1) / batch;
            TestContext.WriteLine(
                $"Pure batch: path={path},batch={batch},rounds={rounds},items={owner.Count},kernelEntries={entries},mode={(path == PureBatchPath.Managed ? 1 : 0)},checksum={checksum}; timing unmeasured."
            );
        }

        [Test]
        public void FrozenIndependentGoldenVectorsAgreeAcrossAllPaths(
            [Values(0, 3)] int rounds,
            [Values(0, 1, 2, 3, 4, 5)] int vector
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
            long expected = rounds == 0 ? simple[vector] : mixed[vector];
            using BufferOwner owner = Create(1);
            owner.Input[1] = new NativeNumericPayload
            {
                Producer = producer[vector],
                Sequence = sequence[vector],
                Value = value[vector],
            };
            foreach (PureBatchPath path in Enum.GetValues(typeof(PureBatchPath)))
            {
                owner.Reset();
                owner.Run(path, 1, rounds, _function, _invoke);
                Assert.That(owner.Output[1], Is.EqualTo(expected), path.ToString());
                owner.AssertModes(path, 1);
                owner.AssertGuards();
            }
        }

        [Test]
        public void InvalidArgumentsRejectBeforeNativeWrites()
        {
            using BufferOwner owner = Create(4);
            long[] original = owner.Output.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Run(PureBatchPath.Managed, 0, 0, _function, _invoke)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Run(PureBatchPath.Managed, -1, 0, _function, _invoke)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Run(PureBatchPath.Managed, 257, 0, _function, _invoke)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Run((PureBatchPath)99, 1, 0, _function, _invoke)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Run(PureBatchPath.Managed, 1, -1, _function, _invoke)
            );
            Assert.Throws<ArgumentException>(() =>
                owner.Run(PureBatchPath.PointerBatch, 1, 0, default, _invoke)
            );
            Assert.Throws<ArgumentNullException>(() =>
                owner.Run(PureBatchPath.PointerScalar, 1, 0, _function, null)
            );
            CollectionAssert.AreEqual(original, owner.Output.ToArray());
            CollectionAssert.AreEqual(
                Enumerable.Repeat(-1, owner.Count).ToArray(),
                owner.Modes.ToArray()
            );
            Assert.Throws<ArgumentOutOfRangeException>(() => new BufferOwner(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BufferOwner(4097));
        }

        [Test]
        public void PendingJobRejectsResetOrReuseAndDisposalCompletesBeforeRelease(
            [Values(PureBatchPath.JobDirect, PureBatchPath.JobPointer)] PureBatchPath path
        )
        {
            BufferOwner owner = Create(1027);
            owner.Schedule(path, 64, 3, _function);
            Assert.Throws<InvalidOperationException>(() => owner.Reset());
            Assert.Throws<InvalidOperationException>(() =>
                owner.Run(PureBatchPath.Managed, 64, 3, _function, _invoke)
            );
            owner.Dispose();
            Assert.That(owner.CompletedJob, Is.True);
            Assert.That(owner.CompletedNative, Is.True);
            Assert.That(owner.ReleaseCount, Is.EqualTo(3));
            Assert.Throws<ObjectDisposedException>(() => owner.Reset());
            Assert.Throws<ObjectDisposedException>(() =>
                owner.Run(path, 64, 3, _function, _invoke)
            );
        }

        [Test]
        public void ResetAndReuseRetainsGuardsAcrossManagedAndNativePaths()
        {
            using BufferOwner owner = Create(17);
            foreach (PureBatchPath path in Enum.GetValues(typeof(PureBatchPath)))
            {
                for (int repeat = 0; repeat < 3; ++repeat)
                {
                    owner.Reset();
                    owner.Run(path, 4, repeat, _function, _invoke);
                    owner.AssertModes(path, 4);
                    owner.AssertGuards();
                    Assert.That(
                        owner.Output[1],
                        Is.EqualTo(PureBatchKernels.Compute(owner.Input[1], repeat))
                    );
                }
            }
        }

        private BufferOwner Create(int count)
        {
            BufferOwner owner = new(count);
            _owned.Add(owner);
            return owner;
        }

        private sealed class BufferOwner : IDisposable
        {
            internal NativeArray<NativeNumericPayload> Input;
            internal NativeArray<long> Output;
            internal NativeArray<int> Modes;
            internal readonly int Count;
            internal bool HasPending;
            internal bool CompletedJob;
            internal bool CompletedNative;
            internal int ReleaseCount;
            private JobHandle _pending;
            private bool _disposed;
            private int _pendingEntries;
            private const long Guard = 1234605616436508552L;

            internal BufferOwner(int count)
            {
                if (count < 1 || 4096 < count)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }
                Count = count;
                try
                {
                    Input = new NativeArray<NativeNumericPayload>(count + 2, Allocator.Persistent);
                    Output = new NativeArray<long>(count + 2, Allocator.Persistent);
                    Modes = new NativeArray<int>(count, Allocator.Persistent);
                    Input[0] = Input[count + 1] = InputGuard();
                    for (int index = 0; index < count; ++index)
                    {
                        NativeNumericPayload payload = NativeNumericPayload.Create(
                            index % 4,
                            index
                        );
                        payload.Value =
                            index % 3 == 0 ? long.MinValue + index
                            : index % 3 == 1 ? long.MaxValue - index
                            : index * 17L;
                        Input[index + 1] = payload;
                    }
                    Reset();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal void Reset()
            {
                RequireIdle();
                for (int index = 0; index < Output.Length; ++index)
                {
                    Output[index] = Guard;
                }
                for (int index = 0; index < Modes.Length; ++index)
                {
                    Modes[index] = -1;
                }
            }

            internal void Run(
                PureBatchPath path,
                int batch,
                int rounds,
                FunctionPointer<NumericBatchDelegate> function,
                NumericBatchDelegate invoke
            )
            {
                Validate(path, batch, rounds, function);
                if (path == PureBatchPath.JobDirect || path == PureBatchPath.JobPointer)
                {
                    Schedule(path, batch, rounds, function);
                    Complete();
                    return;
                }
                if (
                    (path == PureBatchPath.PointerBatch || path == PureBatchPath.PointerScalar)
                    && invoke == null
                )
                {
                    throw new ArgumentNullException(nameof(invoke));
                }
                NativeNumericPayload* input = (NativeNumericPayload*)Input.GetUnsafeReadOnlyPtr();
                long* output = (long*)Output.GetUnsafePtr();
                int* modes = (int*)Modes.GetUnsafePtr();
                int width = path == PureBatchPath.PointerScalar ? 1 : batch;
                int entry = 0;
                for (int offset = 0; offset < Count; offset += width)
                {
                    int count = Math.Min(width, Count - offset);
                    switch (path)
                    {
                        case PureBatchPath.Managed:
                            PureBatchKernels.Managed(
                                count,
                                input + offset + 1,
                                output + offset + 1,
                                modes + entry,
                                rounds
                            );
                            break;
                        case PureBatchPath.BurstDirect:
                            PureBatchKernels.Direct(
                                count,
                                input + offset + 1,
                                output + offset + 1,
                                modes + entry,
                                rounds
                            );
                            break;
                        default:
                            invoke(
                                count,
                                input + offset + 1,
                                output + offset + 1,
                                modes + entry,
                                rounds
                            );
                            break;
                    }
                    ++entry;
                }
            }

            internal void Schedule(
                PureBatchPath path,
                int batch,
                int rounds,
                FunctionPointer<NumericBatchDelegate> function
            )
            {
                Validate(path, batch, rounds, function);
                if (path != PureBatchPath.JobDirect && path != PureBatchPath.JobPointer)
                {
                    throw new ArgumentException("Only job paths may be scheduled.", nameof(path));
                }
                _pending = new PureBatchJob
                {
                    Input = Input,
                    Output = Output,
                    Modes = Modes,
                    Function = function,
                    Count = Count,
                    Batch = batch,
                    Rounds = rounds,
                    UsePointer = path == PureBatchPath.JobPointer,
                }.Schedule();
                _pendingEntries = (Count + batch - 1) / batch;
                HasPending = true;
            }

            private void Complete()
            {
                if (!HasPending)
                {
                    return;
                }
                _pending.Complete();
                HasPending = false;
                CompletedJob = true;
                CompletedNative = true;
                for (int index = 0; index < _pendingEntries; ++index)
                {
                    CompletedNative &= Modes[index] == 0;
                }
            }

            private void Validate(
                PureBatchPath path,
                int batch,
                int rounds,
                FunctionPointer<NumericBatchDelegate> function
            )
            {
                RequireIdle();
                if (
                    batch < 1
                    || 256 < batch
                    || rounds < 0
                    || 3 < rounds
                    || path < PureBatchPath.Managed
                    || PureBatchPath.JobPointer < path
                )
                {
                    throw new ArgumentOutOfRangeException(nameof(batch));
                }
                if (
                    (
                        path == PureBatchPath.PointerBatch
                        || path == PureBatchPath.PointerScalar
                        || path == PureBatchPath.JobPointer
                    ) && !function.IsCreated
                )
                {
                    throw new ArgumentException(
                        "A compiled function is required.",
                        nameof(function)
                    );
                }
            }

            private void RequireIdle()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(BufferOwner));
                }
                if (HasPending)
                {
                    throw new InvalidOperationException(
                        "Complete the known job before reset or reuse."
                    );
                }
            }

            internal void AssertModes(PureBatchPath path, int batch)
            {
                RequireIdle();
                int entries =
                    path == PureBatchPath.PointerScalar ? Count : (Count + batch - 1) / batch;
                int mode = path == PureBatchPath.Managed ? 1 : 0;
                int[] expected = Enumerable.Repeat(-1, Count).ToArray();
                for (int index = 0; index < entries; ++index)
                {
                    expected[index] = mode;
                }
                CollectionAssert.AreEqual(
                    expected,
                    Modes.ToArray(),
                    "Exact entry count and compiler mode; no fallback."
                );
            }

            internal void AssertGuards()
            {
                RequireIdle();
                Assert.That(Output[0], Is.EqualTo(Guard));
                Assert.That(Output[Count + 1], Is.EqualTo(Guard));
                Assert.That(Input[0], Is.EqualTo(InputGuard()));
                Assert.That(Input[Count + 1], Is.EqualTo(InputGuard()));
            }

            private static NativeNumericPayload InputGuard()
            {
                return new NativeNumericPayload
                {
                    Producer = -999,
                    Sequence = -777,
                    Value = Guard,
                };
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                Complete();
                if (Input.IsCreated)
                {
                    Input.Dispose();
                    ++ReleaseCount;
                }
                if (Output.IsCreated)
                {
                    Output.Dispose();
                    ++ReleaseCount;
                }
                if (Modes.IsCreated)
                {
                    Modes.Dispose();
                    ++ReleaseCount;
                }
                _disposed = true;
            }
        }
    }
}
#endif
