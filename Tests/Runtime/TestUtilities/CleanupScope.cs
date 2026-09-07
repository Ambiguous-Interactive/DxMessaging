#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;

    internal sealed class CleanupScope : IDisposable
    {
        private readonly Action _cleanup;
        private bool _disposed;

        public CleanupScope(Action cleanup)
        {
            _cleanup = cleanup;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanup();
        }
    }
}
#endif
