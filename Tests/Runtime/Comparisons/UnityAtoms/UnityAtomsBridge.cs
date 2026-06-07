#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
#if UNITY_ATOMS_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using global::UnityAtoms.BaseAtoms;
    using UnityEngine;

    /// <summary>
    /// Bridges Unity Atoms using its idiomatic <see cref="IntEvent"/> ScriptableObject event
    /// asset. Global dispatch is <c>event.Register(Action&lt;int&gt;)</c> + <c>event.Raise(int)</c>
    /// on a single asset; keyed dispatch uses 16 distinct <see cref="IntEvent"/> assets and
    /// raises exactly one. The struct scenario is Atoms' native <c>int</c>-carrying
    /// <see cref="IntEvent"/> raise, so its per-raise allocation is measured honestly. All
    /// created assets are destroyed in <see cref="Dispose"/>.
    ///
    /// Sixteen-subscriber fan-out registers 16 DISTINCT handler delegates (rather than the
    /// same delegate 16 times) so the fan-out count is exactly 16 regardless of whether the
    /// Atoms event store dedups equal delegates. Priority, filtering, and post-processing
    /// have no idiomatic Atoms hook, so those scenarios are declared unsupported.
    /// </summary>
    public sealed class UnityAtomsBridge : IMessagingTechBridge
    {
        public string TechName => "Unity Atoms";

        public string TechKey => "UnityAtoms";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _progress;

        private const int KeyedListenerCount = 16;

        private ComparisonScenario _scenario;
        private long _progress;

        private IntEvent _event;
        private readonly List<IntEvent> _events = new();

        // Cached, reused churn handler so the SubscribeUnsubscribe scenario measures the
        // event register/unregister cost rather than per-cycle delegate allocation.
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

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;

            void Handle(int value)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                case ComparisonScenario.StructMessageZeroCopy:
                    _event = CreateEvent();
                    _event.Register(Handle);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    _event = CreateEvent();
                    for (int index = 0; index < ComparisonScenarios.FanOutSubscribers; index++)
                    {
                        // 16 DISTINCT delegates so the fan-out is exactly 16 even if the
                        // event store would dedup equal delegates.
                        _event.Register(value => _progress++);
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int index = 0; index < KeyedListenerCount; index++)
                    {
                        IntEvent keyedEvent = CreateEvent();
                        keyedEvent.Register(Handle);
                    }
                    _event = _events[0];
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _event = CreateEvent();
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
                    _event.Register(_churnHandler);
                    _event.Unregister(_churnHandler);
                    _progress++;
                    return;
                default:
                    _event.Raise(0);
                    return;
            }
        }

        public void Dispose()
        {
            for (int index = _events.Count - 1; index >= 0; index--)
            {
                IntEvent created = _events[index];
                if (created != null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                }
            }
            _events.Clear();
            _event = null;
            _churnHandler = null;
        }

        private IntEvent CreateEvent()
        {
            IntEvent created = ScriptableObject.CreateInstance<IntEvent>();
            _events.Add(created);
            return created;
        }
    }
#endif
}
#endif
