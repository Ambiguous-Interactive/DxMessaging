#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using DxMessaging.Core.Messages;
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;
    using global::Unity.Jobs;

    public struct NativeNumericPayload : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
    {
        public int Producer;
        public int Sequence;
        public long Value;

        public static NativeNumericPayload Create(int producer, int sequence)
        {
            return new NativeNumericPayload
            {
                Producer = producer,
                Sequence = sequence,
                Value = ((long)producer << 32) | (uint)sequence,
            };
        }
    }

    public static class NativeProducerMode
    {
        [BurstDiscard]
        public static void MarkManaged(ref int marker)
        {
            marker = 1;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct QueueProducerJob : IJobParallelFor
    {
        public NativeQueue<NativeNumericPayload>.ParallelWriter Writer;

        [WriteOnly]
        public NativeArray<int> Modes;

        [WriteOnly]
        public NativeArray<int> WorkerIds;
        public int Items;

        [NativeSetThreadIndex]
        public int ThreadIndex;

        public void Execute(int index)
        {
            int managed = 0;
            NativeProducerMode.MarkManaged(ref managed);
            Modes[index] = managed;
            WorkerIds[index] = ThreadIndex;
            for (int sequence = 0; sequence < Items; ++sequence)
            {
                Writer.Enqueue(NativeNumericPayload.Create(index, sequence));
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct StreamProducerJob : IJobParallelFor
    {
        public NativeStream.Writer Writer;

        [WriteOnly]
        public NativeArray<int> Modes;

        [WriteOnly]
        public NativeArray<int> WorkerIds;
        public int Items;

        [NativeSetThreadIndex]
        public int ThreadIndex;

        public void Execute(int index)
        {
            int managed = 0;
            NativeProducerMode.MarkManaged(ref managed);
            Modes[index] = managed;
            WorkerIds[index] = ThreadIndex;
            Writer.BeginForEachIndex(index);
            for (int sequence = 0; sequence < Items; ++sequence)
            {
                Writer.Write(NativeNumericPayload.Create(index, sequence));
            }
            Writer.EndForEachIndex();
        }
    }
}
#endif
