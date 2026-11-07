// cspell:ignore Cdecl
#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using global::AOT;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void NumericBatchDelegate(
        int count,
        NativeNumericPayload* input,
        long* output,
        int* mode,
        int rounds
    );

    public enum PureBatchPath
    {
        Managed,
        BurstDirect,
        PointerBatch,
        PointerScalar,
        JobDirect,
        JobPointer,
    }

    [BurstCompile]
    public static unsafe class PureBatchKernels
    {
        public static void Managed(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds
        )
        {
            Process(count, input, output, mode, rounds);
        }

        [BurstCompile(CompileSynchronously = true)]
        public static void Direct(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds
        )
        {
            Process(count, input, output, mode, rounds);
        }

        [BurstCompile(CompileSynchronously = true, DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NumericBatchDelegate))]
        public static void Pointer(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds
        )
        {
            Process(count, input, output, mode, rounds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Process(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds
        )
        {
            int managed = 0;
            NativeProducerMode.MarkManaged(ref managed);
            *mode = managed;
            for (int index = 0; index < count; ++index)
            {
                output[index] = Compute(input[index], rounds);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Compute(NativeNumericPayload payload, int rounds)
        {
            unchecked
            {
                ulong value =
                    (ulong)payload.Value
                    + (ulong)((long)payload.Producer * 97)
                    + (ulong)(long)payload.Sequence;
                for (int round = 0; round < rounds; ++round)
                {
                    value = (value ^ (value >> 17)) * 6364136223846793005UL + 1442695040888963407UL;
                }
                return (long)value;
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct PureBatchJob : IJob
    {
        [ReadOnly]
        public NativeArray<NativeNumericPayload> Input;

        [WriteOnly]
        public NativeArray<long> Output;

        [WriteOnly]
        public NativeArray<int> Modes;
        public FunctionPointer<NumericBatchDelegate> Function;
        public int Count;
        public int Batch;
        public int Rounds;
        public bool UsePointer;

        public void Execute()
        {
            NativeNumericPayload* input = (NativeNumericPayload*)Input.GetUnsafeReadOnlyPtr();
            long* output = (long*)Output.GetUnsafePtr();
            int* modes = (int*)Modes.GetUnsafePtr();
            int entry = 0;
            for (int offset = 0; offset < Count; offset += Batch)
            {
                int count = System.Math.Min(Batch, Count - offset);
                if (UsePointer)
                {
                    Function.Invoke(
                        count,
                        input + offset + 1,
                        output + offset + 1,
                        modes + entry,
                        Rounds
                    );
                }
                else
                {
                    PureBatchKernels.Process(
                        count,
                        input + offset + 1,
                        output + offset + 1,
                        modes + entry,
                        Rounds
                    );
                }
                ++entry;
            }
        }
    }
}
#endif
