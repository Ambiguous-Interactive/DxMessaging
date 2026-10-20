namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Consumed keyed handles must not retain later users of a recycled map.</summary>
    /// <remarks>
    /// 2026-10-20: A consumed handle captured a shared context map. After ordinary
    /// trim and reuse, retaining that value retained an unrelated bus and receiver.
    /// The receiver owns its token, as in a component subscription lifecycle.
    /// NoInlining factories keep temporary strong references out of collection checks.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    [Category("MemoryReclaim")]
    public sealed class ConsumedContextHandleRetentionTests
    {
        private bool _previousDebug;

        [SetUp]
        public void DisableDebugForQuietAging()
        {
            _previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
        }

        [TearDown]
        public void RestoreDebugSetting()
        {
            MessagingDebug.enabled = _previousDebug;
        }

        private static MessageBus CreateBus() =>
            MessageBus.CreateForInternalUse(
                new ConstantClock(),
                idleEvictionTicks: 30,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                if (scenario.Kind != MessageKind.Targeted && scenario.Kind != MessageKind.Broadcast)
                {
                    continue;
                }
                foreach (bool oldPost in new[] { false, true })
                {
                    foreach (bool newPost in new[] { false, true })
                    {
                        foreach (bool sameType in new[] { false, true })
                        {
                            for (int mode = 0; mode < 3; mode++)
                            {
                                yield return new TestCaseData(
                                    scenario.Kind,
                                    oldPost,
                                    newPost,
                                    sameType,
                                    mode
                                ).SetName(
                                    $"ConsumedContextHandle_{scenario.Kind}_OldPost{oldPost}_NewPost{newPost}_SameType{sameType}_Mode{mode}"
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void ConsumedHandleDoesNotRootAnUnrelatedRegistrationGraph(
            MessageKind kind,
            bool oldPost,
            bool newPost,
            bool sameType,
            int mode
        )
        {
            OldGraph old = CreateOld(kind, oldPost, mode);
            ForeignGraph foreign = null;
            try
            {
                foreign = sameType
                    ? CreateForeign<OldMessage>(kind, newPost, old.Map)
                    : CreateForeign<NewMessage>(kind, newPost, old.Map);
                if (mode == 1)
                {
                    old.Root.Handle = default;
                }
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                bool[] alive = new[]
                {
                    foreign.Bus.IsAlive,
                    foreign.Handler.IsAlive,
                    foreign.Token.IsAlive,
                    foreign.Receiver.IsAlive,
                };
                Assert.That(
                    foreign.Reused,
                    Is.EqualTo(mode != 2),
                    "The control must prove exact map reuse or force-flush isolation."
                );
                Assert.That(
                    foreign.Calls,
                    Is.EqualTo(1),
                    "Removing the duplicate raw registration must leave the real callback active."
                );
                Assert.That(
                    alive,
                    Is.EqualTo(new[] { false, false, false, false }),
                    $"A consumed old handle must allow the unrelated bus, handler, token and receiver to collect. Actual: [{string.Join(", ", alive)}]."
                );
            }
            finally
            {
                CleanupForeign(foreign);
                old.Root.Handle = default;
                _ = old.Bus.Trim(force: true);
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(old.Bus),
                    Is.Zero,
                    "The old registration was consumed exactly once."
                );
                GC.KeepAlive(old.Root);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static OldGraph CreateOld(MessageKind kind, bool post, int mode)
        {
            MessageBus bus = CreateBus();
            _ = bus.Trim(force: true);
            using LeakWatcher watcher = new LeakWatcher(bus, label: "consumed context handle");
            MessageHandler handler = new MessageHandler(new InstanceId(9101), bus)
            {
                active = true,
            };
            InstanceId context = new InstanceId(9102);
            MessageBusRegistration handle = RegisterRaw<OldMessage>(
                bus,
                handler,
                kind,
                post,
                context
            );
            MessageBusRegistration copy = handle;
            MessageBusRegistration distinct = RegisterRaw<OldMessage>(
                bus,
                handler,
                kind,
                post,
                context
            );
            int hash = handle.GetHashCode();
            Assert.That(copy, Is.EqualTo(handle), "A copied live handle preserves identity.");
            Assert.That(copy.GetHashCode(), Is.EqualTo(hash), "Equal handles preserve their hash.");
            Assert.That(
                distinct,
                Is.Not.EqualTo(handle),
                "Independent same-bucket registrations have distinct identities."
            );
            WeakReference map = new WeakReference(ObserveMap(bus));
            bus.Deregister<OldMessage>(in distinct);
            bus.Deregister<OldMessage>(in handle);
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.Zero,
                "Both raw registrations must be removed before maintenance."
            );
            for (int i = 0; i < 64; i++)
            {
                QuietMessage message = new QuietMessage();
                bus.UntargetedBroadcast(ref message);
            }
            _ = bus.Trim(force: false);
            Assert.That(
                bus.OccupiedTargetSlots,
                Is.Zero,
                "Counter aging and ordinary trim must reclaim the empty context."
            );
            Assert.That(handle, Is.EqualTo(copy), "Maintenance preserves copied value identity.");
            Assert.That(
                handle.GetHashCode(),
                Is.EqualTo(hash),
                "Maintenance preserves the value hash."
            );
            if (mode == 2)
            {
                _ = bus.Trim(force: true);
            }
            return new OldGraph
            {
                Bus = bus,
                Root = new HandleRoot { Handle = handle },
                Map = map,
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ForeignGraph CreateForeign<T>(
            MessageKind kind,
            bool post,
            WeakReference oldMap
        )
            where T : struct, ITargetedMessage, IBroadcastMessage
        {
            MessageBus bus = CreateBus();
            MessageHandler handler = new MessageHandler(new InstanceId(9201), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            Receiver<T> receiver = new Receiver<T> { Token = token };
            InstanceId context = new InstanceId(9202);
            if (kind == MessageKind.Targeted)
            {
                if (post)
                {
                    _ = token.RegisterTargetedPostProcessor<T>(context, receiver.Receive);
                }
                else
                {
                    _ = token.RegisterTargeted<T>(context, receiver.Receive);
                }
            }
            else
            {
                if (post)
                {
                    _ = token.RegisterBroadcastPostProcessor<T>(context, receiver.Receive);
                }
                else
                {
                    _ = token.RegisterBroadcast<T>(context, receiver.Receive);
                }
            }
            token.Enable();
            MessageBusRegistration extra = RegisterRaw<T>(bus, handler, kind, post, context);
            bool reused = ReferenceEquals(oldMap.Target, ObserveMap(bus));
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.EqualTo(2),
                "The extra raw handle adds one real registration reference."
            );
            bus.Deregister<T>(in extra);
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.EqualTo(1),
                "Valid raw cleanup preserves the token registration."
            );
            T message = default;
            if (kind == MessageKind.Targeted)
            {
                bus.TargetedBroadcast(ref context, ref message);
            }
            else
            {
                bus.SourcedBroadcast(ref context, ref message);
            }
            return new ForeignGraph
            {
                Bus = new WeakReference(bus),
                Handler = new WeakReference(handler),
                Token = new WeakReference(token),
                Receiver = new WeakReference(receiver),
                Reused = reused,
                Calls = receiver.Calls,
            };
        }

        private static MessageBusRegistration RegisterRaw<T>(
            MessageBus bus,
            MessageHandler handler,
            MessageKind kind,
            bool post,
            InstanceId context
        )
            where T : struct, ITargetedMessage, IBroadcastMessage
        {
            if (kind == MessageKind.Targeted)
            {
                return post
                    ? bus.RegisterTargetedPostProcessor<T>(context, handler)
                    : bus.RegisterTargeted<T>(context, handler);
            }
            return post
                ? bus.RegisterBroadcastPostProcessor<T>(context, handler)
                : bus.RegisterSourcedBroadcast<T>(context, handler);
        }

        private static object ObserveMap(MessageBus bus)
        {
            IDictionary maps = (IDictionary)
                MessageBusInspection.ReadField(bus, "_contextMapHighWaterCounts");
            Assert.That(maps.Count, Is.EqualTo(1), "This setup creates exactly one context map.");
            IDictionaryEnumerator iterator = maps.GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True, "The active map must be present.");
            return iterator.Key;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CleanupForeign(ForeignGraph graph)
        {
            if (graph == null)
            {
                return;
            }
            MessageBus bus = graph.Bus.Target as MessageBus;
            MessageRegistrationToken token = graph.Token.Target as MessageRegistrationToken;
            token?.Dispose();
            if (bus != null)
            {
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.Zero,
                    "Owned cleanup removes every surviving foreign registration."
                );
                _ = bus.Trim(force: true);
            }
        }

        private sealed class ConstantClock : IDxMessagingClock
        {
            public double NowSeconds => 0d;
        }

        private sealed class Receiver<T>
        {
            public MessageRegistrationToken Token;
            public int Calls;

            public void Receive(T message)
            {
                Calls++;
                GC.KeepAlive(Token);
            }
        }

        private sealed class HandleRoot
        {
            public MessageBusRegistration Handle;
        }

        private sealed class OldGraph
        {
            public MessageBus Bus;
            public HandleRoot Root;
            public WeakReference Map;
        }

        private sealed class ForeignGraph
        {
            public WeakReference Bus;
            public WeakReference Handler;
            public WeakReference Token;
            public WeakReference Receiver;
            public bool Reused;
            public int Calls;
        }

        private readonly struct OldMessage : ITargetedMessage, IBroadcastMessage
        {
            public Type MessageType => typeof(OldMessage);
        }

        private readonly struct NewMessage : ITargetedMessage, IBroadcastMessage
        {
            public Type MessageType => typeof(NewMessage);
        }

        private readonly struct QuietMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(QuietMessage);
        }
    }
}
