namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Text;

    /// <summary>
    /// Immutable description of a single parameterized test case, consumed by
    /// NUnit <c>[ValueSource]</c>. The harness uses <see cref="Kind"/> to pick the
    /// right registration / emission overloads, while the boolean toggles let the
    /// same test method exercise interceptor, post-processor, and diagnostics
    /// permutations.
    /// </summary>
    public sealed class MessageScenario : IEquatable<MessageScenario>
    {
        public MessageKind Kind { get; }

        public string DisplayName { get; }

        public bool UseInterceptor { get; }

        public bool UsePostProcessor { get; }

        public bool DiagnosticsEnabled { get; }

        public MessageScenario(
            MessageKind kind,
            bool useInterceptor = false,
            bool usePostProcessor = false,
            bool diagnosticsEnabled = false
        )
        {
            Kind = kind;
            UseInterceptor = useInterceptor;
            UsePostProcessor = usePostProcessor;
            DiagnosticsEnabled = diagnosticsEnabled;
            DisplayName = ComposeDisplayName(
                kind,
                useInterceptor,
                usePostProcessor,
                diagnosticsEnabled
            );
        }

        public static MessageScenario Untargeted()
        {
            return new MessageScenario(MessageKind.Untargeted);
        }

        public static MessageScenario Targeted()
        {
            return new MessageScenario(MessageKind.Targeted);
        }

        public static MessageScenario Broadcast()
        {
            return new MessageScenario(MessageKind.Broadcast);
        }

        public static MessageScenario TargetedWithoutTargeting()
        {
            return new MessageScenario(MessageKind.TargetedWithoutTargeting);
        }

        public static MessageScenario BroadcastWithoutSource()
        {
            return new MessageScenario(MessageKind.BroadcastWithoutSource);
        }

        public MessageScenario WithInterceptor(bool useInterceptor)
        {
            return new MessageScenario(
                Kind,
                useInterceptor: useInterceptor,
                usePostProcessor: UsePostProcessor,
                diagnosticsEnabled: DiagnosticsEnabled
            );
        }

        public MessageScenario WithPostProcessor(bool usePostProcessor)
        {
            return new MessageScenario(
                Kind,
                useInterceptor: UseInterceptor,
                usePostProcessor: usePostProcessor,
                diagnosticsEnabled: DiagnosticsEnabled
            );
        }

        public MessageScenario WithDiagnostics(bool diagnosticsEnabled)
        {
            return new MessageScenario(
                Kind,
                useInterceptor: UseInterceptor,
                usePostProcessor: UsePostProcessor,
                diagnosticsEnabled: diagnosticsEnabled
            );
        }

        public override string ToString()
        {
            return DisplayName;
        }

        public bool Equals(MessageScenario other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Kind == other.Kind
                && UseInterceptor == other.UseInterceptor
                && UsePostProcessor == other.UsePostProcessor
                && DiagnosticsEnabled == other.DiagnosticsEnabled;
        }

        public override bool Equals(object obj)
        {
            return obj is MessageScenario other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = (hash * 397) ^ (UseInterceptor ? 1 : 0);
                hash = (hash * 397) ^ (UsePostProcessor ? 1 : 0);
                hash = (hash * 397) ^ (DiagnosticsEnabled ? 1 : 0);
                return hash;
            }
        }

        private static string ComposeDisplayName(
            MessageKind kind,
            bool useInterceptor,
            bool usePostProcessor,
            bool diagnosticsEnabled
        )
        {
            StringBuilder builder = new StringBuilder(kind.ToString());
            if (useInterceptor)
            {
                builder.Append("+Interceptor");
            }

            if (usePostProcessor)
            {
                builder.Append("+PostProcessor");
            }

            if (diagnosticsEnabled)
            {
                builder.Append("+Diagnostics");
            }

            return builder.ToString();
        }
    }
}
