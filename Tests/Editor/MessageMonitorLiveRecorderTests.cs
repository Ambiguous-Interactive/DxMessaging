#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using NUnit.Framework;

    /// <summary>
    /// Covers the live Monitor's draining, coalescing and loss-accounting rules. The recorder
    /// touches no GUI API and takes its clock as a parameter, so every case here runs as a plain
    /// unit test with no editor panel and no version gate.
    /// </summary>
    [TestFixture]
    public sealed class MessageMonitorLiveRecorderTests
    {
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _clock = 0;
        }

        [Test]
        public void IngestAddsEveryNewSequencedEmission()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            Assert.IsTrue(recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B"), Entry(3, "C"))));

            Assert.AreEqual(3, recorder.Entries.Count);
            Assert.AreEqual(3, recorder.ObservedCount);
            Assert.AreEqual(0, recorder.MissedCount);
            Assert.AreEqual(3, recorder.Cursor);
            CollectionAssert.AreEqual(
                new[] { "A", "B", "C" },
                MessageTypeNames(recorder),
                "Rows are kept oldest first."
            );
        }

        [Test]
        public void IngestOrdersByTraceIdRegardlessOfSnapshotOrder()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            // The window snapshots the bus buffer newest first, so the recorder must not rely on
            // the order it is handed.
            recorder.Ingest(Bus(Entry(3, "C"), Entry(2, "B"), Entry(1, "A")));

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, MessageTypeNames(recorder));
            Assert.AreEqual(3, recorder.Cursor);
        }

        [Test]
        public void IngestSkipsEmissionsAlreadyDrained()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B")));

            // A poll re-reads the whole bus buffer, so the overlap must not be counted twice.
            Assert.IsTrue(recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B"), Entry(3, "C"))));

            Assert.AreEqual(3, recorder.ObservedCount);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, MessageTypeNames(recorder));
        }

        [Test]
        public void IngestReturnsFalseAndChangesNothingWhenNothingIsNew()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A")));
            long revision = recorder.Revision;

            Assert.IsFalse(recorder.Ingest(Bus(Entry(1, "A"))));

            Assert.AreEqual(
                revision,
                recorder.Revision,
                "An idle poll must not bump the revision."
            );
            Assert.AreEqual(1, recorder.ObservedCount);
        }

        [Test]
        public void IngestIgnoresUnsequencedEmissions()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            // Trace id 0 means the record carries no dispatch sequence, so it cannot be
            // de-duplicated across polls and would otherwise reappear on every drain.
            Assert.IsFalse(recorder.Ingest(Bus(Entry(0, "A"))));

            Assert.AreEqual(0, recorder.Entries.Count);
            Assert.AreEqual(0, recorder.ObservedCount);
        }

        [Test]
        public void IngestIsANoOpWhileNotRecording()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Recording = false;

            Assert.IsFalse(recorder.Ingest(Bus(Entry(1, "A"))));

            Assert.AreEqual(0, recorder.Entries.Count);
            Assert.AreEqual(0, recorder.Cursor, "A paused recorder must not advance its cursor.");
        }

        [Test]
        public void ResumingAfterAPauseCountsWhatTheBusOverwroteAsMissed()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A")));

            recorder.Recording = false;
            recorder.Ingest(Bus(Entry(2, "B"), Entry(3, "C")));
            recorder.Recording = true;

            // Emissions 2 and 3 fell out of the bus ring while the drain was paused; only 4 is
            // still there, so exactly two are unrecoverable.
            recorder.Ingest(Bus(Entry(4, "D")));

            Assert.AreEqual(2, recorder.MissedCount);
            Assert.AreEqual(2, recorder.ObservedCount);
            CollectionAssert.AreEqual(new[] { "A", "D" }, MessageTypeNames(recorder));
        }

        [Test]
        public void TheFirstDrainAdoptsTheBusPositionWithoutReportingLoss()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            // Opening the window on a bus that has been running for a while is not data loss: those
            // emissions happened before anyone asked to record them.
            recorder.Ingest(Bus(Entry(500, "A")));

            Assert.AreEqual(0, recorder.MissedCount);
            Assert.AreEqual(500, recorder.Cursor);
        }

        [Test]
        public void ClearRebaselinesSoTheNextDrainReportsNoLoss()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A")));

            recorder.Clear();
            recorder.Ingest(Bus(Entry(90, "B")));

            Assert.AreEqual(0, recorder.MissedCount);
            Assert.AreEqual(1, recorder.ObservedCount);
            CollectionAssert.AreEqual(new[] { "B" }, MessageTypeNames(recorder));
        }

        [Test]
        public void ClearResetsTheLogAndItsStatistics()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A")));
            recorder.Ingest(Bus(Entry(9, "B")));
            Assert.AreEqual(7, recorder.MissedCount);
            long revision = recorder.Revision;

            recorder.Clear();

            Assert.AreEqual(0, recorder.Entries.Count);
            Assert.AreEqual(0, recorder.ObservedCount);
            Assert.AreEqual(0, recorder.MissedCount);
            Assert.AreEqual(
                9,
                recorder.Cursor,
                "The cursor survives, or the next drain would re-ingest what was just cleared."
            );
            Assert.Greater(recorder.Revision, revision);
        }

        [Test]
        public void ClearingDoesNotRefillFromWhatTheBusStillHasBuffered()
        {
            // The bus keeps its own ring, and a poll re-reads all of it. Rewinding the cursor on
            // Clear would make the log visibly refill itself within one poll.
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            IReadOnlyList<MessageMonitorEntry> busBuffer = Bus(
                Entry(1, "A"),
                Entry(2, "B"),
                Entry(3, "C")
            );
            recorder.Ingest(busBuffer);

            recorder.Clear();

            Assert.IsFalse(
                recorder.Ingest(busBuffer),
                "Re-reading the same bus buffer after a clear must find nothing new."
            );
            Assert.AreEqual(0, recorder.Entries.Count);
            Assert.AreEqual(0, recorder.ObservedCount);
        }

        [Test]
        public void RebasingOntoAResetBusTakesTheWholeNewRun()
        {
            // The case sequence inference cannot catch: the previous run left the cursor at 40, the
            // bus resets, and the new run emits past 40 before the next poll. Against the old cursor
            // its opening emissions look already-drained, so they would be dropped silently.
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(39, "Old"), Entry(40, "Older")));

            recorder.RebaseTo(0);
            recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B"), Entry(41, "C")));

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, MessageTypeNames(recorder));
            Assert.AreEqual(41, recorder.Cursor);
            Assert.AreEqual(0, recorder.MissedCount, "A rebase is a fresh baseline, not loss.");
        }

        [Test]
        public void RebasingOntoALiveBusSkipsEverythingBeforeIt()
        {
            // The bus did not reset -- a play-mode transition with the domain reload disabled leaves
            // the same counter running -- so the rebase must not re-ingest the backlog.
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(1, "A")));

            recorder.RebaseTo(40);

            Assert.IsFalse(recorder.Ingest(Bus(Entry(1, "A"), Entry(40, "B"))));
            Assert.AreEqual(0, recorder.Entries.Count);
            Assert.IsTrue(recorder.Ingest(Bus(Entry(41, "C"))));
            CollectionAssert.AreEqual(new[] { "C" }, MessageTypeNames(recorder));
            Assert.AreEqual(0, recorder.MissedCount);
        }

        [Test]
        public void ANegativeRebaseTargetIsTreatedAsTheStartOfARun()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            recorder.RebaseTo(-5);

            Assert.AreEqual(0, recorder.Cursor);
        }

        [Test]
        public void ClearOnAnAlreadyEmptyRecorderDoesNotBumpTheRevision()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            long revision = recorder.Revision;

            recorder.Clear();

            Assert.AreEqual(revision, recorder.Revision);
        }

        [Test]
        public void ABusResetRebasesTheCursorAndDropsThePreviousRun()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(Bus(Entry(40, "A"), Entry(41, "B")));

            // MessageBus.Reset restarts the dispatch sequence at 0, so the next snapshot's ids sit
            // below the cursor without being stale.
            Assert.IsTrue(recorder.Ingest(Bus(Entry(1, "C"), Entry(2, "D"))));

            Assert.AreEqual(2, recorder.Cursor);
            Assert.AreEqual(0, recorder.MissedCount, "A reset is not dropped data.");
            CollectionAssert.AreEqual(
                new[] { "C", "D" },
                MessageTypeNames(recorder),
                "Rows from the old run are dropped: their #N labels would collide with the new run."
            );
        }

        [Test]
        public void AdjacentIdenticalEmissionsCoalesceIntoOneCountedRow()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "A"), Entry(3, "A")));

            Assert.AreEqual(1, recorder.Entries.Count);
            MessageMonitorLiveEntry row = recorder.Entries[0];
            Assert.AreEqual(3, row.Count);
            Assert.AreEqual(1, row.FirstTraceId);
            Assert.AreEqual(3, row.LastTraceId);
            Assert.AreEqual(
                3,
                recorder.ObservedCount,
                "Coalescing is a display concern; every emission is still counted."
            );
        }

        [Test]
        public void CoalescingSpansPollsSoABurstStaysOneRow()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            _clock = 1;
            recorder.Ingest(Bus(Entry(1, "A")));
            _clock = 2;
            recorder.Ingest(Bus(Entry(2, "A")));

            Assert.AreEqual(1, recorder.Entries.Count);
            Assert.AreEqual(2, recorder.Entries[0].Count);
            Assert.AreEqual(1, recorder.Entries[0].FirstObservedSeconds);
            Assert.AreEqual(2, recorder.Entries[0].LastObservedSeconds);
        }

        [TestCase(
            "A",
            "Context: 1",
            "Untargeted",
            "A",
            "Context: 2",
            "Untargeted",
            TestName = "different context"
        )]
        [TestCase(
            "A",
            "Context: 1",
            "Untargeted",
            "B",
            "Context: 1",
            "Untargeted",
            TestName = "different message type"
        )]
        [TestCase(
            "A",
            "Context: 1",
            "Untargeted",
            "A",
            "Context: 1",
            "Broadcast",
            TestName = "different route kind"
        )]
        public void RowsOnlyCoalesceWhenEveryRenderedFieldMatches(
            string firstType,
            string firstContext,
            string firstRouteKind,
            string secondType,
            string secondContext,
            string secondRouteKind
        )
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            recorder.Ingest(
                Bus(
                    Entry(1, firstType, firstContext, firstRouteKind),
                    Entry(2, secondType, secondContext, secondRouteKind)
                )
            );

            Assert.AreEqual(2, recorder.Entries.Count);
        }

        [Test]
        public void ANonAdjacentRepeatStartsANewRow()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B"), Entry(3, "A")));

            Assert.AreEqual(3, recorder.Entries.Count);
            Assert.AreEqual(1, recorder.Entries[2].Count);
        }

        [Test]
        public void TheLogNeverGrowsPastItsCapacityAndKeepsTheNewestRows()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder(capacity: 3);

            recorder.Ingest(Bus(Entry(1, "A"), Entry(2, "B"), Entry(3, "C"), Entry(4, "D")));

            Assert.AreEqual(3, recorder.Entries.Count);
            CollectionAssert.AreEqual(new[] { "B", "C", "D" }, MessageTypeNames(recorder));
            Assert.AreEqual(
                4,
                recorder.ObservedCount,
                "Aging a row out of the retained window is not the same as never seeing it."
            );
            Assert.AreEqual(0, recorder.MissedCount);
        }

        [Test]
        public void ObservedTimesAreRelativeToTheRecorderStart()
        {
            _clock = 1_000;
            MessageMonitorLiveRecorder recorder = CreateRecorder();
            _clock = 1_002.5;

            recorder.Ingest(Bus(Entry(1, "A")));

            Assert.AreEqual(2.5, recorder.Entries[0].FirstObservedSeconds, 1e-9);
        }

        [Test]
        public void IngestRejectsANullSnapshot()
        {
            MessageMonitorLiveRecorder recorder = CreateRecorder();

            Assert.Throws<ArgumentNullException>(() => recorder.Ingest(null));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void ANonPositiveCapacityIsRejected(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MessageMonitorLiveRecorder(capacity)
            );
        }

        /// <summary>
        /// The whole pull-based design rests on the bus stamping a monotonic dispatch sequence onto
        /// every buffered emission and the Monitor's snapshot carrying it through. Every other case
        /// here builds that sequence by hand, so this one drives a real bus end to end.
        /// </summary>
        [Test]
        public void ARealBusSnapshotDrainsIntoTheLogInDispatchOrder()
        {
            MessageBus messageBus = new() { DiagnosticsMode = true };
            RecorderProbeMessage message = default;
            messageBus.UntargetedBroadcast(ref message);
            messageBus.UntargetedBroadcast(ref message);

            MessageMonitorSnapshot snapshot = DxMessagingMessageMonitorWindow.CaptureSnapshot(
                messageBus
            );
            Assert.AreEqual(2, snapshot.Entries.Count);
            foreach (MessageMonitorEntry entry in snapshot.Entries)
            {
                Assert.Greater(
                    entry.TraceId,
                    0,
                    "Bus-side emission records must carry their dispatch sequence number."
                );
            }

            MessageMonitorLiveRecorder recorder = CreateRecorder();
            Assert.IsTrue(recorder.Ingest(snapshot.Entries));

            Assert.AreEqual(2, recorder.ObservedCount);
            Assert.AreEqual(0, recorder.MissedCount);
            Assert.AreEqual(
                1,
                recorder.Entries.Count,
                "Two identical emissions coalesce into one counted row."
            );
            Assert.AreEqual(2, recorder.Entries[0].Count);
            Assert.AreEqual(
                nameof(RecorderProbeMessage),
                recorder.Entries[0].Entry.MessageTypeName
            );
            Assert.Less(
                recorder.Entries[0].FirstTraceId,
                recorder.Entries[0].LastTraceId,
                "The row spans the dispatch range it stands for."
            );

            // A second drain of the same snapshot must be inert: this is what every poll does.
            Assert.IsFalse(recorder.Ingest(snapshot.Entries));
            Assert.AreEqual(2, recorder.ObservedCount);
        }

        /// <summary>
        /// The window skips capturing a snapshot when the bus's dispatch counter already matches the
        /// cursor. That shortcut is only sound if a fully drained recorder ends up holding exactly
        /// that counter.
        /// </summary>
        [Test]
        public void AFullyDrainedRecorderCursorMatchesTheBusDispatchCounter()
        {
            MessageBus messageBus = new() { DiagnosticsMode = true };
            RecorderProbeMessage message = default;
            messageBus.UntargetedBroadcast(ref message);
            messageBus.UntargetedBroadcast(ref message);

            MessageMonitorLiveRecorder recorder = CreateRecorder();
            recorder.Ingest(DxMessagingMessageMonitorWindow.CaptureSnapshot(messageBus).Entries);

            Assert.AreEqual(messageBus.EmissionId, recorder.Cursor);
        }

        private readonly struct RecorderProbeMessage : IUntargetedMessage<RecorderProbeMessage> { }

        private MessageMonitorLiveRecorder CreateRecorder(
            int capacity = MessageMonitorLiveRecorder.DefaultCapacity
        )
        {
            return new MessageMonitorLiveRecorder(capacity, () => _clock);
        }

        private static IReadOnlyList<MessageMonitorEntry> Bus(params MessageMonitorEntry[] entries)
        {
            return entries;
        }

        private static MessageMonitorEntry Entry(
            long traceId,
            string messageTypeName,
            string contextText = "Context: none",
            string routeKind = DxMessagingEditorPalette.UntargetedKind
        )
        {
            return new MessageMonitorEntry(
                messageTypeName,
                contextText,
                stackTrace: string.Empty,
                messageTypeIdentity: messageTypeName,
                messageTypeDisplayPath: messageTypeName,
                routeKind: routeKind,
                traceId: traceId
            );
        }

        private static string[] MessageTypeNames(MessageMonitorLiveRecorder recorder)
        {
            string[] names = new string[recorder.Entries.Count];
            for (int index = 0; index < names.Length; index++)
            {
                names[index] = recorder.Entries[index].Entry.MessageTypeName;
            }

            return names;
        }
    }
}
#endif
