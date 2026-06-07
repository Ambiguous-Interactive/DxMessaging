#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Bridges the canonical ScriptableObject event-channel architecture. One channel asset
    /// with one or many listeners models global dispatch; K channel assets (one per key)
    /// model keyed dispatch; a struct-carrying channel models the struct path. ScriptableObject
    /// channels have no priority, filtering, or post-processing, so those scenarios are
    /// unsupported.
    /// </summary>
    public sealed class ScriptableObjectChannelBridge : IMessagingTechBridge
    {
        public string TechName => "ScriptableObject channel";

        public string TechKey => "ScriptableObject";

        public bool RequiresPlayMode => false;

        public long ProgressMarker => _progress;

        private const int KeyCount = 16;

        private ComparisonScenario _scenario;
        private long _progress;

        private ScriptableObjectEventChannel _global;
        private ScriptableObjectStructEventChannel _structGlobal;
        private readonly List<ScriptableObjectEventChannel> _keyed = new();
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

            void Handle(int payload)
            {
                _progress++;
            }

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    _global = ScriptableObject.CreateInstance<ScriptableObjectEventChannel>();
                    _global.Register(Handle);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    _global = ScriptableObject.CreateInstance<ScriptableObjectEventChannel>();
                    for (int index = 0; index < ComparisonScenarios.FanOutSubscribers; index++)
                    {
                        _global.Register(Handle);
                    }
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int key = 0; key < KeyCount; key++)
                    {
                        ScriptableObjectEventChannel channel =
                            ScriptableObject.CreateInstance<ScriptableObjectEventChannel>();
                        channel.Register(Handle);
                        _keyed.Add(channel);
                    }
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _global = ScriptableObject.CreateInstance<ScriptableObjectEventChannel>();
                    _churnHandler = Handle;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    _structGlobal =
                        ScriptableObject.CreateInstance<ScriptableObjectStructEventChannel>();
                    _structGlobal.Register(HandleStruct);
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
                    _keyed[0].Raise(0);
                    return;
                case ComparisonScenario.SubscribeUnsubscribeChurn:
                    _global.Register(_churnHandler);
                    _global.Unregister(_churnHandler);
                    _progress++;
                    return;
                case ComparisonScenario.StructMessageZeroCopy:
                    _structGlobal.Raise(new ComparisonStructPayload(1));
                    return;
                default:
                    _global.Raise(0);
                    return;
            }
        }

        public void Dispose()
        {
            if (_global != null)
            {
                Object.DestroyImmediate(_global);
                _global = null;
            }
            if (_structGlobal != null)
            {
                Object.DestroyImmediate(_structGlobal);
                _structGlobal = null;
            }
            for (int index = 0; index < _keyed.Count; index++)
            {
                if (_keyed[index] != null)
                {
                    Object.DestroyImmediate(_keyed[index]);
                }
            }
            _keyed.Clear();
            _churnHandler = null;
        }
    }
}
#endif
