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

        // The payload is a VALUE type sent through SendMessage's object-typed parameter, so it BOXES
        // on every dispatch (see SendPing). That per-call box is the unavoidable GC cost of
        // reflection-based messaging and is exactly what the comparison's GC-allocation column must
        // surface.
        //
        // DO NOT cache a pre-boxed `object` payload here. Doing so reuses one heap object and reads
        // 0 allocations / 0 bytes -- proven on the host editor (Unity 6000.4, PlayMode): a pre-boxed
        // payload reads 0/0 while a per-call box reads 1 allocation / ~20 bytes per dispatch.
        // A cached box would make Unity SendMessage look allocation-free when no real caller of
        // SendMessage(value) can avoid the box, misrepresenting the technology in DxMessaging's
        // comparison tables. Guarded by ComparisonAllocationHonestyTests.
        //
        // Deliberately NOT `const`: a constant int 0 would bind to the SendMessage(string,
        // SendMessageOptions) overload (the literal 0 converts to the enum) and silently drop the
        // argument; a non-constant int forces the SendMessage(string, object) value overload. The
        // call site also casts to object explicitly as a second guard.
        private static readonly int PingPayload = 0;

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
            // Cast to object EXPLICITLY so this binds to SendMessage(string methodName, object value)
            // -- the value overload -- and boxes the value-type payload on every call. The cast is
            // the cost we measure (boxing is never cached in C#, so it allocates one object per
            // dispatch) AND it disambiguates overload resolution away from SendMessage(string,
            // SendMessageOptions), which a bare 0 would bind to (dropping the argument).
            gameObject.SendMessage(MessageName, (object)PingPayload);
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
