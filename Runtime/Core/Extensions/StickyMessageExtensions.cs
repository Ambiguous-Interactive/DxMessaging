namespace DxMessaging.Core.Extensions
{
    using System;
    using MessageBus;
    using Messages;

    /// <summary>Accesses optional retained-state support through an IMessageBus reference.</summary>
    public static class StickyMessageExtensions
    {
        /// <summary>Retains an offered value before normal delivery on a capable bus.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="bus">Bus implementing IStickyMessageBus.</param>
        /// <param name="message">Offered state and mutable pipeline message.</param>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="NotSupportedException">The bus does not implement IStickyMessageBus.</exception>
        /// <remarks>
        /// Added in 4.0.0. Uses the storage and lifetime contract of IStickyMessageBus.
        /// <para>Performance: expected O(1) retained-state update plus normal delivery.</para>
        /// <para>Allocations: first bus/type publication creates typed storage; warm updates reuse it.</para>
        /// <para>Thread Safety: single-threaded bus ownership is required.</para>
        /// </remarks>
        public static void EmitSticky<TMessage>(this IMessageBus bus, ref TMessage message)
            where TMessage : IUntargetedMessage
        {
            RequireStickyBus(bus).EmitSticky(ref message);
        }

        /// <summary>Reads retained state from a capable bus, including a published default or null.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="bus">Bus implementing IStickyMessageBus.</param>
        /// <param name="message">Retained state, or default when absent.</param>
        /// <returns>True when this type has a retained value.</returns>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="NotSupportedException">The bus does not implement IStickyMessageBus.</exception>
        /// <remarks>
        /// <para>Performance: expected O(1).</para>
        /// <para>Allocations: none for a built-in bus lookup.</para>
        /// <para>Thread Safety: single-threaded bus ownership is required.</para>
        /// </remarks>
        public static bool TryGetLatest<TMessage>(this IMessageBus bus, out TMessage message)
            where TMessage : IUntargetedMessage
        {
            return RequireStickyBus(bus).TryGetLatest(out message);
        }

        /// <summary>Releases one retained value on a capable bus.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="bus">Bus implementing IStickyMessageBus.</param>
        /// <returns>True when a retained value was removed.</returns>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="NotSupportedException">The bus does not implement IStickyMessageBus.</exception>
        /// <remarks>
        /// <para>Performance: expected O(1).</para>
        /// <para>Allocations: none for a built-in bus clear.</para>
        /// <para>Thread Safety: single-threaded bus ownership is required.</para>
        /// </remarks>
        public static bool ClearLatest<TMessage>(this IMessageBus bus)
            where TMessage : IUntargetedMessage
        {
            return RequireStickyBus(bus).ClearLatest<TMessage>();
        }

        internal static IStickyMessageBus RequireStickyBus(IMessageBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }
            return bus as IStickyMessageBus
                ?? throw new NotSupportedException(
                    "This bus does not support retained messages. Use a bus implementing IStickyMessageBus."
                );
        }
    }
}
