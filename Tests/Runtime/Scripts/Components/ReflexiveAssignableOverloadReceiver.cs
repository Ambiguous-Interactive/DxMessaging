#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using UnityEngine;
    using UnityEngine.Scripting;

    public sealed class ReflexiveAssignableOverloadReceiver : MonoBehaviour
    {
        public Action<string, int, int> OnMessage;
        public int GenericCalls;

        [Preserve]
        public void PublicObject<T>(int first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        public void PublicObject(object first, int second)
        {
            OnMessage?.Invoke("PublicObject", (int)first, second);
        }

        [Preserve]
        public void PublicInterface<T>(int first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        public void PublicInterface(IConvertible first, int second)
        {
            OnMessage?.Invoke("PublicInterface", first.ToInt32(null), second);
        }

        [Preserve]
        public void PublicObjectControl(object first, int second)
        {
            OnMessage?.Invoke("PublicObjectControl", (int)first, second);
        }

        [Preserve]
        private void PrivateObject<T>(int first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        private void PrivateObject(object first, int second)
        {
            OnMessage?.Invoke("PrivateObject", (int)first, second);
        }

        [Preserve]
        private void PrivateInterface<T>(int first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        private void PrivateInterface(IConvertible first, int second)
        {
            OnMessage?.Invoke("PrivateInterface", first.ToInt32(null), second);
        }

        [Preserve]
        private void PrivateObjectControl(object first, int second)
        {
            OnMessage?.Invoke("PrivateObjectControl", (int)first, second);
        }

        [Preserve]
        public void ReceiveRecovery(int first, int second)
        {
            OnMessage?.Invoke(nameof(ReceiveRecovery), first, second);
        }
    }
}
#endif
