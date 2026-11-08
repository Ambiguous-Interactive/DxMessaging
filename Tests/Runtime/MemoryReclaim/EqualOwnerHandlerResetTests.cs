namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using Bus = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Reset clears each physical handler's callbacks even when their owners compare equal.</summary>
    /// <remarks>
    /// 2026-11-06: a reset collector deduplicated distinct handler objects by owner equality.
    /// Both callbacks first deliver at separate priorities. After public default-bus reset, only
    /// a fresh registration may deliver. Custom-global preservation is a separate positive control.
    /// Diagnostic storage reads never drive the production observation. Explicit internal cleanup
    /// runs after observations so a failing parent cannot strand test-owned custom registrations.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class EqualOwnerHandlerResetTests
    {
        private static readonly InstanceId Context = new InstanceId(925);
        private GlobalBusScope _global;
        private bool _debug;
        private Action<LogLevel, string> _log;
        private DiagnosticsTarget _diagnostics;
        private bool _stackTraces;
        private int _bufferSize;

        [SetUp]
        public void CaptureStaticState()
        {
            _debug = MessagingDebug.enabled;
            _log = MessagingDebug.LogFunction;
            _diagnostics = IMessageBus.GlobalDiagnosticsTargets;
            _stackTraces = IMessageBus.GlobalDiagnosticsStackTraces;
            _bufferSize = IMessageBus.GlobalMessageBufferSize;
            _global = GlobalBusScope.CaptureAndReset();
            MessagingDebug.enabled = false;
        }

        [TearDown]
        public void RestoreStaticState()
        {
            try
            {
                DxMessagingStaticState.Reset();
            }
            finally
            {
                MessagingDebug.enabled = _debug;
                MessagingDebug.LogFunction = _log;
                IMessageBus.GlobalDiagnosticsTargets = _diagnostics;
                IMessageBus.GlobalDiagnosticsStackTraces = _stackTraces;
                IMessageBus.GlobalMessageBufferSize = _bufferSize;
                _global.Dispose();
            }
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                MessageScenario basis in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            foreach (bool fast in new[] { false, true })
            {
                if (basis.Kind == MessageKind.Untargeted && basis.UsePostProcessor && !fast)
                {
                    continue;
                }
                foreach (int identity in new[] { 0, 1, 2 })
                foreach (bool freshFirst in new[] { false, true })
                foreach (int removal in new[] { 0, 1, 2 })
                foreach (bool reset in new[] { false, true })
                foreach (bool diagnostics in new[] { false, true })
                foreach (bool reverse in new[] { false, true })
                foreach (bool custom in new[] { false, true })
                {
                    MessageScenario scenario = basis.WithDiagnostics(diagnostics);
                    yield return new TestCaseData(
                        scenario,
                        fast,
                        identity,
                        freshFirst,
                        removal,
                        reset,
                        reverse,
                        custom
                    ).SetName(
                        $"EqualOwnerReset_{scenario}_fast{fast}_identity{identity}_freshFirst{freshFirst}"
                            + $"_removal{removal}_reset{reset}_reverse{reverse}_custom{custom}"
                    );
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void ResetRetiresEveryHandlerInstanceAndPreservesFreshCallbacks(
            MessageScenario scenario,
            bool fast,
            int identity,
            bool freshFirst,
            int removal,
            bool reset,
            bool reverse,
            bool custom
        )
        {
            Bus bus = custom ? new Bus() : (Bus)MessageHandler.InitialGlobalMessageBus;
            bus.DiagnosticsMode = scenario.DiagnosticsEnabled;
            if (custom)
            {
                MessageHandler.SetGlobalMessageBus(bus);
            }
            MessageHandler first = new MessageHandler(new InstanceId(902), bus) { active = true };
            MessageHandler second =
                identity == 0
                    ? first
                    : new MessageHandler(new InstanceId(identity == 1 ? 902 : 903), bus)
                    {
                        active = true,
                    };
            string label =
                $"[{scenario}] fast={fast},identity={identity},freshFirst={freshFirst},removal={removal},reset={reset},reverse={reverse},custom={custom}";
            List<string> trace = new List<string>();
            int firstCalls = 0;
            int secondCalls = 0;
            int freshCalls = 0;
            int firstAfterFresh = -1;
            int secondAfterFresh = -1;
            int freshAfterFirst = -1;
            int freshAfterStale = -1;
            int beforeFreshRegistered = -1;
            int firstDepth = -1;
            int secondDepth = -1;
            string[] warmTrace = Array.Empty<string>();
            string[] freshTrace = Array.Empty<string>();
            string[] staleTrace = Array.Empty<string>();
            MessageRegistrationToken a = MessageRegistrationToken.Create(first, bus);
            MessageRegistrationToken b = MessageRegistrationToken.Create(second, bus);
            MessageRegistrationToken fresh = null;
            using (
                LeakWatcher watch = LeakWatcher.WatchWithSlots(bus, handler: first, label: label)
            )
            using (
                LeakWatcher other = ReferenceEquals(first, second)
                    ? null
                    : LeakWatcher.WatchWithSlots(bus, handler: second, label: label + ":second")
            )
            {
                try
                {
                    Register(
                        a,
                        scenario,
                        fast,
                        () =>
                        {
                            ++firstCalls;
                            trace.Add("A");
                        },
                        0
                    );
                    Register(
                        b,
                        scenario,
                        fast,
                        () =>
                        {
                            ++secondCalls;
                            trace.Add("B");
                        },
                        10
                    );
                    if (reverse)
                    {
                        b.Enable();
                        a.Enable();
                    }
                    else
                    {
                        a.Enable();
                        b.Enable();
                    }
                    Emit(bus, scenario.Kind);
                    warmTrace = trace.ToArray();
                    Assert.That(
                        warmTrace,
                        Is.EqualTo(new[] { "A", "B" }),
                        label + ": independent initial deliveries"
                    );
                    if (1 <= removal)
                    {
                        b.Disable();
                    }
                    if (removal == 2)
                    {
                        a.Disable();
                    }
                    if (reset)
                    {
                        DxMessagingStaticState.Reset();
                        MessagingDebug.enabled = false;
                        bus.DiagnosticsMode = scenario.DiagnosticsEnabled;
                    }
                    beforeFreshRegistered =
                        bus.RegisteredUntargeted
                        + bus.RegisteredTargeted
                        + bus.RegisteredBroadcast
                        + bus.RegisteredPostProcessors;
                    firstDepth = StorageDepth(first);
                    secondDepth = StorageDepth(second);
                    fresh = MessageRegistrationToken.Create(freshFirst ? first : second, bus);
                    Register(
                        fresh,
                        scenario,
                        fast,
                        () =>
                        {
                            ++freshCalls;
                            trace.Add("C");
                        },
                        freshFirst ? 0 : 10
                    );
                    fresh.Enable();
                    trace.Clear();
                    Emit(bus, scenario.Kind);
                    freshTrace = trace.ToArray();
                    firstAfterFresh = firstCalls;
                    secondAfterFresh = secondCalls;
                    freshAfterFirst = freshCalls;
                    a.Dispose();
                    b.Dispose();
                    trace.Clear();
                    Emit(bus, scenario.Kind);
                    staleTrace = trace.ToArray();
                    freshAfterStale = freshCalls;
                }
                finally
                {
                    fresh?.Dispose();
                    b.Dispose();
                    a.Dispose();
                    bus.ResetState();
                    first.ResetAllTypedSlotsForBusReset(bus);
                    if (!ReferenceEquals(first, second))
                    {
                        second.ResetAllTypedSlotsForBusReset(bus);
                    }
                    bus.Trim(force: true);
                    first.active = false;
                    second.active = false;
                }
            }
            bool preserve = custom || !reset;
            string report =
                label
                + $"; equalOwners={first.Equals(second)},sameReference={ReferenceEquals(first, second)}"
                + $"; storage=[{firstDepth},{secondDepth}],registeredBeforeFresh={beforeFreshRegistered}"
                + $"; warm=[{string.Join(",", warmTrace)}],fresh=[{string.Join(",", freshTrace)}],stale=[{string.Join(",", staleTrace)}]"
                + $"; callsAfterFresh=[{firstAfterFresh},{secondAfterFresh},{freshAfterFirst}],freshAfterStale={freshAfterStale}";
            TestContext.WriteLine(report);
            AssertState(() =>
            {
                if (reset && !custom)
                {
                    Assert.That(
                        firstDepth,
                        Is.Zero,
                        report + ": default reset releases first handler storage"
                    );
                    Assert.That(
                        secondDepth,
                        Is.Zero,
                        report + ": default reset releases second handler storage"
                    );
                }
                Assert.That(ReferenceEquals(first, second), Is.EqualTo(identity == 0), report);
                Assert.That(first.Equals(second), Is.EqualTo(identity != 2), report);
                Assert.That(beforeFreshRegistered, Is.EqualTo(preserve ? 2 - removal : 0), report);
                Assert.That(
                    firstAfterFresh,
                    Is.EqualTo(1 + (preserve && removal != 2 ? 1 : 0)),
                    report
                );
                Assert.That(
                    secondAfterFresh,
                    Is.EqualTo(1 + (preserve && removal == 0 ? 1 : 0)),
                    report
                );
                Assert.That(freshAfterFirst, Is.EqualTo(1), report);
                Assert.That(freshAfterStale, Is.EqualTo(2), report);
                Assert.That(
                    freshTrace,
                    Is.EqualTo(ExpectedTrace(preserve, freshFirst, removal)),
                    report
                );
                if (!(custom && reset))
                {
                    Assert.That(staleTrace, Is.EqualTo(new[] { "C" }), report);
                }
            });
        }

        private static string[] ExpectedTrace(bool preserve, bool freshFirst, int removal)
        {
            List<string> expected = new List<string>();
            if (preserve && removal != 2)
            {
                expected.Add("A");
            }
            if (freshFirst)
            {
                expected.Add("C");
            }
            if (preserve && removal == 0)
            {
                expected.Add("B");
            }
            if (!freshFirst)
            {
                expected.Add("C");
            }
            return expected.ToArray();
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool useFast,
            Action record,
            int priority
        )
        {
            Action<ProbeMessage> scalar = _ => record();
            Action<InstanceId, ProbeMessage> context = (_, __) => record();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => record();
            MessageHandler.FastHandlerWithContext<ProbeMessage> fastContext = (
                in InstanceId _,
                in ProbeMessage __
            ) => record();
            return (scenario.Kind, scenario.UsePostProcessor, !useFast) switch
            {
                (MessageKind.Untargeted, false, true) => token.RegisterUntargeted(
                    scalar,
                    priority: priority
                ),
                (MessageKind.Untargeted, false, false) => token.RegisterUntargeted(
                    fast,
                    priority: priority
                ),
                (MessageKind.Untargeted, true, _) => token.RegisterUntargetedPostProcessor(
                    fast,
                    priority: priority
                ),
                (MessageKind.Targeted, false, true) => token.RegisterTargeted(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Targeted, false, false) => token.RegisterTargeted(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Targeted, true, true) => token.RegisterTargetedPostProcessor(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Targeted, true, false) => token.RegisterTargetedPostProcessor(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Broadcast, false, true) => token.RegisterBroadcast(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Broadcast, false, false) => token.RegisterBroadcast(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Broadcast, true, true) => token.RegisterBroadcastPostProcessor(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Broadcast, true, false) => token.RegisterBroadcastPostProcessor(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.TargetedWithoutTargeting, false, true) =>
                    token.RegisterTargetedWithoutTargeting(context, priority: priority),
                (MessageKind.TargetedWithoutTargeting, false, false) =>
                    token.RegisterTargetedWithoutTargeting(fastContext, priority: priority),
                (MessageKind.TargetedWithoutTargeting, true, true) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(
                        context,
                        priority: priority
                    ),
                (MessageKind.TargetedWithoutTargeting, true, false) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(
                        fastContext,
                        priority: priority
                    ),
                (MessageKind.BroadcastWithoutSource, false, true) =>
                    token.RegisterBroadcastWithoutSource(context, priority: priority),
                (MessageKind.BroadcastWithoutSource, false, false) =>
                    token.RegisterBroadcastWithoutSource(fastContext, priority: priority),
                (MessageKind.BroadcastWithoutSource, true, true) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(context, priority: priority),
                (MessageKind.BroadcastWithoutSource, true, false) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(
                        fastContext,
                        priority: priority
                    ),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static int StorageDepth(MessageHandler handler) =>
            (
                (IDictionary)MessageBusInspection.ReadField(handler, "_handlersByTypeByMessageBus")
            ).Count;

        private static void AssertState(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions.Invoke);
#endif
        }

        private static void Emit(Bus bus, MessageKind kind)
        {
            ProbeMessage message = default;
            InstanceId context = Context;
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
