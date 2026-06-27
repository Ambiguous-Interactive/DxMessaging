#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
#if MESSAGEPIPE_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using MessagePipe;

    /// <summary>
    /// Bridges Cysharp MessagePipe using its idiomatic builder + broker API on a dedicated
    /// <see cref="BuiltinContainerBuilder"/> provider (no shared global broker state leaks
    /// between cases). Keyless scenarios use <c>AddMessageBroker&lt;int&gt;()</c> with
    /// <see cref="IPublisher{TMessage}"/>/<see cref="ISubscriber{TMessage}"/>; the keyed
    /// scenario uses <c>AddMessageBroker&lt;int, int&gt;()</c>; the filtered scenario uses a
    /// <see cref="MessageHandlerFilter{T}"/>. MessagePipe is fully generic, so the struct
    /// scenario dispatches a <see cref="ComparisonStructPayload"/> value with no boxing on
    /// the dispatch path. The same <c>int</c> payload used by the zero-dependency bridges is
    /// reused so the comparison stays apples-to-apples.
    ///
    /// Priority-ordered and post-processing dispatch have no first-class idiomatic hook in
    /// MessagePipe's publish/subscribe surface, so they are declared unsupported.
    /// </summary>
    public sealed class MessagePipeBridge : IMessagingTechBridge
    {
        public string TechName => "MessagePipe";

        public string TechKey => "MessagePipe";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _fanOut?.Count ?? _progress;

        private const int DispatchKey = 0;

        // Single-sourced from the canonical scenario constant so the keyed
        // lookup-table size stays identical (1:1) across every comparison bridge.
        private const int KeyedListenerCount = ComparisonScenarios.KeyedListenerCount;

        private ComparisonScenario _scenario;
        private long _progress;
        private FanOut _fanOut;

        private IServiceProvider _provider;

        private IPublisher<int> _publisher;
        private ISubscriber<int> _subscriber;
        private IPublisher<int, int> _keyedPublisher;
        private ISubscriber<int, int> _keyedSubscriber;
        private IPublisher<ComparisonStructPayload> _structPublisher;
        private ISubscriber<ComparisonStructPayload> _structSubscriber;

        private readonly List<IDisposable> _subscriptions = new();

        // Cached, reused churn handler so the SubscribeUnsubscribe scenario measures the
        // broker subscribe/dispose cost rather than per-cycle delegate allocation.
        private Action<int> _churnHandler;

        public bool Supports(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                case ComparisonScenario.GlobalToManySubscribers:
                case ComparisonScenario.KeyedToOneOfMany:
                case ComparisonScenario.FilteredDispatch:
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                case ComparisonScenario.StructMessageNoBoxing:
                    return true;
                default:
                    return false;
            }
        }

        public long InvocationsPerOperation(ComparisonScenario scenario) =>
            scenario switch
            {
                ComparisonScenario.GlobalToManySubscribers => ComparisonScenarios.FanOutSubscribers,
                _ => 1,
            };

        public Type DispatchedPayloadType(ComparisonScenario scenario)
        {
            if (!Supports(scenario))
            {
                return null;
            }
            return scenario == ComparisonScenario.StructMessageNoBoxing
                ? typeof(ComparisonStructPayload)
                : typeof(int);
        }

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;

            BuiltinContainerBuilder builder = new();
            builder.AddMessagePipe();

            void Handle(int message)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    builder.AddMessageBroker<int>();
                    BuildProvider(builder);
                    _publisher = GlobalMessagePipe.GetPublisher<int>();
                    _subscriber = GlobalMessagePipe.GetSubscriber<int>();
                    _subscriptions.Add(_subscriber.Subscribe(Handle));
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    builder.AddMessageBroker<int>();
                    BuildProvider(builder);
                    _publisher = GlobalMessagePipe.GetPublisher<int>();
                    _subscriber = GlobalMessagePipe.GetSubscriber<int>();
                    // Genuinely-distinct subscribers model 16 independent listeners; this keeps
                    // every bridge's fan-out immune to value-equality dedup. See FanOut.
                    _fanOut = new FanOut(ComparisonScenarios.FanOutSubscribers);
                    foreach (FanOut.Subscriber subscriber in _fanOut.Subscribers)
                    {
                        _subscriptions.Add(_subscriber.Subscribe(subscriber.Handle));
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    builder.AddMessageBroker<int, int>();
                    BuildProvider(builder);
                    _keyedPublisher = GlobalMessagePipe.GetPublisher<int, int>();
                    _keyedSubscriber = GlobalMessagePipe.GetSubscriber<int, int>();
                    for (int key = 0; key < KeyedListenerCount; key++)
                    {
                        _subscriptions.Add(_keyedSubscriber.Subscribe(key, Handle));
                    }
                    return;
                case ComparisonScenario.FilteredDispatch:
                    builder.AddMessageBroker<int>();
                    BuildProvider(builder);
                    _publisher = GlobalMessagePipe.GetPublisher<int>();
                    _subscriber = GlobalMessagePipe.GetSubscriber<int>();
                    _subscriptions.Add(_subscriber.Subscribe(Handle, new PassThroughFilter()));
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    builder.AddMessageBroker<int>();
                    BuildProvider(builder);
                    _publisher = GlobalMessagePipe.GetPublisher<int>();
                    _subscriber = GlobalMessagePipe.GetSubscriber<int>();
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    builder.AddMessageBroker<ComparisonStructPayload>();
                    BuildProvider(builder);
                    _structPublisher = GlobalMessagePipe.GetPublisher<ComparisonStructPayload>();
                    _structSubscriber = GlobalMessagePipe.GetSubscriber<ComparisonStructPayload>();
                    _subscriptions.Add(_structSubscriber.Subscribe(HandleStruct));
                    return;
                default:
                    return;
            }

            void HandleStruct(ComparisonStructPayload message)
            {
                _progress++;
            }
        }

        public void EmitOnce()
        {
            switch (_scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    _keyedPublisher.Publish(DispatchKey, DispatchKey);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    IDisposable subscription = _subscriber.Subscribe(_churnHandler);
                    subscription.Dispose();
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _structPublisher.Publish(new ComparisonStructPayload(1));
                    return;
                default:
                    _publisher.Publish(0);
                    return;
            }
        }

        public void Dispose()
        {
            for (int index = _subscriptions.Count - 1; index >= 0; index--)
            {
                _subscriptions[index]?.Dispose();
            }
            _subscriptions.Clear();

            // The provider (BuiltinContainerBuilderServiceProvider) and its brokers are
            // per-case and GC-collected; the provider is not IDisposable, and GlobalMessagePipe
            // only retains the last provider statically, so dropping the reference is enough.
            _provider = null;
            _publisher = null;
            _subscriber = null;
            _keyedPublisher = null;
            _keyedSubscriber = null;
            _structPublisher = null;
            _structSubscriber = null;
            _churnHandler = null;
            _fanOut = null;
        }

        private void BuildProvider(BuiltinContainerBuilder builder)
        {
            _provider = builder.BuildServiceProvider();
            GlobalMessagePipe.SetProvider(_provider);
        }

        /// <summary>
        /// Idiomatic MessagePipe interception: a filter that simply forwards every message
        /// to the next stage. Its body runs on every dispatch, so the filtered scenario pays
        /// the real interception cost while the handler still increments the counter.
        /// </summary>
        private sealed class PassThroughFilter : MessageHandlerFilter<int>
        {
            public override void Handle(int message, Action<int> next)
            {
                next(message);
            }
        }
    }
#endif
}
#endif
