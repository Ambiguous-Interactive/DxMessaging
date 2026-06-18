namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Marks a field as optional when used with <see cref="DxAutoConstructorAttribute"/>.
    /// </summary>
    /// <remarks>
    /// The source generator will emit a constructor parameter with a default value for fields annotated
    /// with this attribute. This is helpful for messages with sensible defaults.
    /// Overloads accepting explicit default values require compile-time constants and are validated by
    /// the source generator against the field type.
    /// </remarks>
    /// <example>
    /// <code>
    /// [DxAutoConstructor]
    /// public readonly partial struct Example
    /// {
    ///     public readonly int required;
    ///     [DxOptionalParameter] public readonly int optional; // defaults to 0
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class DxOptionalParameterAttribute : Attribute
    {
        /// <summary>
        /// Marks the field as optional with the type's default value.
        /// </summary>
        public DxOptionalParameterAttribute() { }

        /// <summary>
        /// Initializes the attribute with the specified default boolean value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(bool value) { }

        /// <summary>
        /// Initializes the attribute with the specified default character value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(char value) { }

        /// <summary>
        /// Initializes the attribute with the specified default string value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(string value) { }

        /// <summary>
        /// Initializes the attribute with the specified default byte value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(byte value) { }

        /// <summary>
        /// Initializes the attribute with the specified default signed byte value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(sbyte value) { }

        /// <summary>
        /// Initializes the attribute with the specified default short value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(short value) { }

        /// <summary>
        /// Initializes the attribute with the specified default unsigned short value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(ushort value) { }

        /// <summary>
        /// Initializes the attribute with the specified default integer value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(int value) { }

        /// <summary>
        /// Initializes the attribute with the specified default unsigned integer value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(uint value) { }

        /// <summary>
        /// Initializes the attribute with the specified default long value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(long value) { }

        /// <summary>
        /// Initializes the attribute with the specified default unsigned long value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(ulong value) { }

        /// <summary>
        /// Initializes the attribute with the specified default single-precision floating point value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(float value) { }

        /// <summary>
        /// Initializes the attribute with the specified default double-precision floating point value.
        /// </summary>
        /// <param name="value">Default value used when the constructor parameter is omitted.</param>
        public DxOptionalParameterAttribute(double value) { }

        /// <summary>
        /// Advanced: supply a C# expression to use as the default value.
        /// For example: <c>Expression = "null"</c>, <c>Expression = "nameof(SomeConst)"</c>, or <c>Expression = "MyEnum.Value"</c>.
        /// The generator inserts this expression verbatim and the C# compiler enforces type safety.
        /// </summary>
        public string Expression { get; set; }
    }
}
