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
        internal const string SeparatorClassName = "dx-sep";
        internal const string AdmonitionClassName = "dx-admonition";
        internal const string AdmonitionTitleClassName = "dx-admonition__title";
        internal const string NoteClassName = "dx-note";
        internal const string WarningClassName = "dx-warning";
        internal const string DangerClassName = "dx-danger";
        internal const string PriorityClassName = "dx-prio";
        internal const string TypeBadgeClassName = "dx-typebadge";
        internal const string TypeBadgeUntargetedClassName = "dx-typebadge--u";
        internal const string TypeBadgeTargetedClassName = "dx-typebadge--t";
        internal const string TypeBadgeBroadcastClassName = "dx-typebadge--b";
        internal const string TypeBadgeGlobalObserverClassName = "dx-typebadge--g";
        internal const string DotClassName = "dx-dot";
        internal const string DotUntargetedClassName = "dx-dot--u";
        internal const string DotTargetedClassName = "dx-dot--t";
        internal const string DotBroadcastClassName = "dx-dot--b";
        internal const string ChipClassName = "dx-chip";
        internal const string ChipUntargetedClassName = "dx-chip--u";
        internal const string ChipTargetedClassName = "dx-chip--t";
        internal const string ChipBroadcastClassName = "dx-chip--b";

        /// <summary>
        /// Widens a taxonomy chip so it can carry its route kind's name and count instead of a
        /// single letter. A chip that names itself is the Monitor's color legend.
        /// </summary>
        internal const string ChipWideClassName = "dx-chip--wide";
        internal const string FilterClassName = "dx-filter";
        internal const string RecordClassName = "dx-record";
        internal const string ListHeaderClassName = "dx-list-header";
        internal const string ColumnTimeClassName = "dx-col-time";
        internal const string ColumnTypeClassName = "dx-col-type";
        internal const string ColumnMessageClassName = "dx-col-msg";
        internal const string ColumnRouteClassName = "dx-col-route";
        internal const string ColumnCountClassName = "dx-col-count";
        internal const string RowClassName = "dx-row";
        internal const string RowAlternateClassName = "dx-row--alt";
        internal const string RowTimeClassName = "dx-row__time";
        internal const string RowTypeClassName = "dx-row__type";
        internal const string RowMessageClassName = "dx-row__msg";
        internal const string RowRouteClassName = "dx-row__route";
        internal const string RowCountClassName = "dx-row__count";
        internal const string DetailClassName = "dx-detail";
        internal const string DetailHeadClassName = "dx-detail__head";
        internal const string DetailTitleClassName = "dx-detail__title";
        internal const string DetailFrameClassName = "dx-detail__frame";
        internal const string DetailLinkClassName = "dx-detail__link";
        internal const string DetailActiveClassName = "dx-detail__active";

        /// <summary>
        /// Carries the pointer cursor for anything that answers a click. Issue #344 reported
        /// that nothing in the window says what is clickable; a hover style only tells a
        /// reader who already guessed, and the cursor tells one who did not.
        /// </summary>
        internal const string ClickableClassName = "dx-clickable";

        /// <summary>
        /// The grab strip <see cref="CreateResizeHandle"/> renders under a resizable panel.
        /// </summary>
        internal const string ResizerClassName = "dx-resizer";
        internal const string KeyValueClassName = "dx-kv";
        internal const string KeyValueKeyClassName = "dx-kv__k";
        internal const string KeyValueValueClassName = "dx-kv__v";
        internal const string FooterClassName = "dx-footer";
        internal const string FooterStatClassName = "dx-footer__stat";
        internal const string FooterNumberClassName = "dx-footer__num";
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
            ApplyCompleteBorder(element, borderColor, CompleteBorderWidth);
        }

        internal static void ApplyCompleteBorder(
            VisualElement element,
            Color borderColor,
            float borderWidth
        )
        {
            if (element == null)
            {
                return;
            }

            element.style.borderTopWidth = borderWidth;
            element.style.borderRightWidth = borderWidth;
            element.style.borderBottomWidth = borderWidth;
            element.style.borderLeftWidth = borderWidth;
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
            AddRouteKindClasses(
                element,
                routeKind,
                TypeBadgeClassName,
                TypeBadgeUntargetedClassName,
                TypeBadgeTargetedClassName,
                TypeBadgeBroadcastClassName
            );
        }

        private static void AddRouteKindClasses(
            VisualElement element,
            string routeKind,
            string baseClassName,
            string untargetedClassName,
            string targetedClassName,
            string broadcastClassName
        )
        {
            if (element == null)
            {
                return;
            }

            element.AddToClassList(baseClassName);
            switch (DxMessagingEditorPalette.NormalizeRouteKind(routeKind))
            {
                case DxMessagingEditorPalette.UntargetedKind:
                    element.AddToClassList(untargetedClassName);
                    break;
                case DxMessagingEditorPalette.TargetedKind:
                    element.AddToClassList(targetedClassName);
                    break;
                case DxMessagingEditorPalette.BroadcastKind:
                    element.AddToClassList(broadcastClassName);
                    break;
            }
        }

        /// <summary>
        /// Adds the <c>.dx-dot</c> taxonomy dot classes for a route kind. The dot is the compact
        /// form of <see cref="AddRouteKindTypeBadgeClasses"/>, for fixed-height rows where a full
        /// badge does not fit.
        /// </summary>
        internal static void AddRouteKindDotClasses(VisualElement element, string routeKind)
        {
            AddRouteKindClasses(
                element,
                routeKind,
                DotClassName,
                DotUntargetedClassName,
                DotTargetedClassName,
                DotBroadcastClassName
            );
        }

        /// <summary>
        /// Adds the <c>.dx-chip</c> taxonomy chip classes for a route kind, used by the route-kind
        /// filter chips.
        /// </summary>
        internal static void AddRouteKindChipClasses(VisualElement element, string routeKind)
        {
            AddRouteKindClasses(
                element,
                routeKind,
                ChipClassName,
                ChipUntargetedClassName,
                ChipTargetedClassName,
                ChipBroadcastClassName
            );
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

            if (!string.IsNullOrWhiteSpace(title))
            {
                Label titleLabel = new(title);
                if (!string.IsNullOrWhiteSpace(titleName))
                {
                    titleLabel.name = titleName;
                }
                titleLabel.AddToClassList(EmptyTitleClassName);
                container.Add(titleLabel);
            }

            if (!string.IsNullOrWhiteSpace(body))
            {
                Label bodyLabel = new(body);
                if (!string.IsNullOrWhiteSpace(bodyName))
                {
                    bodyLabel.name = bodyName;
                }
                bodyLabel.AddToClassList(EmptyBodyClassName);
                bodyLabel.style.whiteSpace = WhiteSpace.Normal;
                container.Add(bodyLabel);
            }

            return container;
        }

        /// <summary>
        /// Builds a drag handle that resizes <paramref name="target"/> vertically. Issue #344
        /// reported that Component Diagnostics and the other capped panels cannot be resized:
        /// each one is pinned to a `max-height` chosen for a window nobody has, so a reader
        /// with room to spare still scrolls a 180px box. The handle raises the cap as it
        /// drags, because a `max-height` left in place would silently win over the new height.
        /// </summary>
        internal static VisualElement CreateResizeHandle(
            VisualElement target,
            float minHeight,
            float maxHeight,
            string name = null
        )
        {
            VisualElement handle = new();
            if (!string.IsNullOrWhiteSpace(name))
            {
                handle.name = name;
            }
            handle.AddToClassList(ResizerClassName);
            handle.tooltip = "Drag to resize this section.";
            if (target == null)
            {
                return handle;
            }

            float pointerStartY = 0f;
            float startHeight = 0f;
            handle.RegisterCallback<PointerDownEvent>(evt =>
            {
                pointerStartY = evt.position.y;
                startHeight = target.resolvedStyle.height;
                handle.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            handle.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!handle.HasPointerCapture(evt.pointerId))
                {
                    return;
                }

                float requested = startHeight + (evt.position.y - pointerStartY);
                ApplyResizedHeight(target, requested, minHeight, maxHeight);
                evt.StopPropagation();
            });
            handle.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (handle.HasPointerCapture(evt.pointerId))
                {
                    handle.ReleasePointer(evt.pointerId);
                }
                evt.StopPropagation();
            });
            return handle;
        }

        /// <summary>
        /// Applies a dragged height to a panel that was built with a `max-height` cap, keeping
        /// the cap from overriding the new size while still bounding it.
        /// </summary>
        internal static void ApplyResizedHeight(
            VisualElement target,
            float requestedHeight,
            float minHeight,
            float maxHeight
        )
        {
            if (target == null)
            {
                return;
            }

            float clamped = Mathf.Clamp(requestedHeight, minHeight, maxHeight);
            target.style.height = clamped;
            target.style.maxHeight = maxHeight;
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
