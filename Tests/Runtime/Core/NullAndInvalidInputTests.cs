#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>
    /// Pins the behavior of the public messaging surface when callers provide
    /// null delegates, default <see cref="InstanceId"/>s, or unknown handles.
    /// Each test creates a fresh bus and token so the global state observed by
    /// the rest of the suite is untouched. Cases marked "Pinning current behavior"
    /// codify what the implementation does today; if the contract is ever changed
    /// deliberately, those tests must be updated alongside the source.
    /// </summary>
    [TestFixture]
    public sealed class NullAndInvalidInputTests
    {
        private const int OwnerInstanceId = 1;
        private const int TargetInstanceId = 2;
        private const int SourceInstanceId = 3;

        /// <summary>
        /// Resets all DxMessaging static state before each test so inter-fixture
        /// ordering cannot pollute these tests' starting state.
        /// </summary>
        [SetUp]
        public void ResetBeforeTest()
        {
            DxMessagingStaticState.Reset();
        }

        /// <summary>
        /// Resets all DxMessaging static state after each test so the two cases
        /// that mutate the global message bus (the static-reset sentinel and the
        /// SetGlobalMessageBus null-argument check) cannot leak configuration
        /// into other fixtures or subsequent tests in this fixture.
        /// </summary>
        [TearDown]
        public void ResetGlobalState()
        {
            DxMessagingStaticState.Reset();
        }

        /// <summary>
        /// Parameterized verification that the registration surface rejects null
        /// handler delegates with <see cref="ArgumentNullException"/>. Covers
        /// FastHandler, Action&lt;T&gt;, and interceptor variants for all three
        /// dispatch kinds.
        /// </summary>
        [Test]
        public void RegisterMethodThrowsOnNullHandler(
            [ValueSource(nameof(NullHandlerCases))] NullHandlerCase testCase
        )
        {
            using TokenScope scope = TokenScope.Create();
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
                testCase.Action(scope.Token)
            );
            Assert.IsNotNull(ex, $"Expected ArgumentNullException for case '{testCase}'.");
        }

        /// <summary>
        /// Parameterized verification that <see cref="MessageRegistrationToken.RemoveRegistration"/>
        /// silently tolerates default handles, foreign handles, and double-remove.
        /// </summary>
        [Test]
        public void RemoveRegistrationIsNoOpForUnknownHandle(
            [ValueSource(nameof(NoOpHandleCases))] NoOpHandleCase testCase
        )
        {
            Assert.DoesNotThrow(() => testCase.Action());
        }

        [Test]
        public void StaleAndForeignHandlesCannotRemoveReusedArenaSlot()
        {
            using TokenScope scope = TokenScope.Create();
            MessageRegistrationHandle stale =
                scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );
            scope.Token.RemoveRegistration(stale);

            int invocationCount = 0;
            MessageRegistrationHandle current =
                scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++invocationCount
                );
            using TokenScope foreignScope = TokenScope.Create();
            MessageRegistrationHandle foreign =
                foreignScope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );

            Assert.DoesNotThrow(() => scope.Token.RemoveRegistration(stale));
            Assert.DoesNotThrow(() => scope.Token.RemoveRegistration(foreign));

            SimpleUntargetedMessage message = new();
            scope.Bus.EmitUntargeted(ref message);
            Assert.AreEqual(
                1,
                invocationCount,
                "Slot reuse must validate both the slot index and globally unique handle id."
            );

            scope.Token.RemoveRegistration(current);
        }

        [Test]
        public void ArenaGrowthHeadMiddleTailRemovalAndFreeReusePreserveMetadataOrder()
        {
            using TokenScope scope = TokenScope.Create();
            List<MessageRegistrationHandle> original = new();
            for (int i = 0; i < 6; ++i)
            {
                int capture = i;
                original.Add(
                    scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => GC.KeepAlive(capture)
                    )
                );
            }

            CollectionAssert.AllItemsAreUnique(
                original,
                "Every registration must receive a distinct handle."
            );
            if (RegistrationSlotProperty != null)
            {
                CollectionAssert.AreEqual(
                    new[] { 0, 1, 2, 3, 4, 5 },
                    original.Select(GetRegistrationSlot),
                    "The initial power-of-two arena growth must allocate consecutive slots."
                );
            }

            scope.Token.RemoveRegistration(original[0]);
            scope.Token.RemoveRegistration(original[2]);
            scope.Token.RemoveRegistration(original[5]);

            MessageRegistrationHandle reuseTail =
                scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );
            MessageRegistrationHandle reuseMiddle =
                scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );
            MessageRegistrationHandle reuseHead =
                scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );

            CollectionAssert.AllItemsAreUnique(
                new[] { reuseTail, reuseMiddle, reuseHead },
                "Replacement registrations must receive distinct handles."
            );
            if (RegistrationSlotProperty != null)
            {
                CollectionAssert.AreEqual(
                    new[] { 5, 2, 0 },
                    new[]
                    {
                        GetRegistrationSlot(reuseTail),
                        GetRegistrationSlot(reuseMiddle),
                        GetRegistrationSlot(reuseHead),
                    },
                    "The free list must reuse removed tail, middle, and head slots in O(1) LIFO order."
                );
            }
            MessageRegistrationHandle[] expectedLiveHandles =
            {
                original[1],
                original[3],
                original[4],
                reuseTail,
                reuseMiddle,
                reuseHead,
            };
            IEnumerable<MessageRegistrationHandle> actualLiveHandles = scope.Token._metadata.Select(
                entry => entry.Key
            );
            if (RegistrationSlotProperty != null)
            {
                CollectionAssert.AreEqual(
                    expectedLiveHandles,
                    actualLiveHandles,
                    "Arena metadata enumeration must follow live registration order, not physical slot order."
                );
            }
            else
            {
                CollectionAssert.AreEquivalent(
                    expectedLiveHandles,
                    actualLiveHandles,
                    "Legacy dictionary metadata must contain exactly the surviving and replacement handles."
                );
            }
        }

        private static readonly System.Reflection.PropertyInfo RegistrationSlotProperty =
            typeof(MessageRegistrationHandle).GetProperty(
                "Slot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            );

        private static int GetRegistrationSlot(MessageRegistrationHandle handle)
        {
            Assert.IsNotNull(
                RegistrationSlotProperty,
                "The registration-slot accessor is required when the slot arena is present."
            );
            return (int)RegistrationSlotProperty.GetValue(handle, null);
        }

        [Test]
        public void RegisterTargetedAcceptsDefaultInstanceIdSilently()
        {
            // Pinning current behavior: default(InstanceId) is treated as a normal
            // identifier (zero) by the bus rather than rejected. If the contract
            // changes to disallow it, this test must be updated deliberately.
            using TokenScope scope = TokenScope.Create();
            int invocationCount = 0;
            MessageRegistrationHandle handle = scope.Token.RegisterTargeted<SimpleTargetedMessage>(
                default,
                (ref SimpleTargetedMessage _) => ++invocationCount
            );

            SimpleTargetedMessage message = new();
            message.EmitTargeted(default(InstanceId), scope.Bus);
            Assert.AreEqual(1, invocationCount);

            scope.Token.RemoveRegistration(handle);
        }

        [Test]
        public void RegisterBroadcastAcceptsDefaultInstanceIdSilently()
        {
            // Pinning current behavior: default(InstanceId) is treated as a normal
            // source identifier rather than rejected.
            using TokenScope scope = TokenScope.Create();
            int invocationCount = 0;
            MessageRegistrationHandle handle =
                scope.Token.RegisterBroadcast<SimpleBroadcastMessage>(
                    default,
                    (ref SimpleBroadcastMessage _) => ++invocationCount
                );

            SimpleBroadcastMessage message = new();
            message.EmitBroadcast(default(InstanceId), scope.Bus);
            Assert.AreEqual(1, invocationCount);

            scope.Token.RemoveRegistration(handle);
        }

        [Test]
        public void MessageHandlerMessageBusIsNeverNullAfterStaticReset()
        {
            IMessageBus before = MessageHandler.MessageBus;
            Assert.IsNotNull(before, "Global message bus must be available before reset.");

            DxMessagingStaticState.Reset();

            IMessageBus after = MessageHandler.MessageBus;
            Assert.IsNotNull(
                after,
                "Global message bus must be re-established after DxMessagingStaticState.Reset."
            );
        }

        [Test]
        public void SetGlobalMessageBusRejectsNullArgument()
        {
            Assert.Throws<ArgumentNullException>(() =>
                MessageHandler.SetGlobalMessageBus((BusType)null)
            );
            Assert.Throws<ArgumentNullException>(() =>
                MessageHandler.SetGlobalMessageBus((IMessageBus)null)
            );
        }

        [Test]
        public void MessageRegistrationTokenCreateRejectsNullHandler()
        {
            Assert.Throws<ArgumentNullException>(() => MessageRegistrationToken.Create(null));
        }

        [Test]
        public void EmitUntargetedClassMessageWithNullPayloadDoesNotCrashWithoutHandlers()
        {
            // Pinning current behavior: a null class message dispatched through a
            // bus with zero registered handlers is a no-op rather than an exception.
            // The reflective UntypedUntargetedBroadcast path would dereference the
            // payload, but the strongly typed shorthand does not.
            BusType bus = new BusType();
            Assert.DoesNotThrow(() => bus.EmitUntargeted((ClassUntargetedMessage)null));
        }

        [Test]
        public void EmitUntargetedClassMessageWithNullPayloadAndHandlerInvokesHandler()
        {
            // Pinning current behavior: the bus does not dereference the message
            // reference for dispatch (it uses typeof(TMessage) for the lookup), so
            // a null class payload still reaches a handler that does not access
            // any member of the message.
            using TokenScope scope = TokenScope.Create();
            int invocationCount = 0;
            MessageRegistrationHandle handle =
                scope.Token.RegisterUntargeted<ClassUntargetedMessage>(
                    (ref ClassUntargetedMessage _) => ++invocationCount
                );

            Assert.DoesNotThrow(() => scope.Bus.EmitUntargeted((ClassUntargetedMessage)null));
            Assert.AreEqual(
                1,
                invocationCount,
                "Handler should be invoked even with a null class payload because the bus does not dereference the message reference."
            );

            scope.Token.RemoveRegistration(handle);
        }

        [Test]
        public void EmitUntargetedClassMessageWithNullPayloadThrowsWhenHandlerDereferences()
        {
            // Pins the user-visible boundary: if the caller's handler dereferences
            // a null message payload, the resulting NullReferenceException surfaces
            // through the bus to the emit call. The framework does not catch it.
            using TokenScope scope = TokenScope.Create();
            MessageRegistrationHandle handle =
                scope.Token.RegisterUntargeted<ClassUntargetedMessage>(
                    (ref ClassUntargetedMessage message) => _ = message.GetType()
                );

            Assert.Throws<NullReferenceException>(() =>
                scope.Bus.EmitUntargeted((ClassUntargetedMessage)null)
            );

            scope.Token.RemoveRegistration(handle);
        }

        [Test]
        public void EmitUntargetedThroughNullBusThrows()
        {
            ClassUntargetedMessage message = new ClassUntargetedMessage();
            Assert.Throws<ArgumentNullException>(() =>
                MessageBusExtensions.EmitUntargeted((IMessageBus)null, message)
            );
        }

        [Test]
        public void EmitTargetedThroughNullBusThrows()
        {
            SimpleTargetedMessage message = new();
            InstanceId target = new(TargetInstanceId);
            Assert.Throws<ArgumentNullException>(() =>
                MessageBusExtensions.EmitTargeted((IMessageBus)null, target, ref message)
            );
        }

        [Test]
        public void EmitBroadcastThroughNullBusThrows()
        {
            SimpleBroadcastMessage message = new();
            InstanceId source = new(SourceInstanceId);
            Assert.Throws<ArgumentNullException>(() =>
                MessageBusExtensions.EmitBroadcast((IMessageBus)null, source, ref message)
            );
        }

        [Test]
        public void TargetedBroadcastWithDefaultTargetIsAccepted()
        {
            // Pinning current behavior: default(InstanceId) is a valid target. The
            // bus does not enforce a non-zero identifier on the dispatch path.
            BusType bus = new BusType();
            MessageHandler handler = new MessageHandler(new InstanceId(OwnerInstanceId), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int invocationCount = 0;
            _ = token.RegisterTargeted<SimpleTargetedMessage>(
                default,
                (ref SimpleTargetedMessage _) => ++invocationCount
            );
            token.Enable();

            SimpleTargetedMessage message = new();
            InstanceId zero = default;
            bus.TargetedBroadcast(ref zero, ref message);

            Assert.AreEqual(1, invocationCount);
            token.Dispose();
        }

        public static IEnumerable<NullHandlerCase> NullHandlerCases
        {
            get
            {
                yield return new NullHandlerCase(
                    "RegisterUntargeted FastHandler null",
                    token =>
                        token.RegisterUntargeted<SimpleUntargetedMessage>(
                            (MessageHandler.FastHandler<SimpleUntargetedMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterUntargeted Action null",
                    token =>
                        token.RegisterUntargeted<SimpleUntargetedMessage>(
                            (Action<SimpleUntargetedMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterTargeted FastHandler null",
                    token =>
                        token.RegisterTargeted<SimpleTargetedMessage>(
                            new InstanceId(TargetInstanceId),
                            (MessageHandler.FastHandler<SimpleTargetedMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterTargeted Action null",
                    token =>
                        token.RegisterTargeted<SimpleTargetedMessage>(
                            new InstanceId(TargetInstanceId),
                            (Action<SimpleTargetedMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterBroadcast FastHandler null",
                    token =>
                        token.RegisterBroadcast<SimpleBroadcastMessage>(
                            new InstanceId(SourceInstanceId),
                            (MessageHandler.FastHandler<SimpleBroadcastMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterBroadcast Action null",
                    token =>
                        token.RegisterBroadcast<SimpleBroadcastMessage>(
                            new InstanceId(SourceInstanceId),
                            (Action<SimpleBroadcastMessage>)null
                        )
                );
                yield return new NullHandlerCase(
                    "RegisterUntargetedInterceptor null",
                    token => token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(null)
                );
                yield return new NullHandlerCase(
                    "RegisterTargetedInterceptor null",
                    token => token.RegisterTargetedInterceptor<SimpleTargetedMessage>(null)
                );
                yield return new NullHandlerCase(
                    "RegisterBroadcastInterceptor null",
                    token => token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(null)
                );
            }
        }

        public static IEnumerable<NoOpHandleCase> NoOpHandleCases
        {
            get
            {
                yield return new NoOpHandleCase(
                    "Default handle",
                    () =>
                    {
                        using TokenScope scope = TokenScope.Create();
                        scope.Token.RemoveRegistration(default(MessageRegistrationHandle));
                    }
                );
                yield return new NoOpHandleCase(
                    "Foreign handle",
                    () =>
                    {
                        using TokenScope scope = TokenScope.Create();
                        MessageRegistrationHandle foreign =
                            MessageRegistrationHandle.CreateMessageRegistrationHandle();
                        scope.Token.RemoveRegistration(foreign);
                    }
                );
                yield return new NoOpHandleCase(
                    "Double remove of valid handle",
                    () =>
                    {
                        using TokenScope scope = TokenScope.Create();
                        int invocationCount = 0;
                        MessageRegistrationHandle handle =
                            scope.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                                (ref SimpleUntargetedMessage _) => ++invocationCount
                            );
                        scope.Token.RemoveRegistration(handle);
                        scope.Token.RemoveRegistration(handle);

                        SimpleUntargetedMessage message = new();
                        scope.Bus.EmitUntargeted(ref message);
                        Assert.AreEqual(
                            0,
                            invocationCount,
                            "Doubled removal must not resurrect the handler."
                        );
                    }
                );
            }
        }

        /// <summary>
        /// One null-handler scenario: pairs a description with a delegate that
        /// invokes the failing registration on the supplied token.
        /// </summary>
        public sealed class NullHandlerCase
        {
            public string Description { get; }

            public Action<MessageRegistrationToken> Action { get; }

            public NullHandlerCase(string description, Action<MessageRegistrationToken> action)
            {
                Description = description;
                Action = action;
            }

            public override string ToString()
            {
                return Description;
            }
        }

        /// <summary>
        /// One handle-removal scenario: pairs a description with a delegate that
        /// performs the removal under a freshly created token scope.
        /// </summary>
        public sealed class NoOpHandleCase
        {
            public string Description { get; }

            public Action Action { get; }

            public NoOpHandleCase(string description, Action action)
            {
                Description = description;
                Action = action;
            }

            public override string ToString()
            {
                return Description;
            }
        }

        /// <summary>
        /// Convenience holder that pairs a fresh <see cref="MessageBus"/>,
        /// <see cref="MessageHandler"/>, and enabled <see cref="MessageRegistrationToken"/>
        /// for inline test setup. Every instance is isolated from the global bus so
        /// tests do not leak handlers across cases.
        /// </summary>
        private sealed class TokenScope : IDisposable
        {
            private bool _disposed;

            internal BusType Bus { get; }

            internal MessageHandler Handler { get; }

            internal MessageRegistrationToken Token { get; }

            private TokenScope(BusType bus, MessageHandler handler, MessageRegistrationToken token)
            {
                Bus = bus;
                Handler = handler;
                Token = token;
            }

            internal static TokenScope Create()
            {
                BusType bus = new BusType();
                MessageHandler handler = new MessageHandler(new InstanceId(OwnerInstanceId), bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();
                return new TokenScope(bus, handler, token);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Token.Dispose();
            }
        }
    }
}
#endif
