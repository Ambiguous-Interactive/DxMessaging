#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Benchmarks
{
    using System;
    using System.Diagnostics;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools.Constraints;
    using Is = NUnit.Framework.Is;

    [Category("Performance")]
    public sealed class PerformanceTests : BenchmarkTestBase
    {
        protected override bool MessagingDebugEnabled => false;

        [Test]
        public void Benchmark()
        {
            string operatingSystemSection = BenchmarkDocumentation.GetOperatingSystemSection();
            BenchmarkSession session = new(operatingSystemSection, "## ");

            RunWithSession(
                session,
                () =>
                {
                    TimeSpan timeout = TimeSpan.FromSeconds(5);
                    Stopwatch timer = Stopwatch.StartNew();

                    ComplexTargetedMessage message = new(Guid.NewGuid());
                    ReflexiveMessage reflexiveMessage = new(
                        nameof(SimpleMessageAwareComponent.HandleSlowComplexTargetedMessage),
                        ReflexiveSendMode.Flat,
                        message
                    );

                    RunWithComponent(component =>
                        Unity(timer, timeout, component.gameObject, message)
                    );

                    RunWithComponent(
                        (component, token) =>
                            NormalGameObject(timer, timeout, component, token, message)
                    );
                    RunWithComponent(
                        (component, token) =>
                            NormalComponent(timer, timeout, component, token, message)
                    );
                    RunWithComponent(
                        (component, token) =>
                            NoCopyGameObject(timer, timeout, component, token, message)
                    );
                    RunWithComponent(
                        (component, token) =>
                            NoCopyComponent(timer, timeout, component, token, message)
                    );

                    SimpleUntargetedMessage untargetedMessage = new();
                    RunWithComponent(
                        (component, token) =>
                            NoCopyUntargeted(timer, timeout, component, token, untargetedMessage)
                    );
                    RunWithComponent(
                        (component, token) =>
                            InterceptorHeavyUntargeted(
                                timer,
                                timeout,
                                component,
                                token,
                                untargetedMessage
                            )
                    );
                    RunWithComponent(
                        (component, token) =>
                            PostProcessorHeavyUntargeted(
                                timer,
                                timeout,
                                component,
                                token,
                                untargetedMessage
                            )
                    );
                    RunWithComponent(component =>
                        ReflexiveOneArgument(timer, timeout, component.gameObject, reflexiveMessage)
                    );
                    RunWithComponent(component =>
                        ReflexiveTwoArguments(timer, timeout, component.gameObject)
                    );
                    RunWithComponent(component =>
                        ReflexiveThreeArguments(timer, timeout, component.gameObject)
                    );
                }
            );
        }

        private void DisplayCount(string testName, int count, TimeSpan duration, bool allocating)
        {
            RecordBenchmark(testName, count, duration, allocating);
        }

        private void Unity(
            Stopwatch timer,
            TimeSpan timeout,
            GameObject target,
            ComplexTargetedMessage message
        )
        {
            int count = 0;
            if (!target.TryGetComponent(out SimpleMessageAwareComponent component))
            {
                component = target.AddComponent<SimpleMessageAwareComponent>();
            }

            // RunWithComponent prepared existing behaviours; only the newly added receiver needs setup.
            PrepareBenchmarkBehaviourForSendMessage(component);

            component.slowComplexTargetedHandler = () => ++count;
            // Pre-warm
            target.SendMessage(
                nameof(SimpleMessageAwareComponent.HandleSlowComplexTargetedMessage),
                message
            );

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    target.SendMessage(
                        nameof(SimpleMessageAwareComponent.HandleSlowComplexTargetedMessage),
                        message
                    );
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(
                    () =>
                        target.SendMessage(
                            nameof(SimpleMessageAwareComponent.HandleSlowComplexTargetedMessage),
                            message
                        ),
                    Is.Not.AllocatingGCMemory()
                );
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Unity benchmark should invoke handlers.");
            DisplayCount("Unity", count, timer.Elapsed, allocating);
        }

        private void ReflexiveThreeArguments(Stopwatch timer, TimeSpan timeout, GameObject go)
        {
            int count = 0;
            if (!go.TryGetComponent(out SimpleMessageAwareComponent component))
            {
                component = go.AddComponent<SimpleMessageAwareComponent>();
            }

            // RunWithComponent prepared existing behaviours; only the newly added receiver needs setup.
            PrepareBenchmarkBehaviourForSendMessage(component);

            component.reflexiveThreeArgumentHandler = () => ++count;
            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageThreeArguments),
                ReflexiveSendMode.Flat,
                1,
                2,
                3
            );
            InstanceId target = go;
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Reflexive three-argument benchmark should invoke handlers.");
            DisplayCount("Reflexive (Three Arguments)", count, timer.Elapsed, allocating);
        }

        private void ReflexiveTwoArguments(Stopwatch timer, TimeSpan timeout, GameObject go)
        {
            int count = 0;
            if (!go.TryGetComponent(out SimpleMessageAwareComponent component))
            {
                component = go.AddComponent<SimpleMessageAwareComponent>();
            }

            // RunWithComponent prepared existing behaviours; only the newly added receiver needs setup.
            PrepareBenchmarkBehaviourForSendMessage(component);

            component.reflexiveTwoArgumentHandler = () => ++count;
            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );
            InstanceId target = go;
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Reflexive two-argument benchmark should invoke handlers.");
            DisplayCount("Reflexive (Two Arguments)", count, timer.Elapsed, allocating);
        }

        private void ReflexiveOneArgument(
            Stopwatch timer,
            TimeSpan timeout,
            GameObject go,
            ReflexiveMessage message
        )
        {
            int count = 0;
            if (!go.TryGetComponent(out SimpleMessageAwareComponent component))
            {
                component = go.AddComponent<SimpleMessageAwareComponent>();
            }

            // RunWithComponent prepared existing behaviours; only the newly added receiver needs setup.
            PrepareBenchmarkBehaviourForSendMessage(component);

            component.slowComplexTargetedHandler = () => ++count;
            InstanceId target = go;
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Reflexive one-argument benchmark should invoke handlers.");
            DisplayCount("Reflexive (One Argument)", count, timer.Elapsed, allocating);
        }

        private void NormalGameObject(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            ComplexTargetedMessage message
        )
        {
            int count = 0;

            GameObject go = component.gameObject;
            InstanceId target = go;
            token.RegisterGameObjectTargeted<ComplexTargetedMessage>(go, Handle);
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Normal GameObject benchmark should invoke handlers.");
            DisplayCount("DxMessaging (GameObject) - Normal", count, timer.Elapsed, allocating);
            return;

            void Handle(ComplexTargetedMessage _)
            {
                ++count;
            }
        }

        private void NormalComponent(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            ComplexTargetedMessage message
        )
        {
            int count = 0;
            InstanceId target = component;

            token.RegisterComponentTargeted<ComplexTargetedMessage>(component, Handle);
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "Normal component benchmark should invoke handlers.");
            DisplayCount("DxMessaging (Component) - Normal", count, timer.Elapsed, allocating);
            return;

            void Handle(ComplexTargetedMessage _)
            {
                ++count;
            }
        }

        private void NoCopyGameObject(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            ComplexTargetedMessage message
        )
        {
            int count = 0;

            GameObject go = component.gameObject;
            InstanceId target = go;
            token.RegisterGameObjectTargeted<ComplexTargetedMessage>(go, Handle);
            // Pre-warm
            message.EmitTargeted(target);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitTargeted(target);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitTargeted(target), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "No-copy GameObject benchmark should invoke handlers.");
            DisplayCount("DxMessaging (GameObject) - No-Copy", count, timer.Elapsed, allocating);
            return;

            void Handle(ref ComplexTargetedMessage _)
            {
                ++count;
            }
        }

        private void NoCopyComponent(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            ComplexTargetedMessage message
        )
        {
            int count = 0;

            token.RegisterComponentTargeted<ComplexTargetedMessage>(component, Handle);
            // Pre-warm
            message.EmitComponentTargeted(component);

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitComponentTargeted(component);
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(
                    () => message.EmitComponentTargeted(component),
                    Is.Not.AllocatingGCMemory()
                );
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "No-copy component benchmark should invoke handlers.");
            DisplayCount("DxMessaging (Component) - No-Copy", count, timer.Elapsed, allocating);
            return;

            void Handle(ref ComplexTargetedMessage _)
            {
                ++count;
            }
        }

        private void NoCopyUntargeted(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            SimpleUntargetedMessage message
        )
        {
            int count = 0;

            token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
            // Pre-warm
            message.EmitUntargeted();

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitUntargeted();
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitUntargeted(), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(count, 0, "No-copy untargeted benchmark should invoke handlers.");
            DisplayCount("DxMessaging (Untargeted) - No-Copy", count, timer.Elapsed, allocating);
            return;

            void Handle(ref SimpleUntargetedMessage _)
            {
                ++count;
            }
        }

        private void InterceptorHeavyUntargeted(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            SimpleUntargetedMessage message
        )
        {
            int handlerInvocationCount = 0;
            int interceptorInvocationCount = 0;

            const int InterceptorCount = 8;
            for (int i = 0; i < InterceptorCount; ++i)
            {
                token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) =>
                    {
                        ++interceptorInvocationCount;
                        return true;
                    }
                );
            }

            token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
            message.EmitUntargeted();

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitUntargeted();
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitUntargeted(), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(
                interceptorInvocationCount,
                0,
                "Interceptor-heavy benchmark should invoke registered interceptors."
            );
            Assert.Greater(
                handlerInvocationCount,
                0,
                "Interceptor-heavy benchmark should invoke handlers."
            );

            DisplayCount(
                "DxMessaging (Untargeted) - Interceptors",
                handlerInvocationCount,
                timer.Elapsed,
                allocating
            );
            return;

            void Handle(ref SimpleUntargetedMessage _)
            {
                ++handlerInvocationCount;
            }
        }

        private void PostProcessorHeavyUntargeted(
            Stopwatch timer,
            TimeSpan timeout,
            EmptyMessageAwareComponent component,
            MessageRegistrationToken token,
            SimpleUntargetedMessage message
        )
        {
            int handlerInvocationCount = 0;
            int postProcessorInvocationCount = 0;

            const int PostProcessorCount = 8;
            for (int i = 0; i < PostProcessorCount; ++i)
            {
                token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => ++postProcessorInvocationCount
                );
            }

            token.RegisterUntargeted<SimpleUntargetedMessage>(Handle);
            message.EmitUntargeted();

            timer.Restart();
            do
            {
                for (int i = 0; i < NumInvocationsPerIteration; ++i)
                {
                    message.EmitUntargeted();
                }
            } while (timer.Elapsed < timeout);

            bool allocating;
            try
            {
                Assert.That(() => message.EmitUntargeted(), Is.Not.AllocatingGCMemory());
                allocating = false;
            }
            catch
            {
                allocating = true;
            }

            Assert.Greater(
                postProcessorInvocationCount,
                0,
                "Post-processor benchmark should invoke registered post-processors."
            );
            Assert.Greater(
                handlerInvocationCount,
                0,
                "Post-processor-heavy benchmark should invoke handlers."
            );

            DisplayCount(
                "DxMessaging (Untargeted) - Post-Processors",
                handlerInvocationCount,
                timer.Elapsed,
                allocating
            );
            return;

            void Handle(ref SimpleUntargetedMessage _)
            {
                ++handlerInvocationCount;
            }
        }
    }
}

#endif
