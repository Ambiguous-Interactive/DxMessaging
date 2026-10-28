namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Stops reflexive dispatch when its diagnostic receiver resets the bus.</summary>
    /// <remarks>
    /// 2026-10-24: Warning and unsupported-build logging could reset the bus before
    /// typed dispatch, which then delivered old or newly registered callbacks in
    /// the invalidated emission. Integer contexts isolate bus continuation from
    /// Unity hierarchy traversal. Fresh callbacks belong to the next emission.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ReflexiveLoggingResetBoundaryTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool debug in new[] { false, true })
            {
                foreach (LogLevel trigger in TriggerLevels())
                {
                    foreach (string action in new[] { "None", "Bump", "Reset", "ResetFresh" })
                    {
                        foreach (bool withoutTargeting in new[] { false, true })
                        {
                            foreach (bool fast in new[] { false, true })
                            {
                                yield return new TestCaseData(
                                    debug,
                                    trigger,
                                    action,
                                    withoutTargeting,
                                    fast
                                ).SetName(
                                    $"ReflexiveLogReset_debug{debug}_{trigger}_{action}_without{withoutTargeting}_fast{fast}"
                                );
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<LogLevel> TriggerLevels()
        {
            yield return LogLevel.Warn;
#if !UNITY_2021_3_OR_NEWER
            yield return LogLevel.Error;
#endif
        }

        /// <summary>Checks cancellation from diagnostic callbacks and delivery on the next emission.</summary>
        [TestCaseSource(nameof(Cases))]
        public void ResetFromLoggerStopsCurrentEmissionAndPreservesNext(
            bool debug,
            LogLevel trigger,
            string action,
            bool withoutTargeting,
            bool fast
        )
        {
            using Scope scope = new Scope(debug, trigger, action, withoutTargeting, fast);
            scope.EmitTwice();
            string report = scope.Report;
            bool triggered = trigger == LogLevel.Error || debug;
            bool reset = triggered && action != "None";
            bool cleared = triggered && (action == "Reset" || action == "ResetFresh");
            Assert.That(scope.Triggers, Is.EqualTo(triggered ? 1 : 0), report);
            Assert.That(scope.OldFirst, Is.EqualTo(reset ? 0 : 1), report);
            Assert.That(scope.FreshFirst, Is.Zero, report);
            Assert.That(scope.OldSecond, Is.EqualTo(cleared ? 0 : 1), report);
            Assert.That(
                scope.FreshSecond,
                Is.EqualTo(triggered && action == "ResetFresh" ? 1 : 0),
                report
            );
            if (reset)
            {
                Assert.That(scope.GenerationAfter, Is.Not.EqualTo(scope.GenerationBefore), report);
                if (trigger == LogLevel.Warn)
                {
                    Assert.That(
                        scope.FirstLogs,
                        Is.EqualTo(1),
                        report + "; canceled warning must stop further reflexive work/logging"
                    );
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private static readonly InstanceId Context = new InstanceId(771);
            private readonly bool _savedDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _savedLog = MessagingDebug.LogFunction;
            private readonly DiagnosticsTarget _savedDiagnostics =
                IMessageBus.GlobalDiagnosticsTargets;
            private readonly BusType _bus = new BusType { DiagnosticsMode = false };
            private readonly bool _debug;
            private readonly LogLevel _trigger;
            private readonly string _action;
            private readonly bool _withoutTargeting;
            private readonly bool _fast;
            private readonly List<string> _trace = new List<string>();
            private MessageRegistrationToken _old;
            private MessageRegistrationToken _fresh;
            private int _oldCalls;
            private int _freshCalls;
            private int _logs;
            private bool _armed;

            internal int Triggers;
            internal int OldFirst;
            internal int FreshFirst;
            internal int OldSecond;
            internal int FreshSecond;
            internal int FirstLogs;
            internal long GenerationBefore;
            internal long GenerationAfter;
            internal string Report =>
                $"debug={_debug}, trigger={_trigger}, action={_action}, without={_withoutTargeting}, fast={_fast}; trace={string.Join(",", _trace)}";

            internal Scope(
                bool debug,
                LogLevel trigger,
                string action,
                bool withoutTargeting,
                bool fast
            )
            {
                _debug = debug;
                _trigger = trigger;
                _action = action;
                _withoutTargeting = withoutTargeting;
                _fast = fast;
            }

            internal void EmitTwice()
            {
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                _old = CreateToken(
                    1001,
                    () =>
                    {
                        ++_oldCalls;
                        _trace.Add("old");
                    }
                );
                MessagingDebug.enabled = _debug;
                MessagingDebug.LogFunction = ReceiveLog;
                _armed = true;
                ReflexiveMessage message = new ReflexiveMessage(
                    "NoMatchingHierarchyReceiver",
                    ReflexiveSendMode.Flat,
                    Array.Empty<object>()
                );
                InstanceId target = Context;
                _trace.Add("first-start");
                _bus.TargetedBroadcast(ref target, ref message);
                _trace.Add("first-end");
                OldFirst = _oldCalls;
                FreshFirst = _freshCalls;
                FirstLogs = _logs;
                _armed = false;
                _trace.Add("second-start");
                _bus.TargetedBroadcast(ref target, ref message);
                _trace.Add("second-end");
                OldSecond = _oldCalls - OldFirst;
                FreshSecond = _freshCalls - FreshFirst;
            }

            private MessageRegistrationToken CreateToken(int ownerId, Action callback)
            {
                MessageHandler handler = new MessageHandler(new InstanceId(ownerId), _bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, _bus);
                try
                {
                    if (_withoutTargeting)
                    {
                        if (_fast)
                        {
                            token.RegisterTargetedWithoutTargeting<ReflexiveMessage>(
                                (in InstanceId context, in ReflexiveMessage message) => callback()
                            );
                        }
                        else
                        {
                            token.RegisterTargetedWithoutTargeting<ReflexiveMessage>(
                                (InstanceId context, ReflexiveMessage message) => callback()
                            );
                        }
                    }
                    else if (_fast)
                    {
                        token.RegisterTargeted<ReflexiveMessage>(
                            Context,
                            (in ReflexiveMessage message) => callback()
                        );
                    }
                    else
                    {
                        token.RegisterTargeted<ReflexiveMessage>(
                            Context,
                            (ReflexiveMessage message) => callback()
                        );
                    }
                    token.Enable();
                    return token;
                }
                catch
                {
                    token.Dispose();
                    throw;
                }
            }

            private void ReceiveLog(LogLevel level, string message)
            {
                ++_logs;
                _trace.Add("log:" + level);
                if (!_armed || Triggers != 0 || level != _trigger)
                {
                    return;
                }
                ++Triggers;
                GenerationBefore = BusType.GetResetGeneration(_bus);
                _trace.Add("action:" + _action);
                switch (_action)
                {
                    case "None":
                        break;
                    case "Bump":
                        _bus.BumpResetGeneration();
                        break;
                    case "Reset":
                        _bus.ResetState();
                        break;
                    case "ResetFresh":
                        _bus.ResetState();
                        _fresh = CreateToken(
                            1002,
                            () =>
                            {
                                ++_freshCalls;
                                _trace.Add("fresh");
                            }
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_action));
                }
                GenerationAfter = BusType.GetResetGeneration(_bus);
            }

            public void Dispose()
            {
                try
                {
                    MessagingDebug.enabled = false;
                    MessagingDebug.LogFunction = null;
                    try
                    {
                        _old?.Dispose();
                    }
                    finally
                    {
                        _fresh?.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        /*
                            Bump intentionally invalidates old teardown generations; clear
                            the actual bus before final accounting.
                        */
                        _bus.ResetState();
                        _ = _bus.Trim(force: true);
                        int[] final =
                        {
                            _bus.RegisteredUntargeted,
                            _bus.RegisteredTargeted,
                            _bus.RegisteredBroadcast,
                            _bus.RegisteredInterceptors,
                            _bus.RegisteredPostProcessors,
                            _bus.RegisteredGlobalAcceptAll,
                        };
                        Assert.That(final, Is.All.Zero, Report + "; final bus cleanup");
                    }
                    finally
                    {
                        MessagingDebug.enabled = _savedDebug;
                        MessagingDebug.LogFunction = _savedLog;
                        IMessageBus.GlobalDiagnosticsTargets = _savedDiagnostics;
                    }
                }
            }
        }
    }
}
