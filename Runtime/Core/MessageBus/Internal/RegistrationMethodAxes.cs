namespace DxMessaging.Core.MessageBus.Internal
{
    using System;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Static lookup that translates a <see cref="RegistrationMethod"/> enum
    /// value into the <see cref="SlotKey"/> coordinate that identifies the
    /// dispatch slot it targets. Replaces the legacy 14-case category switch
    /// with a data-driven table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two registration methods --
    /// <see cref="RegistrationMethod.Interceptor"/> and
    /// <see cref="RegistrationMethod.GlobalAcceptAll"/> -- map to
    /// <see cref="SlotKey.None"/>. Interceptor caches are deliberately out of
    /// scope for the slot grid (their machinery is separate); global accept-all
    /// is structurally a multi-slot registration handled by dedicated bus
    /// state.
    /// </para>
    /// <para>
    /// The table is validated once at type-init: every value of
    /// <see cref="RegistrationMethod"/> must have an entry, otherwise an
    /// <see cref="InvalidOperationException"/> is thrown. This guarantees that
    /// adding a new registration method without wiring its axis is a
    /// load-time failure, not a silent dispatch hole.
    /// </para>
    /// </remarks>
    internal static class RegistrationMethodAxes
    {
        private static readonly SlotKey[] Table = BuildAndValidateTable();

        /// <summary>
        /// Returns the <see cref="SlotKey"/> for the supplied registration
        /// method. Returns <see cref="SlotKey.None"/> when the method is
        /// out-of-range or maps to the multi-slot / out-of-scope sentinel.
        /// </summary>
        /// <param name="method">The registration method to translate.</param>
        /// <returns>
        /// The mapped <see cref="SlotKey"/>, or <see cref="SlotKey.None"/> when
        /// the method has no single-slot mapping.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SlotKey GetSlotKey(RegistrationMethod method)
        {
            uint index = (uint)(int)method;
            if ((uint)Table.Length <= index)
            {
                return SlotKey.None;
            }
            return Table[index];
        }

        private static SlotKey[] BuildAndValidateTable()
        {
            Array values = Enum.GetValues(typeof(RegistrationMethod));
            int max = -1;
            for (int i = 0; i < values.Length; i++)
            {
                int raw = (int)values.GetValue(i);
                if (raw < 0)
                {
                    throw new InvalidOperationException(
                        "RegistrationMethod enum contains a negative value: "
                            + values.GetValue(i)
                            + ". RegistrationMethodAxes assumes non-negative ordinals."
                    );
                }
                if (max < raw)
                {
                    max = raw;
                }
            }

            SlotKey[] table = new SlotKey[max + 1];
            for (int i = 0; i <= max; i++)
            {
                table[i] = SlotKey.None;
            }
            bool[] assigned = new bool[max + 1];

            void Assign(RegistrationMethod method, SlotKey key)
            {
                int idx = (int)method;
                if (assigned[idx])
                {
                    throw new InvalidOperationException(
                        "RegistrationMethodAxes table assigned twice for "
                            + method
                            + ". Mapping must be unique."
                    );
                }
                table[idx] = key;
                assigned[idx] = true;
            }

            // Handle phase, default variant.
            Assign(
                RegistrationMethod.Targeted,
                new SlotKey(DispatchKind.Targeted, DispatchPhase.Handle, DispatchVariant.Default)
            );
            Assign(
                RegistrationMethod.Untargeted,
                new SlotKey(DispatchKind.Untargeted, DispatchPhase.Handle, DispatchVariant.Default)
            );
            Assign(
                RegistrationMethod.Broadcast,
                new SlotKey(DispatchKind.Broadcast, DispatchPhase.Handle, DispatchVariant.Default)
            );

            // Handle phase, without-context variant.
            Assign(
                RegistrationMethod.BroadcastWithoutSource,
                new SlotKey(
                    DispatchKind.Broadcast,
                    DispatchPhase.Handle,
                    DispatchVariant.WithoutContext
                )
            );
            Assign(
                RegistrationMethod.TargetedWithoutTargeting,
                new SlotKey(
                    DispatchKind.Targeted,
                    DispatchPhase.Handle,
                    DispatchVariant.WithoutContext
                )
            );

            // Sentinel mappings: multi-slot / out-of-scope.
            Assign(RegistrationMethod.GlobalAcceptAll, SlotKey.None);
            Assign(RegistrationMethod.Interceptor, SlotKey.None);

            // Post-process phase, default variant.
            Assign(
                RegistrationMethod.UntargetedPostProcessor,
                new SlotKey(
                    DispatchKind.Untargeted,
                    DispatchPhase.PostProcess,
                    DispatchVariant.Default
                )
            );
            Assign(
                RegistrationMethod.TargetedPostProcessor,
                new SlotKey(
                    DispatchKind.Targeted,
                    DispatchPhase.PostProcess,
                    DispatchVariant.Default
                )
            );
            Assign(
                RegistrationMethod.BroadcastPostProcessor,
                new SlotKey(
                    DispatchKind.Broadcast,
                    DispatchPhase.PostProcess,
                    DispatchVariant.Default
                )
            );

            // Post-process phase, without-context variant.
            Assign(
                RegistrationMethod.TargetedWithoutTargetingPostProcessor,
                new SlotKey(
                    DispatchKind.Targeted,
                    DispatchPhase.PostProcess,
                    DispatchVariant.WithoutContext
                )
            );
            Assign(
                RegistrationMethod.BroadcastWithoutSourcePostProcessor,
                new SlotKey(
                    DispatchKind.Broadcast,
                    DispatchPhase.PostProcess,
                    DispatchVariant.WithoutContext
                )
            );

            // Validate that every enum value received an explicit mapping.
            for (int i = 0; i < values.Length; i++)
            {
                int raw = (int)values.GetValue(i);
                if (!assigned[raw])
                {
                    throw new InvalidOperationException(
                        "RegistrationMethodAxes is missing a mapping for "
                            + (RegistrationMethod)raw
                            + " (ordinal "
                            + raw
                            + "). Every RegistrationMethod must map to a SlotKey or SlotKey.None."
                    );
                }
            }

            /*
                Tighten validation: walk every ordinal in [0..max]. Any unassigned
                index that does NOT correspond to a defined enum value is a gap
                (e.g. left behind by an [Obsolete]-removed member or sparse enum
                numbering) and would otherwise silently route into the Untargeted
                slot via default(SlotKey). SlotKey.None is the only safe sentinel
                for unmapped ordinals; gap ordinals must fail at type-init.
            */
            bool[] defined = new bool[max + 1];
            for (int i = 0; i < values.Length; i++)
            {
                defined[(int)values.GetValue(i)] = true;
            }
            for (int i = 0; i <= max; i++)
            {
                if (!assigned[i] && !defined[i])
                {
                    throw new InvalidOperationException(
                        "RegistrationMethodAxes ordinal "
                            + i
                            + " between defined values is unassigned. "
                            + "Sparse RegistrationMethod ordinals are not supported; "
                            + "every ordinal in [0..max] must map to a SlotKey or SlotKey.None."
                    );
                }
            }

            return table;
        }
    }
}
