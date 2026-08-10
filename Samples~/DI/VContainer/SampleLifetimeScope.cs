#if VCONTAINER_PRESENT
namespace DxMessaging.Samples.DI.VContainer
{
    using global::System;
    using global::UnityEngine;
    using global::VContainer;
    using global::DxMessaging.Core.Attributes;
    using global::DxMessaging.Core.Extensions;
    using global::DxMessaging.Core.MessageBus;
    using global::DxMessaging.Unity.Integrations.VContainer;
    using global::VContainer.Unity;

    /// <summary>
    /// Sample lifetime scope showing DI-friendly registration via IMessageRegistrationBuilder.
    /// Requires the VCONTAINER_PRESENT scripting define and VContainer package.
    /// </summary>
    public sealed partial class SampleLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // Always register MessageBus through RegisterDxMessagingBus. The bare pattern
            // builder.Register<MessageBus>(Lifetime.Singleton).As<IMessageBus>() fails at
            // resolution time because VContainer's TypeAnalyzer scans both public and private
            // constructors and prefers the one with the most parameters; that overload takes
            // an IDxMessagingClock that is not registered with the container.
            builder.RegisterDxMessagingBus();
            builder.RegisterMessageRegistrationBuilder();

            builder.RegisterEntryPoint<ScoreboardService>(Lifetime.Singleton);
        }

        [DxUntargetedMessage]
        [DxAutoConstructor]
        private readonly partial struct ScoreUpdated
        {
            public readonly int value;

            public int Value => value;
        }

        private sealed class ScoreboardService : IStartable, ITickable, IDisposable
        {
            private readonly MessageRegistrationLease lease;
            private int observedScores;

            public ScoreboardService(IMessageRegistrationBuilder registrationBuilder)
            {
                lease = registrationBuilder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Configure = token =>
                        {
                            _ = token.RegisterUntargeted<ScoreUpdated>(OnScoreUpdated);
                        },
                    }
                );
            }

            public void Start()
            {
                lease.Activate();
            }

            public void Tick()
            {
                // Emit periodically for demo purposes
                ScoreUpdated message = new ScoreUpdated(UnityEngine.Random.Range(0, 100));
                message.Emit();
            }

            public void Dispose()
            {
                lease.Dispose();
            }

            private void OnScoreUpdated(ref ScoreUpdated message)
            {
                observedScores = message.Value;
                Debug.Log($"Score observed: {observedScores}");
            }
        }
    }
}
#endif
