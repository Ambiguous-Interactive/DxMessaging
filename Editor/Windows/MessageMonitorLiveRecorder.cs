#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One row of the live Message Monitor log: a message emission, plus the run of identical
    /// emissions that immediately followed it.
    /// </summary>
    /// <remarks>
    /// A bus can emit the same message thousands of times per second, so the recorder folds an
    /// adjacent run of identical emissions (same message type, context and route kind) into a
    /// single row with a <see cref="Count"/>. That keeps the log readable and bounds the number of
    /// rows the list has to render, while <see cref="FirstTraceId"/>/<see cref="LastTraceId"/>
    /// preserve the exact dispatch range the row stands for.
    /// </remarks>
    internal readonly struct MessageMonitorLiveEntry
    {
        internal MessageMonitorLiveEntry(
            MessageMonitorEntry entry,
            long firstTraceId,
            long lastTraceId,
            int count,
            double firstObservedSeconds,
            double lastObservedSeconds
        )
        {
            Entry = entry;
            FirstTraceId = firstTraceId;
            LastTraceId = lastTraceId;
            Count = count;
            FirstObservedSeconds = firstObservedSeconds;
            LastObservedSeconds = lastObservedSeconds;
        }

        internal MessageMonitorEntry Entry { get; }

        /// <summary>Dispatch sequence number of the first emission folded into this row.</summary>
        internal long FirstTraceId { get; }

        /// <summary>Dispatch sequence number of the most recent emission folded into this row.</summary>
        internal long LastTraceId { get; }

        /// <summary>Number of identical emissions this row stands for; always at least 1.</summary>
        internal int Count { get; }

        /// <summary>
        /// Recorder clock reading when the first emission of this row was drained, in seconds since
        /// the recorder started. This is an observation time, not an emission time: the recorder
        /// polls, so it trails the emission by up to one poll interval.
        /// </summary>
        internal double FirstObservedSeconds { get; }

        /// <summary>Recorder clock reading when the most recent emission of this row was drained.</summary>
        internal double LastObservedSeconds { get; }

        internal MessageMonitorLiveEntry Fold(long traceId, double observedSeconds)
        {
            return new MessageMonitorLiveEntry(
                Entry,
                FirstTraceId,
                traceId,
                Count + 1,
                FirstObservedSeconds,
                observedSeconds
            );
        }
    }

    /// <summary>
    /// Bounded, append-only log of message emissions drained from the bus emission buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorder is pull-based on purpose. The bus already keeps every emission in its own ring
    /// buffer when diagnostics are on, so a live view needs no push tap, no background thread and
    /// no runtime hook: it re-reads that ring on a timer and folds in whatever is new. The cost of
    /// the live Monitor is therefore zero when the window is closed, and the shipped runtime is
    /// untouched.
    /// </para>
    /// <para>
    /// "New" is decided by the bus dispatch sequence number carried on every record
    /// (<see cref="MessageMonitorEntry.TraceId"/>), which starts at 1 and increases monotonically.
    /// The recorder keeps the highest number it has drained as its <see cref="Cursor"/> and ignores
    /// anything at or below it, so overlapping polls never double-count. A jump in that sequence
    /// means the bus ring overwrote emissions before the recorder got to them, which is counted
    /// exactly in <see cref="MissedCount"/> rather than silently swallowed.
    /// </para>
    /// <para>
    /// Records with a non-positive trace id carry no sequence and cannot be de-duplicated across
    /// polls, so they are skipped. Only records built by the public
    /// <see cref="Core.Diagnostics.MessageEmissionData"/> constructor look like that; every record
    /// the default bus writes to its emission buffer is sequenced.
    /// </para>
    /// <para>
    /// This type deliberately touches no GUI or editor API so the draining, coalescing and
    /// bookkeeping rules stay testable with plain NUnit, without an editor panel or a version gate.
    /// The clock is injected for the same reason.
    /// </para>
    /// </remarks>
    internal sealed class MessageMonitorLiveRecorder
    {
        internal const int DefaultCapacity = 512;

        private readonly List<MessageMonitorLiveEntry> _entries = new();
        private readonly Func<double> _clock;
        private readonly double _startSeconds;

        private bool _started;

        internal MessageMonitorLiveRecorder(
            int capacity = DefaultCapacity,
            Func<double> clock = null
        )
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "The live recorder needs room for at least one row."
                );
            }

            Capacity = capacity;
            _clock = clock ?? DefaultClock;
            _startSeconds = _clock();
            Recording = true;
        }

        /// <summary>Maximum number of rows retained; the oldest are dropped past this.</summary>
        internal int Capacity { get; }

        /// <summary>
        /// While false, <see cref="Ingest"/> is a no-op and the cursor stays put. The bus keeps
        /// recording either way, so resuming drains whatever is still in the bus ring and counts
        /// anything that fell out of it as missed.
        /// </summary>
        internal bool Recording { get; set; }

        /// <summary>Retained rows, oldest first.</summary>
        internal IReadOnlyList<MessageMonitorLiveEntry> Entries => _entries;

        /// <summary>Total emissions drained since the last <see cref="Clear"/>, before coalescing.</summary>
        internal long ObservedCount { get; private set; }

        /// <summary>
        /// Emissions the bus ring overwrote before the recorder could drain them. Non-zero means the
        /// log has a hole: either the poll interval is too slow for the emission rate and the bus
        /// buffer too small, or recording was paused long enough for the bus ring to wrap.
        /// </summary>
        internal long MissedCount { get; private set; }

        /// <summary>Highest dispatch sequence number drained so far; 0 before the first drain.</summary>
        internal long Cursor { get; private set; }

        /// <summary>
        /// Change counter, bumped once per <see cref="Ingest"/> that altered the log. Lets a polling
        /// view skip a rebuild when a poll found nothing new.
        /// </summary>
        internal long Revision { get; private set; }

        /// <summary>
        /// Folds every emission newer than <see cref="Cursor"/> into the log.
        /// </summary>
        /// <param name="busEntries">
        /// A bus emission snapshot in any order; only <see cref="MessageMonitorEntry.TraceId"/>
        /// decides what is new and how rows are sequenced.
        /// </param>
        /// <returns>True when the log changed, so a view knows to rebuild.</returns>
        internal bool Ingest(IReadOnlyList<MessageMonitorEntry> busEntries)
        {
            if (busEntries == null)
            {
                throw new ArgumentNullException(nameof(busEntries));
            }

            if (!Recording || busEntries.Count == 0)
            {
                return false;
            }

            long highestTraceId = HighestTraceId(busEntries);
            if (highestTraceId <= 0)
            {
                return false;
            }

            // A bus reset restarts the dispatch sequence at 0, so a snapshot whose highest sequence
            // number sits below the cursor is a different run of the counter, not stale data. This
            // has to be settled before anything is collected, because against the old cursor every
            // record of the new run looks already-drained. This inference is a best-effort net for a
            // reset the caller did not announce: a run that emits past the old cursor before the
            // next poll is indistinguishable from ordinary progress. A caller that knows the bus
            // restarted should say so through <see cref="RebaseTo"/> instead of relying on this.
            if (highestTraceId < Cursor)
            {
                // The retained rows belong to the previous run of the counter, and their "#N"
                // dispatch labels would now collide with the new run's. Dropping them keeps the log
                // unambiguous instead of interleaving two runs that both start at #1.
                _entries.Clear();
                Cursor = 0;
                _started = false;
            }

            List<MessageMonitorEntry> pending = CollectNewerThanCursor(busEntries);
            if (pending.Count == 0)
            {
                return false;
            }

            pending.Sort(CompareByTraceId);

            long lowestTraceId = pending[0].TraceId;
            if (_started && lowestTraceId > Cursor + 1)
            {
                MissedCount += lowestTraceId - Cursor - 1;
            }

            double observedSeconds = _clock() - _startSeconds;
            foreach (MessageMonitorEntry entry in pending)
            {
                Append(entry, observedSeconds);
            }

            Cursor = pending[pending.Count - 1].TraceId;
            _started = true;
            Trim();
            Revision++;
            return true;
        }

        /// <summary>
        /// Empties the log and its statistics, so the log restarts from the next emission.
        /// </summary>
        /// <remarks>
        /// The cursor is deliberately <em>kept</em>. Rewinding it to 0 would make the very next
        /// drain re-ingest everything the bus still has buffered, so clearing would visibly refill
        /// itself within one poll. Only the loss baseline is dropped, so the emissions this call
        /// discarded are not then reported as missed.
        /// </remarks>
        internal void Clear()
        {
            RebaseTo(Cursor);
        }

        /// <summary>
        /// Empties the log and moves the cursor to the bus's current dispatch counter, so the next
        /// drain takes exactly what the bus emits from here on.
        /// </summary>
        /// <remarks>
        /// This is how a caller that <em>knows</em> the bus restarted says so, and it exists because
        /// the sequence alone cannot always tell. <see cref="Ingest"/> can only infer a restart
        /// while the new run is still numbered below the cursor; a run that emits past the old
        /// cursor before the next poll is indistinguishable from ordinary progress, and its opening
        /// emissions would be dropped as already-drained. Rebasing onto the live counter at the
        /// moment of the restart removes the guess: pass the counter of the bus that is about to
        /// produce the next emissions, whether or not it reset.
        /// </remarks>
        /// <param name="busCursor">
        /// The bus's dispatch counter (<see cref="Core.MessageBus.MessageBus.EmissionId"/>), or 0
        /// when the bus has been reset and should be drained from its first emission.
        /// </param>
        internal void RebaseTo(long busCursor)
        {
            long normalizedBusCursor = Math.Max(0, busCursor);
            if (
                _entries.Count == 0
                && ObservedCount == 0
                && MissedCount == 0
                && !_started
                && Cursor == normalizedBusCursor
            )
            {
                return;
            }

            _entries.Clear();
            ObservedCount = 0;
            MissedCount = 0;
            Cursor = normalizedBusCursor;
            _started = false;
            Revision++;
        }

        private static long HighestTraceId(IReadOnlyList<MessageMonitorEntry> busEntries)
        {
            long highestTraceId = 0;
            for (int index = 0; index < busEntries.Count; index++)
            {
                long traceId = busEntries[index].TraceId;
                if (traceId > highestTraceId)
                {
                    highestTraceId = traceId;
                }
            }

            return highestTraceId;
        }

        private List<MessageMonitorEntry> CollectNewerThanCursor(
            IReadOnlyList<MessageMonitorEntry> busEntries
        )
        {
            List<MessageMonitorEntry> pending = new();
            for (int index = 0; index < busEntries.Count; index++)
            {
                MessageMonitorEntry entry = busEntries[index];
                if (entry.TraceId > 0 && entry.TraceId > Cursor)
                {
                    pending.Add(entry);
                }
            }

            return pending;
        }

        private void Append(MessageMonitorEntry entry, double observedSeconds)
        {
            ObservedCount++;
            int lastIndex = _entries.Count - 1;
            if (lastIndex >= 0 && IsSameRun(_entries[lastIndex].Entry, entry))
            {
                _entries[lastIndex] = _entries[lastIndex].Fold(entry.TraceId, observedSeconds);
                return;
            }

            _entries.Add(
                new MessageMonitorLiveEntry(
                    entry,
                    entry.TraceId,
                    entry.TraceId,
                    count: 1,
                    observedSeconds,
                    observedSeconds
                )
            );
        }

        private void Trim()
        {
            if (_entries.Count <= Capacity)
            {
                return;
            }

            _entries.RemoveRange(0, _entries.Count - Capacity);
        }

        private static bool IsSameRun(MessageMonitorEntry left, MessageMonitorEntry right)
        {
            return string.Equals(
                    left.MessageTypeIdentity,
                    right.MessageTypeIdentity,
                    StringComparison.Ordinal
                )
                && string.Equals(left.ContextText, right.ContextText, StringComparison.Ordinal)
                && string.Equals(left.RouteKind, right.RouteKind, StringComparison.Ordinal);
        }

        private static int CompareByTraceId(MessageMonitorEntry left, MessageMonitorEntry right)
        {
            return left.TraceId.CompareTo(right.TraceId);
        }

        private static double DefaultClock()
        {
            return UnityEditor.EditorApplication.timeSinceStartup;
        }
    }
}
#endif
