namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Analyzers;
    using UnityEngine;
    using UnityEngine.UIElements;

    internal static class MessageAwareComponentInspectorView
    {
        internal const string RootName = "dxmessaging-inspector-warning";
        internal const string SeverityRailName = "dxmessaging-inspector-warning-rail";
        internal const string TitleLabelName = "dxmessaging-inspector-warning-title";
        internal const string BodyLabelName = "dxmessaging-inspector-warning-body";
        internal const string MethodListName = "dxmessaging-inspector-warning-methods";
        internal const string StaleCacheNoteName = "dxmessaging-inspector-warning-stale-note";
        internal const string OpenScriptButtonName = "dxmessaging-inspector-warning-open-script";
        internal const string IgnoreTypeButtonName = "dxmessaging-inspector-warning-ignore-type";
        internal const string StopIgnoringButtonName =
            "dxmessaging-inspector-warning-stop-ignoring";

        internal const string RootClassName = "dxmessaging-inspector-warning";
        internal const string StateNoneClassName = "dxmessaging-inspector-warning--none";
        internal const string StateInfoClassName = "dxmessaging-inspector-warning--info";
        internal const string StateWarningClassName = "dxmessaging-inspector-warning--warning";
        internal const string SeverityRailClassName = "dxmessaging-inspector-warning__rail";
        internal const string TitleClassName = "dxmessaging-inspector-warning__title";
        internal const string BodyClassName = "dxmessaging-inspector-warning__body";
        internal const string MethodListClassName = "dxmessaging-inspector-warning__methods";
        internal const string MethodClassName = "dxmessaging-inspector-warning__method";
        internal const string StaleCacheNoteClassName = "dxmessaging-inspector-warning__stale-note";
        internal const string ActionRowClassName = "dxmessaging-inspector-warning__actions";
        internal const string ActionButtonClassName = "dxmessaging-inspector-warning__button";

        private static readonly Color InfoColor = DxMessagingEditorPalette.Untargeted;
        private static readonly Color WarningColor = DxMessagingEditorPalette.Amber;
        private static readonly Color BorderColor = DxMessagingEditorPalette.BorderPanel;

        internal static VisualElement Create(
            MessageAwareComponentInspectorState state,
            MessageAwareComponentInspectorViewActions actions
        )
        {
            VisualElement root = CreateRoot(state);
            switch (state.Kind)
            {
                case MessageAwareComponentInspectorStateKind.None:
                    return root;
                case MessageAwareComponentInspectorStateKind.HarvesterUnavailable:
                    PopulateInfo(
                        root,
                        "Inspector overlay unavailable",
                        "DxMessaging inspector overlay is disabled on this Unity version. Check the console for DXMSG006/007/009 warnings instead.",
                        InfoColor
                    );
                    return root;
                case MessageAwareComponentInspectorStateKind.IgnoredType:
                    PopulateInfo(
                        root,
                        "Base-call check ignored",
                        $"{state.FullName} is excluded from the DxMessaging base-call check.",
                        InfoColor
                    );
                    AddActionRow(
                        root,
                        CreateActionButton(
                            StopIgnoringButtonName,
                            "Stop ignoring",
                            actions.OnStopIgnoring,
                            state
                        )
                    );
                    return root;
                case MessageAwareComponentInspectorStateKind.MissingBaseCallWarning:
                    PopulateWarning(root, state, actions);
                    return root;
                default:
                    return root;
            }
        }

        private static VisualElement CreateRoot(MessageAwareComponentInspectorState state)
        {
            VisualElement root = new() { name = RootName };
            root.AddToClassList(RootClassName);
            root.style.marginTop = 6;
            root.style.marginBottom = 6;

            switch (state.Kind)
            {
                case MessageAwareComponentInspectorStateKind.None:
                    root.AddToClassList(StateNoneClassName);
                    break;
                case MessageAwareComponentInspectorStateKind.MissingBaseCallWarning:
                    root.AddToClassList(StateWarningClassName);
                    break;
                default:
                    root.AddToClassList(StateInfoClassName);
                    break;
            }

            return root;
        }

        private static void PopulateInfo(
            VisualElement root,
            string titleText,
            string bodyText,
            Color railColor
        )
        {
            ConfigurePanel(root, railColor);
            root.Add(CreateSeverityRail(railColor));
            root.Add(CreateLabel(TitleLabelName, TitleClassName, titleText, bold: true));
            root.Add(CreateLabel(BodyLabelName, BodyClassName, bodyText, bold: false));
        }

        private static void PopulateWarning(
            VisualElement root,
            MessageAwareComponentInspectorState state,
            MessageAwareComponentInspectorViewActions actions
        )
        {
            ConfigurePanel(root, WarningColor);
            root.Add(CreateSeverityRail(WarningColor));
            root.Add(
                CreateLabel(
                    TitleLabelName,
                    TitleClassName,
                    "Missing MessageAwareComponent base calls",
                    bold: true
                )
            );
            root.Add(
                CreateLabel(
                    BodyLabelName,
                    BodyClassName,
                    $"{state.FullName} has lifecycle methods that do not chain to MessageAwareComponent. DxMessaging will not function on this component.",
                    bold: false
                )
            );
            root.Add(CreateMethodList(state));

            if (!state.IsFreshThisSession)
            {
                root.Add(
                    CreateLabel(
                        StaleCacheNoteName,
                        StaleCacheNoteClassName,
                        "Report is cached from previous session; refreshing...",
                        bold: false
                    )
                );
            }

            AddActionRow(
                root,
                CreateActionButton(
                    OpenScriptButtonName,
                    "Open Script",
                    actions.OnOpenScript,
                    state
                ),
                CreateActionButton(
                    IgnoreTypeButtonName,
                    "Ignore this type",
                    actions.OnIgnoreType,
                    state
                )
            );
        }

        private static void ConfigurePanel(VisualElement root, Color railColor)
        {
            root.style.borderLeftWidth = 3;
            root.style.borderLeftColor = railColor;
            root.style.borderTopWidth = 1;
            root.style.borderTopColor = BorderColor;
            root.style.borderRightWidth = 1;
            root.style.borderRightColor = BorderColor;
            root.style.borderBottomWidth = 1;
            root.style.borderBottomColor = BorderColor;
            root.style.paddingTop = 8;
            root.style.paddingRight = 10;
            root.style.paddingBottom = 8;
            root.style.paddingLeft = 10;
        }

        private static VisualElement CreateSeverityRail(Color railColor)
        {
            VisualElement rail = new() { name = SeverityRailName };
            rail.AddToClassList(SeverityRailClassName);
            rail.style.height = 2;
            rail.style.marginBottom = 5;
            rail.style.backgroundColor = railColor;
            return rail;
        }

        private static Label CreateLabel(string name, string className, string text, bool bold)
        {
            Label label = new(text) { name = name };
            label.AddToClassList(className);
            label.style.whiteSpace = WhiteSpace.Normal;
            if (bold)
            {
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            return label;
        }

        private static VisualElement CreateMethodList(MessageAwareComponentInspectorState state)
        {
            VisualElement methodList = new() { name = MethodListName };
            methodList.AddToClassList(MethodListClassName);
            methodList.style.marginTop = 5;

            if (state.Entry == null || state.Entry.missingBaseFor == null)
            {
                return methodList;
            }

            foreach (string missingMethod in state.Entry.missingBaseFor)
            {
                Label method = CreateLabel(
                    name: null,
                    className: MethodClassName,
                    text: $"{missingMethod}: {BaseCallTypeScannerCore.GetMissingBaseConsequenceLine(missingMethod, state.FullName)}",
                    bold: false
                );
                method.style.marginTop = 2;
                methodList.Add(method);
            }

            return methodList;
        }

        private static Button CreateActionButton(
            string name,
            string text,
            Action<MessageAwareComponentInspectorState> action,
            MessageAwareComponentInspectorState state
        )
        {
            Button button = new() { name = name, text = text };
            button.AddToClassList(ActionButtonClassName);
            if (action != null)
            {
                button.RegisterCallback<ClickEvent>(_ => action.Invoke(state));
            }
            button.SetEnabled(action != null);
            return button;
        }

        private static void AddActionRow(VisualElement root, params Button[] buttons)
        {
            VisualElement row = new();
            row.AddToClassList(ActionRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 7;

            foreach (Button button in buttons)
            {
                button.style.marginRight = 6;
                row.Add(button);
            }

            root.Add(row);
        }
    }

    internal readonly struct MessageAwareComponentInspectorViewActions
    {
        internal MessageAwareComponentInspectorViewActions(
            Action<MessageAwareComponentInspectorState> onOpenScript,
            Action<MessageAwareComponentInspectorState> onIgnoreType,
            Action<MessageAwareComponentInspectorState> onStopIgnoring
        )
        {
            OnOpenScript = onOpenScript;
            OnIgnoreType = onIgnoreType;
            OnStopIgnoring = onStopIgnoring;
        }

        internal static MessageAwareComponentInspectorViewActions None { get; } =
            new(onOpenScript: null, onIgnoreType: null, onStopIgnoring: null);

        internal Action<MessageAwareComponentInspectorState> OnOpenScript { get; }

        internal Action<MessageAwareComponentInspectorState> OnIgnoreType { get; }

        internal Action<MessageAwareComponentInspectorState> OnStopIgnoring { get; }
    }
#endif
}
