namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Checks intentional current-state retention and immediate payload release.</summary>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class StickyStateRetentionTests
    {
        [Test]
        public void ReplacementClearAndResetReleasePreviousPayload(
            [Values(false, true)] bool referenceMessage,
            [Values(0, 1, 2)] int release
        )
        {
            using DiagnosticsScope diagnostics = new DiagnosticsScope(
                diagnosticsTargets: DiagnosticsTarget.Off
            );
            MessageBus bus = new MessageBus();
            WeakReference payload = Publish(bus, referenceMessage);
            bus.Trim(force: true);
            Collect();
            Assert.That(
                payload.IsAlive,
                Is.True,
                "Trim must retain intentionally published current state."
            );
            Release(bus, referenceMessage, release);
            Collect();
            Assert.That(
                payload.IsAlive,
                Is.False,
                $"referenceMessage={referenceMessage},release={release}: old payload must become collectible."
            );
            GC.KeepAlive(bus);
        }

        [Test]
        public void EmptyHolderIsReleasedImmediatelyAndResetClearsEveryType()
        {
            MessageBus bus = new MessageBus();
            FieldInfo storage = typeof(MessageBus).GetField(
                "_stickyValues",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(storage, Is.Not.Null, "The retained-state holder must be inspectable.");
            Assert.That(
                storage.GetValue(bus),
                Is.Null,
                "Ordinary buses allocate no state dictionary."
            );
            Publish(bus, false);
            Publish(bus, true);
            Assert.That(
                storage.GetValue(bus),
                Is.Not.Null,
                "Explicit publication creates owned state."
            );
            bus.ClearLatest<ValueState>();
            Assert.That(
                bus.TryGetLatest<ReferenceState>(out _),
                Is.True,
                "Clearing one type preserves another."
            );
            bus.ClearLatest<ReferenceState>();
            Assert.That(
                storage.GetValue(bus),
                Is.Null,
                "Final clear releases the dictionary immediately without a sweep."
            );
            Publish(bus, false);
            Publish(bus, true);
            bus.ResetState();
            Assert.That(bus.TryGetLatest<ValueState>(out _), Is.False, "Reset clears value state.");
            Assert.That(
                bus.TryGetLatest<ReferenceState>(out _),
                Is.False,
                "Reset clears reference state."
            );
            Assert.That(storage.GetValue(bus), Is.Null, "Reset drops every holder.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Publish(MessageBus bus, bool referenceMessage)
        {
            object payload = new object();
            WeakReference observed = new WeakReference(payload);
            if (referenceMessage)
            {
                ReferenceState message = new ReferenceState(payload);
                bus.EmitSticky(ref message);
            }
            else
            {
                ValueState message = new ValueState(payload);
                bus.EmitSticky(ref message);
            }
            return observed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Release(MessageBus bus, bool referenceMessage, int release)
        {
            if (release == 0)
            {
                Publish(bus, referenceMessage);
            }
            else if (release == 1)
            {
                if (referenceMessage)
                {
                    bus.ClearLatest<ReferenceState>();
                }
                else
                {
                    bus.ClearLatest<ValueState>();
                }
            }
            else
            {
                bus.ResetState();
            }
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private readonly struct ValueState : IUntargetedMessage<ValueState>
        {
            public readonly object Payload;

            internal ValueState(object payload) => Payload = payload;
        }

        private sealed class ReferenceState : IUntargetedMessage<ReferenceState>
        {
            public readonly object Payload;

            internal ReferenceState(object payload) => Payload = payload;
        }
    }
}
