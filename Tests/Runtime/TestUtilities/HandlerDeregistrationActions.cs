namespace DxMessaging.Tests
{
    using System;
    using DxMessaging.Core;

    /// <summary>Adapts real teardown state to the Action shape used by test harness cleanup.</summary>
    internal static class HandlerDeregistrationActions
    {
        internal static Action AsAction(this MessageHandler.HandlerDeregistration deregistration) =>
            deregistration == null ? null : deregistration.Deregister;

        internal static Action AsAction<T>(
            this MessageHandler.InterceptorDeregistrationState<T> state
        )
            where T : IMessage
        {
            MessageHandler.HandlerDeregistration deregistration = state;
            return deregistration.Deregister;
        }

        internal static Action AsAction(
            this MessageHandler.GlobalAcceptAllDeregistrationState state
        )
        {
            MessageHandler.HandlerDeregistration deregistration = state;
            return deregistration.Deregister;
        }

        internal static Action AsAction<T>(
            this MessageHandler.TypedHandler<T>.TypedHandlerDeregistrationState state
        )
            where T : IMessage
        {
            MessageHandler.HandlerDeregistration deregistration = state;
            return deregistration.Deregister;
        }
    }
}
