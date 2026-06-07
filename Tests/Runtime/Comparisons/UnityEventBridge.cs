#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System.Collections.Generic;
    using UnityEngine.Events;

    /// <summary>
    /// Bridges Unity's <see cref="UnityEvent{T0}"/>. A single event with one or many listeners
    /// models global dispatch; an array of per-key events models keyed dispatch; a
    /// <see cref="UnityEvent{T0}"/> over <see cref="ComparisonStructPayload"/> exercises the struct
    /// path so the allocation column reflects UnityEvent's behaviour honestly. UnityEvent has
    /// no priority, filtering, or post-processing, so those scenarios are unsupported.
    /// </summary>
    public sealed class UnityEventBridge : IMessagingTechBridge
    {
        private sealed class IntEvent : UnityEvent<int> { }

        private sealed class StructEvent : UnityEvent<ComparisonStructPayload> { }

        public string TechName => "UnityEvent";

        public string TechKey => "UnityEvent";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _progress;

        private const int KeyCount = 16;

        private ComparisonScenario _scenario;
        private long _progress;

        private readonly IntEvent _global = new();
        private readonly StructEvent _structGlobal = new();
        private readonly List<IntEvent> _keyed = new();
        private UnityAction<int> _churnHandler;

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
                    _global.AddListener(Handle);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    for (int index = 0; index < ComparisonScenarios.FanOutSubscribers; index++)
                    {
                        _global.AddListener(Handle);
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int key = 0; key < KeyCount; key++)
                    {
                        IntEvent keyedEvent = new();
                        keyedEvent.AddListener(Handle);
                        _keyed.Add(keyedEvent);
                    }
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    _structGlobal.AddListener(HandleStruct);
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
                    _keyed[0].Invoke(0);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _global.AddListener(_churnHandler);
                    _global.RemoveListener(_churnHandler);
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    _structGlobal.Invoke(new ComparisonStructPayload(1));
                    return;
                default:
                    _global.Invoke(0);
                    return;
            }
        }

        public void Dispose()
        {
            _global.RemoveAllListeners();
            _structGlobal.RemoveAllListeners();
            for (int index = 0; index < _keyed.Count; index++)
            {
                _keyed[index].RemoveAllListeners();
            }
            _keyed.Clear();
            _churnHandler = null;
        }
    }
}
#endif
