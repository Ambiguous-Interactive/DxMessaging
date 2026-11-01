#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using UnityEngine;

    /// <summary>Receives a two-argument reflexive message without adding other components.</summary>
    public sealed class ReflexiveMetadataReceiver : MonoBehaviour
    {
        internal Action<int, int> OnMessage { get; set; }

        public void Receive(int first, int second) => OnMessage?.Invoke(first, second);
    }
}
#endif
