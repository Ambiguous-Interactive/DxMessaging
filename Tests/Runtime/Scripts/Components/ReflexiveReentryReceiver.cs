#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using UnityEngine;

    /// <summary>Receives distinct two-argument methods during metadata reentry.</summary>
    public sealed class ReflexiveReentryReceiver : MonoBehaviour
    {
        internal Action<string, int, int> OnMessage { get; set; }

        public void Receive(int first, int second) =>
            OnMessage?.Invoke(nameof(Receive), first, second);

        public void ReceiveOther(int first, int second) =>
            OnMessage?.Invoke(nameof(ReceiveOther), first, second);
    }
}
#endif
