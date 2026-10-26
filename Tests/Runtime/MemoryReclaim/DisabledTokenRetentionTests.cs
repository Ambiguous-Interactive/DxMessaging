namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Disabled staging must release consumed teardown ownership.</summary>
    /// <remarks>
    /// 2026-10-20: A disabled registration kept its consumed inline teardown,
    /// retaining a pooled typed context map and an unrelated later receiver.
    /// The staged callback remains available for Enable. Factories return only
    /// weak references to the later graph and keep the old bus alive throughout.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    [Category("MemoryReclaim")]
    public sealed class DisabledTokenRetentionTests
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
                            foreach (bool fast in new[] { false, true })
                            {
                                for (int mode = 0; mode < 3; mode++)
                                {
                                    yield return new TestCaseData(
                                        scenario.Kind,
                                        oldPost,
                                        newPost,
                                        sameType,
                                        fast,
                                        mode
                                    ).SetName(
                                        $"DisabledTokenRetention_{scenario.Kind}_OldPost{oldPost}_NewPost{newPost}_SameType{sameType}_Fast{fast}_Mode{mode}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void DisabledStagingDoesNotRetainAnUnrelatedReusedMapGraph(
            MessageKind kind,
            bool oldPost,
            bool newPost,
            bool sameType,
            bool fast,
            int mode
        )
        {
            OldGraph old = CreateOld(kind, oldPost, fast, mode);
            ForeignGraph foreign = null;
            try
            {
                foreign = sameType
                    ? CreateForeign<OldMessage>(kind, newPost, old.Map)
                    : CreateForeign<NewMessage>(kind, newPost, old.Map);
                if (mode == 1)
                {
                    old.Root.Token = null;
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
                    "The collection control must prove exact dictionary reuse or isolation."
                );
                Assert.That(
                    foreign.Calls,
                    Is.EqualTo(1),
                    "Valid raw duplicate cleanup must preserve the real token callback."
                );
                try
                {
                    Assert.That(
                        alive,
                        Is.EqualTo(new[] { false, false, false, false }),
                        $"Disabled staging must release the unrelated bus, handler, token and receiver. Actual: [{string.Join(", ", alive)}]."
                    );
                }
                catch (AssertionException)
                {
#if UNITY_2021_3_OR_NEWER
                    if (alive[0])
                    {
                        NativeRetentionDiagnostics.Report(
                            "DisabledToken:mode" + mode,
                            foreign.Bus,
                            old
                        );
                    }
#endif
                    throw;
                }
                if (old.Root.Token != null)
                {
                    old.Root.Token.Enable();
                }
                Emit<OldMessage>(old.Bus, kind, new InstanceId(9102));
                Assert.That(
                    old.Counter.Calls,
                    Is.EqualTo(mode == 1 ? 0 : 1),
                    "Retained disabled staging must replay exactly its original callback; cleared staging must stay absent."
                );
                old.Root.Token?.Disable();
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(old.Bus),
                    Is.Zero,
                    "A second successful disable must remove the replayed registration."
                );
            }
            finally
            {
                CleanupForeign(foreign);
                old.Root.Token?.Dispose();
                old.Root.Token = null;
                _ = old.Bus.Trim(force: true);
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(old.Bus),
                    Is.Zero,
                    "Owned old cleanup must leave no registrations."
                );
                GC.KeepAlive(old.Root);
                GC.KeepAlive(old.Bus);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static OldGraph CreateOld(MessageKind kind, bool post, bool fast, int mode)
        {
            MessageBus bus = CreateBus();
            _ = bus.Trim(force: true);
            using LeakWatcher watcher = new LeakWatcher(bus, label: "disabled token context map");
            MessageHandler handler = new MessageHandler(new InstanceId(9101), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            Counter counter = new Counter();
            InstanceId context = new InstanceId(9102);
            Action<OldMessage> callback = _ => counter.Calls++;
            MessageHandler.FastHandler<OldMessage> fastCallback = (in OldMessage _) =>
                counter.Calls++;
            if (kind == MessageKind.Targeted)
            {
                if (post)
                {
                    if (fast)
                    {
                        _ = token.RegisterTargetedPostProcessor<OldMessage>(context, fastCallback);
                    }
                    else
                    {
                        _ = token.RegisterTargetedPostProcessor<OldMessage>(context, callback);
                    }
                }
                else
                {
                    if (fast)
                    {
                        _ = token.RegisterTargeted<OldMessage>(context, fastCallback);
                    }
                    else
                    {
                        _ = token.RegisterTargeted<OldMessage>(context, callback);
                    }
                }
            }
            else
            {
                if (post)
                {
                    if (fast)
                    {
                        _ = token.RegisterBroadcastPostProcessor<OldMessage>(context, fastCallback);
                    }
                    else
                    {
                        _ = token.RegisterBroadcastPostProcessor<OldMessage>(context, callback);
                    }
                }
                else
                {
                    if (fast)
                    {
                        _ = token.RegisterBroadcast<OldMessage>(context, fastCallback);
                    }
                    else
                    {
                        _ = token.RegisterBroadcast<OldMessage>(context, callback);
                    }
                }
            }
            token.Enable();
            WeakReference map = new WeakReference(ObserveMap(token));
            token.Disable();
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.Zero,
                "Successful disable must remove the old active registration before recycling."
            );
            Assert.That(
                counter.Calls,
                Is.Zero,
                "The old staged callback must not run during teardown."
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
                "Counter aging and ordinary trim must reclaim the empty keyed slot."
            );
            if (mode == 2)
            {
                _ = bus.Trim(force: true);
            }
            return new OldGraph
            {
                Bus = bus,
                Root = new TokenRoot { Token = token },
                Map = map,
                Counter = counter,
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
            bool reused = ReferenceEquals(oldMap.Target, ObserveMap(token));
            MessageBusRegistration extra;
            if (kind == MessageKind.Targeted)
            {
                extra = post
                    ? bus.RegisterTargetedPostProcessor<T>(context, handler)
                    : bus.RegisterTargeted<T>(context, handler);
            }
            else
            {
                extra = post
                    ? bus.RegisterBroadcastPostProcessor<T>(context, handler)
                    : bus.RegisterSourcedBroadcast<T>(context, handler);
            }
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.EqualTo(2),
                "A duplicate raw handle adds one registration reference."
            );
            bus.Deregister<T>(in extra);
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.EqualTo(1),
                "Valid raw cleanup must retain the staged token reference."
            );
            Emit<T>(bus, kind, context);
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

        private static void Emit<T>(MessageBus bus, MessageKind kind, InstanceId context)
            where T : struct, ITargetedMessage, IBroadcastMessage
        {
            T message = default;
            if (kind == MessageKind.Targeted)
            {
                bus.TargetedBroadcast(ref context, ref message);
            }
            else
            {
                bus.SourcedBroadcast(ref context, ref message);
            }
        }

        private static object ObserveMap(MessageRegistrationToken token)
        {
            Array slots = (Array)MessageBusInspection.ReadField(token, "_slots");
            object registration = MessageBusInspection.ReadField(slots.GetValue(0), "Registration");
            object state = MessageBusInspection.ReadField(registration, "_typedDeregistration");
            object map = MessageBusInspection.ReadField(state, "_handlersByContext");
            Assert.That(
                map,
                Is.Not.Null,
                "An enabled keyed registration must own its real typed context map."
            );
            return map;
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
                    "Owned surviving foreign cleanup must remove every registration."
                );
                _ = bus.Trim(force: true);
            }
        }

        private sealed class ConstantClock : IDxMessagingClock
        {
            public double NowSeconds => 0d;
        }

        private sealed class Counter
        {
            public int Calls;
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

        private sealed class TokenRoot
        {
            public MessageRegistrationToken Token;
        }

        private sealed class OldGraph
        {
            public MessageBus Bus;
            public TokenRoot Root;
            public WeakReference Map;
            public Counter Counter;
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
