#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Debug = UnityEngine.Debug;
    using Object = UnityEngine.Object;
    using Random = System.Random;

    public abstract class MessagingTestBase
    {
        private const string TestSeedEnvVar = "DXMESSAGING_TEST_SEED";
        private const int DefaultTestSeed = unchecked((int)0xDB1ABCED);

        private const string VerboseLogEnvVar = "DXM_TEST_VERBOSE_LOG";

        private static int? _cachedSeed;
        private static bool? _cachedVerbose;

        protected int _numRegistrations;
        protected readonly List<GameObject> _spawned = new();
        protected Random _random = new(DefaultTestSeed);

        protected virtual bool MessagingDebugEnabled => true;

        protected virtual int StressRegistrations => 150;

        /// <summary>
        /// Maximum number of polled frames the loop in
        /// <see cref="WaitUntilMessageHandlerIsFresh"/> yields, waiting for the
        /// message bus to drain before failing. Override in derived fixtures
        /// that need a tighter or looser bound.
        /// </summary>
        /// <remarks>
        /// This budget is expressed in FRAMES, not wall-clock time, on purpose.
        /// A wall-clock threshold (the prior 1.5s <see cref="TimeSpan"/> bound)
        /// is inherently runner-speed dependent: a slower CI runner (observed on
        /// Unity 2021.3 PlayMode) can exceed a fixed millisecond budget for the
        /// same amount of deterministic work and flake, even though the bus
        /// drains in the same NUMBER of frames everywhere. The bus drains
        /// synchronously on the deferred-destroy flush, which Unity performs in
        /// a single frame, so the happy path clears in zero-to-one polled
        /// frames. The budget below is a generous deterministic safety margin
        /// that polls a fixed maximum number of frames regardless of how fast
        /// each frame executes; it cannot flake on a slow machine because it is
        /// not measuring wall clock.
        /// </remarks>
        protected virtual int FreshHandlerWaitFrameBudget => 600;

        /// <summary>
        /// Resolved test seed cached for the lifetime of the process. The
        /// environment variable is parsed once and reused across every
        /// fixture/test to avoid repeated lookups during Setup.
        /// </summary>
        protected static int TestSeed
        {
            get
            {
                _cachedSeed ??= ResolveTestSeed();
                return _cachedSeed.Value;
            }
        }

        /// <summary>
        /// When <c>true</c>, the test-harness <see cref="MessagingDebug.LogFunction"/>
        /// routes <see cref="LogLevel.Debug"/>/<see cref="LogLevel.Info"/> messages and
        /// the per-test/per-fixture status dumps to <see cref="Debug.Log"/>. Default is
        /// <c>false</c>: the bus emits an <see cref="LogLevel.Info"/> "Could not find a
        /// matching ... handler" line on normal deregistered-emit flow, and each
        /// <see cref="Debug.Log"/> captures a full stack trace, so leaving this on during
        /// a full PlayMode run produced a multi-tens-of-megabytes log. Opt in by setting
        /// the <c>DXM_TEST_VERBOSE_LOG</c> environment variable to a truthy value
        /// (<c>1</c>/<c>true</c>/<c>yes</c>, case-insensitive). Warnings and errors are
        /// always routed regardless of this flag.
        /// </summary>
        /// <remarks>
        /// Resolved once for the lifetime of the process (mirroring <see cref="TestSeed"/>)
        /// so the environment variable is parsed a single time rather than on every Setup.
        /// </remarks>
        protected static bool VerboseConsoleLogging
        {
            get
            {
                _cachedVerbose ??= ResolveVerboseConsoleLogging();
                return _cachedVerbose.Value;
            }
        }

        [OneTimeSetUp]
        public virtual void LogTestSeedOnce()
        {
            if (!VerboseConsoleLogging)
            {
                return;
            }

            Debug.Log($"DxMessaging test seed = {TestSeed} (env {TestSeedEnvVar}).");
        }

        /// <summary>
        /// Reseeds the per-test random source from the resolved seed and
        /// applies the default messaging-debug configuration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <see cref="DxMessagingStaticState.Reset"/> call has moved to
        /// <see cref="UnitySetup"/> so the prior test's deferred
        /// <c>Object.Destroy</c> queue can drain (yielded for one frame) before
        /// the bus sinks are wiped. Resetting synchronously here would race the
        /// destroy queue: the next test's <c>[SetUp]</c> would clear sinks, then
        /// Unity would flush the previous test's destroys, firing
        /// <see cref="MessageAwareComponent.OnDisable"/> against an emptied bus
        /// and surfacing spurious over-deregistration errors attributed to the
        /// current test. If a fixture needs per-test global state, set it
        /// inside the test body or in a derived <c>[SetUp]</c> that runs after
        /// <c>base.Setup()</c>; do not rely on configuration that survives the
        /// reset performed in <c>UnitySetup</c>. The seed log line is emitted
        /// once per fixture from <see cref="LogTestSeedOnce"/> rather than per
        /// test.
        /// </para>
        /// <para>
        /// <c>_numRegistrations</c> defaults to <c>25</c>, the smoke-check
        /// depth used by most fixtures. Fixtures whose <c>Run(...)</c> helper
        /// calls rely on stress fan-out (for example legacy registration
        /// stress) should override <c>Setup</c> and assign
        /// <c>_numRegistrations = StressRegistrations</c> after invoking
        /// <c>base.Setup()</c>.
        /// </para>
        /// </remarks>
        [SetUp]
        public virtual void Setup()
        {
            _random = new Random(TestSeed);

            MessagingDebug.enabled = MessagingDebugEnabled;
            MessagingDebug.LogFunction = (level, message) =>
            {
                switch (level)
                {
                    case LogLevel.Debug:
                    case LogLevel.Info:
                        if (VerboseConsoleLogging)
                        {
                            Debug.Log(message);
                        }
                        return;
                    case LogLevel.Warn:
                        Debug.LogWarning(message);
                        return;
                    case LogLevel.Error:
                        Debug.LogError(message);
                        return;
                }
            };
            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus);
            messageBus.Log.Enabled = true;
            _numRegistrations = 25;

            LogMessageBusStatus();
        }

        private static int ResolveTestSeed()
        {
            string raw = Environment.GetEnvironmentVariable(TestSeedEnvVar);
            if (string.IsNullOrEmpty(raw))
            {
                return DefaultTestSeed;
            }

            if (int.TryParse(raw, out int parsed))
            {
                return parsed;
            }

            if (
                raw.StartsWith("0x", StringComparison.Ordinal)
                || raw.StartsWith("0X", StringComparison.Ordinal)
            )
            {
                string stripped = raw.Substring(2);
                if (
                    int.TryParse(
                        stripped,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out int hexParsed
                    )
                )
                {
                    return hexParsed;
                }
            }

            Debug.LogWarning(
                $"DXMESSAGING_TEST_SEED='{raw}' is not a valid integer or hex value. "
                    + $"Falling back to default seed 0x{DefaultTestSeed:X8}."
            );
            return DefaultTestSeed;
        }

        private static bool ResolveVerboseConsoleLogging()
        {
            string raw = Environment.GetEnvironmentVariable(VerboseLogEnvVar);
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            string normalized = raw.Trim();
            return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        protected void LogMessageBusStatus()
        {
            if (!VerboseConsoleLogging)
            {
                return;
            }

            IMessageBus messageBus = MessageHandler.MessageBus;
            Debug.Log(DescribeMessageBusState(messageBus));
        }

        [TearDown]
        public virtual void Cleanup()
        {
            foreach (GameObject spawned in _spawned)
            {
                if (spawned == null)
                {
                    continue;
                }

                DestroyTrackedObject(spawned);
            }

            _spawned.Clear();
        }

        /// <summary>
        /// Resets DxMessaging static state once per fixture after every test
        /// has run. <c>Cleanup</c> intentionally leaves static state intact so
        /// the cleanup-robustness test can observe it mid-test; this hook
        /// makes sure no debug flags, log functions, or custom buses leak into
        /// fixtures that do not derive from <see cref="MessagingTestBase"/>.
        /// </summary>
        [OneTimeTearDown]
        public virtual void OneTimeCleanup()
        {
            DxMessagingStaticState.Reset();
        }

        [UnityTearDown]
        public IEnumerator UnityCleanup()
        {
            // Mark every tracked object for destruction FIRST, then yield a
            // SINGLE frame. In play mode Object.Destroy is deferred to the
            // end-of-frame flush, and every destroy queued within the same
            // frame flushes together, so one yield drains all callbacks at once
            // -- O(1) per teardown, not the O(n) a yield-per-object loop would
            // cost a 128-component fixture (~128 teardown frames).
            //
            // LIFECYCLE NOTE (verified via the MCP loop on 6000.4; standard
            // Unity Test Framework ordering): in the normal per-test flow the
            // synchronous [TearDown] Cleanup() runs BEFORE this [UnityTearDown]
            // and has already destroyed + cleared _spawned, so this loop
            // normally iterates an EMPTY set and the drain below is skipped
            // (destroyedAny stays false). The deferred destroys Cleanup() queued
            // are flushed by the NEXT test's UnitySetup drain (before its Reset);
            // that is where the real drain-before-the-next-test guarantee lives,
            // not here. This destroy+drain path runs only when UnityCleanup() is
            // invoked DIRECTLY against a populated _spawned
            // (MessagingTestBaseCleanupRobustnessTests' "unity-*" scenarios),
            // which is why it is retained rather than folded into Cleanup().
            bool destroyedAny = false;
            foreach (GameObject spawned in _spawned)
            {
                if (spawned == null)
                {
                    continue;
                }

                DestroyTrackedObject(spawned);
                destroyedAny = true;
            }

            _spawned.Clear();

            if (destroyedAny && Application.isPlaying)
            {
                yield return null;
            }

            // Assert the bus drained fully inside this test, instead of
            // letting a stuck handler bleed into the next test's logs.
            IEnumerator freshHandler = WaitUntilMessageHandlerIsFresh();
            while (freshHandler.MoveNext())
            {
                yield return freshHandler.Current;
            }
        }

        [UnitySetUp]
        public virtual IEnumerator UnitySetup()
        {
            // Drain the prior test's deferred Object.Destroy queue before
            // wiping bus state. Otherwise queued OnDisable callbacks would
            // fire against an emptied bus and log over-deregistration errors
            // against the next test (see ResetState's _resetGeneration guard
            // for the production-side hardening).
            //
            // This single yield is the only per-test frame the harness costs
            // after the domain/scene reload-off win and the batched teardown
            // (the WaitUntil...Fresh polls below are zero-frame on the happy
            // path). It is load-bearing and cannot be removed: many fixtures
            // call Object.Destroy directly in the test body (lifecycle / mutation
            // / base-call tests), so the next test cannot safely Reset() until
            // any such deferred destroy has flushed. Making the harness-tracked
            // teardown synchronous (DestroyImmediate) would not let this drain
            // go away while those direct-Destroy fixtures exist.
            if (Application.isPlaying)
            {
                yield return null;
            }
            DxMessagingStaticState.Reset();
            IEnumerator freshHandler = WaitUntilMessageHandlerIsFresh();
            while (freshHandler.MoveNext())
            {
                yield return freshHandler.Current;
            }
        }

        protected void Run(
            Func<IEnumerable<MessageRegistrationHandle>> register,
            Action emit,
            Action assert,
            Action finalAssert,
            MessageRegistrationToken token,
            bool synchronizeDeregistrations = false
        )
        {
            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                List<List<MessageRegistrationHandle>> indexedRegistrations = new(_numRegistrations);
                for (int i = 0; i < _numRegistrations; ++i)
                {
                    List<MessageRegistrationHandle> registrations = register().ToList();
                    foreach (MessageRegistrationHandle handle in registrations)
                    {
                        handles.Add(handle);
                    }

                    indexedRegistrations.Add(registrations);
                }

                if (synchronizeDeregistrations)
                {
                    foreach (
                        int index in Enumerable
                            .Range(0, indexedRegistrations.Count)
                            .OrderBy(_ => _random.Next())
                    )
                    {
                        emit();
                        assert();
                        foreach (MessageRegistrationHandle handle in indexedRegistrations[index])
                        {
                            handles.Remove(handle);
                            token.RemoveRegistration(handle);
                        }
                    }
                }
                else
                {
                    foreach (
                        MessageRegistrationHandle handle in handles
                            .OrderBy(_ => _random.Next())
                            .ToList()
                    )
                    {
                        emit();
                        assert();
                        handles.Remove(handle);
                        token.RemoveRegistration(handle);
                    }
                }

                emit();
                finalAssert();
                emit();
                finalAssert();
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }
        }

        protected static MessageRegistrationToken GetToken(MessageAwareComponent component)
        {
            return component.Token;
        }

        // NOTE: This polling loop should eventually be replaced by a bus-side
        // version-counter check (Issue 14). Until that lands, callers fall back
        // to the per-frame yield below to detect when the bus has drained.
        protected IEnumerator WaitUntilMessageHandlerIsFresh()
        {
            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus);

            // Deterministic, runner-speed-independent budget: poll a bounded
            // NUMBER of frames rather than a wall-clock interval. The loop
            // exits as soon as state clears (zero-to-one frames on the happy
            // path), so this only bites under extreme load. Frame counting
            // cannot flake on a slow CI runner the way the prior 1.5s
            // wall-clock bound could (see FreshHandlerWaitFrameBudget remarks).
            int frameBudget = FreshHandlerWaitFrameBudget;
            int framesWaited = 0;

            while (IsStale() && framesWaited < frameBudget)
            {
                ++framesWaited;
                yield return null;
            }

            Assert.IsFalse(
                IsStale(),
                "MessageHandler remained stale after polling {0} frames (budget {1}, isPlaying={2}). {3}",
                framesWaited,
                frameBudget,
                Application.isPlaying,
                DescribeMessageBusState(messageBus, includeLog: true)
            );
            yield break;

            bool IsStale()
            {
                return messageBus.RegisteredUntargeted != 0
                    || messageBus.RegisteredTargeted != 0
                    || messageBus.RegisteredBroadcast != 0;
            }
        }

        private static void DestroyTrackedObject(GameObject spawned)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(spawned);
                return;
            }

            Object.DestroyImmediate(spawned);
        }

        protected static string DescribeMessageBusState(
            IMessageBus messageBus,
            bool includeLog = false
        )
        {
            if (messageBus == null)
            {
                return "MessageBus=<null>.";
            }

            string details =
                $"Untargeted={messageBus.RegisteredUntargeted}, "
                + $"Targeted={messageBus.RegisteredTargeted}, "
                + $"Broadcast={messageBus.RegisteredBroadcast}.";
            if (!includeLog)
            {
                return details;
            }

            return $"{details} Registration log: {messageBus.Log}.";
        }
    }
}

#endif
