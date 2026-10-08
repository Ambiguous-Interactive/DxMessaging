// cspell:ignore Cdecl
#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT && DXM_505_MATHEMATICS_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using global::AOT;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;
    using global::Unity.Mathematics;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void VectorBatchDelegate(
        int count,
        NativeNumericPayload* input,
        long* output,
        int* metrics,
        int rounds
    );

    public enum VectorBatchPath
    {
        ManagedScalar,
        ManagedVector,
        BurstScalar,
        BurstVector,
        PointerScalarBatch,
        PointerVectorBatch,
        PointerVectorOne,
        JobScalar,
        JobVector,
        JobPointerVector,
    }

    [BurstCompile]
    public static unsafe class VectorPureKernels
    {
        [BurstCompile(CompileSynchronously = true)]
        public static void ScalarDirect(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        ) => Scalar(count, input, output, metrics, rounds);

        [BurstCompile(CompileSynchronously = true)]
        public static void VectorDirect(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        ) => Vector(count, input, output, metrics, rounds);

        [BurstCompile(CompileSynchronously = true, DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(VectorBatchDelegate))]
        public static void ScalarPointer(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        ) => Scalar(count, input, output, metrics, rounds);

        [BurstCompile(CompileSynchronously = true, DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(VectorBatchDelegate))]
        public static void VectorPointer(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        ) => Vector(count, input, output, metrics, rounds);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Scalar(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        )
        {
            int mode = 0;
            NativeProducerMode.MarkManaged(ref mode);
            metrics[0] = mode;
            metrics[1] = 0;
            metrics[2] = count;
            for (int i = 0; i < count; ++i)
                output[i] = PureBatchKernels.Compute(input[i], rounds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Vector(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* metrics,
            int rounds
        )
        {
            int mode = 0;
            NativeProducerMode.MarkManaged(ref mode);
            metrics[0] = mode;
            int i = 0,
                groups = 0;
            unchecked
            {
                for (; i + 4 <= count; i += 4)
                {
                    ulong a = (ulong)PureBatchKernels.Compute(input[i], 0);
                    ulong b = (ulong)PureBatchKernels.Compute(input[i + 1], 0);
                    ulong c = (ulong)PureBatchKernels.Compute(input[i + 2], 0);
                    ulong d = (ulong)PureBatchKernels.Compute(input[i + 3], 0);
                    uint4 low = new((uint)a, (uint)b, (uint)c, (uint)d);
                    uint4 high = new(
                        (uint)(a >> 32),
                        (uint)(b >> 32),
                        (uint)(c >> 32),
                        (uint)(d >> 32)
                    );
                    for (int round = 0; round < rounds; ++round)
                    {
                        uint4 mixedLow = low ^ ((low >> 17) | (high << 15));
                        uint4 mixedHigh = high ^ (high >> 17);
                        uint4 productLow = mixedLow * 0x4c957f2du;
                        uint4 productHigh =
                            mixedHigh * 0x4c957f2du
                            + mixedLow * 0x5851f42du
                            + MultiplyHigh(mixedLow, 0x4c957f2du);
                        low = productLow + 0xf767814fu;
                        high =
                            productHigh
                            + 0x14057b7eu
                            + math.select(new uint4(0u), new uint4(1u), low < productLow);
                    }
                    output[i] = (long)(((ulong)high.x << 32) | low.x);
                    output[i + 1] = (long)(((ulong)high.y << 32) | low.y);
                    output[i + 2] = (long)(((ulong)high.z << 32) | low.z);
                    output[i + 3] = (long)(((ulong)high.w << 32) | low.w);
                    ++groups;
                }
                int tails = count - i;
                for (; i < count; ++i)
                    output[i] = PureBatchKernels.Compute(input[i], rounds);
                metrics[1] = groups;
                metrics[2] = tails;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 MultiplyHigh(uint4 value, uint multiplier)
        {
            unchecked
            {
                uint4 low = value & 65535u;
                uint4 high = value >> 16;
                uint4 p0 = low * (multiplier & 65535u);
                uint4 p1 = low * (multiplier >> 16);
                uint4 p2 = high * (multiplier & 65535u);
                uint4 p3 = high * (multiplier >> 16);
                uint4 middle = (p0 >> 16) + (p1 & 65535u) + (p2 & 65535u);
                return p3 + (p1 >> 16) + (p2 >> 16) + (middle >> 16);
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct VectorPureJob : IJob
    {
        [ReadOnly]
        public NativeArray<NativeNumericPayload> Input;

        [WriteOnly]
        public NativeArray<long> Output;

        [WriteOnly]
        public NativeArray<int> Metrics;
        public FunctionPointer<VectorBatchDelegate> Function;
        public int Count;
        public int Batch;
        public int Rounds;
        public VectorBatchPath Path;

        public void Execute()
        {
            NativeNumericPayload* input = (NativeNumericPayload*)Input.GetUnsafeReadOnlyPtr();
            long* output = (long*)Output.GetUnsafePtr();
            int* metrics = (int*)Metrics.GetUnsafePtr();
            int entry = 0;
            for (int offset = 0; offset < Count; offset += Batch)
            {
                int count = System.Math.Min(Batch, Count - offset);
                if (Path == VectorBatchPath.JobPointerVector)
                    Function.Invoke(
                        count,
                        input + offset + 1,
                        output + offset + 1,
                        metrics + 1 + entry * 3,
                        Rounds
                    );
                else if (Path == VectorBatchPath.JobVector)
                    VectorPureKernels.Vector(
                        count,
                        input + offset + 1,
                        output + offset + 1,
                        metrics + 1 + entry * 3,
                        Rounds
                    );
                else
                    VectorPureKernels.Scalar(
                        count,
                        input + offset + 1,
                        output + offset + 1,
                        metrics + 1 + entry * 3,
                        Rounds
                    );
                ++entry;
            }
        }
    }
}
#endif
