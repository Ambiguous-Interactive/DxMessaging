#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Bridges Unity's <c>GameObject.SendMessage</c> reflection-based dispatch. One receiver
    /// MonoBehaviour models global-to-one; many receivers on a single GameObject model fan-out;
    /// many GameObjects with SendMessage to one model keyed dispatch. SendMessage requires
    /// PlayMode (it operates on live GameObjects) and has no priority, filtering, post-processing,
    /// idiomatic churn, or boxing-free struct path, so only the dispatch-shape scenarios are
    /// supported.
    /// </summary>
    public sealed class UnitySendMessageBridge : IMessagingTechBridge
    {
        private const string MessageName = "OnPing";
        private static readonly object PingPayload = 0;

        private sealed class PingReceiver : MonoBehaviour
        {
            public Action Callback;

            // ReSharper disable once UnusedMember.Local - invoked by UnityEngine.GameObject.SendMessage.
            private void OnPing(int payload)
            {
                Callback?.Invoke();
            }
        }

        public string TechName => "Unity SendMessage";

        public string TechKey => "UnitySendMessage";

        public bool RequiresPlayMode => true;

        public long ProgressMarker => _progress;

        private const int DispatchKey = 0;

        // Single-sourced from the canonical scenario constant so the keyed
        // lookup-table size stays identical (1:1) across every comparison bridge.
        private const int KeyCount = ComparisonScenarios.KeyedListenerCount;

        private ComparisonScenario _scenario;
        private long _progress;

        private GameObject _primary;
        private readonly List<GameObject> _keyed = new();
        private GameObject _dispatchTarget;

        public bool Supports(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                case ComparisonScenario.GlobalToManySubscribers:
                case ComparisonScenario.KeyedToOneOfMany:
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
            return Supports(scenario) ? typeof(int) : null;
        }

        public void Prepare(ComparisonScenario scenario)
        {
            _scenario = scenario;

            switch (scenario)
            {
                case ComparisonScenario.GlobalToOneSubscriber:
                    _primary = CreateReceiverObject("SendMessageReceiver", 1);
                    return;
                case ComparisonScenario.GlobalToManySubscribers:
                    _primary = CreateReceiverObject(
                        "SendMessageReceiver",
                        ComparisonScenarios.FanOutSubscribers
                    );
                    return;
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int key = 0; key < KeyCount; key++)
                    {
                        GameObject receiver = CreateReceiverObject($"SendMessageReceiver{key}", 1);
                        _keyed.Add(receiver);
                        if (key == DispatchKey)
                        {
                            _dispatchTarget = receiver;
                        }
                    }
                    return;
                default:
                    return;
            }
        }

        public void EmitOnce()
        {
            switch (_scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    SendPing(_dispatchTarget);
                    return;
                default:
                    SendPing(_primary);
                    return;
            }
        }

        public void Dispose()
        {
            DestroyObject(_primary);
            _primary = null;
            for (int index = 0; index < _keyed.Count; index++)
            {
                DestroyObject(_keyed[index]);
            }
            _keyed.Clear();
            _dispatchTarget = null;
        }

        private GameObject CreateReceiverObject(string name, int receiverCount)
        {
            GameObject gameObject = new(name);
            for (int index = 0; index < receiverCount; index++)
            {
                PingReceiver receiver = gameObject.AddComponent<PingReceiver>();
                receiver.Callback = Increment;
            }
            return gameObject;
        }

        private void Increment()
        {
            _progress++;
        }

        private static void SendPing(GameObject gameObject)
        {
            gameObject.SendMessage(MessageName, PingPayload);
        }

        private static void DestroyObject(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(gameObject);
            }
            else
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
#endif
