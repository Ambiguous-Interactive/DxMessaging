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
    public sealed partial class SampleInstaller : IInstaller
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
                alert.EmitBroadcast(alert.Source, _messageBus);
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
