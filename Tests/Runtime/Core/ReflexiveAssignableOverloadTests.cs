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

    public sealed class ReflexiveAssignableOverloadTests : MessagingTestBase
    {
        public enum ReceiverShape
        {
            Object,
            Interface,
            ObjectControl,
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool isPublic in new[] { false, true })
            {
                foreach (ReceiverShape shape in Enum.GetValues(typeof(ReceiverShape)))
                {
                    yield return new TestCaseData(isPublic, shape).SetName(
                        $"ReflexiveAssignableOverload_public{isPublic}_{shape}"
                    );
                }
            }
        }

        /// <remarks>
        /// 2026-10-31: a generic exact signature must not hide a callable concrete
        /// object or interface overload. Ordinary C# calls select the concrete
        /// methods without inventing unused generic type arguments.
        /// </remarks>
        [TestCaseSource(nameof(Cases))]
        public void AssignableConcreteReceiverMethodsDeliver(bool isPublic, ReceiverShape shape)
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
                    AssertReceiverMetadata(method, isPublic, shape);
                    for (int cycle = 0; cycle < 10; ++cycle)
                    {
                        bus.ResetState();
                        GameObject host = new("AssignableOverload" + cycle);
                        _spawned.Add(host);
                        ReflexiveAssignableOverloadReceiver receiver =
                            host.AddComponent<ReflexiveAssignableOverloadReceiver>();
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
                            nameof(ReflexiveAssignableOverloadReceiver.ReceiveRecovery),
                            ReflexiveSendMode.Flat,
                            7,
                            11
                        );
                        Exception recoveryError = EmitAndCapture(ref recovery, target, bus);
                        int recoveryCache = MessageBusInspection.ReflexiveMethodCacheCount(bus);
                        int expectedCache = 1;
                        string expectedTrace = method;
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
                            && deliveries[0]
                                == nameof(ReflexiveAssignableOverloadReceiver.ReceiveRecovery)
                            && MessageBus.GetResetGeneration(bus) == generation;
                        string observation =
                            $"public={isPublic}; shape={shape}; cycle={cycle}; firstDeliveries={firstDeliveries}; firstTrace={firstTrace}; secondDeliveries={secondDeliveries}; secondTrace={secondTrace}; firstCache={firstCache}; secondCache={secondCache}; recoveryCache={recoveryCache}; genericCalls={receiver.GenericCalls}; validArguments={validArguments}; recoveryDeliveries={deliveries.Count}; generationBefore={generation}; generationAfter={MessageBus.GetResetGeneration(bus)}; firstError={firstError?.GetType().FullName ?? "none"}; secondError={secondError?.GetType().FullName ?? "none"}; recoveryError={recoveryError?.GetType().FullName ?? "none"}; passed={passed}";
                        TestContext.Out.WriteLine("DXM_REFLEXIVE_ASSIGNABLE " + observation);
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
            foreach (
                MethodInfo candidate in typeof(ReflexiveAssignableOverloadReceiver).GetMethods(
                    flags
                )
            )
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
                    Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)));
                }
                else
                {
                    ++concreteDefinitions;
                    Assert.That(candidate.ContainsGenericParameters, Is.False);
                    Type expected =
                        shape == ReceiverShape.Interface ? typeof(IConvertible) : typeof(object);
                    Assert.That(parameters[0].ParameterType, Is.EqualTo(expected));
                }
            }
            bool mixed = shape != ReceiverShape.ObjectControl;
            Assert.That(definitions, Is.EqualTo(mixed ? 2 : 1));
            Assert.That(genericDefinitions, Is.EqualTo(mixed ? 1 : 0));
            Assert.That(concreteDefinitions, Is.EqualTo(1));
            TestContext.Out.WriteLine(
                $"DXM_REFLEXIVE_ASSIGNABLE_METADATA public={isPublic}; shape={shape}; definitions={definitions}; genericDefinitions={genericDefinitions}; concreteDefinitions={concreteDefinitions}"
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
