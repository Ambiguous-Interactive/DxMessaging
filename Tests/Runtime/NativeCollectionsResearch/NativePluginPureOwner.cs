// cspell:ignore Cdecl libSystem dlopen dlsym dlclose RTLD
#if UNITY_EDITOR_OSX && UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using global::Unity.Collections;
    using global::Unity.Collections.LowLevel.Unsafe;

    public enum NativePluginPath
    {
        Managed,
        NativeBatch,
        NativeScalar,
    }

    internal sealed unsafe class NativePluginLibrary : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AbiDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LayoutDelegate(int field);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProcessDelegate(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds,
            int inputCapacity,
            int outputCapacity
        );

        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string path, int flags);

        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private IntPtr _handle;
        private AbiDelegate _abi;
        private LayoutDelegate _layout;
        private ProcessDelegate _process;
        private bool _attempted;
        private bool _disposed;
        internal int OpenCount { get; private set; }
        internal int CloseCount { get; private set; }
        internal int LastCloseStatus { get; private set; } = -1;
        internal bool Ready => !_disposed && _process != null;
        internal bool RootsCleared =>
            _handle == IntPtr.Zero && _abi == null && _layout == null && _process == null;

        internal void Load(string path)
        {
            RequireLive();
            if (_attempted)
                throw new InvalidOperationException("Library load already attempted.");
            _attempted = true;
            try
            {
                _handle = dlopen(path, 2 | 4); // RTLD_NOW | RTLD_LOCAL on Darwin.
                if (_handle == IntPtr.Zero)
                    throw new InvalidOperationException("Native library open failed.");
                ++OpenCount;
                _abi = Resolve<AbiDelegate>("dxm_plugin_abi");
                _layout = Resolve<LayoutDelegate>("dxm_plugin_layout");
                _process = Resolve<ProcessDelegate>("dxm_plugin_process");
                if (
                    _abi() != 38801
                    || _layout(0) != 16
                    || _layout(1) != 0
                    || _layout(2) != 4
                    || _layout(3) != 8
                )
                    throw new InvalidOperationException(
                        "Unsupported native ABI or payload layout."
                    );
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private T Resolve<T>(string symbol)
            where T : Delegate
        {
            IntPtr address = dlsym(_handle, symbol);
            if (address == IntPtr.Zero)
                throw new InvalidOperationException("Required native export is missing: " + symbol);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        internal int Layout(int field)
        {
            RequireReady();
            return _layout(field);
        }

        internal int Invoke(
            int count,
            NativeNumericPayload* input,
            long* output,
            int* mode,
            int rounds,
            int inputCapacity,
            int outputCapacity
        )
        {
            RequireReady();
            return _process(count, input, output, mode, rounds, inputCapacity, outputCapacity);
        }

        private void RequireLive()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                throw new ObjectDisposedException(nameof(NativePluginLibrary));
        }

        private void RequireReady()
        {
            RequireLive();
            if (!Ready)
                throw new InvalidOperationException("Native library is not ready.");
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                return;
            _disposed = true;
            _process = null;
            _layout = null;
            _abi = null;
            if (_handle != IntPtr.Zero)
            {
                IntPtr handle = _handle;
                _handle = IntPtr.Zero;
                LastCloseStatus = dlclose(handle);
                ++CloseCount;
                if (LastCloseStatus != 0)
                    throw new InvalidOperationException("Native library reference release failed.");
            }
        }
    }

    internal sealed unsafe class NativePluginPureOwner : IDisposable
    {
        internal const long Guard = 1234605616436508552L;
        internal const int ModeGuard = 777777;
        internal NativeArray<NativeNumericPayload> Input;
        internal NativeArray<long> Output;
        internal NativeArray<int> Modes;
        internal readonly int Count;
        internal readonly NativePluginLibrary Library = new();
        internal int Entries { get; private set; }
        internal int ReleaseCount { get; private set; }
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private bool _disposed;

        internal NativePluginPureOwner(int count, string path)
        {
            if (count < 0 || 4096 < count)
                throw new ArgumentOutOfRangeException(nameof(count));
            Count = count;
            try
            {
                Library.Load(path);
                Input = new NativeArray<NativeNumericPayload>(count + 2, Allocator.Persistent);
                Output = new NativeArray<long>(count + 2, Allocator.Persistent);
                Modes = new NativeArray<int>(count + 2, Allocator.Persistent);
                Input[0] = Input[count + 1] = new NativeNumericPayload
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

        private void RequireLive()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                throw new ObjectDisposedException(nameof(NativePluginPureOwner));
        }

        internal void Reset()
        {
            RequireLive();
            for (int i = 0; i < Output.Length; ++i)
                Output[i] = Guard;
            for (int i = 0; i < Modes.Length; ++i)
                Modes[i] = -1;
            Modes[0] = Modes[Modes.Length - 1] = ModeGuard;
            Entries = 0;
        }

        internal void Run(NativePluginPath path, int batch, int rounds)
        {
            RequireLive();
            if (Entries != 0)
                throw new InvalidOperationException("Reset completed buffers before reuse.");
            if (
                path != NativePluginPath.Managed
                && path != NativePluginPath.NativeBatch
                && path != NativePluginPath.NativeScalar
            )
                throw new ArgumentOutOfRangeException(nameof(path));
            if (batch < 1 || 256 < batch)
                throw new ArgumentOutOfRangeException(nameof(batch));
            if (rounds < 0 || 64 < rounds)
                throw new ArgumentOutOfRangeException(nameof(rounds));
            NativeNumericPayload* input = (NativeNumericPayload*)Input.GetUnsafeReadOnlyPtr();
            long* output = (long*)Output.GetUnsafePtr();
            int* modes = (int*)Modes.GetUnsafePtr();
            int width = path == NativePluginPath.NativeScalar ? 1 : batch;
            for (int offset = 0; offset < Count; offset += width)
            {
                int count = Math.Min(width, Count - offset);
                if (path == NativePluginPath.Managed)
                    PureBatchKernels.Managed(
                        count,
                        input + 1 + offset,
                        output + 1 + offset,
                        modes + 1 + Entries,
                        rounds
                    );
                else if (
                    Library.Invoke(
                        count,
                        input + 1 + offset,
                        output + 1 + offset,
                        modes + 1 + Entries,
                        rounds,
                        count,
                        count
                    ) != 0
                )
                    throw new InvalidOperationException(
                        "Native processing rejected valid owned buffers."
                    );
                ++Entries;
            }
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Creating thread required.");
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                Library.Dispose();
            }
            finally
            {
                if (Input.IsCreated)
                {
                    Input.Dispose();
                    Input = default;
                    ++ReleaseCount;
                }
                if (Output.IsCreated)
                {
                    Output.Dispose();
                    Output = default;
                    ++ReleaseCount;
                }
                if (Modes.IsCreated)
                {
                    Modes.Dispose();
                    Modes = default;
                    ++ReleaseCount;
                }
            }
        }
    }
}
#endif
