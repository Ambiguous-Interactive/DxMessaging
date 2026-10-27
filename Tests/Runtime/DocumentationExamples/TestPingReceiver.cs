#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.DocumentationExamples
{
    using DxMessaging.Unity;

    public sealed class TestPingReceiver : MessageAwareComponent
    {
        public int Received { get; private set; }

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();
            _ = Token.RegisterUntargeted<TestPing>(OnPing);
        }

        private void OnPing(in TestPing message)
        {
            Received++;
        }
    }
}
#endif
