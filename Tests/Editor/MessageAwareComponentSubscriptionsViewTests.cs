#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using System.Linq;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor.CustomEditors;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Covers the themed subscriptions section: what it captures from a live registration token,
    /// how it renders each state, and the design-system classes it must carry.
    /// </summary>
    [TestFixture]
    public sealed class MessageAwareComponentSubscriptionsViewTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();
        }

        [Test]
        public void CaptureWithoutTokenReportsNoToken()
        {
            SubscriptionsTestComponent component = CreateComponent(out _);

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);

            Assert.That(state.HasToken, Is.False);
            Assert.That(state.Rows, Is.Empty);
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateSummaryText(state),
                Is.EqualTo("No token")
            );
        }

        [Test]
        public void CaptureOfDestroyedComponentReportsNoToken()
        {
            SubscriptionsTestComponent component = CreateComponent(out _);
            Object.DestroyImmediate(component);

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);

            Assert.That(state.HasToken, Is.False);
            Assert.That(state.Rows, Is.Empty);
        }

        [Test]
        public void CaptureWithTokenButNoRegistrationsReportsEmptyBody()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);

            Assert.That(state.HasToken, Is.True);
            Assert.That(state.Rows, Is.Empty);
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateEmptyBodyText(state),
                Does.Contain("registered no handlers")
            );
        }

        [Test]
        public void CaptureListsEveryRegistrationSortedAndTyped()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);

            Assert.That(state.HasToken, Is.True);
            Assert.That(
                state.Rows.Select(row => row.MessageTypeName).ToArray(),
                Is.EqualTo(
                    new[] { nameof(SubscriptionsAlphaMessage), nameof(SubscriptionsBetaMessage) }
                ),
                "Rows must be ordered by message type name so the section does not reshuffle between polls."
            );
            Assert.That(
                state.Rows.Select(row => row.RegistrationTypeName).ToArray(),
                Is.EqualTo(
                    new[]
                    {
                        MessageRegistrationType.Untargeted.ToString(),
                        MessageRegistrationType.Targeted.ToString(),
                    }
                )
            );
        }

        [Test]
        public void RowsAreLiveOnlyWhileTheTokenIsEnabled()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            MessageAwareComponentSubscriptionsState idle =
                MessageAwareComponentSubscriptionsState.Capture(component);
            Assert.That(idle.TokenEnabled, Is.False);
            Assert.That(idle.Rows.All(row => !row.IsLive), Is.True);
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateSummaryText(idle),
                Is.EqualTo("Disabled | 2 registrations")
            );

            component.TestToken.Enable();
            try
            {
                MessageAwareComponentSubscriptionsState live =
                    MessageAwareComponentSubscriptionsState.Capture(component);
                Assert.That(live.TokenEnabled, Is.True);
                Assert.That(live.Rows.All(row => row.IsLive), Is.True);
                Assert.That(
                    MessageAwareComponentSubscriptionsView.CreateSummaryText(live),
                    Is.EqualTo("Listening | 2 registrations")
                );
            }
            finally
            {
                component.TestToken.Disable();
            }
        }

        [Test]
        public void CallCountsAreUnknownUntilDiagnosticsRecordThem()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            // A token inherits IMessageBus.GlobalDiagnosticsMode, which the host project's
            // DxMessaging settings can leave on. Pin both halves of the behavior here rather
            // than reading whatever the ambient project default happens to be.
            component.TestToken.DiagnosticMode = false;
            MessageAwareComponentSubscriptionsState quiet =
                MessageAwareComponentSubscriptionsState.Capture(component);
            Assert.That(quiet.DiagnosticsEnabled, Is.False);
            Assert.That(
                quiet.Rows.All(row =>
                    row.CallCount == MessageAwareComponentSubscriptionRow.UnknownCallCount
                ),
                Is.True
            );
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateRowMetaText(quiet.Rows[0]),
                Does.Contain("calls n/a"),
                "Diagnostics-off must read as unknown, not as a confident zero."
            );

            component.TestToken.DiagnosticMode = true;
            component.TestToken.Enable();
            try
            {
                SubscriptionsAlphaMessage message = default;
                MessageHandler.MessageBus.UntargetedBroadcast(ref message);

                MessageAwareComponentSubscriptionsState recorded =
                    MessageAwareComponentSubscriptionsState.Capture(component);
                Assert.That(recorded.DiagnosticsEnabled, Is.True);

                MessageAwareComponentSubscriptionRow alpha = recorded.Rows.Single(row =>
                    row.MessageTypeName == nameof(SubscriptionsAlphaMessage)
                );
                Assert.That(alpha.CallCount, Is.EqualTo(1));
                Assert.That(
                    MessageAwareComponentSubscriptionsView.CreateRowMetaText(alpha),
                    Does.Contain("1 call")
                );

                MessageAwareComponentSubscriptionRow beta = recorded.Rows.Single(row =>
                    row.MessageTypeName == nameof(SubscriptionsBetaMessage)
                );
                Assert.That(beta.CallCount, Is.EqualTo(0));
                Assert.That(
                    MessageAwareComponentSubscriptionsView.CreateRowMetaText(beta),
                    Does.Contain("0 calls")
                );
            }
            finally
            {
                component.TestToken.DiagnosticMode = false;
                component.TestToken.Disable();
            }
        }

        [Test]
        public void RevisionMovesOnlyWhenTheRenderedStateChanges()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            long first = MessageAwareComponentSubscriptionsState.Capture(component).Revision;
            long unchanged = MessageAwareComponentSubscriptionsState.Capture(component).Revision;
            Assert.That(unchanged, Is.EqualTo(first));

            component.TestToken.Enable();
            try
            {
                Assert.That(
                    MessageAwareComponentSubscriptionsState.Capture(component).Revision,
                    Is.Not.EqualTo(first),
                    "Enabling the token flips every row to live, so the section must rebuild."
                );
            }
            finally
            {
                component.TestToken.Disable();
            }
        }

        [Test]
        public void ViewCarriesTheDesignSystemSubscriptionClasses()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            VisualElement root = MessageAwareComponentSubscriptionsView.Create(
                MessageAwareComponentSubscriptionsState.Capture(component)
            );

            Assert.That(root.name, Is.EqualTo(MessageAwareComponentSubscriptionsView.RootName));
            Assert.That(
                root.ClassListContains(MessageAwareComponentSubscriptionsView.RootClassName),
                Is.True
            );

            Label title = root.Q<Label>(MessageAwareComponentSubscriptionsView.TitleLabelName);
            Assert.That(title, Is.Not.Null);
            Assert.That(title.text, Is.EqualTo(MessageAwareComponentSubscriptionsView.Title));
            Label meta = root.Q<Label>(MessageAwareComponentSubscriptionsView.MetaLabelName);
            Assert.That(meta, Is.Not.Null);
            Assert.That(meta.text, Is.EqualTo("Disabled | 2 registrations"));

            foreach (
                string className in new[]
                {
                    MessageAwareComponentSubscriptionsView.HeadClassName,
                    MessageAwareComponentSubscriptionsView.TitleClassName,
                    MessageAwareComponentSubscriptionsView.MetaClassName,
                }
            )
            {
                Assert.That(
                    root.Query<VisualElement>()
                        .ToList()
                        .Any(element => element.ClassListContains(className)),
                    Is.True,
                    $"Subscriptions section must render {className}."
                );
            }

            List<VisualElement> rows = root.Q<VisualElement>(
                    MessageAwareComponentSubscriptionsView.RowsName
                )
                .Children()
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            foreach (VisualElement row in rows)
            {
                Assert.That(
                    row.ClassListContains(MessageAwareComponentSubscriptionsView.RowClassName),
                    Is.True
                );
                Assert.That(
                    row.Children()
                        .Count(child =>
                            child.ClassListContains(
                                MessageAwareComponentSubscriptionsView.RowIdleClassName
                            )
                        ),
                    Is.EqualTo(1),
                    "Each row carries exactly one state dot."
                );
            }
        }

        [Test]
        public void ViewBordersEveryEdgeOfTheSection()
        {
            VisualElement root = MessageAwareComponentSubscriptionsView.Create(
                MessageAwareComponentSubscriptionsState.None
            );

            Assert.That(root.style.borderTopWidth.value, Is.EqualTo(1));
            Assert.That(root.style.borderRightWidth.value, Is.EqualTo(1));
            Assert.That(root.style.borderBottomWidth.value, Is.EqualTo(1));
            Assert.That(root.style.borderLeftWidth.value, Is.EqualTo(1));
        }

        [Test]
        public void ViewWithoutRowsRendersTheThemedEmptyState()
        {
            VisualElement root = MessageAwareComponentSubscriptionsView.Create(
                MessageAwareComponentSubscriptionsState.None
            );

            Label body = root.Q<Label>(MessageAwareComponentSubscriptionsView.EmptyBodyName);
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Does.Contain("Play mode"));
            Assert.That(
                root.Q<VisualElement>(MessageAwareComponentSubscriptionsView.RowsName).childCount,
                Is.EqualTo(1)
            );
        }

        private SubscriptionsTestComponent CreateComponent(
            out MessagingComponent messagingComponent
        )
        {
            GameObject host = new(nameof(MessageAwareComponentSubscriptionsViewTests));
            _createdObjects.Add(host);
            messagingComponent = host.AddComponent<MessagingComponent>();
            return host.AddComponent<SubscriptionsTestComponent>();
        }
    }

    // Registrations are normally created in Awake, which the editor never runs for a plain
    // MonoBehaviour. ConfigureForEditorTest wires the same token the runtime path would, so the
    // section is exercised against a real MessageRegistrationToken rather than a stand-in.
    [AddComponentMenu("")]
    internal sealed class SubscriptionsTestComponent : MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;

        internal MessageRegistrationToken TestToken => _messageRegistrationToken;

        internal void ConfigureForEditorTest(MessagingComponent messagingComponent)
        {
            _messageRegistrationToken = messagingComponent.Create(this);
        }

        internal void RegisterTestHandlers()
        {
            _ = _messageRegistrationToken.RegisterUntargeted<SubscriptionsAlphaMessage>(OnAlpha);
            _ = _messageRegistrationToken.RegisterComponentTargeted<SubscriptionsBetaMessage>(
                this,
                OnBeta
            );
        }

        private void OnAlpha(ref SubscriptionsAlphaMessage message) { }

        private void OnBeta(ref SubscriptionsBetaMessage message) { }
    }

    internal readonly struct SubscriptionsAlphaMessage : IUntargetedMessage { }

    internal readonly struct SubscriptionsBetaMessage : ITargetedMessage { }
}
#endif
