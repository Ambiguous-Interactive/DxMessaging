#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Benchmarks
{
    using System;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Unity;
    using UnityEngine;
    using Object = UnityEngine.Object;

    public abstract class BenchmarkTestBase : MessagingTestBase
    {
        protected const int NumInvocationsPerIteration = BenchmarkProtocol.BatchSize;

        private BenchmarkSession _session;

        protected BenchmarkSession CurrentSession => _session;

        protected void RunWithSession(BenchmarkSession session, Action body)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            _session = session;
            try
            {
                using (session)
                {
                    body();
                }
            }
            finally
            {
                _session = null;
            }
        }

        protected void RecordBenchmark(string label, int count, TimeSpan duration, bool allocating)
        {
            long operationsPerSecond =
                duration.TotalSeconds <= 0.0 ? 0 : (long)Math.Floor(count / duration.TotalSeconds);

            if (_session != null)
            {
                _session.Record(label, operationsPerSecond, allocating);
                return;
            }

            string formatted = operationsPerSecond.ToString("N0", CultureInfo.InvariantCulture);
            Debug.Log($"| {label} | {formatted} | {(allocating ? "Yes" : "No")} |");
        }

        protected GameObject CreateBenchmarkGameObject()
        {
            GameObject target = new(
                "Benchmark",
                typeof(EmptyMessageAwareComponent),
                typeof(SpriteRenderer),
                typeof(Rigidbody2D),
                typeof(CircleCollider2D),
                typeof(LineRenderer)
            );
            _spawned.Add(target);
            return target;
        }

        protected void RunWithComponent(Action<EmptyMessageAwareComponent> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            RunWithComponent((component, _) => action(component));
        }

        protected void RunWithComponent(
            Action<EmptyMessageAwareComponent, MessageRegistrationToken> action
        )
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            GameObject go = CreateBenchmarkGameObject();
            MessageRegistrationToken token = null;
            try
            {
                EmptyMessageAwareComponent component =
                    go.GetComponent<EmptyMessageAwareComponent>();
                if (component == null)
                {
                    throw new InvalidOperationException(
                        "Benchmark GameObject was missing EmptyMessageAwareComponent."
                    );
                }

                token = GetOrCreateEnabledToken(go, component);
                PrepareBenchmarkGameObjectForSendMessage(go);
                action(component, token);
            }
            finally
            {
                token?.UnregisterAll();
                _spawned.Remove(go);
                if (Application.isPlaying)
                {
                    Object.Destroy(go);
                }
                else
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static MessageRegistrationToken GetOrCreateEnabledToken(
            GameObject go,
            EmptyMessageAwareComponent component
        )
        {
            MessageRegistrationToken token = component.Token;
            if (token == null)
            {
                MessagingComponent messagingComponent = go.GetComponent<MessagingComponent>();
                if (messagingComponent == null)
                {
                    throw new InvalidOperationException(
                        $"Benchmark GameObject '{go.name}' is missing {nameof(MessagingComponent)}."
                    );
                }

                token = messagingComponent.Create(component);
                // Benchmarks register handlers explicitly per scenario, so they do not depend on
                // MessageAwareComponent.RegisterMessageHandlers being invoked here.
            }

            if (!token.Enabled)
            {
                token.Enable();
            }

            return token;
        }

        protected static void PrepareBenchmarkGameObjectForSendMessage(GameObject target)
        {
            target.SetActive(true);

            foreach (MonoBehaviour behaviour in target.GetComponents<MonoBehaviour>())
            {
                PrepareBenchmarkBehaviourForSendMessage(behaviour);
            }
        }

        protected static void PrepareBenchmarkBehaviourForSendMessage(MonoBehaviour behaviour)
        {
            if (behaviour == null)
            {
                return;
            }

            behaviour.enabled = true;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // EditMode SendMessage requires runnable behaviours, otherwise Unity logs
                // repeated ShouldRunBehaviour assertions that fail benchmark tests.
                behaviour.runInEditMode = true;
            }
#endif
        }
    }
}

#endif
