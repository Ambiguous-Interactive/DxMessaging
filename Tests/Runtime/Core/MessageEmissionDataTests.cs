namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
#if !UNITY_2021_3_OR_NEWER
    using System.Collections.Generic;
    using System.Reflection;
    using Assert = NUnit.Framework.Legacy.ClassicAssert;
#endif

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

#if !UNITY_2021_3_OR_NEWER
        [Test]
        public void ExternalCallerFrameSurvivesPackageNamesInItsSourcePath()
        {
            using DiagnosticsScope scope = new(diagnosticsStackTraces: true);
            MessageEmissionData data = DiagnosticTraceCallSite.Capture(
                new TestUntargetedMessage(),
                out string raw
            );
            Assert.That(
                raw,
                Does.Contain(nameof(DiagnosticTraceCallSite)),
                "The independent raw stack must contain the non-inlined consumer helper."
            );
            Assert.That(
                raw,
                Does.Contain("DxMessaging.Core.TraceCallSite.cs"),
                "The raw control must include the actual physical consumer source filename."
            );
            Assert.That(
                data.stackTrace,
                Does.Contain(nameof(DiagnosticTraceCallSite)),
                $"A consumer frame must survive a package namespace in its source path. Raw:{Environment.NewLine}{raw}{Environment.NewLine}Filtered:{Environment.NewLine}{data.stackTrace}"
            );
        }

        private static IEnumerable<TestCaseData> DeclaringOwnerCases()
        {
            (string Name, string Net, string Unity, bool Keep)[] frames =
            {
                (
                    "InternalCore",
                    "   at DxMessaging.Core.Diagnostics.MessageEmissionData.Capture() in /Package/Capture.cs:line 10",
                    "DxMessaging.Core.Diagnostics.MessageEmissionData:Capture () (at Packages/Capture.cs:10)",
                    false
                ),
                (
                    "InternalUnity",
                    "   at DxMessaging.Unity.MessagingComponent.Emit() in /Package/Component.cs:line 11",
                    "DxMessaging.Unity.MessagingComponent:Emit () (at Packages/Component.cs:11)",
                    false
                ),
                (
                    "InternalEditor",
                    "   at DxMessaging.Editor.MessageMonitor.Capture() in /Package/Monitor.cs:line 12",
                    "DxMessaging.Editor.MessageMonitor:Capture () (at Packages/Monitor.cs:12)",
                    false
                ),
                (
                    "Consumer",
                    "   at Game.Player.Emit() in /Game/Player.cs:line 13",
                    "Game.Player:Emit () (at Assets/Player.cs:13)",
                    true
                ),
                (
                    "CoreFile",
                    "   at Game.Player.Emit() in /Game/DxMessaging.Core.Player.cs:line 14",
                    "Game.Player:Emit () (at Assets/DxMessaging.Core.Player.cs:14)",
                    true
                ),
                (
                    "UnityFile",
                    "   at Game.Player.Emit() in /Game/DxMessaging.Unity.Player.cs:line 15",
                    "Game.Player:Emit () (at Assets/DxMessaging.Unity.Player.cs:15)",
                    true
                ),
                (
                    "EditorFile",
                    "   at Game.Player.Emit() in /Game/DxMessaging.Editor.Player.cs:line 16",
                    "Game.Player:Emit () (at Assets/DxMessaging.Editor.Player.cs:16)",
                    true
                ),
                (
                    "ConsumerNamespace",
                    "   at Game.DxMessaging.Core.Player.Emit() in /Game/Player.cs:line 17",
                    "Game.DxMessaging.Core.Player:Emit () (at Assets/Player.cs:17)",
                    true
                ),
                (
                    "ConsumerDirectory",
                    "   at Game.Player.Emit() in /Game/DxMessaging.Core.Generated/Player.cs:line 18",
                    "Game.Player:Emit () (at Assets/DxMessaging.Core.Generated/Player.cs:18)",
                    true
                ),
            };
            (string Name, string Value)[] separators =
            {
                ("LF", "\n"),
                ("CR", "\r"),
                ("CRLF", "\r\n"),
            };
            foreach ((string Name, string Net, string Unity, bool Keep) frame in frames)
            {
                (string Name, string Value, string Anchor)[] formats =
                {
                    ("Net", frame.Net, "   at Game.Entry.Start() in /Game/Entry.cs:line 1"),
                    ("Unity", frame.Unity, "Game.Entry:Start () (at Assets/Entry.cs:1)"),
                };
                foreach ((string Name, string Value, string Anchor) format in formats)
                {
                    foreach ((string Name, string Value) separator in separators)
                    {
                        string input = string.Concat(
                            separator.Value,
                            format.Value,
                            separator.Value,
                            " \t",
                            separator.Value,
                            format.Anchor,
                            separator.Value
                        );
                        string expected = frame.Keep
                            ? format.Value + Environment.NewLine + format.Anchor
                            : format.Anchor;
                        yield return new TestCaseData(input, expected).SetName(
                            $"DiagnosticFrame_{frame.Name}_{format.Name}_{separator.Name}"
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(DeclaringOwnerCases))]
        public void TrimmerClassifiesDeclaringOwnerAndPreservesConsumerText(
            string input,
            string expected
        )
        {
            MethodInfo trim = typeof(MessageEmissionData).GetMethod(
                "TrimInternalFrames",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(trim, Is.Not.Null, "Exercise the actual private production trimmer.");
            string actual = (string)trim.Invoke(null, new object[] { input });
            Assert.That(
                actual,
                Is.EqualTo(expected),
                $"Retain external frames exactly and in order, removing blank lines and internal owners. Input:{Environment.NewLine}{input}"
            );
        }
#endif

        private static MessageEmissionData CaptureMessageEmission()
        {
            return new MessageEmissionData(new TestUntargetedMessage());
        }

        private readonly struct TestUntargetedMessage : IUntargetedMessage { }
    }
}
