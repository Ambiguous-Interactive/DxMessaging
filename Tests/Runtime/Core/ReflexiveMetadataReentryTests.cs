#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;
    using Object = UnityEngine.Object;

    public sealed class ReflexiveMetadataReentryTests : MessagingTestBase
    {
        public enum ReentryAction
        {
            Observe,
            SameMessage,
            FreshSameSignature,
            DifferentMethod,
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool warm in new[] { false, true })
            {
                foreach (int slot in new[] { 0, 1 })
                {
                    foreach (ReentryAction action in Enum.GetValues(typeof(ReentryAction)))
                    {
                        yield return new TestCaseData(warm, slot, action).SetName(
                            $"ReflexiveReentry_warm{warm}_slot{slot}_{action}"
                        );
                    }
                }
            }
        }

        /// <remarks>
        /// 2026-10-30: test same-generation cache publication after a real metadata getter
        /// recursively emits. Retain every complete cycle before reporting failures.
        /// </remarks>
        [TestCaseSource(nameof(Cases))]
        public void MetadataReentryPreservesBothDeliveries(
            bool warmCache,
            int metadataSlot,
            ReentryAction action
        )
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            try
            {
                settings._bufferMaxDistinctEntries = 4;
                using IDisposable settingsOverride = RuntimeSettingsFixture.Override(settings);
                MessageBus bus = new();
                List<string> failures = new();
                try
                {
                    for (int cycle = 0; cycle < 10; ++cycle)
                    {
                        bus.ResetState();
                        GameObject host = new(
                            "ReflexiveReentry" + cycle,
                            typeof(ReflexiveReentryReceiver)
                        );
                        _spawned.Add(host);
                        ReflexiveReentryReceiver receiver =
                            host.GetComponent<ReflexiveReentryReceiver>();
                        Assert.That(host.GetComponents<MonoBehaviour>(), Has.Length.EqualTo(1));
                        List<string> deliveries = new();
                        bool validArguments = true;
                        receiver.OnMessage = (method, first, second) =>
                        {
                            deliveries.Add(method);
                            validArguments &= first == 1 && second == 2;
                        };
                        InstanceId target = host;
                        ReflexiveMessage message = default;
                        Type[] types = { typeof(int), typeof(int) };
                        bool armed = false;
                        int callbacks = 0;
                        int nestedReturns = 0;
                        MetadataCallbackType metadata = new(
                            typeof(int),
                            () =>
                            {
                                if (!armed)
                                {
                                    return;
                                }
                                armed = false;
                                ++callbacks;
                                if (action == ReentryAction.SameMessage)
                                {
                                    message.EmitTargeted(target, bus);
                                    ++nestedReturns;
                                }
                                else if (action != ReentryAction.Observe)
                                {
                                    ReflexiveMessage nested = new(
                                        action == ReentryAction.DifferentMethod
                                            ? nameof(ReflexiveReentryReceiver.ReceiveOther)
                                            : nameof(ReflexiveReentryReceiver.Receive),
                                        ReflexiveSendMode.Flat,
                                        new object[] { 1, 2 },
                                        types
                                    );
                                    nested.EmitTargeted(target, bus);
                                    ++nestedReturns;
                                }
                            }
                        );
                        types[metadataSlot] = metadata;
                        message = new ReflexiveMessage(
                            nameof(ReflexiveReentryReceiver.Receive),
                            ReflexiveSendMode.Flat,
                            new object[] { 1, 2 },
                            types
                        );
                        Assert.That(
                            metadata.Reads,
                            Is.Zero,
                            "Construction must not invoke metadata."
                        );
                        Assert.That(callbacks, Is.Zero);
                        if (warmCache)
                        {
                            message.EmitTargeted(target, bus);
                            Assert.That(
                                deliveries,
                                Is.EqualTo(new[] { nameof(ReflexiveReentryReceiver.Receive) })
                            );
                            Assert.That(metadata.Reads, Is.GreaterThan(0));
                        }
                        Assert.That(
                            MessageBusInspection.ReflexiveMethodCacheCount(bus),
                            Is.EqualTo(warmCache ? 1 : 0)
                        );
                        deliveries.Clear();
                        int readsBefore = metadata.Reads;
                        long generationBefore = MessageBus.GetResetGeneration(bus);
                        armed = true;
                        Exception error = null;
                        try
                        {
                            message.EmitTargeted(target, bus);
                        }
                        catch (Exception exception)
                        {
                            error = exception;
                        }
                        armed = false;
                        long generationAfter = MessageBus.GetResetGeneration(bus);
                        int reads = metadata.Reads - readsBefore;
                        int firstDeliveries = deliveries.Count;
                        string firstTrace = string.Join(",", deliveries);
                        int firstCacheCount = MessageBusInspection.ReflexiveMethodCacheCount(bus);
                        deliveries.Clear();
                        Exception recoveryError = null;
                        try
                        {
                            message.EmitTargeted(target, bus);
                        }
                        catch (Exception exception)
                        {
                            recoveryError = exception;
                        }
                        int nextDeliveries = deliveries.Count;
                        bool nestedExpected = !warmCache && action != ReentryAction.Observe;
                        int expectedDeliveries = nestedExpected ? 2 : 1;
                        int expectedCacheCount =
                            !warmCache && action == ReentryAction.DifferentMethod ? 2 : 1;
                        string expectedTrace = nestedExpected
                            ? (
                                action == ReentryAction.DifferentMethod
                                    ? nameof(ReflexiveReentryReceiver.ReceiveOther)
                                    : nameof(ReflexiveReentryReceiver.Receive)
                            )
                                + ","
                                + nameof(ReflexiveReentryReceiver.Receive)
                            : nameof(ReflexiveReentryReceiver.Receive);
                        bool passed =
                            error == null
                            && recoveryError == null
                            && callbacks == (warmCache ? 0 : 1)
                            && nestedReturns == (nestedExpected ? 1 : 0)
                            && (warmCache ? reads == 0 : 0 < reads)
                            && generationAfter == generationBefore
                            && validArguments
                            && firstDeliveries == expectedDeliveries
                            && firstTrace == expectedTrace
                            && firstCacheCount == expectedCacheCount
                            && nextDeliveries == 1;
                        string observation =
                            $"warmCache={warmCache}; slot={metadataSlot}; action={action}; cycle={cycle}; callbacks={callbacks}; nestedReturns={nestedReturns}; metadataReads={reads}; firstDeliveries={firstDeliveries}; expectedDeliveries={expectedDeliveries}; firstTrace={firstTrace}; firstCacheCount={firstCacheCount}; expectedCacheCount={expectedCacheCount}; nextDeliveries={nextDeliveries}; generationBefore={generationBefore}; generationAfter={generationAfter}; validArguments={validArguments}; error={error?.GetType().FullName ?? "none"}; recoveryError={recoveryError?.GetType().FullName ?? "none"}; passed={passed}";
                        TestContext.Out.WriteLine("DXM_REFLEXIVE_REENTRY " + observation);
                        if (!passed)
                        {
                            failures.Add(
                                observation
                                    + "; exception="
                                    + error
                                    + "; recoveryException="
                                    + recoveryError
                            );
                        }
                    }
                }
                finally
                {
                    bus.ResetState();
                }
                Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private sealed class MetadataCallbackType : System.Reflection.TypeDelegator
        {
            private readonly Type _represented;
            private readonly Action _callback;
            internal int Reads { get; private set; }

            internal MetadataCallbackType(Type represented, Action callback)
                : base(represented)
            {
                _represented = represented;
                _callback = callback;
            }

            public override Type UnderlyingSystemType
            {
                get
                {
                    ++Reads;
                    _callback();
                    return _represented.UnderlyingSystemType;
                }
            }

            public override int GetHashCode() => _represented.GetHashCode();
        }
    }
}
#endif
