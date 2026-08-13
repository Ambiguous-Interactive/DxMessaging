#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
#if UNITY_ATOMS_CORE_PRESENT && UNITY_ATOMS_BASE_ATOMS_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using global::UnityAtoms;
    using global::UnityAtoms.BaseAtoms;
    using UnityEngine;

    internal sealed class ComparisonStructAtomEvent : AtomEvent<ComparisonStructPayload> { }

    /// <summary>
    /// Bridges Unity Atoms using its idiomatic <see cref="IntEvent"/> ScriptableObject event
    /// asset. Global dispatch is <c>event.Register(Action&lt;int&gt;)</c> + <c>event.Raise(int)</c>
    /// on a single asset; keyed dispatch uses 16 distinct <see cref="IntEvent"/> assets and
    /// raises exactly one. Struct dispatch uses a local
    /// <see cref="AtomEvent{T}"/> specialization for <see cref="ComparisonStructPayload"/>.
    /// Every event disables replay buffering so the benchmark measures dispatch rather than
    /// retaining the last payload, and all created assets are destroyed synchronously in
    /// <see cref="Dispose"/>.
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

        public long ProgressMarker => _fanOut?.Count ?? _progress;

        private const int DispatchKey = 0;

        // Single-sourced from the canonical scenario constant so the keyed
        // lookup-table size stays identical (1:1) across every comparison bridge.
        private const int KeyedListenerCount = ComparisonScenarios.KeyedListenerCount;

        private ComparisonScenario _scenario;
        private long _progress;
        private FanOut _fanOut;

        private IntEvent _event;
        private ComparisonStructAtomEvent _structEvent;
        private readonly List<ScriptableObject> _events = new();

        internal IReadOnlyList<ScriptableObject> CreatedEvents => _events;

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

            void Handle(int value)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    _event = CreateEvent();
                    _event.Register(Handle);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    _event = CreateEvent();
                    // Genuinely-distinct subscribers so the fan-out is exactly 16 even if the Atoms
                    // event store deduped equal delegates. See FanOut for why a loop of identical
                    // lambdas would collapse to one subscriber under value-equality dedup.
                    _fanOut = new FanOut(ComparisonScenarios.FanOutSubscribers);
                    foreach (FanOut.Subscriber subscriber in _fanOut.Subscribers)
                    {
                        _event.Register(subscriber.Handle);
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int index = 0; index < KeyedListenerCount; index++)
                    {
                        IntEvent keyedEvent = CreateEvent();
                        keyedEvent.Register(Handle);
                        if (index == DispatchKey)
                        {
                            _event = keyedEvent;
                        }
                    }
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _event = CreateEvent();
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageNoBoxing:
                    _structEvent = CreateStructEvent();
                    _structEvent.Register(HandleStruct);
                    return;
                default:
                    return;
            }

            void HandleStruct(ComparisonStructPayload value)
            {
                _progress++;
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
                case ComparisonScenario.StructMessageNoBoxing:
                    _structEvent.Raise(new ComparisonStructPayload(1));
                    return;
                default:
                    _event.Raise(DispatchKey);
                    return;
            }
        }

        public void Dispose()
        {
            for (int index = _events.Count - 1; index >= 0; index--)
            {
                ScriptableObject created = _events[index];
                if (created != null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                }
            }
            _events.Clear();
            _event = null;
            _structEvent = null;
            _churnHandler = null;
            _fanOut = null;
        }

        private IntEvent CreateEvent()
        {
            IntEvent created = ScriptableObject.CreateInstance<IntEvent>();
            created.ReplayBufferSize = 0;
            _events.Add(created);
            return created;
        }

        private ComparisonStructAtomEvent CreateStructEvent()
        {
            ComparisonStructAtomEvent created =
                ScriptableObject.CreateInstance<ComparisonStructAtomEvent>();
            created.ReplayBufferSize = 0;
            _events.Add(created);
            return created;
        }
    }
#endif
}
#endif
