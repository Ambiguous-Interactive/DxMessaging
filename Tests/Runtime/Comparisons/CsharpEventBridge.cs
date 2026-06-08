#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Bridges plain C# delegates/events. A multicast <c>event Action&lt;int&gt;</c> models global
    /// dispatch; a dictionary of per-key events models keyed dispatch; a generic
    /// <c>Action&lt;ComparisonStructPayload&gt;</c> models the struct path (no boxing). C# events have no
    /// built-in priority, filtering, or post-processing, so those scenarios are unsupported.
    /// </summary>
    public sealed class CsharpEventBridge : IMessagingTechBridge
    {
        public string TechName => "C# event";

        public string TechKey => "CsEvent";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _fanOut?.Count ?? _progress;

        private const int KeyCount = 16;

        private ComparisonScenario _scenario;
        private long _progress;
        private FanOut _fanOut;

        private event Action<int> Global;
        private event Action<ComparisonStructPayload> StructGlobal;
        private readonly Dictionary<int, Action<int>> _keyed = new();
        private Action<int> _churnHandler;

        public bool Supports(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                case ComparisonScenario.GlobalToManySubscribers:
                case ComparisonScenario.KeyedToOneOfMany:
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
            return scenario == ComparisonScenario.StructMessageZeroCopy
                ? typeof(ComparisonStructPayload)
                : typeof(int);
        }

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;

            void Handle(int payload)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    Global += Handle;
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    // Genuinely-distinct subscribers model 16 independent listeners. A multicast
                    // event would happily invoke the same delegate 16 times, but distinct
                    // subscribers keep every bridge's fan-out immune to value-equality dedup. See
                    // FanOut.
                    _fanOut = new FanOut(ComparisonScenarios.FanOutSubscribers);
                    foreach (FanOut.Subscriber subscriber in _fanOut.Subscribers)
                    {
                        Global += subscriber.Handle;
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int key = 0; key < KeyCount; key++)
                    {
                        _keyed[key] = Handle;
                    }
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    StructGlobal += HandleStruct;
                    return;
                default:
                    return;
            }

            void HandleStruct(ComparisonStructPayload payload)
            {
                _progress++;
            }
        }

        public void EmitOnce()
        {
            switch (_scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    _keyed[0](0);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    Global += _churnHandler;
                    Global -= _churnHandler;
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    StructGlobal?.Invoke(new ComparisonStructPayload(1));
                    return;
                default:
                    Global?.Invoke(0);
                    return;
            }
        }

        public void Dispose()
        {
            Global = null;
            StructGlobal = null;
            _keyed.Clear();
            _churnHandler = null;
            _fanOut = null;
        }
    }
}
#endif
