#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Parameterized emit smoke tests that exercise the basic register-emit-receive
    /// loop across all three message kinds via <see cref="ScenarioHarness"/>. The
    /// per-kind variants of these tests live in the kind-specific files
    /// <c>EmitUntargetedSpecificTests.cs</c>, <c>EmitTargetedSpecificTests.cs</c>,
    /// and <c>EmitBroadcastSpecificTests.cs</c>; only logic that is provably
    /// identical across all three kinds (after factoring out kind-specific
    /// register/emit calls) is consolidated here. Tests with kind-specific
    /// assertion semantics (different expected counts, asymmetric routing rules,
    /// without-targeting variants, etc.) intentionally remain in the per-kind
    /// files to preserve assertion fidelity.
    /// </summary>
    public sealed class EmitTests : MessagingTestBase
    {
        [Test]
        public void HandlerReceivesEmittedMessage(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerReceivesEmittedMessage) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            const int numEmissions = 100;

            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => ++count
                    );
                    SimpleUntargetedMessage message = new();
                    for (int i = 0; i < numEmissions; ++i)
                    {
                        Assert.AreEqual(i, count);
                        ScenarioHarness.EmitUntargeted(scenario, ref message);
                    }

                    break;
                }
                case MessageKind.Targeted:
                {
                    _ = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        component,
                        (ref SimpleTargetedMessage _) => ++count
                    );
                    SimpleTargetedMessage message = new();
                    for (int i = 0; i < numEmissions; ++i)
                    {
                        Assert.AreEqual(i, count);
                        ScenarioHarness.EmitTargeted(scenario, ref message, component);
                    }

                    break;
                }
                case MessageKind.Broadcast:
                {
                    _ = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        component,
                        (ref SimpleBroadcastMessage _) => ++count
                    );
                    SimpleBroadcastMessage message = new();
                    for (int i = 0; i < numEmissions; ++i)
                    {
                        Assert.AreEqual(i, count);
                        ScenarioHarness.EmitBroadcast(scenario, ref message, component);
                    }

                    break;
                }
                default:
                {
                    Assert.Fail("Unhandled MessageKind: {0}.", scenario.Kind);
                    break;
                }
            }

            Assert.AreEqual(numEmissions, count);
        }
    }
}

#endif
