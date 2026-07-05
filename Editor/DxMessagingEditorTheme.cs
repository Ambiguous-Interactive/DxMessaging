#if UNITY_EDITOR
namespace DxMessaging.Editor
{
    using System;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;

    internal static class DxMessagingEditorTheme
    {
        internal const string PackageRoot = "Packages/com.wallstop-studios.dxmessaging";
        internal const string TokensUssPath = PackageRoot + "/Editor/Theme/DxTokens.uss";
        internal const string ThemeUssPath = PackageRoot + "/Editor/Theme/DxMessagingTheme.uss";
        internal const string IconDir = PackageRoot + "/Editor/Icons";
        internal const string Icon32FileName = "dxmessaging-icon-32.png";
        internal const string Icon48FileName = "dxmessaging-icon-48.png";
        internal const string Icon256FileName = "dxmessaging-icon-256.png";

        internal const string ThemeClassName = "dx-theme";
        internal const string LightSkinClassName = "dx-light";
        internal const string DarkSkinClassName = "dx-dark";
        internal const string WindowClassName = "dx-window";
        internal const string ToolbarClassName = "dx-toolbar";
        internal const string ToolButtonClassName = "dx-tool-btn";
        internal const string SearchClassName = "dx-search";
        internal const string CardClassName = "dx-card";
        internal const string CardLabelClassName = "dx-card__label";
        internal const string EmptyClassName = "dx-empty";
        internal const string EmptyTitleClassName = "dx-empty__title";
        internal const string EmptyBodyClassName = "dx-empty__body";
        internal const string ButtonAccentClassName = "dx-btn-accent";
        internal const string ButtonGhostClassName = "dx-btn-ghost";
        internal const string AdmonitionClassName = "dx-admonition";
        internal const string NoteClassName = "dx-note";
        internal const string WarningClassName = "dx-warning";
        internal const string TypeBadgeClassName = "dx-typebadge";
        internal const string TypeBadgeUntargetedClassName = "dx-typebadge--u";
        internal const string TypeBadgeTargetedClassName = "dx-typebadge--t";
        internal const string TypeBadgeBroadcastClassName = "dx-typebadge--b";
        internal const int CompleteBorderWidth = 1;

        internal static void Apply(VisualElement root)
        {
            if (root == null)
            {
                return;
            }

            root.AddToClassList(ThemeClassName);
            root.EnableInClassList(LightSkinClassName, !EditorGUIUtility.isProSkin);
            root.EnableInClassList(DarkSkinClassName, EditorGUIUtility.isProSkin);
            AddStyleSheet(root, TokensUssPath);
            AddStyleSheet(root, ThemeUssPath);
        }

        internal static void ApplyWindow(VisualElement root)
        {
            Apply(root);
            root?.AddToClassList(WindowClassName);
        }

        internal static void ApplyCompleteBorder(VisualElement element, Color borderColor)
        {
            if (element == null)
            {
                return;
            }

            element.style.borderTopWidth = CompleteBorderWidth;
            element.style.borderRightWidth = CompleteBorderWidth;
            element.style.borderBottomWidth = CompleteBorderWidth;
            element.style.borderLeftWidth = CompleteBorderWidth;
            element.style.borderTopColor = borderColor;
            element.style.borderRightColor = borderColor;
            element.style.borderBottomColor = borderColor;
            element.style.borderLeftColor = borderColor;
        }

        internal static StyleSheet LoadTokensStylesheet()
        {
            return LoadStyleSheet(TokensUssPath);
        }

        internal static StyleSheet LoadThemeStylesheet()
        {
            return LoadStyleSheet(ThemeUssPath);
        }

        internal static Texture2D LoadIcon(string fileName = Icon32FileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(IconDir + "/" + fileName);
        }

        internal static void AddRouteKindTypeBadgeClasses(VisualElement element, string routeKind)
        {
            if (element == null)
            {
                return;
            }

            element.AddToClassList(TypeBadgeClassName);
            switch (DxMessagingEditorPalette.NormalizeRouteKind(routeKind))
            {
                case DxMessagingEditorPalette.UntargetedKind:
                    element.AddToClassList(TypeBadgeUntargetedClassName);
                    break;
                case DxMessagingEditorPalette.TargetedKind:
                    element.AddToClassList(TypeBadgeTargetedClassName);
                    break;
                case DxMessagingEditorPalette.BroadcastKind:
                    element.AddToClassList(TypeBadgeBroadcastClassName);
                    break;
            }
        }

        /// <summary>
        /// Builds a themed empty-state block: a centered <c>.dx-empty</c> container holding an
        /// optional bold <c>.dx-empty__title</c> headline and a muted, wrapping
        /// <c>.dx-empty__body</c> detail label. Callers may name the title/body labels so tests
        /// and callbacks can query them.
        /// </summary>
        internal static VisualElement CreateEmptyState(
            string title,
            string body,
            string bodyName = null,
            string titleName = null
        )
        {
            VisualElement container = new();
            container.AddToClassList(EmptyClassName);

            if (!string.IsNullOrEmpty(title))
            {
                Label titleLabel = new(title);
                if (!string.IsNullOrEmpty(titleName))
                {
                    titleLabel.name = titleName;
                }
                titleLabel.AddToClassList(EmptyTitleClassName);
                container.Add(titleLabel);
            }

            Label bodyLabel = new(body);
            if (!string.IsNullOrEmpty(bodyName))
            {
                bodyLabel.name = bodyName;
            }
            bodyLabel.AddToClassList(EmptyBodyClassName);
            bodyLabel.style.whiteSpace = WhiteSpace.Normal;
            container.Add(bodyLabel);

            return container;
        }

        private static StyleSheet LoadStyleSheet(string path)
        {
            return AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
        }

        private static void AddStyleSheet(VisualElement root, string path)
        {
            StyleSheet styleSheet = LoadStyleSheet(path);
            if (styleSheet == null)
            {
                DxMessagingEditorLog.LogWarning(
                    $"DxMessaging editor stylesheet was not found at '{path}'.",
                    exception: null
                );
                return;
            }

            if (!HasStyleSheet(root, styleSheet))
            {
                root.styleSheets.Add(styleSheet);
            }
        }

        private static bool HasStyleSheet(VisualElement root, StyleSheet styleSheet)
        {
            for (int index = 0; index < root.styleSheets.count; index++)
            {
                if (root.styleSheets[index] == styleSheet)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
