namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    public sealed class MessageEmissionDataTests
    {
        [Test]
        public void StackTraceOmitsDxMessagingFramesWhenCaptureEnabled()
        {
            using DiagnosticsScope scope = new(diagnosticsStackTraces: true);

            MessageEmissionData data = CaptureMessageEmission();

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(data.stackTrace),
                "Stack trace should capture emission site when capture is enabled."
            );

            string[] lines = data.stackTrace.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );

            bool containsInternalFrame = lines.Any(line =>
                line.Contains("DxMessaging.Core.", StringComparison.Ordinal)
                || line.Contains("DxMessaging.Unity.", StringComparison.Ordinal)
            );
            if (containsInternalFrame)
            {
                Assert.Fail(
                    $"Stack trace should omit DxMessaging internal frames.{Environment.NewLine}{data.stackTrace}"
                );
            }

            bool containsTestMethod = lines.Any(line =>
                line.Contains(
                    nameof(StackTraceOmitsDxMessagingFramesWhenCaptureEnabled),
                    StringComparison.Ordinal
                )
            );
            if (!containsTestMethod)
            {
                Assert.Fail(
                    $"Stack trace should include calling test method for debugging context.{Environment.NewLine}{data.stackTrace}"
                );
            }

            bool containsBlankLine = lines.Any(string.IsNullOrWhiteSpace);
            Assert.IsFalse(containsBlankLine, "Trimmed stack trace should not retain blank lines.");
        }

        [Test]
        public void StackTraceIsEmptyWhenCaptureDisabled()
        {
            using DiagnosticsScope scope = new(diagnosticsStackTraces: false);

            MessageEmissionData data = CaptureMessageEmission();

            Assert.AreEqual(
                string.Empty,
                data.stackTrace,
                "Emission-site capture must stay off unless explicitly enabled; it costs hundreds "
                    + "of microseconds and tens of allocations per record."
            );
        }

        [Test]
        public void RecordedPayloadIsPreservedRegardlessOfCapture(
            [Values(true, false)] bool captureStackTraces
        )
        {
            using DiagnosticsScope scope = new(diagnosticsStackTraces: captureStackTraces);
            InstanceId expectedContext = new(12345);

            MessageEmissionData data = new(new TestUntargetedMessage(), expectedContext);

            Assert.That(
                data.context.HasValue,
                Is.True,
                "Context should be captured when supplied."
            );
            Assert.That(data.context.Value, Is.EqualTo(expectedContext));
            Assert.That(data.message, Is.TypeOf<TestUntargetedMessage>());
        }

        private static MessageEmissionData CaptureMessageEmission()
        {
            return new MessageEmissionData(new TestUntargetedMessage());
        }

        private readonly struct TestUntargetedMessage : IUntargetedMessage { }
    }
}
