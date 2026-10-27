// cspell:ignore Cdecl
#if UNITY_6000_5 && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Runtime.InteropServices;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>Checks the existing native snapshot API for usable stack data in one frozen player.</summary>
    /// <remarks>
    /// 2026-10-27: the qualified Unity 6000.5.2f1 x64 player exports both snapshot functions.
    /// Its PDB defines this 96-byte header and 24-byte section layout. Validate controlled
    /// roots and copied heap bytes before treating empty stack data as a capability verdict.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
    public sealed class ZNativeSnapshotStackCapabilityTests
    {
        [Test]
        [Order(int.MaxValue)]
        public void NativeSnapshotContainsStackBytesForRootAttribution()
        {
            Assert.That(Application.unityVersion, Is.EqualTo("6000.5.2f1"), "Frozen native ABI.");
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsPlayer));
            Assert.That(Debug.isDebugBuild, Is.False, "Keep the observed nondevelopment player.");
            Assert.That(IntPtr.Size, Is.EqualTo(8), "Frozen Windows x64 ABI.");
            byte[] positive = new byte[32];
            positive[0] = 0xA9;
            positive[31] = 0x51;
            byte[] negative = new byte[32];
            GCHandle positiveHandle = default;
            GCHandle negativeHandle = default;
            IntPtr snapshot = IntPtr.Zero;
            try
            {
                positiveHandle = GCHandle.Alloc(positive, GCHandleType.Pinned);
                negativeHandle = GCHandle.Alloc(negative, GCHandleType.Pinned);
                IntPtr positiveData = positiveHandle.AddrOfPinnedObject();
                IntPtr negativeData = negativeHandle.AddrOfPinnedObject();
                negativeHandle.Free();
                snapshot = CaptureMemorySnapshot();
                long stackBytes = ValidateSnapshotControls(snapshot, positiveData, negativeData);
                Assert.That(
                    stackBytes,
                    Is.GreaterThan(0),
                    "Controlled snapshot is valid, but this native API supplies no stack bytes."
                );
            }
            finally
            {
                try
                {
                    if (snapshot != IntPtr.Zero)
                    {
                        FreeMemorySnapshot(snapshot);
                    }
                }
                finally
                {
                    if (positiveHandle.IsAllocated)
                    {
                        positiveHandle.Free();
                    }
                    if (negativeHandle.IsAllocated)
                    {
                        negativeHandle.Free();
                    }
                    GC.KeepAlive(positive);
                    GC.KeepAlive(negative);
                }
            }
        }

        private static long ValidateSnapshotControls(
            IntPtr snapshot,
            IntPtr positiveData,
            IntPtr negativeData
        )
        {
            Assert.That(snapshot, Is.Not.EqualTo(IntPtr.Zero), "Native snapshot exists.");
            Assert.That(Marshal.ReadInt32(snapshot, 64), Is.EqualTo(8), "Pointer size.");
            Assert.That(Marshal.ReadInt32(snapshot, 68), Is.EqualTo(16), "Object header size.");
            Assert.That(Marshal.ReadInt32(snapshot, 72), Is.EqualTo(32), "Array header size.");
            Assert.That(Marshal.ReadInt32(snapshot, 76), Is.EqualTo(16), "Array bounds offset.");
            Assert.That(Marshal.ReadInt32(snapshot, 80), Is.EqualTo(24), "Array length offset.");
            Assert.That(Marshal.ReadInt32(snapshot, 84), Is.EqualTo(16), "Allocation granularity.");
            int heapCount = Marshal.ReadInt32(snapshot);
            int stackCount = Marshal.ReadInt32(snapshot, 16);
            int typeCount = Marshal.ReadInt32(snapshot, 32);
            int handleCount = Marshal.ReadInt32(snapshot, 48);
            Assert.That(heapCount, Is.InRange(1, 65536), "Bounded heap section count.");
            Assert.That(stackCount, Is.InRange(0, 65536), "Bounded stack section count.");
            Assert.That(typeCount, Is.GreaterThan(0), "Snapshot includes type metadata.");
            Assert.That(handleCount, Is.InRange(1, 1048576), "Bounded strong-handle count.");
            IntPtr heap = Marshal.ReadIntPtr(snapshot, 8);
            IntPtr stacks = Marshal.ReadIntPtr(snapshot, 24);
            IntPtr handles = Marshal.ReadIntPtr(snapshot, 56);
            Assert.That(heap, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(handles, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(Marshal.ReadIntPtr(snapshot, 40), Is.Not.EqualTo(IntPtr.Zero));
            long positiveObject = checked(positiveData.ToInt64() - 32);
            long negativeObject = checked(negativeData.ToInt64() - 32);
            Assert.That(positiveObject, Is.Not.EqualTo(negativeObject));
            bool positiveRoot = false;
            bool negativeRoot = false;
            for (int index = 0; index < handleCount; ++index)
            {
                long target = Marshal.ReadInt64(handles, checked(index * 8));
                positiveRoot |= target == positiveObject;
                negativeRoot |= target == negativeObject;
            }
            bool positiveHeap = false;
            for (int index = 0; index < heapCount; ++index)
            {
                IntPtr section = IntPtr.Add(heap, checked(index * 24));
                long start = Marshal.ReadInt64(section);
                uint size = unchecked((uint)Marshal.ReadInt32(section, 8));
                IntPtr bytes = Marshal.ReadIntPtr(section, 16);
                Assert.That(size, Is.GreaterThan(0), "Heap section has bytes.");
                Assert.That(bytes, Is.Not.EqualTo(IntPtr.Zero));
                if (
                    start <= positiveObject
                    && checked(positiveObject + 64) <= checked(start + size)
                )
                {
                    int offset = checked((int)(positiveObject - start));
                    positiveHeap |=
                        Marshal.ReadInt64(bytes, checked(offset + 24)) == 32
                        && Marshal.ReadByte(bytes, checked(offset + 32)) == 0xA9
                        && Marshal.ReadByte(bytes, checked(offset + 63)) == 0x51;
                }
            }
            long stackBytes = 0;
            if (0 < stackCount)
            {
                Assert.That(stacks, Is.Not.EqualTo(IntPtr.Zero));
                for (int index = 0; index < stackCount; ++index)
                {
                    IntPtr section = IntPtr.Add(stacks, checked(index * 24));
                    uint size = unchecked((uint)Marshal.ReadInt32(section, 8));
                    if (size == 0)
                    {
                        continue;
                    }
                    Assert.That(Marshal.ReadIntPtr(section, 16), Is.Not.EqualTo(IntPtr.Zero));
                    stackBytes = checked(stackBytes + size);
                }
            }
            TestContext.Out.WriteLine(
                $"SNAPSHOT_CAPABILITY heapSections={heapCount}, stackSections={stackCount}, "
                    + $"types={typeCount}, strongHandles={handleCount}, positiveRoot={positiveRoot}, "
                    + $"freedRoot={negativeRoot}, positiveHeap={positiveHeap}, stackBytes={stackBytes}."
            );
            Assert.That(positiveRoot, Is.True, "Pinned positive root appears in strong handles.");
            Assert.That(
                negativeRoot,
                Is.False,
                "Freed negative handle is absent from strong handles."
            );
            Assert.That(
                positiveHeap,
                Is.True,
                "Copied heap contains the positive array and markers."
            );
            return stackBytes;
        }

        [DllImport(
            "GameAssembly",
            EntryPoint = "il2cpp_capture_memory_snapshot",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        private static extern IntPtr CaptureMemorySnapshot();

        [DllImport(
            "GameAssembly",
            EntryPoint = "il2cpp_free_captured_memory_snapshot",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        private static extern void FreeMemorySnapshot(IntPtr snapshot);
    }
}
#endif
