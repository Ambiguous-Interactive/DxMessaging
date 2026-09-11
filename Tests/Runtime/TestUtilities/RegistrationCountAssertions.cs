#if UNITY_2021_3_OR_NEWER
/*
    CallerArgumentExpressionAttribute polyfill for build environments whose
    BCL predates .NET 6 (Unity 2021.3 ships a Mono runtime without this
    attribute). Roslyn recognizes the attribute by full name regardless of
    origin, so a hand-rolled type in System.Runtime.CompilerServices is
    sufficient. The polyfill is wrapped in a #if so newer runtimes that ship
    the attribute do not see a duplicate definition.
*/
#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        public string ParameterName { get; }
    }
}
#endif

namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Text;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>
    /// Asserts the public per-kind registration counters on an
    /// <see cref="IMessageBus"/> in a single call, surfacing the diverging
    /// buckets on failure so a stale assertion can be diagnosed in seconds
    /// rather than minutes. The helper pairs every check with a diagnostic
    /// message that includes:
    /// <list type="bullet">
    /// <item>a per-bucket "expected vs actual (delta)" line for every counter
    /// that diverged, listed first so the actionable diff is unmissable;</item>
    /// <item>the call site (file path + line number) supplied by
    /// <c>[CallerFilePath]</c> / <c>[CallerLineNumber]</c>;</item>
    /// <item>the full expected vs actual triple on a tail line for context;</item>
    /// <item>the bus expression captured by
    /// <c>[CallerArgumentExpression]</c> so failures involving multiple buses
    /// in one test can be attributed to the specific bus that failed;</item>
    /// <item>any caller-supplied <paramref name="context"/> label so a single
    /// fixture can call this multiple times without ambiguity.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bucketing reminder: <see cref="IMessageBus.RegisteredUntargeted"/>
    /// counts only the pure untargeted scalar sink. Registrations created via
    /// <c>RegisterTargetedWithoutTargeting</c> bucket under
    /// <see cref="IMessageBus.RegisteredTargeted"/>, and
    /// <c>RegisterBroadcastWithoutSource</c> registrations bucket under
    /// <see cref="IMessageBus.RegisteredBroadcast"/>. The helper does not
    /// special-case these; it simply reads the public counters and reports
    /// drift against the supplied expected values. Callers can suppress the
    /// reminder via <paramref name="includeBucketingReminder"/> when the
    /// expected failure mode has nothing to do with bucketing.
    /// </para>
    /// <para>
    /// This helper is intentionally bus-agnostic. Pass any
    /// <see cref="IMessageBus"/> instance; tests that operate on the global
    /// bus typically pass <c>MessageHandler.MessageBus</c>.
    /// </para>
    /// </remarks>
    public static class RegistrationCountAssertions
    {
        /// <summary>
        /// Asserts that the bus reports exactly the supplied per-kind counts.
        /// On any mismatch, the failure message lists the diverging buckets
        /// first (with explicit deltas), then the call site, then the full
        /// triple plus the caller-supplied context label and bus expression.
        /// </summary>
        /// <param name="bus">Bus to inspect. Must not be null.</param>
        /// <param name="untargeted">Expected
        /// <see cref="IMessageBus.RegisteredUntargeted"/>.</param>
        /// <param name="targeted">Expected
        /// <see cref="IMessageBus.RegisteredTargeted"/>.</param>
        /// <param name="broadcast">Expected
        /// <see cref="IMessageBus.RegisteredBroadcast"/>.</param>
        /// <param name="context">
        /// Required label embedded in the failure message describing the
        /// lifecycle moment being asserted (for example
        /// "after second component disabled"). Tests typically call this
        /// helper across several lifecycle transitions, and a missing label
        /// makes the resulting failure much harder to triage; making the
        /// parameter mandatory prevents future drift where call sites lose
        /// their lifecycle context.
        /// </param>
        /// <param name="includeBucketingReminder">
        /// When true (default) the failure message ends with the
        /// TargetedWithoutTargeting / BroadcastWithoutSource bucketing
        /// reminder. Pass <see langword="false"/> in fixtures whose expected
        /// failure mode has nothing to do with bucketing so the message
        /// stays focused on the actual delta.
        /// </param>
        /// <param name="busExpression">Auto-supplied by the compiler from the
        /// expression passed for <paramref name="bus"/>. Surfaces in the
        /// failure message so tests that operate on multiple buses can
        /// identify which one diverged.</param>
        /// <param name="callerFilePath">Auto-supplied by the compiler.</param>
        /// <param name="callerLineNumber">Auto-supplied by the compiler.</param>
        /// <param name="callerMemberName">Auto-supplied by the compiler.</param>
        public static void AssertRegistrationCounts(
            IMessageBus bus,
            int untargeted,
            int targeted,
            int broadcast,
            string context,
            bool includeBucketingReminder = true,
            [CallerArgumentExpression("bus")] string busExpression = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLineNumber = 0,
            [CallerMemberName] string callerMemberName = null
        )
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            int actualUntargeted = bus.RegisteredUntargeted;
            int actualTargeted = bus.RegisteredTargeted;
            int actualBroadcast = bus.RegisteredBroadcast;

            if (
                actualUntargeted == untargeted
                && actualTargeted == targeted
                && actualBroadcast == broadcast
            )
            {
                return;
            }

            string message = BuildFailureMessage(
                expectedUntargeted: untargeted,
                expectedTargeted: targeted,
                expectedBroadcast: broadcast,
                actualUntargeted: actualUntargeted,
                actualTargeted: actualTargeted,
                actualBroadcast: actualBroadcast,
                context: context,
                includeBucketingReminder: includeBucketingReminder,
                busExpression: busExpression,
                callerFilePath: callerFilePath,
                callerLineNumber: callerLineNumber,
                callerMemberName: callerMemberName
            );
            Assert.Fail(message);
        }

        private static string BuildFailureMessage(
            int expectedUntargeted,
            int expectedTargeted,
            int expectedBroadcast,
            int actualUntargeted,
            int actualTargeted,
            int actualBroadcast,
            string context,
            bool includeBucketingReminder,
            string busExpression,
            string callerFilePath,
            int callerLineNumber,
            string callerMemberName
        )
        {
            string contextSuffix = string.IsNullOrEmpty(context) ? string.Empty : $" [{context}]";
            string busLabel = string.IsNullOrEmpty(busExpression) ? "<unknown bus>" : busExpression;
            string location = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1} ({2})",
                callerFilePath ?? "<unknown file>",
                callerLineNumber,
                callerMemberName ?? "<unknown member>"
            );
            string expected = string.Format(
                CultureInfo.InvariantCulture,
                "Untargeted={0}, Targeted={1}, Broadcast={2}",
                expectedUntargeted,
                expectedTargeted,
                expectedBroadcast
            );
            string actual = string.Format(
                CultureInfo.InvariantCulture,
                "Untargeted={0}, Targeted={1}, Broadcast={2}",
                actualUntargeted,
                actualTargeted,
                actualBroadcast
            );

            List<string> mismatches = new(3);
            AppendMismatch(mismatches, "Untargeted", expectedUntargeted, actualUntargeted);
            AppendMismatch(mismatches, "Targeted", expectedTargeted, actualTargeted);
            AppendMismatch(mismatches, "Broadcast", expectedBroadcast, actualBroadcast);

            StringBuilder builder = new();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "Registration counts mismatch on bus '{0}'{1}.",
                busLabel,
                contextSuffix
            );
            foreach (string line in mismatches)
            {
                builder.Append(' ');
                builder.Append(line);
            }
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                " At {0}. Full triple: expected ({1}); actual ({2}).",
                location,
                expected,
                actual
            );
            if (includeBucketingReminder)
            {
                builder.Append(
                    " Bucketing reminder: TargetedWithoutTargeting registrations land "
                        + "under RegisteredTargeted; BroadcastWithoutSource registrations "
                        + "land under RegisteredBroadcast."
                );
            }

            return builder.ToString();
        }

        private static void AppendMismatch(
            List<string> mismatches,
            string bucket,
            int expected,
            int actual
        )
        {
            if (expected == actual)
            {
                return;
            }

            int delta = actual - expected;
            string sign = 0 <= delta ? "+" : string.Empty;
            mismatches.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: expected {1}, actual {2} (delta {3}{4}).",
                    bucket,
                    expected,
                    actual,
                    sign,
                    delta
                )
            );
        }
    }
}
#endif
