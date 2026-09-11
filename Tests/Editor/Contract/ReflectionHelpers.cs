namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Reflection;

    /// <summary>
    /// Shared reflection helpers for contract tests that reach into nested
    /// generic types in the production codebase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These helpers exist to prevent a recurring class of mistakes around
    /// reflection on nested types declared inside generic outer types. When a
    /// nested type uses one or more of the outer's generic parameters (or is
    /// itself generic), <see cref="System.Type.GetNestedType(string, BindingFlags)"/>
    /// invoked on a closed outer generic returns the OPEN nested type --
    /// <see cref="System.Type.ContainsGenericParameters"/> on the result is
    /// still <c>true</c>, and <see cref="System.Activator.CreateInstance(System.Type)"/>
    /// throws <see cref="System.ArgumentException"/>.
    /// </para>
    /// <para>
    /// Callers must close the nested type with the outer's generic arguments
    /// via <see cref="System.Type.MakeGenericType(System.Type[])"/> before
    /// constructing instances. This helper centralizes that contract so
    /// future contract tests do not have to rediscover the open-vs-closed
    /// distinction.
    /// </para>
    /// </remarks>
    internal static class ReflectionHelpers
    {
        /// <summary>
        /// Resolves a nested type declared inside a closed generic outer type
        /// and returns it in fully-closed form so callers can pass the result
        /// directly to <see cref="System.Activator.CreateInstance(System.Type)"/>.
        /// </summary>
        /// <remarks>
        /// This overload supports nested types whose only unbound generic
        /// parameters are inherited from the outer type. If the nested type
        /// declares ITS OWN generic parameters (for example
        /// <c>Entry&lt;U&gt;</c> nested inside <c>HandlerActionCache&lt;T&gt;</c>),
        /// this overload throws <see cref="System.InvalidOperationException"/>
        /// with a message directing the caller to the
        /// <see cref="CloseNestedGeneric(Type, string, BindingFlags, Type[])"/>
        /// overload that accepts the nested type's own arguments explicitly.
        /// </remarks>
        /// <param name="closedOuter">
        /// A fully-closed outer type whose nested type is being looked up.
        /// </param>
        /// <param name="nestedName">
        /// The unqualified name of the nested type. For nested generic types
        /// pass the arity-suffixed name (e.g. <c>"Entry`1"</c>).
        /// </param>
        /// <param name="flags">
        /// <see cref="BindingFlags"/> used for the <see cref="System.Type.GetNestedType(string, BindingFlags)"/>
        /// lookup. Typical callers pass
        /// <see cref="BindingFlags.NonPublic"/> for <c>internal</c> nested
        /// types.
        /// </param>
        /// <returns>
        /// The nested type closed against <paramref name="closedOuter"/>'s
        /// generic arguments when the nested type carries unbound generic
        /// parameters; otherwise the nested type itself. The returned
        /// <see cref="System.Type"/> always satisfies
        /// <c>!result.ContainsGenericParameters</c>.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="closedOuter"/> or
        /// <paramref name="nestedName"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when <paramref name="closedOuter"/> still contains unbound
        /// generic parameters, when the nested type cannot be found, when
        /// the nested type is generic but the outer has no generic arguments
        /// to close it with, or when the nested type declares its own
        /// generic parameters not inherited from the outer.
        /// </exception>
        public static Type CloseNestedGeneric(
            Type closedOuter,
            string nestedName,
            BindingFlags flags
        )
        {
            return CloseNestedGeneric(closedOuter, nestedName, flags, Type.EmptyTypes);
        }

        /// <summary>
        /// Resolves a nested type declared inside a closed generic outer type
        /// and returns it in fully-closed form, supplying explicit type
        /// arguments for any generic parameters the nested type declares
        /// itself (i.e. parameters not inherited from
        /// <paramref name="closedOuter"/>).
        /// </summary>
        /// <param name="closedOuter">
        /// A fully-closed outer type whose nested type is being looked up.
        /// </param>
        /// <param name="nestedName">
        /// The unqualified name of the nested type. For nested generic types
        /// pass the arity-suffixed name (e.g. <c>"Entry`1"</c>).
        /// </param>
        /// <param name="flags">
        /// <see cref="BindingFlags"/> used for the <see cref="System.Type.GetNestedType(string, BindingFlags)"/>
        /// lookup. Typical callers pass
        /// <see cref="BindingFlags.NonPublic"/> for <c>internal</c> nested
        /// types.
        /// </param>
        /// <param name="nestedOwnArgs">
        /// Type arguments supplied for any generic parameters the nested
        /// type declares itself, in declaration order. Pass
        /// <see cref="Type.EmptyTypes"/> (or rely on the three-argument
        /// overload) when the nested type does not introduce its own
        /// parameters.
        /// </param>
        /// <returns>
        /// The nested type closed against the outer's generic arguments
        /// followed by <paramref name="nestedOwnArgs"/>. The returned
        /// <see cref="System.Type"/> always satisfies
        /// <c>!result.ContainsGenericParameters</c>.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="closedOuter"/>,
        /// <paramref name="nestedName"/>, or <paramref name="nestedOwnArgs"/>
        /// is <c>null</c>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when <paramref name="closedOuter"/> still contains unbound
        /// generic parameters, when the nested type cannot be found, when
        /// the nested type is generic but the outer has no generic arguments
        /// to close it with, or when <paramref name="nestedOwnArgs"/> does
        /// not match the count of generic parameters the nested type
        /// declares itself.
        /// </exception>
        public static Type CloseNestedGeneric(
            Type closedOuter,
            string nestedName,
            BindingFlags flags,
            Type[] nestedOwnArgs
        )
        {
            if (closedOuter == null)
            {
                throw new ArgumentNullException(nameof(closedOuter));
            }
            if (nestedName == null)
            {
                throw new ArgumentNullException(nameof(nestedName));
            }
            if (nestedOwnArgs == null)
            {
                throw new ArgumentNullException(nameof(nestedOwnArgs));
            }
            if (closedOuter.ContainsGenericParameters)
            {
                throw new InvalidOperationException(
                    "CloseNestedGeneric requires a fully-closed outer Type; received "
                        + closedOuter.FullName
                        + " which still contains unbound generic parameters."
                );
            }

            Type nested = closedOuter.GetNestedType(nestedName, flags);
            if (nested == null)
            {
                throw new InvalidOperationException(
                    "Nested type '"
                        + nestedName
                        + "' was not found on "
                        + closedOuter.FullName
                        + " with binding flags "
                        + flags
                        + "."
                );
            }

            return Close(closedOuter, nested, nestedOwnArgs);
        }

        /// <summary>
        /// Closes <paramref name="openNested"/> against the generic arguments
        /// of <paramref name="closedOuter"/> and returns the fully-closed
        /// nested type. Use this overload when callers already hold the open
        /// nested <see cref="Type"/> (for example obtained from a different
        /// reflection lookup) and want to reuse the close-only logic without
        /// redoing the name-based lookup.
        /// </summary>
        /// <param name="closedOuter">
        /// The fully-closed outer type whose generic arguments will be used
        /// to close any of <paramref name="openNested"/>'s generic
        /// parameters that were inherited from the outer.
        /// </param>
        /// <param name="openNested">
        /// The nested type as returned by
        /// <see cref="System.Type.GetNestedType(string, BindingFlags)"/>
        /// (i.e. potentially still open).
        /// </param>
        /// <returns>
        /// <paramref name="openNested"/> closed against
        /// <paramref name="closedOuter"/>'s generic arguments when the
        /// nested type carries unbound generic parameters; otherwise
        /// <paramref name="openNested"/> itself. The returned
        /// <see cref="System.Type"/> always satisfies
        /// <c>!result.ContainsGenericParameters</c>.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="closedOuter"/> or
        /// <paramref name="openNested"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when <paramref name="closedOuter"/> still contains unbound
        /// generic parameters, when the nested type is generic but the
        /// outer has no generic arguments to close it with, or when the
        /// nested type declares its own generic parameters not inherited
        /// from the outer (use the
        /// <see cref="Close(Type, Type, Type[])"/> overload in that case).
        /// </exception>
        public static Type Close(Type closedOuter, Type openNested)
        {
            return Close(closedOuter, openNested, Type.EmptyTypes);
        }

        /// <summary>
        /// Closes <paramref name="openNested"/> against the generic arguments
        /// of <paramref name="closedOuter"/> together with the explicit
        /// arguments supplied for any generic parameters the nested type
        /// declares itself. Use this overload when callers already hold the
        /// open nested <see cref="Type"/> and the nested type introduces its
        /// own generic parameters.
        /// </summary>
        /// <param name="closedOuter">
        /// The fully-closed outer type whose generic arguments will be used
        /// to close any of <paramref name="openNested"/>'s generic
        /// parameters that were inherited from the outer.
        /// </param>
        /// <param name="openNested">
        /// The nested type as returned by
        /// <see cref="System.Type.GetNestedType(string, BindingFlags)"/>.
        /// </param>
        /// <param name="nestedOwnArgs">
        /// Type arguments supplied for any generic parameters the nested
        /// type declares itself, in declaration order. Pass
        /// <see cref="Type.EmptyTypes"/> when the nested type does not
        /// introduce its own parameters.
        /// </param>
        /// <returns>
        /// The fully-closed nested type. The returned
        /// <see cref="System.Type"/> always satisfies
        /// <c>!result.ContainsGenericParameters</c>.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when any argument is <c>null</c>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when <paramref name="closedOuter"/> still contains unbound
        /// generic parameters, when the nested type is generic but the
        /// outer has no generic arguments to close it with, or when
        /// <paramref name="nestedOwnArgs"/> does not match the count of
        /// generic parameters the nested type declares itself.
        /// </exception>
        public static Type Close(Type closedOuter, Type openNested, Type[] nestedOwnArgs)
        {
            if (closedOuter == null)
            {
                throw new ArgumentNullException(nameof(closedOuter));
            }
            if (openNested == null)
            {
                throw new ArgumentNullException(nameof(openNested));
            }
            if (nestedOwnArgs == null)
            {
                throw new ArgumentNullException(nameof(nestedOwnArgs));
            }
            if (closedOuter.ContainsGenericParameters)
            {
                throw new InvalidOperationException(
                    "Close requires a fully-closed outer Type; received "
                        + closedOuter.FullName
                        + " which still contains unbound generic parameters."
                );
            }

            /*
                A non-generic nested type returned by GetNestedType has no
                generic parameters at all; nothing to close.
            */
            if (!openNested.ContainsGenericParameters)
            {
                if (nestedOwnArgs.Length != 0)
                {
                    throw new InvalidOperationException(
                        "Nested type '"
                            + openNested.FullName
                            + "' is non-generic but "
                            + nestedOwnArgs.Length
                            + " nested own type argument(s) were supplied."
                    );
                }
                return openNested;
            }

            Type[] outerArgs = closedOuter.GetGenericArguments();
            /*
                The nested type's full generic-argument list is laid out as:
                first the outer's generic parameters (inherited), then any
                generic parameters the nested type declares itself. Per .NET
                metadata layout the nested type's own arity is
                (totalArgs - outerArity); the arity-suffixed nested name
                (e.g. "Entry`1") encodes the same number.
            */
            Type[] nestedAllArgs = openNested.GetGenericArguments();
            int outerArity = outerArgs.Length;
            int totalArity = nestedAllArgs.Length;
            int nestedOwnArity = totalArity - outerArity;

            if (nestedOwnArity < 0)
            {
                throw new InvalidOperationException(
                    "Nested type '"
                        + openNested.FullName
                        + "' has fewer generic parameters ("
                        + totalArity
                        + ") than the outer type "
                        + closedOuter.FullName
                        + " supplies ("
                        + outerArity
                        + "); the nested type does not appear to be declared "
                        + "inside this outer."
                );
            }

            if (0 < totalArity && outerArity == 0 && nestedOwnArgs.Length == 0)
            {
                throw new InvalidOperationException(
                    "Nested type '"
                        + openNested.FullName
                        + "' has unbound generic parameters but the outer type "
                        + closedOuter.FullName
                        + " is non-generic; cannot close the nested type."
                );
            }

            if (nestedOwnArity != nestedOwnArgs.Length)
            {
                throw new InvalidOperationException(
                    "Nested type '"
                        + openNested.FullName
                        + "' declares "
                        + nestedOwnArity
                        + " generic parameter(s) of its own (in addition to "
                        + outerArity
                        + " inherited from "
                        + closedOuter.FullName
                        + "); use the overload that accepts them explicitly "
                        + "and pass exactly "
                        + nestedOwnArity
                        + " argument(s) (received "
                        + nestedOwnArgs.Length
                        + ")."
                );
            }

            Type[] composed = new Type[totalArity];
            for (int i = 0; i < outerArity; ++i)
            {
                composed[i] = outerArgs[i];
            }
            for (int i = 0; i < nestedOwnArity; ++i)
            {
                composed[outerArity + i] = nestedOwnArgs[i];
            }

            Type definition = openNested.IsGenericTypeDefinition
                ? openNested
                : openNested.GetGenericTypeDefinition();
            return definition.MakeGenericType(composed);
        }
    }
}
