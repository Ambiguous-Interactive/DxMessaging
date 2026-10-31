#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using UnityEngine;
    using UnityEngine.Scripting;

    public sealed class ReflexiveOpenGenericReceiver : MonoBehaviour
    {
        public Action<string, int, int> OnMessage;
        public int GenericCalls;

        [Preserve]
        public void PublicGenericOnly<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicGenericOnly.generic", first, second);
        }

        [Preserve]
        public void PublicGenericFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicGenericFirst.generic", first, second);
        }

        [Preserve]
        public void PublicGenericFirst(int first, int second)
        {
            OnMessage?.Invoke("PublicGenericFirst", first, second);
        }

        [Preserve]
        public void PublicConcreteFirst(int first, int second)
        {
            OnMessage?.Invoke("PublicConcreteFirst", first, second);
        }

        [Preserve]
        public void PublicConcreteFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PublicConcreteFirst.generic", first, second);
        }

        [Preserve]
        public void PublicConcreteOnly(int first, int second)
        {
            OnMessage?.Invoke("PublicConcreteOnly", first, second);
        }

        [Preserve]
        public void PublicGenericParameterUsed<T>(T first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        private void PrivateGenericOnly<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateGenericOnly.generic", first, second);
        }

        [Preserve]
        private void PrivateGenericFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateGenericFirst.generic", first, second);
        }

        [Preserve]
        private void PrivateGenericFirst(int first, int second)
        {
            OnMessage?.Invoke("PrivateGenericFirst", first, second);
        }

        [Preserve]
        private void PrivateConcreteFirst(int first, int second)
        {
            OnMessage?.Invoke("PrivateConcreteFirst", first, second);
        }

        [Preserve]
        private void PrivateConcreteFirst<T>(int first, int second)
        {
            ++GenericCalls;
            OnMessage?.Invoke("PrivateConcreteFirst.generic", first, second);
        }

        [Preserve]
        private void PrivateConcreteOnly(int first, int second)
        {
            OnMessage?.Invoke("PrivateConcreteOnly", first, second);
        }

        [Preserve]
        private void PrivateGenericParameterUsed<T>(T first, int second)
        {
            ++GenericCalls;
        }

        [Preserve]
        public void ReceiveRecovery(int first, int second)
        {
            OnMessage?.Invoke(nameof(ReceiveRecovery), first, second);
        }
    }
}
#endif
