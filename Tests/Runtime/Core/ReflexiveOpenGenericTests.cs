#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;
    using Object = UnityEngine.Object;

    public sealed class ReflexiveOpenGenericTests : MessagingTestBase
    {
        public enum ReceiverShape
        {
            GenericOnly,
            GenericFirst,
            ConcreteFirst,
            ConcreteOnly,
            GenericParameterUsed,
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool isPublic in new[] { false, true })
            {
                foreach (ReceiverShape shape in Enum.GetValues(typeof(ReceiverShape)))
                {
                    yield return new TestCaseData(isPublic, shape).SetName(
                        $"ReflexiveOpenGeneric_public{isPublic}_{shape}"
                    );
                }
            }
        }

        /// <remarks>
        /// 2026-10-31: independent binders select an open generic definition or report
        /// ambiguous generic/concrete overloads. The actual bus must keep callable
        /// overloads and ignore methods that still need generic type arguments.
        /// </remarks>
        [TestCaseSource(nameof(Cases))]
        public void OnlyCallableReceiverMethodsDeliver(bool isPublic, ReceiverShape shape)
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
                    string method = (isPublic ? "Public" : "Private") + shape;
                    bool expectsDelivery =
                        shape != ReceiverShape.GenericOnly
                        && shape != ReceiverShape.GenericParameterUsed;
                    AssertReceiverMetadata(method, isPublic, shape);
                    for (int cycle = 0; cycle < 10; ++cycle)
                    {
                        bus.ResetState();
                        GameObject host = new("OpenGeneric" + cycle);
                        _spawned.Add(host);
                        ReflexiveOpenGenericReceiver receiver =
                            host.AddComponent<ReflexiveOpenGenericReceiver>();
                        Assert.That(host.GetComponents<MonoBehaviour>(), Has.Length.EqualTo(1));
                        List<string> deliveries = new();
                        bool validArguments = true;
                        receiver.OnMessage = (name, first, second) =>
                        {
                            deliveries.Add(name);
                            validArguments &= first == 7 && second == 11;
                        };
                        InstanceId target = host;
                        ReflexiveMessage message = new(method, ReflexiveSendMode.Flat, 7, 11);
                        long generation = MessageBus.GetResetGeneration(bus);
                        Exception firstError = EmitAndCapture(ref message, target, bus);
                        int firstCache = MessageBusInspection.ReflexiveMethodCacheCount(bus);
                        int firstDeliveries = deliveries.Count;
                        string firstTrace = string.Join(",", deliveries);
                        deliveries.Clear();
                        Exception secondError = EmitAndCapture(ref message, target, bus);
                        int secondCache = MessageBusInspection.ReflexiveMethodCacheCount(bus);
                        int secondDeliveries = deliveries.Count;
                        string secondTrace = string.Join(",", deliveries);
                        deliveries.Clear();
                        ReflexiveMessage recovery = new(
                            nameof(ReflexiveOpenGenericReceiver.ReceiveRecovery),
                            ReflexiveSendMode.Flat,
                            7,
                            11
                        );
                        Exception recoveryError = EmitAndCapture(ref recovery, target, bus);
                        int recoveryCache = MessageBusInspection.ReflexiveMethodCacheCount(bus);
                        int expectedCache = expectsDelivery ? 1 : 0;
                        string expectedTrace = expectsDelivery ? method : string.Empty;
                        bool passed =
                            firstError == null
                            && secondError == null
                            && recoveryError == null
                            && validArguments
                            && receiver.GenericCalls == 0
                            && firstDeliveries == expectedCache
                            && secondDeliveries == expectedCache
                            && firstTrace == expectedTrace
                            && secondTrace == expectedTrace
                            && firstCache == expectedCache
                            && secondCache == expectedCache
                            && recoveryCache == expectedCache + 1
                            && deliveries.Count == 1
                            && deliveries[0] == nameof(ReflexiveOpenGenericReceiver.ReceiveRecovery)
                            && MessageBus.GetResetGeneration(bus) == generation;
                        string observation =
                            $"public={isPublic}; shape={shape}; cycle={cycle}; firstDeliveries={firstDeliveries}; firstTrace={firstTrace}; secondDeliveries={secondDeliveries}; secondTrace={secondTrace}; firstCache={firstCache}; secondCache={secondCache}; recoveryCache={recoveryCache}; genericCalls={receiver.GenericCalls}; validArguments={validArguments}; recoveryDeliveries={deliveries.Count}; generationBefore={generation}; generationAfter={MessageBus.GetResetGeneration(bus)}; firstError={firstError?.GetType().FullName ?? "none"}; secondError={secondError?.GetType().FullName ?? "none"}; recoveryError={recoveryError?.GetType().FullName ?? "none"}; passed={passed}";
                        TestContext.Out.WriteLine("DXM_REFLEXIVE_OPEN_GENERIC " + observation);
                        if (!passed)
                        {
                            failures.Add(observation);
                            if (firstError != null)
                            {
                                failures.Add("Original first exception: " + firstError);
                            }
                            if (secondError != null)
                            {
                                failures.Add("Original second exception: " + secondError);
                            }
                            if (recoveryError != null)
                            {
                                failures.Add("Original recovery exception: " + recoveryError);
                            }
                        }
                    }
                }
                finally
                {
                    bus.ResetState();
                }
                Assert.That(failures, Is.Empty, string.Join("\n", failures));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static void AssertReceiverMetadata(
            string method,
            bool isPublic,
            ReceiverShape shape
        )
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.DeclaredOnly;
            flags |= isPublic ? BindingFlags.Public : BindingFlags.NonPublic;
            int definitions = 0;
            int genericDefinitions = 0;
            int concreteDefinitions = 0;
            foreach (MethodInfo candidate in typeof(ReflexiveOpenGenericReceiver).GetMethods(flags))
            {
                if (candidate.Name != method)
                {
                    continue;
                }
                ++definitions;
                ParameterInfo[] parameters = candidate.GetParameters();
                Assert.That(parameters, Has.Length.EqualTo(2));
                Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(int)));
                if (candidate.IsGenericMethodDefinition)
                {
                    ++genericDefinitions;
                    Assert.That(candidate.ContainsGenericParameters, Is.True);
                    Assert.That(candidate.GetGenericArguments(), Has.Length.EqualTo(1));
                    Assert.That(
                        parameters[0].ParameterType.IsGenericParameter,
                        Is.EqualTo(shape == ReceiverShape.GenericParameterUsed)
                    );
                    if (shape != ReceiverShape.GenericParameterUsed)
                    {
                        Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)));
                    }
                }
                else
                {
                    ++concreteDefinitions;
                    Assert.That(candidate.ContainsGenericParameters, Is.False);
                    Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)));
                }
            }
            bool mixed =
                shape == ReceiverShape.GenericFirst || shape == ReceiverShape.ConcreteFirst;
            int expectedGeneric = shape == ReceiverShape.ConcreteOnly ? 0 : 1;
            int expectedConcrete = mixed || shape == ReceiverShape.ConcreteOnly ? 1 : 0;
            Assert.That(definitions, Is.EqualTo(mixed ? 2 : 1));
            Assert.That(genericDefinitions, Is.EqualTo(expectedGeneric));
            Assert.That(concreteDefinitions, Is.EqualTo(expectedConcrete));
            TestContext.Out.WriteLine(
                $"DXM_REFLEXIVE_OPEN_GENERIC_METADATA public={isPublic}; shape={shape}; definitions={definitions}; genericDefinitions={genericDefinitions}; concreteDefinitions={concreteDefinitions}"
            );
        }

        private static Exception EmitAndCapture(
            ref ReflexiveMessage message,
            InstanceId target,
            MessageBus bus
        )
        {
            try
            {
                message.EmitTargeted(target, bus);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }
    }
}
#endif
