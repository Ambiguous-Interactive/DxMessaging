namespace DxMessaging.Core.MessageBus
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Logs MessageHandler registrations for diagnostics.
    /// </summary>
    /// <remarks>
    /// Disabled by default for performance. Set <see cref="Enabled"/> to true to start collecting
    /// <see cref="MessagingRegistration"/> entries, then inspect via <see cref="Registrations"/>,
    /// <see cref="GetRegistrations"/>, or <see cref="ToString()"/>.
    /// </remarks>
    public sealed class RegistrationLog
    {
        public IReadOnlyList<MessagingRegistration> Registrations =>
            _finalizedRegistrations ??= new List<MessagingRegistration>();

        private List<MessagingRegistration> _finalizedRegistrations;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        private bool _enabled;

        /// <summary>
        /// Creates a new registration log.
        /// </summary>
        /// <param name="enabled">
        /// When <c>true</c>, logging starts immediately; otherwise call <see cref="Enabled"/> to enable later.
        /// </param>
        public RegistrationLog(bool enabled = false)
        {
            _enabled = enabled;
        }

        /// <summary>
        /// Logs a MessagingRegistration.
        /// </summary>
        /// <param name="registration">MessagingRegistration to record.</param>
        public void Log(MessagingRegistration registration)
        {
            if (!_enabled)
            {
                return;
            }
            (_finalizedRegistrations ??= new List<MessagingRegistration>()).Add(registration);
        }

        /// <summary>
        /// Retrieves all registrations for the provided InstanceId.
        /// </summary>
        /// <param name="instanceId">InstanceId to search for.</param>
        /// <returns>All registrations for the provided InstanceId.</returns>
        public IEnumerable<MessagingRegistration> GetRegistrations(InstanceId instanceId)
        {
            List<MessagingRegistration> registrations = _finalizedRegistrations;
            if (registrations == null)
            {
                yield break;
            }

            for (int i = 0; i < registrations.Count; ++i)
            {
                MessagingRegistration registration = registrations[i];
                if (registration.id == instanceId)
                {
                    yield return registration;
                }
            }
        }

        /// <summary>
        /// Pretty-print all of the logged Messaging registrations using the provided print function.
        /// </summary>
        /// <param name="serializer">Serialization function to use. If null, defaults to MessagingRegistration.ToString.</param>
        /// <returns>The string representing all logged MessagingRegistrations.</returns>
        public string ToString(Func<MessagingRegistration, string> serializer)
        {
            List<MessagingRegistration> finalizedRegistrations = _finalizedRegistrations;
            if (finalizedRegistrations == null || finalizedRegistrations.Count == 0)
            {
                return "[]";
            }

            serializer ??= registration => registration.ToString();

            StringBuilder registrations = new();
            _ = registrations.Append('[');
            for (int i = 0; i < finalizedRegistrations.Count; ++i)
            {
                if (0 < i)
                {
                    _ = registrations.Append(", ");
                }
                MessagingRegistration finalizedRegistration = finalizedRegistrations[i];
                string prettyFinalizedRegistration = serializer(finalizedRegistration);
                _ = registrations.Append(prettyFinalizedRegistration);
            }
            _ = registrations.Append(']');
            return registrations.ToString();
        }

        /// <summary>
        /// Serializes the log using the default formatter (<see cref="MessagingRegistration.ToString"/>).
        /// </summary>
        /// <returns>String containing all recorded registrations.</returns>
        public override string ToString()
        {
            return ToString(null);
        }

        /// <summary>
        /// Removes all MessagingRegistrations that satisfy the provided function, or all registrations if no function is provided.
        /// </summary>
        /// <param name="shouldRemove">Null if all MessagingRegistrations should be removed, or a custom function that returns true for any MessagingRegistration that should be removed.</param>
        /// <returns>Number of MessagingRegistrations removed.</returns>
        public int Clear(Predicate<MessagingRegistration> shouldRemove = null)
        {
            List<MessagingRegistration> finalizedRegistrations = _finalizedRegistrations;
            if (finalizedRegistrations == null)
            {
                return 0;
            }

            if (shouldRemove == null)
            {
                int currentCount = finalizedRegistrations.Count;
                finalizedRegistrations.Clear();
                return currentCount;
            }

            return finalizedRegistrations.RemoveAll(shouldRemove);
        }
    }
}
