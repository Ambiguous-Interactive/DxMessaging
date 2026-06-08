#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;

    /// <summary>
    /// The fan-out subscribers for the "global -> many subscribers" comparison scenario. A
    /// <see cref="FanOut"/> owns one shared <see cref="Count"/> and N independent
    /// <see cref="Subscriber"/> objects; each subscriber's <see cref="Subscriber.Handle"/> bumps
    /// that single counter once per message.
    ///
    /// Two properties matter, and a loop of identical lambdas gives neither:
    /// 1. DISTINCT subscribers. Each <see cref="Subscriber"/> is a separate object, so the
    ///    delegate handed to a bus (<c>subscriber.Handle</c>) has a distinct
    ///    <see cref="Delegate.Target"/> and is never <see cref="Delegate.Equals(object)"/>-equal to
    ///    another's. A lambda that captures only an instance field (i.e. captures <c>this</c>)
    ///    compiles to one shared method, so every <see cref="Action{T}"/> it produces wraps the
    ///    same <c>(target, method)</c> pair and they are all value-equal -- a bus that dedupes
    ///    callbacks by value-equality then collapses the fan-out (Zenject's <c>SignalBus</c> throws,
    ///    a silently-deduping bus drops subscribers and fails the harness count assertion).
    /// 2. NO EXTRA DISPATCH HOP. <see cref="Subscriber.Handle"/> increments the shared counter
    ///    directly -- one delegate call plus one increment, exactly what each bridge's own handler
    ///    cost before, and exactly what the DxMessaging bridge pays per listener. Routing through a
    ///    second <see cref="Action"/> would add a per-dispatch call to every competitor's fan-out
    ///    column but not DxMessaging's, skewing the comparison.
    ///
    /// Build this in <c>Prepare()</c>, outside any measurement window. Each subscribed delegate's
    /// target is its <see cref="Subscriber"/>, which references this <see cref="FanOut"/>, so the
    /// whole group stays alive for the subscription's lifetime without retaining it separately.
    ///
    /// A bridge holds at most one <see cref="FanOut"/>, assigned ONLY in the fan-out scenario, and
    /// surfaces it as <c>ProgressMarker =&gt; _fanOut?.Count ?? _progress</c>. The two counters are
    /// mutually exclusive per bridge instance (a fresh bridge is built per benchmarked case, and no
    /// scenario both creates a <see cref="FanOut"/> and bumps the bridge's own <c>_progress</c>), so
    /// the null-coalescing read always reflects exactly one live source.
    /// </summary>
    public sealed class FanOut
    {
        private long _count;
        private readonly Subscriber[] _subscribers;

        public FanOut(int subscriberCount)
        {
            if (subscriberCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subscriberCount),
                    subscriberCount,
                    "Fan-out subscriber count must be non-negative."
                );
            }

            _subscribers = new Subscriber[subscriberCount];
            for (int index = 0; index < subscriberCount; index++)
            {
                _subscribers[index] = new Subscriber(this);
            }
        }

        /// <summary>Total messages delivered across all subscribers since construction.</summary>
        public long Count => _count;

        /// <summary>The N independent subscribers; pass each <c>Subscriber.Handle</c> to a bus.</summary>
        public Subscriber[] Subscribers => _subscribers;

        public sealed class Subscriber
        {
            private readonly FanOut _owner;

            internal Subscriber(FanOut owner)
            {
                _owner = owner;
            }

            public void Handle(int message)
            {
                _owner._count++;
            }
        }
    }
}
#endif
