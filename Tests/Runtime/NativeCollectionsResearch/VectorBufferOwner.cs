#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT && DXM_505_MATHEMATICS_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Threading;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;

    internal sealed unsafe class VectorBufferOwner : IDisposable
    {
        internal const long Guard = 1234605616436508552L;
        internal const int MetricGuard = 123456789;
        internal NativeArray<NativeNumericPayload> Input;
        internal NativeArray<long> Output;
        internal NativeArray<int> Metrics;
        internal readonly int Count;
        internal int Entries { get; private set; }
        internal int ReleaseCount { get; private set; }
        internal bool HasPending { get; private set; }
        internal bool CompletedJob { get; private set; }
        internal long LastCompletedChecksum { get; private set; }
        private JobHandle _pending;
        private bool _disposed;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;

        internal VectorBufferOwner(int count)
        {
            if (count < 0 || 4096 < count)
                throw new ArgumentOutOfRangeException(nameof(count));
            Count = count;
            try
            {
                Input = new NativeArray<NativeNumericPayload>(count + 2, Allocator.Persistent);
                Output = new NativeArray<long>(count + 2, Allocator.Persistent);
                Metrics = new NativeArray<int>(count * 3 + 2, Allocator.Persistent);
                Input[0] = Input[count + 1] = new NativeNumericPayload
                {
                    Producer = -1,
                    Sequence = -2,
                    Value = Guard,
                };
                for (int i = 0; i < count; ++i)
                {
                    var payload = NativeNumericPayload.Create(i % 4, i);
                    payload.Value =
                        i % 3 == 0 ? long.MinValue + i
                        : i % 3 == 1 ? long.MaxValue - i
                        : i * 17L;
                    Input[i + 1] = payload;
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
            for (int i = 0; i < Output.Length; ++i)
                Output[i] = Guard;
            for (int i = 0; i < Metrics.Length; ++i)
                Metrics[i] = -1;
            Metrics[0] = Metrics[Metrics.Length - 1] = MetricGuard;
            Entries = 0;
        }

        internal void Run(
            VectorBatchPath path,
            int batch,
            int rounds,
            FunctionPointer<VectorBatchDelegate> scalar,
            FunctionPointer<VectorBatchDelegate> vector,
            VectorBatchDelegate scalarInvoke,
            VectorBatchDelegate vectorInvoke
        )
        {
            Validate(path, batch, rounds, scalar, vector);
            if (
                path == VectorBatchPath.JobScalar
                || path == VectorBatchPath.JobVector
                || path == VectorBatchPath.JobPointerVector
            )
            {
                Schedule(path, batch, rounds, vector);
                Complete();
                return;
            }
            bool scalarPointer = path == VectorBatchPath.PointerScalarBatch;
            bool vectorPointer =
                path == VectorBatchPath.PointerVectorBatch
                || path == VectorBatchPath.PointerVectorOne;
            if (scalarPointer && scalarInvoke == null || vectorPointer && vectorInvoke == null)
                throw new ArgumentNullException(nameof(vectorInvoke));
            NativeNumericPayload* input = (NativeNumericPayload*)Input.GetUnsafeReadOnlyPtr();
            long* output = (long*)Output.GetUnsafePtr();
            int* metrics = (int*)Metrics.GetUnsafePtr();
            int width = path == VectorBatchPath.PointerVectorOne ? 1 : batch;
            for (int offset = 0; offset < Count; offset += width)
            {
                int count = Math.Min(width, Count - offset);
                int* record = metrics + 1 + Entries * 3;
                switch (path)
                {
                    case VectorBatchPath.ManagedScalar:
                        VectorPureKernels.Scalar(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                    case VectorBatchPath.ManagedVector:
                        VectorPureKernels.Vector(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                    case VectorBatchPath.BurstScalar:
                        VectorPureKernels.ScalarDirect(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                    case VectorBatchPath.BurstVector:
                        VectorPureKernels.VectorDirect(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                    case VectorBatchPath.PointerScalarBatch:
                        scalarInvoke(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                    case VectorBatchPath.PointerVectorBatch:
                    case VectorBatchPath.PointerVectorOne:
                        vectorInvoke(
                            count,
                            input + offset + 1,
                            output + offset + 1,
                            record,
                            rounds
                        );
                        break;
                }
                ++Entries;
            }
        }

        internal void Schedule(
            VectorBatchPath path,
            int batch,
            int rounds,
            FunctionPointer<VectorBatchDelegate> vector
        )
        {
            Validate(path, batch, rounds, default, vector);
            if (
                path != VectorBatchPath.JobScalar
                && path != VectorBatchPath.JobVector
                && path != VectorBatchPath.JobPointerVector
            )
                throw new ArgumentException("Only a job path can be scheduled.");
            _pending = new VectorPureJob
            {
                Input = Input,
                Output = Output,
                Metrics = Metrics,
                Function = vector,
                Count = Count,
                Batch = batch,
                Rounds = rounds,
                Path = path,
            }.Schedule();
            HasPending = true;
            Entries = (Count + batch - 1) / batch;
        }

        internal void Complete()
        {
            RequireThread();
            if (_disposed)
                throw new ObjectDisposedException(nameof(VectorBufferOwner));
            CompleteKnownJob();
        }

        private void CompleteKnownJob()
        {
            if (!HasPending)
                return;
            _pending.Complete();
            HasPending = false;
            CompletedJob = true;
            long sum = 0;
            for (int i = 1; i <= Count; ++i)
                sum = unchecked(sum + Output[i]);
            LastCompletedChecksum = sum;
        }

        public void Dispose()
        {
            RequireThread();
            if (_disposed)
                return;
            CompleteKnownJob();
            if (Metrics.IsCreated)
            {
                Metrics.Dispose();
                ++ReleaseCount;
            }
            if (Output.IsCreated)
            {
                Output.Dispose();
                ++ReleaseCount;
            }
            if (Input.IsCreated)
            {
                Input.Dispose();
                ++ReleaseCount;
            }
            _disposed = true;
        }

        private void Validate(
            VectorBatchPath path,
            int batch,
            int rounds,
            FunctionPointer<VectorBatchDelegate> scalar,
            FunctionPointer<VectorBatchDelegate> vector
        )
        {
            RequireIdle();
            if (
                path
                    is not (
                        VectorBatchPath.ManagedScalar
                        or VectorBatchPath.ManagedVector
                        or VectorBatchPath.BurstScalar
                        or VectorBatchPath.BurstVector
                        or VectorBatchPath.PointerScalarBatch
                        or VectorBatchPath.PointerVectorBatch
                        or VectorBatchPath.PointerVectorOne
                        or VectorBatchPath.JobScalar
                        or VectorBatchPath.JobVector
                        or VectorBatchPath.JobPointerVector
                    )
                || batch < 1
                || 256 < batch
                || rounds < 0
                || 64 < rounds
            )
                throw new ArgumentOutOfRangeException(nameof(batch));
            if (path == VectorBatchPath.PointerScalarBatch && !scalar.IsCreated)
                throw new ArgumentException("Scalar function pointer is missing.");
            if (
                (
                    path == VectorBatchPath.PointerVectorBatch
                    || path == VectorBatchPath.PointerVectorOne
                    || path == VectorBatchPath.JobPointerVector
                ) && !vector.IsCreated
            )
                throw new ArgumentException("Vector function pointer is missing.");
        }

        private void RequireIdle()
        {
            RequireThread();
            if (_disposed)
                throw new ObjectDisposedException(nameof(VectorBufferOwner));
            if (HasPending)
                throw new InvalidOperationException(
                    "Complete the known job before reset or pointer access."
                );
        }

        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException(
                    "Owner operations are confined to the creating main thread."
                );
        }
    }
}
#endif
