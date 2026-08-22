#if UNITY_EDITOR
namespace DxMessaging.Editor
{
    using System;
    using System.Collections.Generic;
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
        internal const string ToolbarActionsClassName = "dx-toolbar--actions";
        internal const string ToolbarFiltersClassName = "dx-toolbar--filters";
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
        internal const string DetailStackLinkClassName = "dx-detail__stack-link";
        internal const string DetailActiveClassName = "dx-detail__active";

        /// <summary>
        /// Carries the pointer cursor for anything that answers a click. Issue #344 reported
        /// that nothing in the window says what is clickable; a hover style only tells a
        /// reader who already guessed, and the cursor tells one who did not.
        /// </summary>
        internal const string ClickableClassName = "dx-clickable";

        /// <summary>
        /// The grab strip <see cref="CreateResizeHandle"/> renders next to a resizable panel.
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
        /// <param name="initialHeight">
        /// A height the caller remembered from an earlier drag, or 0 for "never resized". Every
        /// filter keystroke rebuilds the sections around it, so a handle that could not be told
        /// where the reader left it would hand back the shipped cap on the next character typed.
        /// </param>
        /// <param name="onHeightChanged">
        /// Raised with each dragged height so the caller can remember it across those rebuilds.
        /// </param>
        /// <param name="growsUpward">
        /// Whether dragging upward increases the target height. Use this when the handle sits
        /// above its target, such as the divider between a log and its lower details pane.
        /// </param>
        /// <param name="allowTargetShrink">
        /// Whether layout may shrink the resized target below its requested height when its
        /// parent has less room. Keep this enabled for a persisted pane in a resizable window.
        /// </param>
        internal static VisualElement CreateResizeHandle(
            VisualElement target,
            float minHeight,
            float maxHeight,
            string name = null,
            float initialHeight = 0f,
            Action<float> onHeightChanged = null,
            bool growsUpward = false,
            bool allowTargetShrink = false
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

            if (initialHeight > 0f)
            {
                ApplyResizedHeight(target, initialHeight, minHeight, maxHeight, allowTargetShrink);
            }

            float pointerStartY = 0f;
            float startHeight = 0f;
            handle.RegisterCallback<PointerDownEvent>(evt =>
            {
                // Only the primary button drags. Without this a right- or middle-click on the
                // 5px strip captures the pointer and starts a resize nobody asked for.
                if (evt.button != 0)
                {
                    return;
                }

                pointerStartY = evt.position.y;
                // A window-level target may resolve below its remembered inline height when the
                // window is short. Start that drag from what the reader can see or a small drag
                // would have to erase hundreds of hidden pixels before anything moved. Fixed-size
                // targets keep using their inline height so successive drags start where the last
                // one ended. UI Toolkit reports
                // `Undefined` when a pixel value IS set and `Null` when none is -- measured, and
                // the opposite of what the names suggest -- so before the first drag this falls
                // back to the resolved height rather than reading a `value` of 0 and jumping.
                startHeight =
                    !allowTargetShrink && target.style.height.keyword == StyleKeyword.Undefined
                        ? target.style.height.value.value
                        : target.resolvedStyle.height;
                handle.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            handle.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!handle.HasPointerCapture(evt.pointerId))
                {
                    return;
                }

                float pointerDelta = evt.position.y - pointerStartY;
                float requested = startHeight + (growsUpward ? -pointerDelta : pointerDelta);
                onHeightChanged?.Invoke(
                    ApplyResizedHeight(target, requested, minHeight, maxHeight, allowTargetShrink)
                );
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
        /// Applies a dragged height to a panel that was built with a `max-height` cap and returns
        /// the height actually used. The cap has to move with it -- a `max-height` left below the
        /// dragged height silently wins -- and so does `flex-shrink`: these panels are built
        /// shrinkable so they give space back when the window is short, which also means a plain
        /// height is only a starting size that Yoga takes straight back unless the caller
        /// explicitly keeps shrink enabled for a window-level pane.
        /// </summary>
        internal static float ApplyResizedHeight(
            VisualElement target,
            float requestedHeight,
            float minHeight,
            float maxHeight,
            bool allowTargetShrink = false
        )
        {
            if (target == null)
            {
                return 0f;
            }

            float clamped = Mathf.Clamp(requestedHeight, minHeight, maxHeight);
            target.style.height = clamped;
            target.style.maxHeight = maxHeight;
            target.style.flexShrink = allowTargetShrink ? 1f : 0f;
            return clamped;
        }

        /// <summary>
        /// Turns wrapping on for a container and keeps that container at least as tall as the
        /// lines its children wrap onto.
        ///
        /// Unity 2021.3 does not grow a wrapping container to fit the extra lines. The container
        /// keeps its single-line height and the wrapped lines draw outside it, on top of whatever
        /// the window draws beneath. Newer editors size the container correctly, so the defect is
        /// invisible on 6000.4. Issue #435 hit it in the Message Monitor toolbar and issue #440
        /// lists the Flow Graph containers with the same shape.
        ///
        /// Measuring the children and applying the result as `min-height` supplies the height
        /// Unity 2021.3 does not derive. The measurement is only applied when the container is
        /// actually too short, so an editor that already sizes the container writes no inline
        /// style at all. `align-content: flex-start` packs the lines at the top, so a container
        /// that just grew cannot stretch its own lines and ask to grow again.
        ///
        /// The children are watched as well as the container. Unity reports a geometry change to
        /// an element only when that element's own box changes, and it does not report a child's
        /// change to the parent in either propagation phase. A container already held at a height
        /// therefore never hears that its text finished measuring and needs more room, which is
        /// how a details header ended up ten pixels short on Unity 6000.3.
        /// </summary>
        internal static void ApplyContentSizedWrap(VisualElement container)
        {
            if (container == null)
            {
                return;
            }

            container.style.flexWrap = Wrap.Wrap;
            container.style.alignContent = Align.FlexStart;
            WrapHeightFit fit = new(container);
            container.RegisterCallback<GeometryChangedEvent, WrapHeightFit>(
                static (_, state) => state.Fit(),
                fit
            );
            container.RegisterCallback<DetachFromPanelEvent, WrapHeightFit>(
                static (_, state) => state.Release(),
                fit
            );
        }

        /// <summary>
        /// The height a wrapping container needs for every line its children occupy, measured in
        /// the container's own coordinate space so a panned or zoomed ancestor cannot skew it.
        ///
        /// Returns 0 when nothing is measurable yet, which includes a child laid out with an
        /// unbounded size. Unity reports that as `Length`'s maximum, 8388608, and treating it as a
        /// real height would ask for a box eight million pixels tall.
        /// </summary>
        internal static float MeasureWrappedContentHeight(VisualElement container)
        {
            if (container == null)
            {
                return 0f;
            }

            float contentBottom = 0f;
            bool measured = false;
            foreach (VisualElement child in container.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None)
                {
                    continue;
                }

                // An absolutely positioned child is out of flow, so it creates no wrapped line
                // and must not drag the container's height with it.
                if (child.resolvedStyle.position == Position.Absolute)
                {
                    continue;
                }

                Rect childLayout = child.layout;
                if (float.IsNaN(childLayout.yMax))
                {
                    continue;
                }

                if (childLayout.yMax >= UnboundedLayoutSize)
                {
                    return 0f;
                }

                contentBottom = Mathf.Max(
                    contentBottom,
                    childLayout.yMax + child.resolvedStyle.marginBottom
                );
                measured = true;
            }

            if (!measured)
            {
                return 0f;
            }

            IResolvedStyle containerStyle = container.resolvedStyle;
            return contentBottom + containerStyle.paddingBottom + containerStyle.borderBottomWidth;
        }

        /// <summary>
        /// Unity's largest representable length (`Length`'s own maximum). A layout that reaches it
        /// was measured with no bound rather than measured, so it is not a height to apply.
        /// </summary>
        private const float UnboundedLayoutSize = 8388608f;

        /// <summary>
        /// Holds the height one container was given, so a container that already fits is never
        /// written to and a container that was grown is only written again when it needs more.
        ///
        /// The height is only ever raised while the container is in a panel. Leaving the panel
        /// releases it, which is what a reused or rebuilt container needs.
        /// </summary>
        private sealed class WrapHeightFit
        {
            private const float Tolerance = 0.5f;

            private readonly VisualElement _container;
            private readonly HashSet<VisualElement> _watchedChildren = new();

            private bool _applied;
            private float _appliedHeight;

            internal WrapHeightFit(VisualElement container)
            {
                _container = container;
            }

            internal void Fit()
            {
                WatchChildren();

                float required = MeasureWrappedContentHeight(_container);
                if (required <= 0f || required >= UnboundedLayoutSize)
                {
                    return;
                }

                float current = _applied ? _appliedHeight : _container.resolvedStyle.height;
                if (required <= current + Tolerance)
                {
                    return;
                }

                _applied = true;
                _appliedHeight = required;
                _container.style.minHeight = required;
            }

            internal void Release()
            {
                _watchedChildren.Clear();
                if (!_applied)
                {
                    return;
                }

                _applied = false;
                _appliedHeight = 0f;
                _container.style.minHeight = StyleKeyword.Null;
            }

            /// <summary>
            /// A child that resizes in place changes what the container needs, and Unity does not
            /// report a child's geometry change to its parent. Each child is watched directly.
            /// </summary>
            private void WatchChildren()
            {
                foreach (VisualElement child in _container.Children())
                {
                    if (!_watchedChildren.Add(child))
                    {
                        continue;
                    }

                    child.RegisterCallback<GeometryChangedEvent, WrapHeightFit>(
                        static (_, state) => state.Fit(),
                        this
                    );
                }
            }
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
