#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Bridges Unity's <c>GameObject.SendMessage</c> reflection-based, addressed dispatch. Sixteen
    /// GameObjects with <c>SendMessage</c> to one model keyed dispatch. Global-to-one and
    /// global-to-many are unsupported because <c>SendMessage</c> always addresses a specific
    /// GameObject rather than a global channel. <c>SendMessage</c> requires PlayMode (it operates
    /// on live GameObjects) and has no priority, filtering, post-processing, idiomatic churn, or
    /// boxing-free struct path, so only keyed dispatch is supported.
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

        private sealed class InvocationCounter
        {
            public long Count;
        }

        private sealed class PingReceiver : MonoBehaviour
        {
            [NonSerialized]
            public InvocationCounter Counter;

            // ReSharper disable once UnusedMember.Local - invoked by UnityEngine.GameObject.SendMessage.
            private void OnPing(int payload)
            {
                Counter.Count++;
            }
        }

        public string TechName => "Unity SendMessage";

        public string TechKey => "UnitySendMessage";

        public bool RequiresPlayMode => true;

        public long ProgressMarker => _counter.Count;

        private const int DispatchKey = 0;

        // Single-sourced from the canonical scenario constant so the keyed
        // lookup-table size stays identical (1:1) across every comparison bridge.
        private const int KeyCount = ComparisonScenarios.KeyedListenerCount;

        private readonly InvocationCounter _counter = new();

        private readonly List<GameObject> _keyed = new();
        private GameObject _dispatchTarget;

        internal GameObject DispatchTargetForTests => _dispatchTarget;

        public bool Supports(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    return true;
                default:
                    return false;
            }
        }

        public long InvocationsPerOperation(ComparisonScenario scenario) => 1;

        public Type DispatchedPayloadType(ComparisonScenario scenario)
        {
            return Supports(scenario) ? typeof(int) : null;
        }

        public void Prepare(ComparisonScenario scenario)
        {
            switch (scenario)
            {
                case ComparisonScenario.KeyedToOneOfMany:
                    for (int key = 0; key < KeyCount; key++)
                    {
                        GameObject receiver = CreateReceiverObject($"SendMessageReceiver{key}");
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
            SendPing(_dispatchTarget);
        }

        public void Dispose()
        {
            for (int index = 0; index < _keyed.Count; index++)
            {
                DestroyObject(_keyed[index]);
            }
            _keyed.Clear();
            _dispatchTarget = null;
        }

        private GameObject CreateReceiverObject(string name)
        {
            GameObject gameObject = new(name);
            PingReceiver receiver = gameObject.AddComponent<PingReceiver>();
            receiver.Counter = _counter;
            return gameObject;
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
            Object.DestroyImmediate(gameObject);
        }
    }
}
#endif
