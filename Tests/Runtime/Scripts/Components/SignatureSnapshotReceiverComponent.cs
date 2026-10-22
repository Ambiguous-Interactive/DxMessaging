#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using UnityEngine;

    public sealed class SignatureSnapshotReceiverComponent : MonoBehaviour
    {
        public int IntegerCalls { get; private set; }
        public int ObjectCalls { get; private set; }

        public void ReceiveSignature(int first, int second)
        {
            ++IntegerCalls;
        }

        public void ReceiveSignature(object first, object second)
        {
            ++ObjectCalls;
        }
    }
}
#endif
