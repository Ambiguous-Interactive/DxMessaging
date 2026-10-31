#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using UnityEngine;

    public sealed class ReflexiveOpenGenericReceiver : MonoBehaviour
    {
        public Action<string, int, int> OnMessage;
        public int GenericCalls;

        public void PublicGenericOnly<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicGenericOnly.generic", first, second);
        }

        public void PublicGenericFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicGenericFirst.generic", first, second);
        }

        public void PublicGenericFirst(int first, int second)
        {
            OnMessage?.Invoke("PublicGenericFirst", first, second);
        }

        public void PublicConcreteFirst(int first, int second)
        {
            OnMessage?.Invoke("PublicConcreteFirst", first, second);
        }

        public void PublicConcreteFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicConcreteFirst.generic", first, second);
        }

        public void PublicConcreteOnly(int first, int second)
        {
            OnMessage?.Invoke("PublicConcreteOnly", first, second);
        }

        public void PublicGenericParameterUsed<T>(T first, int second)
        {
            ++GenericCalls;
        }

        private void PrivateGenericOnly<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateGenericOnly.generic", first, second);
        }

        private void PrivateGenericFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateGenericFirst.generic", first, second);
        }

        private void PrivateGenericFirst(int first, int second)
        {
            OnMessage?.Invoke("PrivateGenericFirst", first, second);
        }

        private void PrivateConcreteFirst(int first, int second)
        {
            OnMessage?.Invoke("PrivateConcreteFirst", first, second);
        }

        private void PrivateConcreteFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateConcreteFirst.generic", first, second);
        }

        private void PrivateConcreteOnly(int first, int second)
        {
            OnMessage?.Invoke("PrivateConcreteOnly", first, second);
        }

        private void PrivateGenericParameterUsed<T>(T first, int second)
        {
            ++GenericCalls;
        }

        public void ReceiveRecovery(int first, int second)
        {
            OnMessage?.Invoke(nameof(ReceiveRecovery), first, second);
        }
    }
}
#endif
