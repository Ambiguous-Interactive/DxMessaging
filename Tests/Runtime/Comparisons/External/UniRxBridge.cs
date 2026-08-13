#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
#if UNIRX_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using UniRx;

    /// <summary>
    /// Bridges UniRx's <see cref="MessageBroker"/> (the MessagePipe-style typed pub/sub bus)
    /// using a DEDICATED broker instance rather than the shared <c>MessageBroker.Default</c>,
    /// so state never leaks between cases and the comparison stays fair against the other
    /// dedicated-container bridges. Global dispatch is <c>broker.Receive&lt;T&gt;().Subscribe</c>
    /// + <c>broker.Publish</c>; the struct scenario publishes a <see cref="ComparisonStructPayload"/>
    /// through the generic broker (no boxing on the dispatch path). Non-struct scenarios reuse
    /// the same <c>int</c> payload as the zero-dependency bridges.
    ///
    /// UniRx's MessageBroker is a flat global-by-type bus with no keyed routing, priority, or
    /// post-processing hook. Filtering uses UniRx's idiomatic <c>Where</c> operator.
    /// </summary>
    public sealed class UniRxBridge : IMessagingTechBridge
    {
        public string TechName => "UniRx MessageBroker";

        public string TechKey => "UniRx";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _fanOut?.Count ?? _progress;

        private ComparisonScenario _scenario;
        private long _progress;
        private FanOut _fanOut;

        private MessageBroker _broker;
        private IObservable<int> _intStream;
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
            _broker = new MessageBroker();

            void Handle(int message)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    _intStream = _broker.Receive<int>();
                    _subscriptions.Add(_intStream.Subscribe(Handle));
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    // Genuinely-distinct subscribers model 16 independent listeners; this keeps
                    // every bridge's fan-out immune to value-equality dedup. See FanOut.
                    _fanOut = new FanOut(ComparisonScenarios.FanOutSubscribers);
                    _intStream = _broker.Receive<int>();
                    foreach (FanOut.Subscriber subscriber in _fanOut.Subscribers)
                    {
                        _subscriptions.Add(_intStream.Subscribe(subscriber.Handle));
                    }
                    return;
                case ComparisonScenario.FilteredDispatch:
                    _intStream = _broker.Receive<int>().Where(Allow);
                    _subscriptions.Add(_intStream.Subscribe(Handle));
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    // No persistent registration; EmitOnce performs one subscribe/dispose
                    // cycle using the cached stream and handler below.
                    _intStream = _broker.Receive<int>();
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _subscriptions.Add(
                        _broker.Receive<ComparisonStructPayload>().Subscribe(HandleStruct)
                    );
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
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    IDisposable subscription = _intStream.Subscribe(_churnHandler);
                    subscription.Dispose();
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _broker.Publish(new ComparisonStructPayload(1));
                    return;
                default:
                    _broker.Publish(0);
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
            _broker?.Dispose();
            _broker = null;
            _intStream = null;
            _churnHandler = null;
            _fanOut = null;
        }

        private static bool Allow(int message)
        {
            return true;
        }
    }
#endif
}
#endif
