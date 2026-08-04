#if UNITY_EDITOR
namespace DxMessaging.Editor
{
    using System;
    using UnityEngine;

    internal static class DxMessagingEditorPalette
    {
        internal static readonly Color Amber = FromHex(0xf4, 0xa8, 0x36);
        internal static readonly Color AmberSoft = FromHex(0xff, 0xd4, 0x8e);
        internal static readonly Color Untargeted = FromHex(0x7f, 0xa6, 0xd8);
        internal static readonly Color Targeted = FromHex(0xec, 0x46, 0x61);
        internal static readonly Color Broadcast = FromHex(0x7f, 0xb8, 0x8a);
        internal static readonly Color Trace = FromHex(0x7f, 0xa6, 0xd8);
        internal static readonly Color TraceMessage = FromHex(0x7f, 0xb8, 0x8a);
        internal static readonly Color TraceTarget = FromHex(0xff, 0xd4, 0x8e);
        internal static readonly Color BorderSoft = new(0.14f, 0.17f, 0.22f, 0.32f);
        internal static readonly Color Border = new(0.14f, 0.17f, 0.22f, 0.38f);
        internal static readonly Color BorderPanel = new(0.14f, 0.17f, 0.22f, 0.42f);
        internal static readonly Color BorderStrong = new(0.14f, 0.17f, 0.22f, 0.52f);
        internal static readonly Color SelectedWash = new(0.96f, 0.66f, 0.21f, 0.1f);

        /// <summary>
        /// Label colors for the two skins, mirroring the <c>--dx-*-text</c> tokens and the
        /// <c>.dx-theme.dx-light</c> overrides. UI Toolkit surfaces get these from the stylesheet;
        /// IMGUI holdouts cannot, so they read them here instead of inventing colors.
        /// </summary>
        internal static readonly Color BroadcastText = FromHex(0xa8, 0xd3, 0xb2);
        internal static readonly Color BroadcastTextOnLight = FromHex(0x33, 0x68, 0x4a);
        internal static readonly Color AmberOnLight = FromHex(0xb0, 0x7d, 0x1f);

        /// <summary>
        /// Picks whichever of a token's two values belongs to the skin in use.
        /// </summary>
        internal static Color ForSkin(Color darkSkinColor, Color lightSkinColor, bool proSkin)
        {
            return proSkin ? darkSkinColor : lightSkinColor;
        }

        internal const string UntargetedKind = "Untargeted";
        internal const string TargetedKind = "Targeted";
        internal const string BroadcastKind = "Broadcast";

        internal static Color RouteKindColor(string routeKind)
        {
            switch (NormalizeRouteKind(routeKind))
            {
                case UntargetedKind:
                    return Untargeted;
                case TargetedKind:
                    return Targeted;
                case BroadcastKind:
                    return Broadcast;
                default:
                    return Amber;
            }
        }

        /// <summary>
        /// Whether a route kind survives the three taxonomy filter chips. A route kind that is
        /// none of the three (unrecognized or missing) is never hidden, so the chips can only ever
        /// hide rows they can also bring back.
        /// </summary>
        /// <remarks>
        /// Shared by the snapshot and live Monitor surfaces so a chip means the same thing in
        /// both. Both store their chip state as what is <em>hidden</em>, so their
        /// <c>default</c> is an unfiltered log.
        /// </remarks>
        internal static bool ShowsRouteKind(
            string routeKind,
            bool showUntargeted,
            bool showTargeted,
            bool showBroadcast
        )
        {
            switch (NormalizeRouteKind(routeKind))
            {
                case UntargetedKind:
                    return showUntargeted;
                case TargetedKind:
                    return showTargeted;
                case BroadcastKind:
                    return showBroadcast;
                default:
                    return true;
            }
        }

        internal static string NormalizeRouteKind(string routeKind)
        {
            string value = string.IsNullOrWhiteSpace(routeKind) ? string.Empty : routeKind.Trim();
            if (value.StartsWith(UntargetedKind, StringComparison.Ordinal))
            {
                return UntargetedKind;
            }
            if (value.StartsWith(TargetedKind, StringComparison.Ordinal))
            {
                return TargetedKind;
            }
            if (value.StartsWith(BroadcastKind, StringComparison.Ordinal))
            {
                return BroadcastKind;
            }
            return string.Empty;
        }

        private static Color FromHex(byte red, byte green, byte blue)
        {
            return new Color(red / 255f, green / 255f, blue / 255f, 1f);
        }
    }
}
#endif
