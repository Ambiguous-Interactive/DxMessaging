namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Reflection;
    using NUnit.Framework;

    /// <summary>
    /// PERF-PLAN T3.0 proof: open-instance delegates bound via
    /// <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/> with the
    /// exact (target, ref message) shape a future flat-dispatch entry would use
    /// must round-trip on every supported backend. The suite runs everywhere; the
    /// Standalone IL2CPP CI leg is the gating result (AOT delegate thunk
    /// generation is the historical risk). If this suite is red on IL2CPP, T3.1
    /// (open-instance delegates in DispatchEntry) must not ship.
    /// </summary>
    public sealed class OpenDelegateRoundtripTests
    {
        private struct StructPayload
        {
            public int value;
        }

        private sealed class ReferencePayload
        {
            public int value;
        }

        private delegate void OpenRefInvoker<TTarget, TPayload>(
            TTarget target,
            ref TPayload payload
        );

        private sealed class StructReceiver
        {
            public int observed;

            public void Handle(ref StructPayload payload)
            {
                observed += payload.value;
                payload.value += 1;
            }
        }

        private sealed class ReferenceReceiver
        {
            public int observed;

            public void Handle(ref ReferencePayload payload)
            {
                observed += payload.value;
                payload.value += 1;
            }
        }

        [Test]
        public void OpenInstanceDelegateOverRefStructParameterRoundTrips()
        {
            MethodInfo method = typeof(StructReceiver).GetMethod(
                nameof(StructReceiver.Handle),
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.IsNotNull(method, "Proof setup: Handle(ref StructPayload) must exist.");

            OpenRefInvoker<StructReceiver, StructPayload> invoker =
                (OpenRefInvoker<StructReceiver, StructPayload>)
                    Delegate.CreateDelegate(
                        typeof(OpenRefInvoker<StructReceiver, StructPayload>),
                        firstArgument: null,
                        method
                    );

            StructReceiver receiver = new();
            StructPayload payload = new() { value = 21 };
            invoker(receiver, ref payload);
            invoker(receiver, ref payload);

            Assert.AreEqual(
                21 + 22,
                receiver.observed,
                "Open-instance invocation must call the bound method with the live target."
            );
            Assert.AreEqual(
                23,
                payload.value,
                "ref struct payload mutations must round-trip through the open delegate."
            );
        }

        [Test]
        public void OpenInstanceDelegateOverRefReferenceParameterRoundTrips()
        {
            MethodInfo method = typeof(ReferenceReceiver).GetMethod(
                nameof(ReferenceReceiver.Handle),
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.IsNotNull(method, "Proof setup: Handle(ref ReferencePayload) must exist.");

            OpenRefInvoker<ReferenceReceiver, ReferencePayload> invoker =
                (OpenRefInvoker<ReferenceReceiver, ReferencePayload>)
                    Delegate.CreateDelegate(
                        typeof(OpenRefInvoker<ReferenceReceiver, ReferencePayload>),
                        firstArgument: null,
                        method
                    );

            ReferenceReceiver receiver = new();
            ReferencePayload payload = new() { value = 5 };
            invoker(receiver, ref payload);

            Assert.AreEqual(5, receiver.observed, "Reference payload must reach the target.");
            Assert.AreEqual(6, payload.value, "ref payload mutation must persist.");
        }
    }
}
