#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using UnityEngine;
    using UnityEngine.Rendering;

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuWordPair
    {
        public uint Low;
        public uint High;
        public long Signed => unchecked((long)(((ulong)High << 32) | Low));

        public static GpuWordPair From(long value) =>
            new() { Low = unchecked((uint)value), High = unchecked((uint)((ulong)value >> 32)) };
    }

    internal sealed class GpuPureOwner : IDisposable
    {
        internal const long Guard = 1234605616436508552L;
        internal readonly int Count;
        internal NativeNumericPayload[] OriginalInput { get; private set; }
        internal NativeNumericPayload[] ObservedInput { get; private set; }
        internal GpuWordPair[] ObservedStage { get; private set; }
        internal GpuWordPair[] ObservedFinal { get; private set; }
        internal int Dispatches { get; private set; }
        internal int Groups { get; private set; }
        internal int CompletedSubmissions { get; private set; }
        internal int ReleaseCount { get; private set; }
        internal bool HasPending => _pending;
        internal bool HasBuffers => _input != null && _stage != null && _final != null;
        internal bool HasResources =>
            _input != null || _stage != null || _final != null || _commands != null;
        internal bool HasReadbackError { get; private set; }
        internal bool ScratchCleared =>
            OriginalInput == null
            && ObservedInput == null
            && ObservedStage == null
            && ObservedFinal == null
            && _shader == null;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private ComputeShader _shader;
        private GraphicsBuffer _input,
            _stage,
            _final;
        private CommandBuffer _commands;
        private GraphicsFence _fence;
        private readonly AsyncGPUReadbackRequest[] _requests = new AsyncGPUReadbackRequest[3];
        private readonly bool[] _copied = new bool[3];
        private int _issued;
        private readonly int _process,
            _consume;
        private bool _pending,
            _submitted,
            _disposed,
            _completionRecorded;

        internal GpuPureOwner(int count, ComputeShader shader)
        {
            if (count < 0 || 4096 < count)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (shader == null)
                throw new ArgumentNullException(nameof(shader));
            Count = count;
            _shader = shader;
            try
            {
                _process = shader.FindKernel("Process");
                _consume = shader.FindKernel("Consume");
                _commands = new CommandBuffer { name = "DxMessaging GPU pure-data research" };
                _input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count + 2, 16);
                _stage = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count + 2, 8);
                _final = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count + 2, 8);
                OriginalInput = new NativeNumericPayload[count + 2];
                OriginalInput[0] = OriginalInput[count + 1] = new NativeNumericPayload
                {
                    Producer = -1,
                    Sequence = -2,
                    Value = Guard,
                };
                for (int i = 0; i < count; ++i)
                {
                    NativeNumericPayload payload = NativeNumericPayload.Create(i % 4, i);
                    payload.Value =
                        i % 3 == 0 ? long.MinValue + i
                        : i % 3 == 1 ? long.MaxValue - i
                        : i * 17L;
                    OriginalInput[i + 1] = payload;
                }
                Reset();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void RequireLive()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                throw new ObjectDisposedException(nameof(GpuPureOwner));
        }

        internal void Reset()
        {
            RequireLive();
            if (_pending)
                throw new InvalidOperationException("GPU work or readback is pending.");
            GpuWordPair[] initial = new GpuWordPair[Count + 2];
            for (int i = 0; i < initial.Length; ++i)
                initial[i] = GpuWordPair.From(Guard);
            _input.SetData(OriginalInput);
            _stage.SetData(initial);
            _final.SetData(initial);
            _commands.Clear();
            ObservedInput = null;
            ObservedStage = null;
            ObservedFinal = null;
            Array.Clear(_requests, 0, _requests.Length);
            Array.Clear(_copied, 0, _copied.Length);
            _issued = 0;
            _submitted = false;
            _completionRecorded = false;
            HasReadbackError = false;
            Dispatches = 0;
            Groups = 0;
        }

        internal bool TrySubmit(int batch, int rounds)
        {
            RequireLive();
            if (batch < 1 || 256 < batch)
                throw new ArgumentOutOfRangeException(nameof(batch));
            if (rounds < 0 || 64 < rounds)
                throw new ArgumentOutOfRangeException(nameof(rounds));
            if (_pending)
                return false;
            if (_submitted)
                throw new InvalidOperationException("Reset before another submission.");
            _commands.SetComputeBufferParam(_shader, _process, "Input", _input);
            _commands.SetComputeBufferParam(_shader, _process, "Stage", _stage);
            _commands.SetComputeBufferParam(_shader, _consume, "Stage", _stage);
            _commands.SetComputeBufferParam(_shader, _consume, "Final", _final);
            _commands.SetComputeIntParam(_shader, "ItemCount", Count);
            _commands.SetComputeIntParam(_shader, "Rounds", rounds);
            for (int offset = 0; offset < Count; offset += batch)
            {
                int count = Math.Min(batch, Count - offset);
                int groups = (count + 63) / 64;
                _commands.SetComputeIntParam(_shader, "Offset", offset);
                _commands.SetComputeIntParam(_shader, "ChunkCount", count);
                _commands.DispatchCompute(_shader, _process, groups, 1, 1);
                _commands.DispatchCompute(_shader, _consume, groups, 1, 1);
                Dispatches += 2;
                Groups += groups * 2;
            }
            _fence = _commands.CreateGraphicsFence(
                GraphicsFenceType.CPUSynchronisation,
                SynchronisationStageFlags.AllGPUOperations
            );
            _submitted = true;
            _pending = true;
            Graphics.ExecuteCommandBuffer(_commands);
            return true;
        }

        internal void BeginUntimedObservation()
        {
            RequireLive();
            if (!_submitted || _issued != 0)
                throw new InvalidOperationException(
                    "Exactly one observation per submission required."
                );
            _pending = true;
            _requests[_issued] = AsyncGPUReadback.Request(_input);
            ++_issued;
            _requests[_issued] = AsyncGPUReadback.Request(_stage);
            ++_issued;
            _requests[_issued] = AsyncGPUReadback.Request(_final);
            ++_issued;
        }

        internal bool PollCompletion()
        {
            RequireLive();
            if (!_pending)
                return true;
            for (int i = 0; i < _issued; ++i)
            {
                if (_copied[i] || !_requests[i].done)
                    continue;
                _copied[i] = true;
                if (_requests[i].hasError)
                    HasReadbackError = true;
                else if (i == 0)
                    ObservedInput = _requests[i].GetData<NativeNumericPayload>().ToArray();
                else if (i == 1)
                    ObservedStage = _requests[i].GetData<GpuWordPair>().ToArray();
                else
                    ObservedFinal = _requests[i].GetData<GpuWordPair>().ToArray();
            }
            if (!_fence.passed)
                return false;
            for (int i = 0; i < _issued; ++i)
                if (!_copied[i])
                    return false;
            _pending = false;
            if (!_completionRecorded)
            {
                ++CompletedSubmissions;
                _completionRecorded = true;
            }
            return true;
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                return;
            if (_pending)
                throw new InvalidOperationException(
                    "Observe actual GPU completion before release."
                );
            if (_commands != null)
            {
                _commands.Release();
                _commands = null;
                ++ReleaseCount;
            }
            if (_input != null)
            {
                _input.Dispose();
                _input = null;
                ++ReleaseCount;
            }
            if (_stage != null)
            {
                _stage.Dispose();
                _stage = null;
                ++ReleaseCount;
            }
            if (_final != null)
            {
                _final.Dispose();
                _final = null;
                ++ReleaseCount;
            }
            OriginalInput = null;
            ObservedInput = null;
            ObservedStage = null;
            ObservedFinal = null;
            _shader = null;
            Array.Clear(_requests, 0, _requests.Length);
            _disposed = true;
        }
    }
}
#endif
