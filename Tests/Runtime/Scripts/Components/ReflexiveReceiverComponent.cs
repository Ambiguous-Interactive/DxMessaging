#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using UnityEngine;

    public sealed class ReflexiveReceiverComponent : MonoBehaviour
    {
        public int InvocationCount { get; private set; }

        public void OnReflexive()
        {
            InvocationCount++;
        }

        public void OnAmbiguous(System.ICloneable value)
        {
            InvocationCount++;
        }

        public void OnAmbiguous(System.Collections.Generic.IEnumerable<char> value)
        {
            InvocationCount++;
        }
    }
}

#endif
