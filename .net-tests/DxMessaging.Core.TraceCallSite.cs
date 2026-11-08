namespace DxMessaging.Tests.Runtime.Core
{
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;

    internal static class DiagnosticTraceCallSite
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MessageEmissionData Capture(IMessage message, out string raw)
        {
            raw = new StackTrace(true).ToString();
            return new MessageEmissionData(message);
        }
    }
}
