#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;

    /// <summary>Test-only bounded managed command payload control for the #505 queue bakeoff.</summary>
    internal sealed class ReferenceCommandQueueControl : IDisposable
    {
        private readonly LockedFixedRingControl<Action> _ring;

        internal ReferenceCommandQueueControl(int capacity)
        {
            _ring = new LockedFixedRingControl<Action>(capacity);
        }

        internal int Count => _ring.Count;

        internal bool TryEnqueue(Action command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            return _ring.TryEnqueue(command);
        }

        internal int Drain(int maxItems)
        {
            if (maxItems <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxItems));
            }
            int completed = 0;
            while (completed < maxItems && _ring.TryDequeue(out Action command))
            {
                command();
                ++completed;
            }
            return completed;
        }

        internal int Reset() => _ring.Reset();

        public void Dispose() => _ring.Dispose();
    }
}
#endif
