#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;

    public enum PureParallelPath
    {
        ParallelFor,
        ParallelBatch,
    }

    internal static class PureParallelOperation
    {
        internal static void Process(
            int index,
            int rounds,
            int thread,
            NativeArray<NativeNumericPayload> input,
            NativeArray<long> output,
            NativeArray<int> modes,
            NativeArray<int> threads
        )
        {
            int managed = 0;
            NativeProducerMode.MarkManaged(ref managed);
            output[index] = PureBatchKernels.Compute(input[index], rounds);
            modes[index] = managed;
            threads[index] = thread;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct PureParallelForJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<NativeNumericPayload> Input;

        [WriteOnly]
        public NativeArray<long> Output;

        [WriteOnly]
        public NativeArray<int> Modes;

        [WriteOnly]
        public NativeArray<int> Threads;
        public int Rounds;

        [NativeSetThreadIndex]
        public int ThreadIndex;

        public void Execute(int index)
        {
            PureParallelOperation.Process(
                index,
                Rounds,
                ThreadIndex,
                Input,
                Output,
                Modes,
                Threads
            );
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct PureParallelBatchJob : IJobParallelForBatch
    {
        [ReadOnly]
        public NativeArray<NativeNumericPayload> Input;

        [WriteOnly]
        public NativeArray<long> Output;

        [WriteOnly]
        public NativeArray<int> Modes;

        [WriteOnly]
        public NativeArray<int> Threads;
        public int Rounds;

        [NativeSetThreadIndex]
        public int ThreadIndex;

        public void Execute(int startIndex, int count)
        {
            for (int index = startIndex; index < startIndex + count; ++index)
            {
                PureParallelOperation.Process(
                    index,
                    Rounds,
                    ThreadIndex,
                    Input,
                    Output,
                    Modes,
                    Threads
                );
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct PureSubsystemJob : IJob
    {
        [ReadOnly]
        public NativeArray<NativeNumericPayload> Input;

        [WriteOnly]
        public NativeArray<long> Output;

        [WriteOnly]
        public NativeArray<int> Modes;

        [WriteOnly]
        public NativeArray<int> Threads;
        public int Rounds;

        [NativeSetThreadIndex]
        public int ThreadIndex;

        public void Execute()
        {
            for (int index = 0; index < Input.Length; ++index)
            {
                PureParallelOperation.Process(
                    index,
                    Rounds,
                    ThreadIndex,
                    Input,
                    Output,
                    Modes,
                    Threads
                );
            }
        }
    }

    // A test reference owns root arrays; interior views are temporary non-owning job arguments.
    internal sealed class ParallelPureBufferOwner : IDisposable
    {
        internal NativeArray<NativeNumericPayload> Input;
        internal NativeArray<long> Output;
        internal NativeArray<int> Modes;
        internal NativeArray<int> Threads;
        internal readonly int Count;
        internal bool HasPending;
        internal bool CompletedJob;
        internal bool CompletedNative;
        internal int ReleaseCount;
        private JobHandle _pending;
        private bool _disposed;
        internal const long Guard = 1234605616436508552L;
        internal const int MarkerGuard = -77777;

        internal ParallelPureBufferOwner(int count, int offset = 0, int logicalProducer = -1)
        {
            if (count < 1 || 4096 < count || offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            Count = count;
            try
            {
                Input = new NativeArray<NativeNumericPayload>(count + 2, Allocator.Persistent);
                Output = new NativeArray<long>(count + 2, Allocator.Persistent);
                Modes = new NativeArray<int>(count + 2, Allocator.Persistent);
                Threads = new NativeArray<int>(count + 2, Allocator.Persistent);
                Input[0] = Input[count + 1] = InputGuard();
                for (int index = 0; index < count; ++index)
                {
                    int sequence = offset + index;
                    Input[index + 1] = new NativeNumericPayload
                    {
                        Producer = logicalProducer < 0 ? sequence % 4 : logicalProducer,
                        Sequence = sequence,
                        Value =
                            sequence % 3 == 0 ? long.MinValue + sequence
                            : sequence % 3 == 1 ? long.MaxValue - sequence
                            : sequence * 17L,
                    };
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
            for (int index = 0; index < Count + 2; ++index)
            {
                Output[index] = Guard;
                Modes[index] = Threads[index] = index == 0 || index == Count + 1 ? MarkerGuard : -1;
            }
        }

        internal void Schedule(PureParallelPath path, int grain, int rounds)
        {
            Validate(grain, rounds);
            if (path != PureParallelPath.ParallelFor && path != PureParallelPath.ParallelBatch)
            {
                throw new ArgumentOutOfRangeException(nameof(path));
            }
            if (path == PureParallelPath.ParallelFor)
            {
                _pending = new PureParallelForJob
                {
                    Input = Input.GetSubArray(1, Count),
                    Output = Output.GetSubArray(1, Count),
                    Modes = Modes.GetSubArray(1, Count),
                    Threads = Threads.GetSubArray(1, Count),
                    Rounds = rounds,
                }.Schedule(Count, grain);
            }
            else
            {
                PureParallelBatchJob job = new()
                {
                    Input = Input.GetSubArray(1, Count),
                    Output = Output.GetSubArray(1, Count),
                    Modes = Modes.GetSubArray(1, Count),
                    Threads = Threads.GetSubArray(1, Count),
                    Rounds = rounds,
                };
#if DXM_505_COLLECTIONS_PARALLEL_SCHEDULE_PRESENT
                _pending = job.ScheduleParallel(Count, grain);
#else
                _pending = job.ScheduleBatch(Count, grain);
#endif
            }
            HasPending = true;
        }

        internal void ScheduleSubsystem(int rounds)
        {
            Validate(1, rounds);
            _pending = new PureSubsystemJob
            {
                Input = Input.GetSubArray(1, Count),
                Output = Output.GetSubArray(1, Count),
                Modes = Modes.GetSubArray(1, Count),
                Threads = Threads.GetSubArray(1, Count),
                Rounds = rounds,
            }.Schedule();
            HasPending = true;
        }

        internal void Complete()
        {
            if (!HasPending)
            {
                return;
            }
            _pending.Complete();
            HasPending = false;
            CompletedJob = true;
            CompletedNative = true;
            for (int index = 1; index <= Count; ++index)
            {
                CompletedNative &= Modes[index] == 0;
            }
        }

        internal long[] Results()
        {
            RequireIdle();
            return Output.GetSubArray(1, Count).ToArray();
        }

        private void Validate(int grain, int rounds)
        {
            RequireIdle();
            if (grain < 1 || 256 < grain || rounds < 0 || 3 < rounds)
            {
                throw new ArgumentOutOfRangeException(nameof(grain));
            }
        }

        private void RequireIdle()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ParallelPureBufferOwner));
            }
            if (HasPending)
            {
                throw new InvalidOperationException(
                    "Complete the known job before reading, reset or reuse."
                );
            }
        }

        internal static NativeNumericPayload InputGuard()
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
            if (Threads.IsCreated)
            {
                Threads.Dispose();
                ++ReleaseCount;
            }
            _disposed = true;
        }
    }
}
#endif
