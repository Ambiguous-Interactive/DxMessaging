#if ZENJECT_PRESENT
namespace DxMessaging.Samples.DI.Zenject
{
    using global::System;
    using global::UnityEngine;
    using global::Zenject;
    using global::DxMessaging.Core;
    using global::DxMessaging.Core.Attributes;
    using global::DxMessaging.Core.MessageBus;

    /// <summary>
    /// Sample scene installer demonstrating how to bridge the registration builder into Zenject services.
    /// Requires the DxMessaging Zenject registration shim and the ZENJECT_PRESENT scripting define.
    /// </summary>
    public sealed partial class SampleInstaller : MonoInstaller
    {
        public override void InstallBindings()
        {
            // The MessageBus is bound elsewhere (typically through
            // ZenjectRegistrationExtensions.BindDxMessagingBus, which uses an explicit factory).
            // Avoid the bare Container.Bind<MessageBus>().AsSingle() pattern: Zenject today picks
            // the public parameterless constructor, but its constructor-selection behaviour is
            // version-sensitive, and a future release could broaden scanning to non-public
            // constructors -- which would surface a clock-taking overload whose
            // IDxMessagingClock dependency is not registered. The helper sidesteps that risk.

            // Ensure the builder is available (provided by DxMessagingRegistrationInstaller).
            Container.BindInterfacesTo<PlayerSpawnTracker>().AsSingle();
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        private readonly partial struct PlayerSpawned
        {
            public readonly string playerName;

            public string PlayerName => playerName;
        }

        private sealed class PlayerSpawnTracker : IInitializable, IDisposable
        {
            private readonly MessageRegistrationLease lease;

            public PlayerSpawnTracker(IMessageRegistrationBuilder builder)
            {
                lease = builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Configure = token =>
                        {
                            _ = token.RegisterBroadcastWithoutSource<PlayerSpawned>(
                                OnPlayerSpawned
                            );
                        },
                    }
                );
            }

            public void Initialize()
            {
                lease.Activate();
            }

            public void Dispose()
            {
                lease.Dispose();
            }

            private static void OnPlayerSpawned(InstanceId player, PlayerSpawned message)
            {
                Debug.Log($"Player spawned: {message.PlayerName}");
            }
        }
    }
}
#endif
