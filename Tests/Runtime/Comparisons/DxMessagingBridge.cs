#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;

#pragma warning disable RCS1242 // Fast handlers intentionally observe mutable messages by readonly reference.
    /// <summary>
    /// Bridges DxMessaging using its readonly by-ref fast-path API on an isolated
    /// <see cref="MessageBus"/>. Mirrors the benchmark suite's isolated
    /// <c>new MessageBus()</c> + <see cref="MessageHandler"/> + <see cref="MessageRegistrationToken"/>
    /// setup so no global state leaks between cases. Supports every scenario; the readonly
    /// by-ref handler and struct messages keep the dispatch path allocation-free.
    ///
    /// Fan-out is modeled the faithful DxMessaging way: each subscriber is its OWN component,
    /// i.e. its own <see cref="MessageHandler"/> behind its own
    /// <see cref="MessageRegistrationToken"/>. DxMessaging's untargeted handler store dedups
    /// equal delegates PER MessageHandler, so registering one handler N times on a single
    /// token fires once; N subscribers therefore require N distinct tokens (one MessageHandler
    /// each) to fan out to N invocations.
    /// </summary>
    public sealed class DxMessagingBridge : IMessagingTechBridge
    {
        public string TechName => "DxMessaging";

        public string TechKey => "DxMessaging";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _progress;

        private const int KeyedTargetBase = 41000;
        private const int OwnerBase = 40000;

        private MessageBus _bus;
        private readonly List<MessageRegistrationToken> _tokens = new();
        private int _nextOwner = OwnerBase;
        private MessageRegistrationToken _token;
        private ComparisonScenario _scenario;
        private long _progress;

        private SimpleUntargetedMessage _untargeted;
        private ComparisonStructPayload _structPayload = new(1);
        private SimpleTargetedMessage _targeted;
        private InstanceId _dispatchTarget;

        // Cached, reused churn handler so the SubscribeUnsubscribe scenario measures the
        // bus subscribe/unsubscribe cost rather than per-cycle delegate allocation.
        private MessageHandler.FastHandler<SimpleUntargetedMessage> _churnHandler;

        public bool Supports(ComparisonScenario scenario)
        {
            return true;
        }

        public long InvocationsPerOperation(ComparisonScenario scenario) =>
            scenario switch
            {
                ComparisonScenario.GlobalToManySubscribers => ComparisonScenarios.FanOutSubscribers,
                ComparisonScenario.PriorityOrderedDispatch => 4,
                _ => 1,
            };

        public Type DispatchedPayloadType(ComparisonScenario scenario)
        {
            return scenario switch
            {
                ComparisonScenario.KeyedToOneOfMany => typeof(SimpleTargetedMessage),
                ComparisonScenario.StructMessageNoBoxing => typeof(ComparisonStructPayload),
                _ => typeof(SimpleUntargetedMessage),
            };
        }

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;
            _bus = new MessageBus { DiagnosticsMode = false };
            _token = CreateToken();

            void Handle(in SimpleUntargetedMessage message)
            {
                _progress++;
            }

            void HandleStruct(in ComparisonStructPayload message)
            {
                _progress++;
            }

            void HandleTargeted(in SimpleTargetedMessage message)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _ = _token.RegisterUntargeted<ComparisonStructPayload>(HandleStruct);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    // 16 subscribers == 16 components == 16 distinct MessageHandlers behind 16
                    // distinct tokens. Dedup is per-MessageHandler, so each of the 16 tokens
                    // fires once => 16 invocations per broadcast. The primary token created
                    // above is the first of the 16 subscribers.
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    for (int index = 1; index < ComparisonScenarios.FanOutSubscribers; index++)
                    {
                        MessageRegistrationToken token = CreateToken();
                        _ = token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int index = 0; index < ComparisonScenarios.KeyedListenerCount; index++)
                    {
                        _ = _token.RegisterTargeted<SimpleTargetedMessage>(
                            new InstanceId(KeyedTargetBase + index),
                            HandleTargeted
                        );
                    }
                    _dispatchTarget = new InstanceId(KeyedTargetBase);
                    return;
                case ComparisonScenario.PriorityOrderedDispatch:
                    // Priority is part of the handler-store key, so 4 priorities on a SINGLE
                    // token produce 4 distinct entries => 4 invocations per broadcast.
                    for (int priority = 0; priority < 4; priority++)
                    {
                        _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle, priority);
                    }
                    return;
                case ComparisonScenario.FilteredDispatch:
                    _ = _token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        AllowUntargeted
                    );
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    return;
                case ComparisonScenario.PostProcessingDispatch:
                    _ = _token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        PostProcess
                    );
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    return;
                case ComparisonScenario.InterceptedPostProcessingDispatch:
                    _ = _token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        AllowUntargeted
                    );
                    _ = _token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        PostProcess
                    );
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    // No persistent registration; EmitOnce performs one register/unregister
                    // cycle using the cached handler below.
                    _churnHandler = Handle;
                    return;
                default:
                    _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
                    return;
            }
        }

        public void EmitOnce()
        {
            switch (_scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    _bus.TargetedBroadcast(ref _dispatchTarget, ref _targeted);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    MessageRegistrationHandle handle =
                        _token.RegisterUntargeted<SimpleUntargetedMessage>(_churnHandler);
                    _token.RemoveRegistration(handle);
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _bus.UntargetedBroadcast(ref _structPayload);
                    return;
                default:
                    _bus.UntargetedBroadcast(ref _untargeted);
                    return;
            }
        }

        public void Dispose()
        {
            for (int index = _tokens.Count - 1; index >= 0; index--)
            {
                _tokens[index].UnregisterAll();
                _tokens[index].Dispose();
            }
            _tokens.Clear();

            _churnHandler = null;
            _token = null;
            _bus = null;
        }

        internal string[] CaptureTopologyForContract() =>
            DispatchThroughputBenchmarks.CaptureTopologyForContract(_bus, _tokens);

        internal MessageBus BusForContract => _bus;

        private MessageRegistrationToken CreateToken()
        {
            MessageHandler handler = new(new InstanceId(_nextOwner++), _bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, _bus);
            token.DiagnosticMode = false;
            token.Enable();
            _tokens.Add(token);
            return token;
        }

        private static bool AllowUntargeted(ref SimpleUntargetedMessage message)
        {
            return true;
        }

        private void PostProcess(in SimpleUntargetedMessage message)
        {
            // Post-processor body intentionally runs without touching the handler marker;
            // its execution is the thing being measured for this scenario.
        }
    }
#pragma warning restore RCS1242
}
#endif
