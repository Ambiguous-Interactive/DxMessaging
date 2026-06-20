#if UNITY_2021_3_OR_NEWER
// ReSharper disable AccessToModifiedClosure
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Verifies the snapshot semantics that govern re-entrant emission. The bus uses a
    /// frozen handler list per emission, so additions and deletions made inside a
    /// handler must not affect the in-flight dispatch but must be visible on the next
    /// emission. These tests pin that contract across all three message kinds.
    /// </summary>
    public sealed class ReentrantEmissionTests : MessagingTestBase
    {
        private const int ReentrantSafetyDepth = 5;

        /// <summary>
        /// A handler that re-emits the same message kind must terminate via an
        /// explicit safety counter rather than running forever or until the stack
        /// blows. The recursion is bounded inside the handler itself; this test
        /// pins the deterministic invocation count so any future regression that
        /// changes dispatch ordering, reentrancy guards, or counter semantics
        /// fails loudly with exact numbers rather than a stack overflow.
        /// </summary>
        [Test]
        public void EmitDuringHandlerDoesNotInfinitelyRecurse(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(EmitDuringHandlerDoesNotInfinitelyRecurse) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            const int MaxRecursionDepth = 2;
            int totalInvocations = 0;
            int currentDepth = 0;

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++totalInvocations;
                    if (currentDepth >= MaxRecursionDepth)
                    {
                        return;
                    }

                    ++currentDepth;
                    try
                    {
                        EmitForScenario(scenario, hostId);
                    }
                    finally
                    {
                        --currentDepth;
                    }
                }
            );

            EmitForScenario(scenario, hostId);

            // Outer call increments to 1 then recurses; depth=1 increments to 2 then
            // recurses; depth=2 increments to 3 and stops. After the cascade unwinds
            // the handler is invoked exactly MaxRecursionDepth + 1 times.
            Assert.AreEqual(
                MaxRecursionDepth + 1,
                totalInvocations,
                "[{0}] Bounded recursive emit must invoke the handler exactly {1} times. totalInvocations={2}.",
                scenario.Kind,
                MaxRecursionDepth + 1,
                totalInvocations
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2 * (MaxRecursionDepth + 1),
                totalInvocations,
                "[{0}] A second top-level emission must reproduce the same bounded cascade. expected={1}, totalInvocations={2}.",
                scenario.Kind,
                2 * (MaxRecursionDepth + 1),
                totalInvocations
            );
        }

        [Test]
        public void RecursiveEmitTerminatesViaInterceptor(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RecursiveEmitTerminatesViaInterceptor) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int handlerInvocations = 0;
            int interceptorInvocations = 0;

            RegisterDepthLimitedInterceptor(
                scenario,
                token,
                threshold: ReentrantSafetyDepth,
                getDepth: () => depth,
                onInvoked: () => ++interceptorInvocations
            );

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++handlerInvocations;
                    ++depth;
                    try
                    {
                        EmitForScenario(scenario, hostId);
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );

            EmitForScenario(scenario, hostId);

            // Depth starts at 0. The interceptor approves while depth < threshold,
            // so the handler runs and increments depth on each level; the cascade
            // halts when depth reaches the threshold. Each emission consults the
            // interceptor exactly once per attempted dispatch (one approval per
            // handler invocation plus one final rejection that cancels dispatch).
            Assert.AreEqual(
                ReentrantSafetyDepth,
                handlerInvocations,
                "[{0}] Handler must run exactly threshold ({1}) times before the interceptor cancels dispatch. handlerInvocations={2}, interceptorInvocations={3}.",
                scenario.Kind,
                ReentrantSafetyDepth,
                handlerInvocations,
                interceptorInvocations
            );
            Assert.AreEqual(
                ReentrantSafetyDepth + 1,
                interceptorInvocations,
                "[{0}] Interceptor must run once per handler invocation plus once for the cancelling level. expected={1}, handlerInvocations={2}, interceptorInvocations={3}.",
                scenario.Kind,
                ReentrantSafetyDepth + 1,
                handlerInvocations,
                interceptorInvocations
            );
        }

        [Test]
        public void RegisterDuringEmitIsDeferredToNextEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RegisterDuringEmitIsDeferredToNextEmission) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++primaryCount;
                    secondaryHandle ??= RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        priority: 0,
                        onInvoked: () => ++secondaryCount
                    );
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                primaryCount,
                "[{0}] First emission must invoke primary exactly once. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );
            Assert.AreEqual(
                0,
                secondaryCount,
                "[{0}] New registration must not run during its own emission. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                primaryCount,
                "[{0}] Second emission must increment primary to 2. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );
            Assert.AreEqual(
                1,
                secondaryCount,
                "[{0}] New registration must be visible to the next emission. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                3,
                primaryCount,
                "[{0}] Third emission must increment primary to 3. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );
            Assert.AreEqual(
                2,
                secondaryCount,
                "[{0}] Third emission must increment secondary to 2. primaryCount={1}, secondaryCount={2}.",
                scenario.Kind,
                primaryCount,
                secondaryCount
            );

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }

        [Test]
        public void DeregisterDuringEmitIsHonouredOnCurrentSnapshot(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeregisterDuringEmitIsHonouredOnCurrentSnapshot) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int firstCount = 0;
            int secondCount = 0;
            MessageRegistrationHandle secondHandle = default;

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++firstCount;
                    if (secondHandle != default)
                    {
                        token.RemoveRegistration(secondHandle);
                        secondHandle = default;
                    }
                }
            );

            secondHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 1,
                onInvoked: () => ++secondCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                firstCount,
                "[{0}] First emission must invoke primary exactly once. firstCount={1}, secondCount={2}.",
                scenario.Kind,
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "[{0}] Snapshot frozen at emission start must invoke handler scheduled for removal. firstCount={1}, secondCount={2}.",
                scenario.Kind,
                firstCount,
                secondCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                firstCount,
                "[{0}] Second emission must invoke primary again. firstCount={1}, secondCount={2}.",
                scenario.Kind,
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "[{0}] Removed handler must not run on the next emission once snapshot is rebuilt. firstCount={1}, secondCount={2}.",
                scenario.Kind,
                firstCount,
                secondCount
            );
        }

        [Test]
        public void MultipleNestedEmissionsRespectSnapshotIsolation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(MultipleNestedEmissionsRespectSnapshotIsolation) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            const int MaxDepth = 3;
            int depth = 0;
            int primaryInvocations = 0;
            int latecomerInvocations = 0;
            bool latecomerRegistered = false;

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++primaryInvocations;
                    if (depth >= MaxDepth)
                    {
                        return;
                    }

                    ++depth;
                    try
                    {
                        EmitForScenario(scenario, hostId);
                        if (!latecomerRegistered)
                        {
                            latecomerRegistered = true;
                            _ = RegisterCountingHandler(
                                scenario,
                                token,
                                hostId,
                                priority: 0,
                                onInvoked: () => ++latecomerInvocations
                            );
                        }
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                MaxDepth + 1,
                primaryInvocations,
                "[{0}] Nested emissions must each invoke the primary handler once. expected={1}, primaryInvocations={2}, latecomerInvocations={3}.",
                scenario.Kind,
                MaxDepth + 1,
                primaryInvocations,
                latecomerInvocations
            );
            Assert.AreEqual(
                0,
                latecomerInvocations,
                "[{0}] Latecomer registered mid-dispatch must not appear in any in-flight snapshot. primaryInvocations={1}, latecomerInvocations={2}.",
                scenario.Kind,
                primaryInvocations,
                latecomerInvocations
            );

            EmitForScenario(scenario, hostId);
            Assert.GreaterOrEqual(
                latecomerInvocations,
                1,
                "[{0}] Latecomer must be visible to subsequent emissions after the recursive cascade settles. primaryInvocations={1}, latecomerInvocations={2}.",
                scenario.Kind,
                primaryInvocations,
                latecomerInvocations
            );
        }

        /// <summary>
        /// Same-priority deregistration during emission must respect the snapshot
        /// frozen at the start of dispatch. Handler-A and handler-B share priority
        /// 0; A removes B during its own callback. B must still run on the current
        /// emission (snapshot semantics) but must NOT run on the second emission.
        /// </summary>
        [Test]
        public void DeregisterSamePriorityDuringEmitIsHonouredOnCurrentSnapshot(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeregisterSamePriorityDuringEmitIsHonouredOnCurrentSnapshot) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            int bCount = 0;
            MessageRegistrationHandle bHandle = default;

            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    if (bHandle != default)
                    {
                        token.RemoveRegistration(bHandle);
                        bHandle = default;
                    }
                }
            );

            bHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++bCount
            );
            MessageRegistrationHandle bHandleSnapshot = bHandle;

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] First emission must invoke A exactly once. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] Same-priority handler scheduled for removal must still run on the current snapshot. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                aCount,
                "[{0}] Second emission must invoke A again. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] B must NOT run on the second emission once removed. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            token.RemoveRegistration(aHandle);
            if (bHandle != default)
            {
                token.RemoveRegistration(bHandle);
            }
            // Reference snapshot to suppress unused-variable analyzer noise across
            // future refactors. The remove above already used the live handle.
            _ = bHandleSnapshot;
        }

        /// <summary>
        /// Removing multiple handlers across distinct priority buckets during
        /// emission must respect the snapshot for the current dispatch and only
        /// take effect on the next emission. Handler-A at priority 0 removes
        /// handler-B (priority 1) and handler-D (priority 3); handler-C
        /// (priority 2) is untouched. All four must run on the first emission;
        /// only A and C must run on the second.
        /// </summary>
        [Test]
        public void DeregisterMultipleHandlersDuringEmitAcrossPriorities(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeregisterMultipleHandlersDuringEmitAcrossPriorities) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            int bCount = 0;
            int cCount = 0;
            int dCount = 0;
            MessageRegistrationHandle bHandle = default;
            MessageRegistrationHandle dHandle = default;

            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    if (bHandle != default)
                    {
                        token.RemoveRegistration(bHandle);
                        bHandle = default;
                    }
                    if (dHandle != default)
                    {
                        token.RemoveRegistration(dHandle);
                        dHandle = default;
                    }
                }
            );
            bHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 1,
                onInvoked: () => ++bCount
            );
            MessageRegistrationHandle cHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 2,
                onInvoked: () => ++cCount
            );
            dHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 3,
                onInvoked: () => ++dCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] First emission must run A once. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] First emission snapshot must still invoke B even though A removed it. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                1,
                cCount,
                "[{0}] First emission must run untouched C. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                1,
                dCount,
                "[{0}] First emission snapshot must still invoke D even though A removed it. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                aCount,
                "[{0}] Second emission must run A again. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] B must NOT run on the second emission once removed. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                2,
                cCount,
                "[{0}] C must run on the second emission. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );
            Assert.AreEqual(
                1,
                dCount,
                "[{0}] D must NOT run on the second emission once removed. aCount={1}, bCount={2}, cCount={3}, dCount={4}.",
                scenario.Kind,
                aCount,
                bCount,
                cCount,
                dCount
            );

            token.RemoveRegistration(aHandle);
            token.RemoveRegistration(cHandle);
            if (bHandle != default)
            {
                token.RemoveRegistration(bHandle);
            }
            if (dHandle != default)
            {
                token.RemoveRegistration(dHandle);
            }
        }

        /// <summary>
        /// Mixed register-and-deregister-during-emit must respect both halves of
        /// the snapshot contract. Handler-A at priority 0 registers a new
        /// handler-X at priority 1 AND removes existing handler-B at priority 2.
        /// First emission: A and B run, X does NOT (registered after snapshot).
        /// Second emission: A and X run, B does NOT (removed before snapshot).
        /// </summary>
        [Test]
        public void RegisterAndDeregisterDuringEmitInteractsCorrectly(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RegisterAndDeregisterDuringEmitInteractsCorrectly) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            int bCount = 0;
            int xCount = 0;
            MessageRegistrationHandle bHandle = default;
            MessageRegistrationHandle? xHandle = null;

            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    xHandle ??= RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        priority: 1,
                        onInvoked: () => ++xCount
                    );
                    if (bHandle != default)
                    {
                        token.RemoveRegistration(bHandle);
                        bHandle = default;
                    }
                }
            );
            bHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 2,
                onInvoked: () => ++bCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] First emission must run A once. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] First emission snapshot must still invoke B even though A removed it. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );
            Assert.AreEqual(
                0,
                xCount,
                "[{0}] X registered during dispatch must NOT run on the same emission. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                aCount,
                "[{0}] Second emission must run A again. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] B must NOT run on the second emission once removed. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );
            Assert.AreEqual(
                1,
                xCount,
                "[{0}] X must run on the second emission once visible to the snapshot. aCount={1}, bCount={2}, xCount={3}.",
                scenario.Kind,
                aCount,
                bCount,
                xCount
            );

            token.RemoveRegistration(aHandle);
            if (bHandle != default)
            {
                token.RemoveRegistration(bHandle);
            }
            if (xHandle.HasValue)
            {
                token.RemoveRegistration(xHandle.Value);
            }
        }

        /// <summary>
        /// Cross-MessageHandler same-priority deregister-during-emit. Two distinct
        /// components host one handler each at the same priority; handler-A on
        /// component-1 removes handler-B on component-2 during dispatch. The
        /// snapshot contract requires both handlers to run on the current emission,
        /// then only A on the next. This locks the contract for the
        /// "single bucket, multi-MessageHandler" case, which historically
        /// required a snapshot-level prefreeze pass even with a single
        /// priority bucket (the flattened dispatch enforces it by
        /// construction: the resolved frozen array cannot observe the
        /// mid-emission removal).
        /// </summary>
        [Test]
        public void DeregisterCrossMessageHandlerSamePriorityIsHonouredOnCurrentSnapshot(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject hostA = new(
                nameof(DeregisterCrossMessageHandlerSamePriorityIsHonouredOnCurrentSnapshot)
                    + "A"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostA);
            GameObject hostB = new(
                nameof(DeregisterCrossMessageHandlerSamePriorityIsHonouredOnCurrentSnapshot)
                    + "B"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostB);

            EmptyMessageAwareComponent componentA =
                hostA.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent componentB =
                hostB.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken tokenA = GetToken(componentA);
            MessageRegistrationToken tokenB = GetToken(componentB);

            // Targeted/broadcast use a single shared instance id so both handlers
            // dispatch in the same emission; untargeted ignores the id parameter.
            InstanceId sharedId = hostA;

            int aCount = 0;
            int bCount = 0;
            MessageRegistrationHandle bHandle = default;

            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                tokenA,
                sharedId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    if (bHandle != default)
                    {
                        tokenB.RemoveRegistration(bHandle);
                        bHandle = default;
                    }
                }
            );

            bHandle = RegisterCountingHandler(
                scenario,
                tokenB,
                sharedId,
                priority: 0,
                onInvoked: () => ++bCount
            );

            EmitForScenario(scenario, sharedId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] First emission must invoke A on its own MessageHandler. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] First emission snapshot must still invoke B on its own MessageHandler even though A removed it. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            EmitForScenario(scenario, sharedId);
            Assert.AreEqual(
                2,
                aCount,
                "[{0}] Second emission must invoke A again. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                bCount,
                "[{0}] B must NOT run on the second emission once removed. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            tokenA.RemoveRegistration(aHandle);
            if (bHandle != default)
            {
                tokenB.RemoveRegistration(bHandle);
            }
        }

        /// <summary>
        /// A handler that removes ITSELF mid-callback must still complete the
        /// in-flight invocation (snapshot semantics) and must not run on the next
        /// emission. This pins the corner case where the deregistration closure
        /// mutates the same HandlerActionCache the in-flight dispatch is iterating.
        /// </summary>
        [Test]
        public void HandlerRemovingItselfDuringEmitIsHonouredOnCurrentSnapshot(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerRemovingItselfDuringEmitIsHonouredOnCurrentSnapshot) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            MessageRegistrationHandle aHandle = default;

            aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    if (aHandle != default)
                    {
                        token.RemoveRegistration(aHandle);
                        aHandle = default;
                    }
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] Self-removing handler must complete its in-flight invocation. aCount={1}.",
                scenario.Kind,
                aCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] Self-removed handler must NOT run on the next emission. aCount={1}.",
                scenario.Kind,
                aCount
            );
        }

        /// <summary>
        /// A handler that removes another handler and THEN throws must still apply
        /// the deregistration to subsequent emissions, even though the bus does not
        /// swallow exceptions and aborts the current dispatch. This pins that the
        /// snapshot bookkeeping is durable across exceptional control flow: the
        /// frozen-snapshot list is unaffected by the throw (no rollback), and the
        /// removal that A performed before throwing takes effect on the next emit.
        /// Pairs with <c>HandlerThrowPreventsSubsequentHandlers</c> in
        /// <c>HandlerExceptionTests</c> which pins the propagate-don't-swallow
        /// contract for the same dispatch.
        /// </summary>
        [Test]
        public void HandlerThrowAfterDeregistrationStillPropagatesRemovalToNextEmit(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerThrowAfterDeregistrationStillPropagatesRemovalToNextEmit)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int aCount = 0;
            int bCount = 0;
            const string ThrowMessage = "DxMessaging-test-throw-after-deregister";
            MessageRegistrationHandle bHandle = default;

            MessageRegistrationHandle aHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++aCount;
                    if (bHandle != default)
                    {
                        token.RemoveRegistration(bHandle);
                        bHandle = default;
                    }
                    throw new InvalidOperationException(ThrowMessage);
                }
            );
            bHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 1,
                onInvoked: () => ++bCount
            );

            // First emit: A runs, removes B, then throws. The exception propagates;
            // B does NOT fire on this emission per the bus's "propagate don't swallow"
            // contract (the snapshot has B but dispatch never reaches it). Although
            // A removed B before throwing, the snapshot was already frozen; but the
            // dispatch loop bails out of the bucket walk after A's throw.
            InvalidOperationException firstThrow = Assert.Throws<InvalidOperationException>(() =>
                EmitForScenario(scenario, hostId)
            );
            Assert.AreEqual(
                ThrowMessage,
                firstThrow.Message,
                "[{0}] First emit must propagate A's exception. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                1,
                aCount,
                "[{0}] A must have run exactly once before throwing. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                0,
                bCount,
                "[{0}] B must NOT run on first emit; propagation aborts the bucket walk. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            // Second emit: A still registered (its registration wasn't unwound by
            // the throw), so it throws again. B's removal from the prior emit took
            // effect; B is no longer in the snapshot, so bCount stays at 0.
            InvalidOperationException secondThrow = Assert.Throws<InvalidOperationException>(() =>
                EmitForScenario(scenario, hostId)
            );
            Assert.AreEqual(
                ThrowMessage,
                secondThrow.Message,
                "[{0}] Second emit must also propagate A's exception. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                2,
                aCount,
                "[{0}] A must run on second emit (still registered). aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );
            Assert.AreEqual(
                0,
                bCount,
                "[{0}] B must remain removed despite the prior exception. aCount={1}, bCount={2}.",
                scenario.Kind,
                aCount,
                bCount
            );

            token.RemoveRegistration(aHandle);
            if (bHandle != default)
            {
                token.RemoveRegistration(bHandle);
            }
        }

        /// <summary>
        /// An interceptor that deregisters a handler causes that handler to be
        /// skipped on the IN-FLIGHT emission. This is intentional and follows
        /// directly from the dispatch order: interceptors run BEFORE the
        /// dispatch snapshot is acquired (see <c>UntargetedBroadcast</c>,
        /// <c>TargetedBroadcast</c>, <c>SourcedBroadcast</c>), so any
        /// registration mutation an interceptor performs is observable to the
        /// dispatch path on the same emit. This test pins that contract so
        /// future refactors that move snapshot acquisition above the
        /// interceptor pass fail loudly. (Contrast with
        /// <see cref="DeregisterDuringEmitIsHonouredOnCurrentSnapshot"/>, where
        /// the deregistration is performed by a peer HANDLER; handlers run
        /// after the snapshot is frozen, so the snapshot still dispatches the
        /// removed peer.)
        /// </summary>
        [Test]
        public void DeregisterFromInterceptorIsObservedOnCurrentEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeregisterFromInterceptorIsObservedOnCurrentEmission) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int interceptorCount = 0;
            int handlerCount = 0;
            MessageRegistrationHandle handlerHandle = default;

            handlerHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++handlerCount
            );

            // Interceptor returns true (allows dispatch to proceed) but removes
            // the handler before dispatch reads its snapshot. Because
            // interceptors run before the snapshot is acquired, the handler is
            // already gone by the time dispatch builds the bucket array.
            RegisterRemovingInterceptor(
                scenario,
                token,
                () =>
                {
                    ++interceptorCount;
                    if (handlerHandle != default)
                    {
                        token.RemoveRegistration(handlerHandle);
                        handlerHandle = default;
                    }
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                interceptorCount,
                "[{0}] Interceptor must run once on first emit. interceptorCount={1}, handlerCount={2}.",
                scenario.Kind,
                interceptorCount,
                handlerCount
            );
            Assert.AreEqual(
                0,
                handlerCount,
                "[{0}] Handler removed by interceptor must NOT run on the same emission "
                    + "(interceptors precede snapshot acquisition). interceptorCount={1}, handlerCount={2}.",
                scenario.Kind,
                interceptorCount,
                handlerCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                interceptorCount,
                "[{0}] Interceptor must run again on second emit. interceptorCount={1}, handlerCount={2}.",
                scenario.Kind,
                interceptorCount,
                handlerCount
            );
            Assert.AreEqual(
                0,
                handlerCount,
                "[{0}] Removed handler must remain absent on subsequent emissions. interceptorCount={1}, handlerCount={2}.",
                scenario.Kind,
                interceptorCount,
                handlerCount
            );
        }

        private static void RegisterRemovingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                case MessageKind.Targeted:
                {
                    _ = ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                case MessageKind.Broadcast:
                {
                    _ = ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            int priority,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void RegisterDepthLimitedInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            int threshold,
            Func<int> getDepth,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                case MessageKind.Targeted:
                {
                    _ = ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                case MessageKind.Broadcast:
                {
                    _ = ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void EmitForScenario(MessageScenario scenario, InstanceId target)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, target);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, target);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}
#endif
