namespace DxMessaging.Tests.Runtime
{
    using System.Collections.Generic;

    /// <summary>
    /// Property-based <see cref="IEnumerable{T}"/> sources for NUnit
    /// <c>[ValueSource]</c>. Each property exposes the cases for one scenario
    /// permutation set; properties are used so the source resolves by name and
    /// returns a fresh enumeration to the test runner on every access.
    /// </summary>
    /// <example>
    /// <code>
    /// [Test]
    /// public void DispatchAcrossKinds(
    ///     [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
    ///     MessageScenario scenario)
    /// {
    ///     // ...
    /// }
    /// </code>
    /// </example>
    public static class MessageScenarios
    {
        public static IEnumerable<MessageScenario> AllKinds
        {
            get
            {
                yield return MessageScenario.Untargeted();
                yield return MessageScenario.Targeted();
                yield return MessageScenario.Broadcast();
            }
        }

        /// <summary>
        /// Five-kind source covering the three canonical dispatch kinds plus
        /// the targeted-without-targeting and broadcast-without-source
        /// dispatch surfaces. Use this source for tests that need to assert
        /// behavior across the without-context dispatch codepaths in addition
        /// to the canonical three. Tests consuming this source must handle
        /// every value of <see cref="MessageKind"/>.
        /// </summary>
        public static IEnumerable<MessageScenario> AllKindsIncludingWithoutContext
        {
            get
            {
                yield return MessageScenario.Untargeted();
                yield return MessageScenario.Targeted();
                yield return MessageScenario.Broadcast();
                yield return MessageScenario.TargetedWithoutTargeting();
                yield return MessageScenario.BroadcastWithoutSource();
            }
        }

        public static IEnumerable<MessageScenario> KindsWithComponentTarget
        {
            get
            {
                yield return MessageScenario.Targeted();
                yield return MessageScenario.Broadcast();
            }
        }

        public static IEnumerable<MessageScenario> WithAndWithoutInterceptor
        {
            get
            {
                foreach (MessageScenario scenario in AllKinds)
                {
                    yield return scenario.WithInterceptor(false);
                    yield return scenario.WithInterceptor(true);
                }
            }
        }

        public static IEnumerable<MessageScenario> WithAndWithoutPostProcessor
        {
            get
            {
                foreach (MessageScenario scenario in AllKinds)
                {
                    yield return scenario.WithPostProcessor(false);
                    yield return scenario.WithPostProcessor(true);
                }
            }
        }

        public static IEnumerable<MessageScenario> WithAndWithoutPostProcessorIncludingWithoutContext
        {
            get
            {
                foreach (MessageScenario scenario in AllKindsIncludingWithoutContext)
                {
                    yield return scenario.WithPostProcessor(false);
                    yield return scenario.WithPostProcessor(true);
                }
            }
        }

        public static IEnumerable<MessageScenario> WithDiagnosticsToggle
        {
            get
            {
                foreach (MessageScenario scenario in AllKinds)
                {
                    yield return scenario.WithDiagnostics(false);
                    yield return scenario.WithDiagnostics(true);
                }
            }
        }

        public static IEnumerable<MessageScenario> WithDiagnosticsToggleIncludingWithoutContext
        {
            get
            {
                foreach (MessageScenario scenario in AllKindsIncludingWithoutContext)
                {
                    yield return scenario.WithDiagnostics(false);
                    yield return scenario.WithDiagnostics(true);
                }
            }
        }

        /// <summary>
        /// Cross-product of interceptor x post-processor presence per kind,
        /// minus the (false, false) baseline. That row was removed for two
        /// reasons: it duplicated the no-feature emit path already pinned by
        /// <c>AllocationMatrixTests.EmitIsZeroAlloc</c>, and the original
        /// (Untargeted, false, false) case proved empirically unstable when
        /// run through this harness.
        /// </summary>
        public static IEnumerable<MessageScenario> WithAtLeastOneFeatureToggle
        {
            get
            {
                foreach (MessageScenario scenario in AllKinds)
                {
                    yield return scenario.WithInterceptor(false).WithPostProcessor(true);
                    yield return scenario.WithInterceptor(true).WithPostProcessor(false);
                    yield return scenario.WithInterceptor(true).WithPostProcessor(true);
                }
            }
        }
    }
}
