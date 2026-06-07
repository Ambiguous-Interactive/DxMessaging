#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;

    /// <summary>
    /// A bridge benchmarks ONE messaging technology. A fresh instance is created per
    /// (tech, scenario) case, so bridges may hold per-scenario state. Each bridge must
    /// use its technology's IDIOMATIC, best-practice API (no strawmanning).
    /// </summary>
    public interface IMessagingTechBridge : IDisposable
    {
        /// <summary>Human label, e.g. "C# event".</summary>
        string TechName { get; }

        /// <summary>
        /// Machine key, e.g. "CsEvent" (no spaces, no underscores in the tech segment).
        /// </summary>
        string TechKey { get; }

        /// <summary>True if it needs GameObjects/runtime (e.g. Unity SendMessage).</summary>
        bool RequiresPlayMode { get; }

        /// <summary>Whether this technology can implement the given scenario idiomatically.</summary>
        bool Supports(ComparisonScenario scenario);

        /// <summary>Set up registrations/subscriptions for the scenario.</summary>
        void Prepare(ComparisonScenario scenario);

        /// <summary>Perform EXACTLY ONE operation of the scenario.</summary>
        void EmitOnce();

        /// <summary>
        /// Increments per handler invocation (or per churn cycle for SubscribeUnsubscribe);
        /// the harness asserts this is greater than zero after measurement.
        /// </summary>
        long ProgressMarker { get; }

        /// Number of handler/listener invocations a single EmitOnce() must produce for
        /// this scenario (e.g. 1 for GlobalToOne, 16 for GlobalToMany, 4 for DxMessaging
        /// PriorityOrdered, 1 per cycle for SubscribeUnsubscribeChurn). The harness asserts
        /// ProgressMarker equals this times the total operations, catching any silent
        /// fan-out mismatch (e.g. a deduped registration).
        long InvocationsPerOperation(ComparisonScenario scenario);
    }

    /// <summary>
    /// Plain value type used by the non-DxMessaging bridges for the
    /// <see cref="ComparisonScenario.StructMessageZeroCopy"/> scenario. Carrying this
    /// through a generic delegate (e.g. <c>Action&lt;ComparisonStructPayload&gt;</c>) avoids
    /// boxing, while carrying it through a non-generic/object channel forces a box; the
    /// allocation column then reveals which technology copies or boxes the payload.
    /// </summary>
    public struct ComparisonStructPayload
    {
        public int Value;

        public ComparisonStructPayload(int value)
        {
            Value = value;
        }
    }
}
#endif
