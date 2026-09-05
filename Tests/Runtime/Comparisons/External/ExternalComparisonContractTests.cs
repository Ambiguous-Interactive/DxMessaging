#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;
#if MESSAGEPIPE_PRESENT
    using MessagePipe;
#endif

#if MESSAGEPIPE_PRESENT && UNIRX_PRESENT && ZENJECT_PRESENT

    /// <summary>
    /// Fast contract suite for the gated external-package bridges (MessagePipe, UniRx,
    /// Zenject SignalBus). It runs the SAME identity + EmitOnce accounting checks as the
    /// zero-dependency <see cref="ComparisonContractTests"/>, but for the bridges that only
    /// compile when their packages are present. It opens no benchmark window, so a broken
    /// bridge -- e.g. a fan-out that Zenject's value-equality dedup collapsed -- fails here in
    /// milliseconds with a precise message instead of inside the multi-minute performance run.
    /// </summary>
    [Category("ComparisonContract")]
    public sealed class ExternalComparisonContractTests
    {
        private static IEnumerable<TestCaseData> BridgeCases() =>
            ComparisonBridgeContract.IdentityCases(ExternalComparisonRoster.Bridges);

        private static IEnumerable<TestCaseData> BridgeScenarioCases() =>
            ComparisonBridgeContract.EmitOnceAccountingCases(ExternalComparisonRoster.Bridges);

        [Test]
        [TestCaseSource(nameof(BridgeCases))]
        public void BridgeHasConsistentTechIdentity(
            string rosterKey,
            Func<IMessagingTechBridge> factory
        )
        {
            ComparisonBridgeContract.AssertTechIdentity(rosterKey, factory);
        }

        [Test]
        [TestCaseSource(nameof(BridgeScenarioCases))]
        public void SupportedScenarioEmitOnceAdvancesProgressByDeclaredFanOut(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            ComparisonBridgeContract.AssertEmitOnceAccounting(rosterKey, factory, scenario);
        }

        [Test]
        [TestCaseSource(nameof(BridgeScenarioCases))]
        public void StructScenarioDispatchesNonPrimitiveStructPayload(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            ComparisonBridgeContract.AssertStructScenarioPayloadFidelity(
                rosterKey,
                factory,
                scenario
            );
        }

        [Test]
        [TestCase("MessagePipe", ComparisonScenario.FilteredDispatch)]
        [TestCase("MessagePipe", ComparisonScenario.PostProcessingDispatch)]
        [TestCase("MessagePipe", ComparisonScenario.InterceptedPostProcessingDispatch)]
        [TestCase("UniRx", ComparisonScenario.FilteredDispatch)]
        [TestCase("ZenjectSignalBus", ComparisonScenario.KeyedToOneOfMany)]
        public void BridgeSupportsItsIdiomaticComparisonScenario(
            string rosterKey,
            ComparisonScenario scenario
        )
        {
            using IMessagingTechBridge bridge = ExternalComparisonRoster.Create(rosterKey);

            Assert.IsTrue(
                bridge.Supports(scenario),
                $"Bridge '{rosterKey}' must support its idiomatic '{scenario}' scenario; reporting N/A would hide a capability the external package provides."
            );
        }
    }
#endif

#if MESSAGEPIPE_PRESENT
    /// <summary>
    /// Characterizes the pinned synchronous MessagePipe implementation and the distinct
    /// DxMessaging untargeted snapshot contract. These are untimed traces, not a semantic parity claim.
    /// Review these implementation observations when the central package pin changes.
    /// </summary>
    [Category("ComparisonContract")]
    public sealed class MessagePipeSemanticCharacterizationTests
    {
        public enum Mutation
        {
            AddWithSpareCapacity,
            AddAtFullCapacity,
            RemovePending,
            GrowThenRemovePending,
            ReplaceLastSubscriber,
            ReuseEarlierSlot,
            NestedPublishAfterAdd,
            ThrowThenPublish,
        }

        private static IEnumerable<TestCaseData> MutationCases()
        {
            foreach (Mutation mutation in Enum.GetValues(typeof(Mutation)))
            {
                foreach (bool keyed in new[] { false, true })
                {
                    foreach (bool structPayload in new[] { false, true })
                    {
                        yield return new TestCaseData(mutation, keyed, structPayload).SetName(
                            $"MessagePipeMutationTrace({mutation},keyed={keyed},struct={structPayload})"
                        );
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> ShapeCases()
        {
            foreach (bool keyed in new[] { false, true })
            {
                foreach (bool structPayload in new[] { false, true })
                {
                    yield return new TestCaseData(keyed, structPayload);
                }
            }
        }

        private static IEnumerable<TestCaseData> SurvivorCases() =>
            ShapePopulationCases(new[] { 0, 8, 15 });

        private static IEnumerable<TestCaseData> EmptyPopulationCases() =>
            ShapePopulationCases(new[] { 4, 8, 16 });

        private static IEnumerable<TestCaseData> ShapePopulationCases(int[] values)
        {
            foreach (bool keyed in new[] { false, true })
            {
                foreach (bool structPayload in new[] { false, true })
                {
                    foreach (int value in values)
                    {
                        yield return new TestCaseData(keyed, structPayload, value);
                    }
                }
            }
        }

        [TestCaseSource(nameof(MutationCases))]
        public void MessagePipeMutationHasPinnedArrayTrace(
            Mutation mutation,
            bool keyed,
            bool structPayload
        )
        {
            using PipeProbe probe = CreatePipe(keyed, structPayload, mutation.ToString());
            ExecuteMutation(probe, mutation);
            string expected = mutation switch
            {
                Mutation.AddWithSpareCapacity => "A:1 B:1 A:2 B:2",
                Mutation.AddAtFullCapacity => "A:1 B:1 C:1 D:1 A:2 B:2 C:2 D:2 E:2",
                Mutation.RemovePending => "A:1 C:1 A:2 C:2",
                Mutation.GrowThenRemovePending => "A:1 B:1 C:1 D:1 A:2 C:2 D:2 E:2",
                Mutation.ReplaceLastSubscriber => keyed ? "A:1 B:2" : "A:1 B:1 B:2",
                Mutation.ReuseEarlierSlot => "B:1 C:1 D:1 E:2 B:2 C:2 D:2",
                Mutation.NestedPublishAfterAdd => "A:1 A:2 B:2 B:1 A:3 B:3",
                Mutation.ThrowThenPublish => "A:1 A:2 B:2",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
            };
            probe.AssertTrace(expected);
        }

        [Test]
        public void DxMessagingMutationHasFrozenSnapshotTrace([Values] Mutation mutation)
        {
            using DxProbe probe = new(mutation.ToString());
            ExecuteMutation(probe, mutation);
            string expected = mutation switch
            {
                Mutation.AddWithSpareCapacity => "A:1 A:2 B:2",
                Mutation.AddAtFullCapacity => "A:1 B:1 C:1 D:1 A:2 B:2 C:2 D:2 E:2",
                Mutation.RemovePending => "A:1 B:1 C:1 A:2 C:2",
                Mutation.GrowThenRemovePending => "A:1 B:1 C:1 D:1 A:2 C:2 D:2 E:2",
                Mutation.ReplaceLastSubscriber => "A:1 B:2",
                Mutation.ReuseEarlierSlot => "B:1 C:1 D:1 B:2 C:2 D:2 E:2",
                Mutation.NestedPublishAfterAdd => "A:1 A:2 B:2 A:3 B:3",
                Mutation.ThrowThenPublish => "A:1 A:2 B:2",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
            };
            probe.AssertTrace(expected);
        }

        private static void ExecuteMutation(TraceProbe probe, Mutation mutation)
        {
            if (mutation == Mutation.ReuseEarlierSlot)
            {
                probe.Add("A");
                probe.Add(
                    "B",
                    message =>
                    {
                        if (message == 1)
                        {
                            probe.Add("E");
                        }
                    }
                );
                probe.Add("C");
                probe.Add("D");
                probe.Remove("A");
            }
            else
            {
                probe.Add(
                    "A",
                    message =>
                    {
                        if (message != 1)
                        {
                            return;
                        }
                        switch (mutation)
                        {
                            case Mutation.AddWithSpareCapacity:
                                probe.Add("B");
                                break;
                            case Mutation.AddAtFullCapacity:
                                probe.Add("E");
                                break;
                            case Mutation.RemovePending:
                                probe.Remove("B");
                                break;
                            case Mutation.GrowThenRemovePending:
                                probe.Add("E");
                                probe.Remove("B");
                                break;
                            case Mutation.ReplaceLastSubscriber:
                                probe.Remove("A");
                                probe.Add("B");
                                break;
                            case Mutation.NestedPublishAfterAdd:
                                probe.Add("B");
                                probe.Publish(2);
                                break;
                            case Mutation.ThrowThenPublish:
                                throw new InvalidOperationException("intentional callback failure");
                        }
                    }
                );
                if (
                    mutation == Mutation.AddAtFullCapacity
                    || mutation == Mutation.GrowThenRemovePending
                )
                {
                    probe.Add("B");
                    probe.Add("C");
                    probe.Add("D");
                }
                else if (mutation == Mutation.RemovePending)
                {
                    probe.Add("B");
                    probe.Add("C");
                }
                else if (mutation == Mutation.ThrowThenPublish)
                {
                    probe.Add("B");
                }
            }
            if (mutation == Mutation.ThrowThenPublish)
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => probe.Publish(1),
                    probe.Context
                );
                Assert.AreEqual("intentional callback failure", error.Message, probe.Context);
            }
            else
            {
                probe.Publish(1);
            }
            probe.Publish(mutation == Mutation.NestedPublishAfterAdd ? 3 : 2);
        }

        [TestCaseSource(nameof(SurvivorCases))]
        public void MessagePipeFragmentationRetainsPhysicalCapacity(
            bool keyed,
            bool structPayload,
            int survivor
        )
        {
            using PipeProbe probe = CreatePipe(keyed, structPayload, $"survivor={survivor}");
            for (int index = 0; index < 16; index++)
            {
                probe.Add("S" + index);
            }
            for (int index = 0; index < 16; index++)
            {
                if (index != survivor)
                {
                    probe.Remove("S" + index);
                }
            }
            probe.AssertShape(1, 16);
            CollectionAssert.AreEqual(new[] { survivor }, probe.OccupiedSlots(), probe.Context);
            probe.Publish(1);
            probe.AssertTrace($"S{survivor}:1");
        }

        [TestCaseSource(nameof(ShapeCases))]
        public void MessagePipeHolesRefillInPhysicalSlotOrder(bool keyed, bool structPayload)
        {
            using PipeProbe probe = CreatePipe(keyed, structPayload, "holes/refill/stale disposal");
            for (int index = 0; index < 16; index++)
            {
                probe.Add("S" + index);
            }
            for (int index = 1; index < 16; index += 2)
            {
                probe.Remove("S" + index);
            }
            probe.AssertShape(8, 16);
            probe.Publish(1);
            probe.AssertTrace("S0:1 S2:1 S4:1 S6:1 S8:1 S10:1 S12:1 S14:1");
            for (int index = 1; index < 16; index += 2)
            {
                probe.Add("N" + index);
                probe.Remove("S" + index);
            }
            probe.AssertShape(16, 16);
            probe.Publish(2);
            probe.AssertTrace(
                "S0:1 S2:1 S4:1 S6:1 S8:1 S10:1 S12:1 S14:1 "
                    + "S0:2 N1:2 S2:2 N3:2 S4:2 N5:2 S6:2 N7:2 S8:2 N9:2 S10:2 N11:2 S12:2 N13:2 S14:2 N15:2"
            );
        }

        [TestCaseSource(nameof(ShapeCases))]
        public void MessagePipeChurnKeepsSurvivorAndCapacity(bool keyed, bool structPayload)
        {
            using PipeProbe probe = CreatePipe(keyed, structPayload, "32 churn cycles");
            for (int index = 0; index < 16; index++)
            {
                probe.Add("S" + index);
            }
            for (int index = 1; index < 16; index++)
            {
                probe.Remove("S" + index);
            }
            for (int index = 0; index < 32; index++)
            {
                probe.Add("C" + index);
                probe.AssertShape(2, 16);
                probe.Remove("C" + index);
                probe.AssertShape(1, 16);
            }
            probe.Publish(1);
            probe.AssertTrace("S0:1");
        }

        [TestCaseSource(nameof(EmptyPopulationCases))]
        public void MessagePipeEmptySubscriptionStorageHasPinnedShape(
            bool keyed,
            bool structPayload,
            int population
        )
        {
            using PipeProbe probe = CreatePipe(
                keyed,
                structPayload,
                $"empty population={population}"
            );
            for (int index = 0; index < population; index++)
            {
                probe.Add("S" + index);
            }
            probe.AssertShape(population, population);
            probe.RemoveAll();
            probe.AssertShape(
                0,
                keyed ? 0
                    : population == 16 ? 4
                    : population
            );
            Assert.AreEqual(0, probe.GroupCount, probe.Context);
            probe.Publish(1);
            probe.AssertTrace("");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MessagePipeFinalSubscriptionRemovesOnlyItsKeyedHolder(bool structPayload)
        {
            using PipeProbe probe = CreatePipe(true, structPayload, "final keyed holder");
            probe.Add("A");
            probe.Add("B", key: 1);
            Assert.AreEqual(2, probe.GroupCount, probe.Context);
            probe.Remove("A");
            Assert.AreEqual(1, probe.GroupCount, probe.Context);
            probe.AssertShape(0, 0);
            probe.AssertShape(1, 4, key: 1);
            probe.Publish(1);
            probe.Publish(2, key: 1);
            probe.AssertTrace("B:2");
            probe.Remove("B");
            Assert.AreEqual(0, probe.GroupCount, probe.Context);
        }

        [TestCaseSource(nameof(ShapeCases))]
        public void MessagePipeCoreDisposalKeepsCapturedCallbacksAndKeyedHolder(
            bool keyed,
            bool structPayload
        )
        {
            using PipeProbe probe = CreatePipe(
                keyed,
                structPayload,
                "core disposal during publish"
            );
            probe.Add("A", message => probe.DisposeCore());
            probe.Add("B");
            probe.Publish(1);
            probe.Publish(2);
            probe.Add("C");
            probe.Publish(3);
            probe.AssertTrace("A:1 B:1");
            probe.AssertShape(0, 0);
            Assert.AreEqual(keyed ? 1 : 0, probe.GroupCount, probe.Context);
            Assert.AreEqual(0, probe.SubscribeCount, probe.Context);
        }

        private static PipeProbe CreatePipe(bool keyed, bool structPayload, string detail) =>
            structPayload
                ? new PipeProbe<ComparisonStructPayload>(
                    keyed,
                    detail,
                    value => new ComparisonStructPayload(value),
                    payload => payload.Value
                )
                : new PipeProbe<int>(keyed, detail, value => value, payload => payload);

        private abstract class TraceProbe : IDisposable
        {
            internal readonly string Context;
            private readonly List<string> _trace = new();
            protected readonly Dictionary<string, IDisposable> Subscriptions = new();

            protected TraceProbe(string context) => Context = context;

            internal abstract void Add(string name, Action<int> callback = null, int key = 0);
            internal abstract void Publish(int value, int key = 0);

            internal void Remove(string name) => Subscriptions[name].Dispose();

            internal void RemoveAll()
            {
                List<Exception> failures = null;
                foreach (IDisposable subscription in Subscriptions.Values)
                {
                    try
                    {
                        subscription.Dispose();
                    }
                    catch (Exception error)
                    {
                        failures ??= new List<Exception>();
                        failures.Add(error);
                    }
                }
                if (failures != null)
                {
                    throw new AggregateException(
                        Context + " subscription cleanup failed",
                        failures
                    );
                }
            }

            protected void Record(string name, int value, Action<int> callback)
            {
                _trace.Add(name + ":" + value);
                callback?.Invoke(value);
            }

            internal void AssertTrace(string expected) =>
                Assert.AreEqual(expected, string.Join(" ", _trace), Context);

            public abstract void Dispose();
        }

        private abstract class PipeProbe : TraceProbe
        {
            private readonly bool _keyed;
            protected object Core;
            protected MessagePipe.MessagePipeDiagnosticsInfo Diagnostics;
            private bool _coreDisposed;

            protected PipeProbe(bool keyed, string detail, Type payload)
                : base($"MessagePipe keyed={keyed}, payload={payload.Name}, {detail}") =>
                _keyed = keyed;

            internal int SubscribeCount => Diagnostics.SubscribeCount;
            internal int GroupCount => _keyed ? Groups.Count : 0;
            private System.Collections.IDictionary Groups =>
                (System.Collections.IDictionary)ReadField(Core, "handlerGroup");

            private object Holder(int key) => _keyed ? Groups[key] : Core;

            private Array Values(int key)
            {
                object holder = Holder(key);
                return holder == null
                    ? Array.Empty<object>()
                    : (Array)ReadField(ReadField(holder, "handlers"), "values");
            }

            internal int[] OccupiedSlots(int key = 0)
            {
                Array values = Values(key);
                List<int> slots = new();
                for (int index = 0; index < values.Length; index++)
                {
                    if (values.GetValue(index) != null)
                    {
                        slots.Add(index);
                    }
                }
                return slots.ToArray();
            }

            internal void AssertShape(int live, int capacity, int key = 0)
            {
                object holder = Holder(key);
                int actualLive =
                    holder == null ? 0 : (int)ReadField(ReadField(holder, "handlers"), "count");
                Assert.AreEqual(live, actualLive, $"{Context}, key={key}, live references");
                Assert.AreEqual(
                    capacity,
                    Values(key).Length,
                    $"{Context}, key={key}, physical capacity"
                );
                Assert.AreEqual(
                    live,
                    OccupiedSlots(key).Length,
                    $"{Context}, key={key}, occupied slots"
                );
            }

            internal void DisposeCore()
            {
                ((IDisposable)Core).Dispose();
                _coreDisposed = true;
            }

            public override void Dispose()
            {
                try
                {
                    RemoveAll();
                    Assert.AreEqual(0, SubscribeCount, Context + " subscription cleanup");
                    // Core disposal retains empty keyed holders; subscription disposal normally removes them.
                    Assert.AreEqual(
                        _keyed && _coreDisposed ? 1 : 0,
                        GroupCount,
                        Context + " holder cleanup"
                    );
                }
                finally
                {
                    DisposeCore();
                }
            }

            private static object ReadField(object value, string name)
            {
                System.Reflection.FieldInfo field = value
                    .GetType()
                    .GetField(
                        name,
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic
                    );
                Assert.IsNotNull(
                    field,
                    $"Pinned MessagePipe field missing: {value.GetType().FullName}.{name}"
                );
                return field.GetValue(value);
            }
        }

        private sealed class PipeProbe<T> : PipeProbe
        {
            private readonly MessagePipe.IPublisher<T> _publisher;
            private readonly MessagePipe.ISubscriber<T> _subscriber;
            private readonly MessagePipe.IPublisher<int, T> _keyedPublisher;
            private readonly MessagePipe.ISubscriber<int, T> _keyedSubscriber;
            private readonly Func<int, T> _payload;
            private readonly Func<T, int> _value;

            internal PipeProbe(bool keyed, string detail, Func<int, T> payload, Func<T, int> value)
                : base(keyed, detail, typeof(T))
            {
                _payload = payload;
                _value = value;
                MessagePipe.BuiltinContainerBuilder builder = new();
                builder.AddMessagePipe();
                if (keyed)
                {
                    builder.AddMessageBroker<int, T>();
                }
                else
                {
                    builder.AddMessageBroker<T>();
                }
                IServiceProvider provider = builder.BuildServiceProvider();
                Diagnostics = provider.GetRequiredService<MessagePipe.MessagePipeDiagnosticsInfo>();
                Assert.IsFalse(
                    provider
                        .GetRequiredService<MessagePipe.MessagePipeOptions>()
                        .EnableCaptureStackTrace,
                    Context
                );
                if (keyed)
                {
                    _keyedPublisher = provider.GetRequiredService<MessagePipe.IPublisher<int, T>>();
                    _keyedSubscriber = provider.GetRequiredService<MessagePipe.ISubscriber<
                        int,
                        T
                    >>();
                    Core = provider.GetRequiredService<MessagePipe.MessageBrokerCore<int, T>>();
                }
                else
                {
                    _publisher = provider.GetRequiredService<MessagePipe.IPublisher<T>>();
                    _subscriber = provider.GetRequiredService<MessagePipe.ISubscriber<T>>();
                    Core = provider.GetRequiredService<MessagePipe.MessageBrokerCore<T>>();
                }
            }

            internal override void Add(string name, Action<int> callback = null, int key = 0)
            {
                Action<T> handler = payload => Record(name, _value(payload), callback);
                Subscriptions.Add(
                    name,
                    _keyedSubscriber != null
                        ? _keyedSubscriber.Subscribe(key, handler)
                        : _subscriber.Subscribe(handler)
                );
            }

            internal override void Publish(int value, int key = 0)
            {
                if (_keyedPublisher != null)
                {
                    _keyedPublisher.Publish(key, _payload(value));
                }
                else
                {
                    _publisher.Publish(_payload(value));
                }
            }
        }

        private sealed class DxProbe : TraceProbe
        {
            private readonly DxMessaging.Core.MessageBus.MessageBus _bus = new()
            {
                DiagnosticsMode = false,
            };

            internal DxProbe(string detail)
                : base("DxMessaging exact struct, " + detail) { }

            internal override void Add(string name, Action<int> callback = null, int key = 0)
            {
                Assert.AreEqual(0, key, Context + " supports only the untargeted counterpart");
                DxMessaging.Core.MessageHandler handler = new(
                    new DxMessaging.Core.InstanceId(48000 + Subscriptions.Count),
                    _bus
                )
                {
                    active = true,
                };
                DxMessaging.Core.MessageRegistrationToken token =
                    DxMessaging.Core.MessageRegistrationToken.Create(handler, _bus);
                token.DiagnosticMode = false;
                Subscriptions.Add(name, token);
                _ = token.RegisterUntargeted<ComparisonStructPayload>(
                    (in ComparisonStructPayload payload) => Record(name, payload.Value, callback)
                );
                token.Enable();
            }

            internal override void Publish(int value, int key = 0)
            {
                Assert.AreEqual(0, key, Context + " supports only the untargeted counterpart");
                ComparisonStructPayload payload = new(value);
                _bus.UntargetedBroadcast(ref payload);
            }

            public override void Dispose()
            {
                RemoveAll();
                Assert.AreEqual(0, _bus.RegisteredUntargeted, Context + " cleanup");
                Assert.AreEqual(0, _bus.RegisteredTargeted, Context + " targeted cleanup");
                Assert.AreEqual(0, _bus.RegisteredBroadcast, Context + " broadcast cleanup");
                Assert.AreEqual(0, _bus.RegisteredInterceptors, Context + " interceptor cleanup");
                Assert.AreEqual(0, _bus.RegisteredPostProcessors, Context + " post cleanup");
                Assert.AreEqual(0, _bus.RegisteredGlobalAcceptAll, Context + " accept-all cleanup");
            }
        }
    }
#endif
}
#endif
