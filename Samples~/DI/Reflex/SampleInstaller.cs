#if REFLEX_PRESENT
namespace DxMessaging.Samples.DI.Reflex
{
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Unity.Integrations.Reflex;
    using Reflex.Core;
    using UnityEngine;

    /// <summary>
    /// Demonstrates wiring <see cref="IMessageRegistrationBuilder"/> inside a Reflex container.
    /// Requires the Reflex package and the REFLEX_PRESENT scripting define.
    /// </summary>
    public sealed class SampleInstaller : IInstaller
    {
        public void InstallBindings(ContainerBuilder builder)
        {
            // Use the explicit factory-based helper so constructor selection cannot drift with
            // the container's reflection policy.
            builder.AddDxMessagingBus();

            // The DxMessagingRegistrationInstaller shim will have been installed elsewhere; we simply resolve the builder.
            builder.AddSingleton(typeof(PlayerAlertService));
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

            public void EmitAlertFor(GameObject source)
            {
                PlayerAlert alert = new PlayerAlert(source);
                _messageBus.Emit(ref alert);
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
