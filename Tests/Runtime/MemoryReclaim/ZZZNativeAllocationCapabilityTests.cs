// cspell:ignore Cdecl
#if UNITY_6000_5 && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Runtime.InteropServices;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>Checks native allocation-event observability with forced allocation controls.</summary>
    /// <remarks>
    /// 2026-10-27: this untimed test does not replace GC.Alloc measurements or measure production
    /// allocation cost. The native counter uses an opaque static context and retains one VM-owned
    /// profiler descriptor until player shutdown. No managed object address enters that context.
    /// </remarks>
    [TestFixture]
    [Category("NativeAllocationCapability")]
    public sealed class ZZZNativeAllocationCapabilityTests
    {
        private const string CounterLibrary = "DxmNativeAllocationCounter";
        private const int Iterations = 32;
        private const int Warmups = 64;
        private const int Kinds = 5;

        [Test]
        [Order(int.MaxValue)]
        public void NativeAllocationEventsObserveForcedObjectsAndDetach()
        {
            Assert.That(Application.unityVersion, Is.EqualTo("6000.5.2f1"), "Frozen native ABI.");
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsPlayer));
            Assert.That(Debug.isDebugBuild, Is.False, "Keep the existing nondevelopment profile.");
            Assert.That(IntPtr.Size, Is.EqualTo(8), "Frozen Windows x64 ABI.");
            object[] sink = new object[Kinds * Iterations];
            object[] detachedSink = new object[Iterations];
            long[] counts = new long[Kinds];
            long[] foreign = new long[Kinds];
            int installed = DxmCounterInstall();
            Assert.That(
                installed,
                Is.Zero,
                "The native counter must bind all three profiler APIs."
            );
            int detached = -1;
            try
            {
                _ = DxmCounterForeign();
                _ = DxmCounterBadContext();
                _ = DxmCounterTotal();
                for (int kind = 0; kind < Kinds; ++kind)
                {
                    for (int index = 0; index < Warmups; ++index)
                    {
                        Allocate(kind, index, sink, index % Iterations);
                    }
                }
                for (int index = 0; index < Warmups; ++index)
                {
                    Assert.That(DxmCounterBegin(), Is.Zero, "Warm the native boundary.");
                    _ = DxmCounterEnd();
                }
                for (int kind = 0; kind < Kinds; ++kind)
                {
                    int began = DxmCounterBegin();
                    if (began != 0)
                    {
                        Assert.Fail($"Native counter cannot begin kind {kind}: status={began}.");
                    }
                    for (int index = 0; index < Iterations; ++index)
                    {
                        Allocate(kind, index, sink, kind * Iterations + index);
                    }
                    counts[kind] = DxmCounterEnd();
                    foreign[kind] = DxmCounterForeign();
                }
            }
            finally
            {
                detached = DxmCounterDetach();
            }

            long beforeDetachedControl = DxmCounterTotal();
            for (int index = 0; index < Iterations; ++index)
            {
                detachedSink[index] = new ControlObject(index);
            }
            long afterDetachedControl = DxmCounterTotal();
            long badContext = DxmCounterBadContext();
            ValidatePayloads(sink);
            for (int index = 0; index < Iterations; ++index)
            {
                Assert.That(((ControlObject)detachedSink[index]).Value, Is.EqualTo(index));
            }
            TestContext.Out.WriteLine(
                $"NATIVE_ALLOCATION_EVENTS iterations={Iterations}, warmups={Warmups}, "
                    + $"counts=[{string.Join(", ", counts)}], foreign=[{string.Join(", ", foreign)}], "
                    + $"payloadsValidated=True, badContext={badContext}, detached={detached}, "
                    + $"beforeDetached={beforeDetachedControl}, afterDetached={afterDetachedControl}"
            );
            Assert.That(detached, Is.Zero, "Remove this descriptor's event flag and callback.");
            Assert.That(badContext, Is.Zero, "Every callback must carry this counter's context.");
            Assert.That(
                afterDetachedControl,
                Is.EqualTo(beforeDetachedControl),
                "Forced allocations after detach must not enter the callback."
            );
            for (int kind = 0; kind < Kinds; ++kind)
            {
                Assert.That(
                    counts[kind],
                    Is.EqualTo(kind == 0 ? 0L : Iterations),
                    $"kind={kind}: native allocation events; forced zero is unavailable, not allocation-free."
                );
            }
            GC.KeepAlive(sink);
            GC.KeepAlive(detachedSink);
        }

        private static void Allocate(int kind, int value, object[] sink, int index)
        {
            switch (kind)
            {
                case 0:
                    sink[index] = null;
                    break;
                case 1:
                    sink[index] = new ControlObject(value);
                    break;
                case 2:
                    byte[] bytes = new byte[16];
                    bytes[0] = (byte)value;
                    sink[index] = bytes;
                    break;
                case 3:
                    sink[index] = new string((char)('a' + value % 26), 16);
                    break;
                case 4:
                    sink[index] = new ControlValue(value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static void ValidatePayloads(object[] sink)
        {
            for (int index = 0; index < Iterations; ++index)
            {
                Assert.That(sink[index], Is.Null);
                Assert.That(((ControlObject)sink[Iterations + index]).Value, Is.EqualTo(index));
                byte[] bytes = (byte[])sink[2 * Iterations + index];
                Assert.That(bytes.Length, Is.EqualTo(16));
                Assert.That(bytes[0], Is.EqualTo((byte)index));
                string text = (string)sink[3 * Iterations + index];
                Assert.That(text.Length, Is.EqualTo(16));
                Assert.That(text[0], Is.EqualTo((char)('a' + index % 26)));
                Assert.That(((ControlValue)sink[4 * Iterations + index]).Value, Is.EqualTo(index));
                for (int prior = 0; prior < index; ++prior)
                {
                    for (int kind = 1; kind < Kinds; ++kind)
                    {
                        Assert.That(
                            ReferenceEquals(
                                sink[kind * Iterations + prior],
                                sink[kind * Iterations + index]
                            ),
                            Is.False,
                            $"kind={kind}, index={index}, prior={prior}: forced objects are distinct."
                        );
                    }
                }
            }
        }

        private sealed class ControlObject
        {
            internal readonly int Value;

            internal ControlObject(int value) => Value = value;
        }

        private readonly struct ControlValue
        {
            internal readonly int Value;

            internal ControlValue(int value) => Value = value;
        }

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DxmCounterInstall();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DxmCounterBegin();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterEnd();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterForeign();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterBadContext();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterTotal();

        [DllImport(CounterLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DxmCounterDetach();
    }
}
#endif
