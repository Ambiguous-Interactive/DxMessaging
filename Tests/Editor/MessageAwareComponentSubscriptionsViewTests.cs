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
    using UnityEditor;
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
        public void RevisionMovesWhenOneRegistrationIsSwappedForAnother()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.TestToken.DiagnosticMode = false;
            MessageRegistrationHandle alpha = component.RegisterAlpha();

            long before = MessageAwareComponentSubscriptionsState.Capture(component).Revision;

            component.TestToken.RemoveRegistration(alpha);
            _ = component.RegisterBeta();

            MessageAwareComponentSubscriptionsState after =
                MessageAwareComponentSubscriptionsState.Capture(component);
            Assert.That(
                after.Rows.Select(row => row.MessageTypeName).ToArray(),
                Is.EqualTo(new[] { nameof(SubscriptionsBetaMessage) })
            );
            Assert.That(
                after.Revision,
                Is.Not.EqualTo(before),
                "A same-size swap must still redraw: with diagnostics off every call count is unknown, "
                    + "so row identity is the only thing that changed."
            );
        }

        [Test]
        public void AggregateCaptureCountsSelectedComponentsAndReportsDivergence()
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();
            first.TestToken.Enable();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            _ = second.RegisterAlpha();

            SubscriptionsTestComponent third = CreateConfiguredComponent();
            _ = third.RegisterBeta();
            third.TestToken.Enable();

            try
            {
                MessageAwareComponentSubscriptionsState state =
                    MessageAwareComponentSubscriptionsState.Capture(
                        new MessageAwareComponent[] { first, second, third }
                    );

                Assert.That(state.IsAggregate, Is.True, "Three targets must use aggregate mode.");
                Assert.That(state.SelectionCount, Is.EqualTo(3), "Every selected target counts.");
                Assert.That(
                    state.TokenCount,
                    Is.EqualTo(3),
                    "Every configured target has a token."
                );
                Assert.That(
                    state.Rows.Count,
                    Is.EqualTo(2),
                    "The selection has two row identities."
                );

                MessageAwareComponentSubscriptionRow alpha = state.Rows.Single(row =>
                    row.MessageTypeName == nameof(SubscriptionsAlphaMessage)
                );
                Assert.That(
                    alpha.SelectedComponentCount,
                    Is.EqualTo(2),
                    "Alpha is present on two of the three selected components."
                );
                Assert.That(
                    alpha.Liveness,
                    Is.EqualTo(MessageAwareComponentSubscriptionLiveness.Mixed),
                    "One Alpha token is enabled and one is disabled."
                );

                MessageAwareComponentSubscriptionRow beta = state.Rows.Single(row =>
                    row.MessageTypeName == nameof(SubscriptionsBetaMessage)
                );
                Assert.That(
                    beta.SelectedComponentCount,
                    Is.EqualTo(1),
                    "Beta is present on one of the three selected components."
                );
                Assert.That(
                    beta.Liveness,
                    Is.EqualTo(MessageAwareComponentSubscriptionLiveness.Live),
                    "The only component carrying Beta is enabled."
                );
            }
            finally
            {
                first.TestToken.Disable();
                third.TestToken.Disable();
            }
        }

        [TestCase(false, false, (int)MessageAwareComponentSubscriptionLiveness.Idle, "disabled")]
        [TestCase(true, true, (int)MessageAwareComponentSubscriptionLiveness.Live, "enabled")]
        [TestCase(false, true, (int)MessageAwareComponentSubscriptionLiveness.Mixed, "mixed")]
        public void AggregateCaptureClassifiesEnabledState(
            bool firstEnabled,
            bool secondEnabled,
            int expectedValue,
            string expectedText
        )
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            _ = second.RegisterAlpha();

            if (firstEnabled)
            {
                first.TestToken.Enable();
            }
            if (secondEnabled)
            {
                second.TestToken.Enable();
            }

            try
            {
                MessageAwareComponentSubscriptionLiveness expected =
                    (MessageAwareComponentSubscriptionLiveness)expectedValue;
                string caseContext =
                    $"firstEnabled={firstEnabled}, secondEnabled={secondEnabled}, expectedValue={expectedValue}, expectedText={expectedText}";
                MessageAwareComponentSubscriptionRow row = MessageAwareComponentSubscriptionsState
                    .Capture(new MessageAwareComponent[] { first, second })
                    .Rows.Single();

                Assert.That(
                    row.Liveness,
                    Is.EqualTo(expected),
                    $"{caseContext}: aggregate liveness must classify as {expected}."
                );
                Assert.That(
                    MessageAwareComponentSubscriptionsView.CreateRowMetaText(row),
                    Does.EndWith("| " + expectedText),
                    $"{caseContext}: aggregate state must render as {expectedText}."
                );
            }
            finally
            {
                first.TestToken.Disable();
                second.TestToken.Disable();
            }
        }

        [Test]
        public void AggregateCaptureCountsEachComponentOnlyOncePerRowIdentity()
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();
            _ = first.RegisterAlpha();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            _ = second.RegisterAlpha();

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(
                    new MessageAwareComponent[] { first, second }
                );

            Assert.That(state.Rows.Count, Is.EqualTo(1), "Equivalent registrations share one row.");
            Assert.That(
                state.Rows[0].SelectedComponentCount,
                Is.EqualTo(2),
                "Two registrations on one component must not inflate selected-component coverage."
            );
        }

        [Test]
        public void AggregateCaptureKeepsRegistrationKindAndPriorityDistinct()
        {
            SubscriptionsTestComponent registered = CreateConfiguredComponent();
            _ = registered.RegisterAlpha(priority: 0);
            _ = registered.RegisterAlpha(priority: 10);
            _ = registered.RegisterAlphaPostProcessor(priority: 0);

            SubscriptionsTestComponent empty = CreateConfiguredComponent();

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(
                    new MessageAwareComponent[] { registered, empty }
                );

            Assert.That(
                state.Rows.Count,
                Is.EqualTo(3),
                "One type with two priorities and two registration kinds needs three rows."
            );
            Assert.That(
                state.Rows.Select(row => row.Priority).Distinct().OrderBy(value => value).ToArray(),
                Is.EqualTo(new[] { 0, 10 }),
                "Priority is part of the aggregate row identity."
            );
            Assert.That(
                state
                    .Rows.Select(row => row.RegistrationTypeName)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray(),
                Is.EqualTo(
                    new[]
                    {
                        MessageRegistrationType.Untargeted.ToString(),
                        MessageRegistrationType.UntargetedPostProcessor.ToString(),
                    }
                ),
                "Registration kind is part of the aggregate row identity."
            );
        }

        [Test]
        public void AggregateCaptureRejectsNullSelection()
        {
            Assert.That(
                () =>
                    MessageAwareComponentSubscriptionsState.Capture(
                        (IReadOnlyList<MessageAwareComponent>)null
                    ),
                Throws.ArgumentNullException,
                "A missing selection is a caller error rather than an empty aggregate."
            );
        }

        [Test]
        public void AggregateCaptureToleratesTargetsDestroyedBetweenPolls()
        {
            SubscriptionsTestComponent destroyed = CreateComponent(out _);
            Object.DestroyImmediate(destroyed);

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(
                    new MessageAwareComponent[] { destroyed, null }
                );

            Assert.That(state.IsAggregate, Is.False, "No live selection remains to aggregate.");
            Assert.That(state.SelectionCount, Is.Zero, "Destroyed targets leave the denominator.");
            Assert.That(state.TokenCount, Is.Zero, "Destroyed targets do not contribute tokens.");
            Assert.That(state.Rows, Is.Empty, "Destroyed targets do not contribute registrations.");
        }

        [Test]
        public void AggregateCaptureReportsPartialTokenCoverageWithoutRows()
        {
            SubscriptionsTestComponent configured = CreateConfiguredComponent();
            SubscriptionsTestComponent noToken = CreateComponent(out _);
            SubscriptionsTestComponent alsoNoToken = CreateComponent(out _);

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(
                    new MessageAwareComponent[] { configured, noToken, alsoNoToken }
                );

            Assert.That(
                state.TokenCount,
                Is.EqualTo(1),
                "Only one selected component has a token."
            );
            Assert.That(state.Rows, Is.Empty, "The one token has no registered handlers.");
        }

        [TestCase(3, 0, "3 selected | No tokens", "do not have registration tokens")]
        [TestCase(3, 1, "3 selected | 1 token | 0 patterns", "other 2 do not")]
        [TestCase(2, 1, "2 selected | 1 token | 0 patterns", "other selected component does not")]
        [TestCase(
            3,
            3,
            "3 selected | 0 patterns",
            "have registration tokens but no registered handlers"
        )]
        public void AggregateEmptyTextReportsTokenCoverage(
            int selectionCount,
            int tokenCount,
            string expectedSummary,
            string expectedBody
        )
        {
            string caseContext =
                $"selectionCount={selectionCount}, tokenCount={tokenCount}, expectedSummary={expectedSummary}, expectedBody={expectedBody}";
            MessageAwareComponentSubscriptionsState state = new(
                hasToken: tokenCount > 0,
                tokenEnabled: false,
                diagnosticsEnabled: false,
                rows: new MessageAwareComponentSubscriptionRow[0],
                isAggregate: true,
                selectionCount: selectionCount,
                tokenCount: tokenCount
            );

            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateSummaryText(state),
                Is.EqualTo(expectedSummary),
                $"{caseContext}: aggregate summary must match."
            );
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateEmptyBodyText(state),
                Does.Contain(expectedBody),
                $"{caseContext}: aggregate empty body must match."
            );
        }

        [Test]
        public void AggregateRevisionMovesWhenCoverageOrLivenessChanges()
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            MessageRegistrationHandle secondAlpha = second.RegisterAlpha();

            MessageAwareComponent[] selection = { first, second };
            long initial = MessageAwareComponentSubscriptionsState.Capture(selection).Revision;

            second.TestToken.Enable();
            long mixed = MessageAwareComponentSubscriptionsState.Capture(selection).Revision;
            Assert.That(
                mixed,
                Is.Not.EqualTo(initial),
                "Changing one carrier from idle to live must rebuild the mixed-state dot."
            );

            second.TestToken.RemoveRegistration(secondAlpha);
            long missing = MessageAwareComponentSubscriptionsState.Capture(selection).Revision;
            Assert.That(
                missing,
                Is.Not.EqualTo(mixed),
                "Removing one carrier must rebuild the selection-coverage count."
            );

            second.TestToken.Disable();
        }

        [Test]
        public void AggregateCaptureKeepsDifferentTypesWithTheSameSimpleNameDistinct()
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            _ = second.RegisterOtherAlpha();

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(
                    new MessageAwareComponent[] { first, second }
                );

            Assert.That(
                state.Rows.Count,
                Is.EqualTo(2),
                "Aggregation must key on System.Type rather than the displayed simple name."
            );
            Assert.That(
                state.Rows.Select(row => row.MessageTypeName).Distinct().Count(),
                Is.EqualTo(1),
                "The fixture must exercise two distinct types with the same displayed name."
            );
            Assert.That(
                state.Rows.Select(row => row.MessageType.AssemblyQualifiedName).Distinct().Count(),
                Is.EqualTo(2),
                "Each row retains the actual type identity for its explanatory tooltip."
            );

            VisualElement root = MessageAwareComponentSubscriptionsView.Create(state);
            List<Label> names = root.Query<Label>(
                    className: MessageAwareComponentSubscriptionsView.RowNameClassName
                )
                .ToList();
            Assert.That(names.Count, Is.EqualTo(2), "Both same-name message rows must render.");
            Assert.That(
                names.Select(label => label.tooltip).Distinct().Count(),
                Is.EqualTo(2),
                "Qualified tooltips must distinguish rows with the same displayed type name."
            );
        }

        [Test]
        public void AggregateViewShowsCoverageAndMixedState()
        {
            SubscriptionsTestComponent first = CreateConfiguredComponent();
            _ = first.RegisterAlpha();
            first.TestToken.Enable();

            SubscriptionsTestComponent second = CreateConfiguredComponent();
            _ = second.RegisterAlpha();

            try
            {
                MessageAwareComponentSubscriptionsState state =
                    MessageAwareComponentSubscriptionsState.Capture(
                        new MessageAwareComponent[] { first, second }
                    );
                VisualElement root = MessageAwareComponentSubscriptionsView.Create(state);

                Assert.That(
                    root.Q<Label>(MessageAwareComponentSubscriptionsView.MetaLabelName).text,
                    Is.EqualTo("2 selected | 1 pattern"),
                    "The header must identify aggregate mode and the number of distinct rows."
                );
                Assert.That(
                    MessageAwareComponentSubscriptionsView.CreateRowMetaText(state.Rows[0]),
                    Is.EqualTo("Untargeted | 2 of 2 selected | mixed"),
                    "Aggregate rows report selection coverage and visible state instead of summed call counts."
                );
                VisualElement status = root.Q<VisualElement>(
                    MessageAwareComponentSubscriptionsView.RowStatusName
                );
                Assert.That(status, Is.Not.Null, "Every aggregate row needs a status dot.");
                Assert.That(
                    status.ClassListContains(
                        MessageAwareComponentSubscriptionsView.RowMixedClassName
                    ),
                    Is.True,
                    "Disagreeing token states use the mixed-state class."
                );
                Assert.That(
                    status.tooltip,
                    Does.Contain("differs"),
                    "The mixed dot tooltip must explain its meaning."
                );
            }
            finally
            {
                first.TestToken.Disable();
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

        /// <summary>
        /// Priority decides dispatch order within a message type, so it is promoted out of the meta
        /// sentence into the design system's accent badge. This is the surface that brings
        /// <c>.dx-prio</c> into use (issue #304).
        /// </summary>
        [Test]
        public void ViewBadgesEachRowsPriority()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            component.RegisterTestHandlers();

            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);
            VisualElement root = MessageAwareComponentSubscriptionsView.Create(state);

            List<Label> badges = root.Query<Label>(
                    MessageAwareComponentSubscriptionsView.RowPriorityLabelName
                )
                .ToList();
            Assert.That(badges.Count, Is.EqualTo(state.Rows.Count));
            foreach (Label badge in badges)
            {
                Assert.That(
                    badge.ClassListContains(
                        MessageAwareComponentSubscriptionsView.PriorityClassName
                    ),
                    Is.True
                );
                Assert.That(badge.tooltip, Is.Not.Empty, "A bare number needs the tooltip.");
            }

            Assert.That(
                MessageAwareComponentSubscriptionsView.CreatePriorityText(state.Rows[0]),
                Is.EqualTo("P" + state.Rows[0].Priority),
                "The badge shows the real priority, prefixed so it does not read as a count."
            );
            Assert.That(
                MessageAwareComponentSubscriptionsView.CreateRowMetaText(state.Rows[0]),
                Does.Not.Contain("priority"),
                "The badge replaces the meta sentence's priority segment rather than duplicating it."
            );
        }

        [Test]
        public void ViewInsetsRowsSoTheyClearTheRoundedBorder()
        {
            VisualElement rows = MessageAwareComponentSubscriptionsView
                .Create(MessageAwareComponentSubscriptionsState.None)
                .Q<VisualElement>(MessageAwareComponentSubscriptionsView.RowsName);

            Assert.That(
                rows.style.paddingLeft.value.value,
                Is.GreaterThan(0),
                "Rows must clear the .dx-inspector corner radius; .dx-sub pads vertically only."
            );
            Assert.That(rows.style.paddingRight.value.value, Is.GreaterThan(0));
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
            GameObject host = EditorUtility.CreateGameObjectWithHideFlags(
                nameof(MessageAwareComponentSubscriptionsViewTests),
                HideFlags.HideAndDontSave
            );
            _createdObjects.Add(host);
            messagingComponent = host.AddComponent<MessagingComponent>();
            return host.AddComponent<SubscriptionsTestComponent>();
        }

        private SubscriptionsTestComponent CreateConfiguredComponent()
        {
            SubscriptionsTestComponent component = CreateComponent(
                out MessagingComponent messagingComponent
            );
            component.ConfigureForEditorTest(messagingComponent);
            return component;
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
            _ = RegisterAlpha();
            _ = RegisterBeta();
        }

        internal MessageRegistrationHandle RegisterAlpha(int priority = 0)
        {
            return _messageRegistrationToken.RegisterUntargeted<SubscriptionsAlphaMessage>(
                OnAlpha,
                priority
            );
        }

        internal MessageRegistrationHandle RegisterAlphaPostProcessor(int priority = 0)
        {
            return _messageRegistrationToken.RegisterUntargetedPostProcessor<SubscriptionsAlphaMessage>(
                OnAlpha,
                priority
            );
        }

        internal MessageRegistrationHandle RegisterBeta()
        {
            return _messageRegistrationToken.RegisterComponentTargeted<SubscriptionsBetaMessage>(
                this,
                OnBeta
            );
        }

        internal MessageRegistrationHandle RegisterOtherAlpha()
        {
            return _messageRegistrationToken.RegisterUntargeted<Other.SubscriptionsAlphaMessage>(
                OnOtherAlpha
            );
        }

        private void OnAlpha(ref SubscriptionsAlphaMessage message) { }

        private void OnBeta(ref SubscriptionsBetaMessage message) { }

        private void OnOtherAlpha(ref Other.SubscriptionsAlphaMessage message) { }
    }

    internal readonly struct SubscriptionsAlphaMessage : IUntargetedMessage { }

    internal readonly struct SubscriptionsBetaMessage : ITargetedMessage { }
}

namespace DxMessaging.Tests.Editor.Other
{
    using Core.Messages;

    internal readonly struct SubscriptionsAlphaMessage : IUntargetedMessage { }
}
#endif
