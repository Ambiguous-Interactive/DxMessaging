namespace DxMessaging.Core
{
    using System;
    using Messages;

    public sealed partial class MessageRegistrationToken
    {
        // SYNC: .docs-tests/DocsSnippetCompiler.cs mirrors StateHandler for readiness guide compilation.
        /// <summary>Handles a message with an explicit state parameter.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">Type of the state stored with the registration.</typeparam>
        /// <param name="state">State supplied when registering. Value types are copied.</param>
        /// <param name="message">Message being delivered, by readonly reference.</param>
        /// <remarks>Added in 4.0.0. Use a static method or static lambda to avoid capturing local state.</remarks>
        public delegate void StateHandler<TMessage, TState>(TState state, in TMessage message)
            where TMessage : IMessage;

        /// <summary>Handles a message and its context with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">Type of the state stored with the registration.</typeparam>
        /// <param name="state">State supplied when registering. Value types are copied.</param>
        /// <param name="context">Actual target or source of the emission.</param>
        /// <param name="message">Message being delivered, by readonly reference.</param>
        /// <remarks>Added in 4.0.0. Use a static method or static lambda to avoid capturing local state.</remarks>
        public delegate void StateHandlerWithContext<TMessage, TState>(
            TState state,
            in InstanceId context,
            in TMessage message
        )
            where TMessage : IMessage;

        /// <summary>Stages an untargeted handler with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterUntargeted<TMessage, TState>(
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterUntargeted<TMessage>(binding.Invoke, priority);
        }

        /// <summary>Stages an untargeted post-processor with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterUntargetedPostProcessor<TMessage, TState>(
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterUntargetedPostProcessor<TMessage>(binding.Invoke, priority);
        }

        /// <summary>Stages a handler for one target with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="target">The target whose messages this registration accepts.</param>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterTargeted<TMessage, TState>(
            InstanceId target,
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterTargeted<TMessage>(target, binding.Invoke, priority);
        }

        /// <summary>Stages a post-processor for one target with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="target">The target whose messages this registration accepts.</param>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterTargetedPostProcessor<TMessage, TState>(
            InstanceId target,
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterTargetedPostProcessor<TMessage>(target, binding.Invoke, priority);
        }

        /// <summary>Stages a handler for one source with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="source">The source whose messages this registration accepts.</param>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterBroadcast<TMessage, TState>(
            InstanceId source,
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterBroadcast<TMessage>(source, binding.Invoke, priority);
        }

        /// <summary>Stages a post-processor for one source with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="source">The source whose messages this registration accepts.</param>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterBroadcastPostProcessor<TMessage, TState>(
            InstanceId source,
            TState state,
            StateHandler<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterBroadcastPostProcessor<TMessage>(source, binding.Invoke, priority);
        }

        /// <summary>Stages a handler for every target with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterTargetedWithoutTargeting<TMessage, TState>(
            TState state,
            StateHandlerWithContext<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterTargetedWithoutTargeting<TMessage>(binding.InvokeWithContext, priority);
        }

        /// <summary>Stages a post-processor for every target with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterTargetedWithoutTargetingPostProcessor<
            TMessage,
            TState
        >(TState state, StateHandlerWithContext<TMessage, TState> handler, int priority = 0)
            where TMessage : ITargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterTargetedWithoutTargetingPostProcessor<TMessage>(
                binding.InvokeWithContext,
                priority
            );
        }

        /// <summary>Stages a handler for every source with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterBroadcastWithoutSource<TMessage, TState>(
            TState state,
            StateHandlerWithContext<TMessage, TState> handler,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterBroadcastWithoutSource<TMessage>(binding.InvokeWithContext, priority);
        }

        /// <summary>Stages a post-processor for every source with explicit caller state.</summary>
        /// <typeparam name="TMessage">Message type accepted by the callback.</typeparam>
        /// <typeparam name="TState">State type. Struct state is copied; reference state remains shared.</typeparam>
        /// <param name="state">State passed to the callback, including null when the state type permits it.</param>
        /// <param name="handler">Callback to invoke with the stored state. Prefer a static callback.</param>
        /// <param name="priority">Execution order; lower values run first.</param>
        /// <returns>A token-owned handle for selective removal or group enrollment.</returns>
        /// <exception cref="ArgumentNullException">The callback is null.</exception>
        /// <remarks>
        /// Added in 4.0.0. Creates a state binding and delegate when registering. This does not make
        /// registration allocation-free. Disable/Enable and bus retargeting reuse the stored state.
        /// State is passed by value on each call; changes to value state do not update the stored copy.
        /// </remarks>
        public MessageRegistrationHandle RegisterBroadcastWithoutSourcePostProcessor<
            TMessage,
            TState
        >(TState state, StateHandlerWithContext<TMessage, TState> handler, int priority = 0)
            where TMessage : IBroadcastMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterBroadcastWithoutSourcePostProcessor<TMessage>(
                binding.InvokeWithContext,
                priority
            );
        }

        private sealed class StateBinding<TMessage, TState>
            where TMessage : IMessage
        {
            private readonly TState _state;
            private readonly StateHandler<TMessage, TState> _handler;
            private readonly StateHandlerWithContext<TMessage, TState> _contextHandler;

            /// <summary>Stores explicit state and rejects a missing callback before staging.</summary>
            internal StateBinding(TState state, StateHandler<TMessage, TState> handler)
            {
                _state = state;
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            /// <summary>Stores explicit state and rejects a missing callback before staging.</summary>
            internal StateBinding(TState state, StateHandlerWithContext<TMessage, TState> handler)
            {
                _state = state;
                _contextHandler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            internal void Invoke(in TMessage message)
            {
                _handler(_state, in message);
            }

            internal void InvokeWithContext(in InstanceId context, in TMessage message)
            {
                _contextHandler(_state, in context, in message);
            }
        }
    }
}
