#if REFLEX_PRESENT
namespace DxMessaging.Samples.DI.Reflex
{
    using global::UnityEngine;
    using global::DxMessaging.Core;
    using global::DxMessaging.Core.Attributes;
    using global::DxMessaging.Core.Extensions;
    using global::DxMessaging.Core.MessageBus;
    using global::DxMessaging.Core.Messages;
    using global::DxMessaging.Unity.Integrations.Reflex;
    using global::Reflex.Core;

    /// <summary>
    /// Demonstrates wiring <see cref="IMessageRegistrationBuilder"/> inside a Reflex container.
    /// Requires the Reflex package and the REFLEX_PRESENT scripting define.
    /// </summary>
    public sealed partial class SampleInstaller : MonoBehaviour, IInstaller
    {
        private ContainerBuilder _builder;
        private PlayerAlertService _service;

        public void InstallBindings(ContainerBuilder builder)
        {
            if (builder == null)
            {
                throw new System.ArgumentNullException(nameof(builder));
            }

            // Use the explicit factory-based helper so constructor selection cannot drift with
            // the container's reflection policy.
            UnsubscribeFromContainerBuilt();
            _builder = builder;
            builder.AddDxMessagingBus();
            new DxMessagingRegistrationInstaller().InstallBindings(builder);
            builder.OnContainerBuilt += OnContainerBuilt;
        }

        public void EmitAlertFor(GameObject gameObject)
        {
            if (gameObject == null)
            {
                throw new System.ArgumentNullException(nameof(gameObject));
            }

            if (_service == null)
            {
                throw new System.InvalidOperationException(
                    "Reflex has not built the sample container yet."
                );
            }

            _service.EmitAlertFor(gameObject);
        }

        private void OnContainerBuilt(Container container)
        {
            UnsubscribeFromContainerBuilt();
            _service?.Dispose();
            _service = container.Construct<PlayerAlertService>();
        }

        private void OnDestroy()
        {
            UnsubscribeFromContainerBuilt();
            _service?.Dispose();
            _service = null;
        }

        private void UnsubscribeFromContainerBuilt()
        {
            if (_builder == null)
            {
                return;
            }

            _builder.OnContainerBuilt -= OnContainerBuilt;
            _builder = null;
        }

        private sealed class PlayerAlertService : System.IDisposable
        {
            private readonly IMessageBus _messageBus;
            private readonly MessageRegistrationLease _lease;

            public PlayerAlertService(IMessageBus messageBus, IMessageRegistrationBuilder builder)
            {
                _messageBus = messageBus;
                _lease = builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Configure = token =>
                        {
                            _ = token.RegisterBroadcastWithoutSource<PlayerAlert>(OnPlayerAlert);
                        },
                        HandlerStartsActive = true,
                        ActivateOnBuild = true,
                    }
                );
            }

            internal void EmitAlertFor(GameObject gameObject)
            {
                PlayerAlert alert = new PlayerAlert(gameObject);
                alert.EmitGameObjectBroadcast(gameObject, _messageBus);
            }

            public void Dispose()
            {
                _lease.Dispose();
            }

            private void OnPlayerAlert(ref InstanceId source, ref PlayerAlert alert)
            {
                Debug.Log($"Reflex alert from {source.Id}");
            }
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        private readonly partial struct PlayerAlert
        {
            public readonly InstanceId source;

            public InstanceId Source => source;
        }
    }
}
#endif
