#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
#if ZENJECT_PRESENT
    using System;
    using DxMessaging.Tests.Runtime.Comparisons;
    using Zenject;

    /// <summary>
    /// Bridges Zenject's <see cref="SignalBus"/> using a dedicated <see cref="DiContainer"/>
    /// per case (installed via <see cref="SignalBusInstaller"/>, declared with
    /// <c>DeclareSignal&lt;T&gt;()</c>, resolved after <c>ResolveRoots()</c>). Global dispatch
    /// is <c>bus.Subscribe&lt;T&gt;</c> + <c>bus.Fire&lt;T&gt;</c>. The struct scenario fires a
    /// <see cref="ComparisonStructPayload"/> signal; Zenject routes signals through an
    /// <c>object</c>-typed internal path, so the value type boxes there. That boxing is
    /// Zenject's real cost and is measured honestly (no artificial boxing is inserted). The
    /// same <c>int</c> payload used by the zero-dependency bridges is reused for parity.
    ///
    /// Zenject's SignalBus is a flat by-type bus with no keyed routing, priority, filtering,
    /// or post-processing hook, so those scenarios are declared unsupported.
    /// </summary>
    public sealed class ZenjectSignalBusBridge : IMessagingTechBridge
    {
        public string TechName => "Zenject SignalBus";

        public string TechKey => "ZenjectSignalBus";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _fanOut?.Count ?? _progress;

        private ComparisonScenario _scenario;
        private long _progress;
        private FanOut _fanOut;

        private DiContainer _container;
        private SignalBus _bus;

        // Cached, reused churn handler so the SubscribeUnsubscribe scenario measures the
        // bus subscribe/unsubscribe cost rather than per-cycle delegate allocation.
        private Action<int> _churnHandler;

        public bool Supports(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                case ComparisonScenario.GlobalToManySubscribers:
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                case ComparisonScenario.StructMessageZeroCopy:
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
            // The dispatched payload TYPE is the value-type ComparisonStructPayload; Zenject's
            // internal object-typed routing boxes it on the dispatch path (its real cost), but
            // the declared payload is still the canonical non-primitive struct.
            return scenario == ComparisonScenario.StructMessageZeroCopy
                ? typeof(ComparisonStructPayload)
                : typeof(int);
        }

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;
            _container = new DiContainer();
            SignalBusInstaller.Install(_container);

            void Handle(int message)
            {
                _progress++;
            }

            void HandleStruct(ComparisonStructPayload message)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.StructMessageZeroCopy:
                    _container.DeclareSignal<ComparisonStructPayload>();
                    _container.ResolveRoots();
                    _bus = _container.Resolve<SignalBus>();
                    _bus.Subscribe<ComparisonStructPayload>(HandleStruct);
                    return;
                case ComparisonScenario.GlobalToOneSubscriber:
                    _container.DeclareSignal<int>();
                    _container.ResolveRoots();
                    _bus = _container.Resolve<SignalBus>();
                    _bus.Subscribe<int>(Handle);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    _container.DeclareSignal<int>();
                    _container.ResolveRoots();
                    _bus = _container.Resolve<SignalBus>();
                    // SignalBus asserts each (signalType, callback) key is unique and throws on a
                    // duplicate, so the fan-out must use genuinely-distinct subscribers (a distinct
                    // delegate target each). See FanOut for why a loop of identical lambdas does not.
                    _fanOut = new FanOut(ComparisonScenarios.FanOutSubscribers);
                    foreach (FanOut.Subscriber subscriber in _fanOut.Subscribers)
                    {
                        _bus.Subscribe<int>(subscriber.Handle);
                    }
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _container.DeclareSignal<int>();
                    _container.ResolveRoots();
                    _bus = _container.Resolve<SignalBus>();
                    _churnHandler = Handle;
                    return;
                default:
                    return;
            }
        }

        public void EmitOnce()
        {
            switch (_scenario)
            {
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _bus.Subscribe(_churnHandler);
                    _bus.TryUnsubscribe(_churnHandler);
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    _bus.Fire(new ComparisonStructPayload(1));
                    return;
                default:
                    _bus.Fire(0);
                    return;
            }
        }

        public void Dispose()
        {
            // The DiContainer/SignalBus are per-case and GC-collected; DiContainer is not
            // IDisposable and the per-case container holds no shared global state, so dropping
            // the references is sufficient. The S2 fan-out subscriptions die with the container.
            _container = null;
            _bus = null;
            _churnHandler = null;
            _fanOut = null;
        }
    }
#endif
}
#endif
