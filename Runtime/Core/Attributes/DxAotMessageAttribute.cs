namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Requests an IL2CPP startup dispatch bridge for one closed message type.
    /// </summary>
    /// <remarks>
    /// Apply this attribute to the assembly that uses the message, once per closed type.
    /// The selected type must be a concrete message accessible from that assembly.
    /// Generic definitions cannot identify a dispatch bridge; name each closed type instead.
    /// Ordinary non-generic attributed messages already generate their own startup bridges.
    /// </remarks>
    /*
        SYNC: GeneratorTestUtilities.SharedStubs and DocsSnippetCompiler.SharedStubs
        carry the public assembly opt-in signature.
    */
    [AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    public sealed class DxAotMessageAttribute : Attribute
    {
        /// <summary>
        /// Selects a closed message type for generated IL2CPP dispatch registration.
        /// </summary>
        /// <param name="messageType">The concrete message type to register.</param>
        /// <exception cref="ArgumentNullException">The type is null.</exception>
        public DxAotMessageAttribute(Type messageType)
        {
            MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));
        }

        /// <summary>
        /// Gets the concrete message type selected for registration.
        /// </summary>
        public Type MessageType { get; }
    }
}
