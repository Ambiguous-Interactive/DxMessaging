namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using NUnit.Framework;

    /// <summary>Checks plain graph collection while static registries retain weak wrappers.</summary>
    /// <remarks>
    /// 2026-10-27: empty bus controls survive the synchronous window while plain acyclic controls
    /// collect. Isolate registry shape without changing production or the original collection window.
    /// A differing outcome alone does not identify a native root or justify a production change.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class NativeWeakRegistryCollectionControlTests
    {
        private static readonly List<WeakReference> NonGenericRegistry = new();
        private static readonly List<WeakReference<ControlNode>> GenericRegistry = new();

        [Test]
        public void SynchronousWindowObservesWeakRegistryGraph(
            [Values(0, 1, 2)] int registry,
            [Values(false, true)] bool selfCycle,
            [Values(false, true)] bool negativeFactoryLast
        )
        {
            Assert.That(NonGenericRegistry.Count, Is.Zero, "Previous nongeneric registry cleanup.");
            Assert.That(GenericRegistry.Count, Is.Zero, "Previous generic registry cleanup.");
            ControlNode positive = new();
            WeakReference positiveReference = new(positive);
            try
            {
                WeakReference observation;
                WeakReference negative;
                if (negativeFactoryLast)
                {
                    observation = CreateProbe(registry, selfCycle);
                    negative = CreateNegativeControl();
                }
                else
                {
                    negative = CreateNegativeControl();
                    observation = CreateProbe(registry, selfCycle);
                }
                for (int index = 0; index < 3; ++index)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                bool negativeAlive = negative.IsAlive;
                bool positiveAlive = positiveReference.IsAlive;
                bool graphAlive = observation.IsAlive;
                string context =
                    $"registry={registry}, selfCycle={selfCycle}, negativeFactoryLast={negativeFactoryLast}, "
                    + $"negativeAlive={negativeAlive}, positiveAlive={positiveAlive}, graphAlive={graphAlive}, "
                    + $"nongenericCount={NonGenericRegistry.Count}, genericCount={GenericRegistry.Count}";
                TestContext.Out.WriteLine("WEAK_REGISTRY_WINDOW " + context);
                Assert.That(negativeAlive, Is.False, context + ": independent negative control");
                Assert.That(positiveAlive, Is.True, context + ": retained positive control");
                Assert.That(
                    NonGenericRegistry.Count,
                    Is.EqualTo(registry == 1 ? 1 : 0),
                    context + ": retained nongeneric wrapper"
                );
                Assert.That(
                    GenericRegistry.Count,
                    Is.EqualTo(registry == 2 ? 1 : 0),
                    context + ": retained generic wrapper"
                );
                Assert.That(graphAlive, Is.False, context + ": unreachable plain graph");
            }
            finally
            {
                NonGenericRegistry.Clear();
                GenericRegistry.Clear();
                GC.KeepAlive(positive);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateNegativeControl() => new(new ControlNode());

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateProbe(int registry, bool selfCycle)
        {
            ControlNode node = new();
            node.Next = selfCycle ? node : null;
            WeakReference observation = new(node);
            if (registry == 1)
            {
                NonGenericRegistry.Add(new WeakReference(node));
            }
            else if (registry == 2)
            {
                GenericRegistry.Add(new WeakReference<ControlNode>(node));
            }
            return observation;
        }

        private sealed class ControlNode
        {
            internal ControlNode Next;
        }
    }
}
