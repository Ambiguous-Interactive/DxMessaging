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
