namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>
    /// Exercises generated constructors on supported Unity language versions.
    /// </summary>
    /// <remarks>
    /// 2026-11-03: Excluded struct storage formerly prevented generated constructors from compiling.
    /// </remarks>
    public sealed partial class GeneratedStructInitializationTests
    {
        public enum Storage
        {
            Private,
            Internal,
            AutoProperty,
            ExplicitProperty,
            Event,
            GenericReference,
            RefLike,
        }

        [TestCase(Storage.Private)]
        [TestCase(Storage.Internal)]
        [TestCase(Storage.AutoProperty)]
        [TestCase(Storage.ExplicitProperty)]
        [TestCase(Storage.Event)]
        [TestCase(Storage.GenericReference)]
        [TestCase(Storage.RefLike)]
        public void GeneratedConstructorDefaultsOnlyExcludedStorage(Storage storage)
        {
            int[] actual;
            int seededState = 73;
            switch (storage)
            {
                case Storage.Private:
                    PrivateState privateValue = new(42);
                    PrivateState privateSeed = new(true);
                    actual = new[]
                    {
                        privateValue.Value,
                        privateValue.State,
                        privateSeed.Value,
                        privateSeed.State,
                    };
                    break;
                case Storage.Internal:
                    InternalState internalValue = new(42);
                    InternalState internalSeed = new(true);
                    actual = new[]
                    {
                        internalValue.Value,
                        internalValue.State,
                        internalSeed.Value,
                        internalSeed.State,
                    };
                    break;
                case Storage.AutoProperty:
                    PropertyState propertyValue = new(42);
                    PropertyState propertySeed = new(true);
                    actual = new[]
                    {
                        propertyValue.Value,
                        propertyValue.State,
                        propertySeed.Value,
                        propertySeed.State,
                    };
                    break;
                case Storage.ExplicitProperty:
                    ExplicitState explicitValue = new(42);
                    ExplicitState explicitSeed = new(true);
                    actual = new[]
                    {
                        explicitValue.Value,
                        ((IState)explicitValue).State,
                        explicitSeed.Value,
                        ((IState)explicitSeed).State,
                    };
                    seededState = 0;
                    break;
                case Storage.Event:
                    EventState eventValue = new(42);
                    EventState eventSeed = new(true);
                    actual = new[]
                    {
                        eventValue.Value,
                        eventValue.State,
                        eventSeed.Value,
                        eventSeed.State,
                    };
                    seededState = 1;
                    break;
                case Storage.GenericReference:
                    GenericState<string> genericValue = new(42);
                    GenericState<string> genericSeed = new(true, "seed");
                    Assert.That(
                        genericValue.State,
                        Is.Null,
                        "Generated generic reference state must start null."
                    );
                    Assert.That(
                        genericSeed.State,
                        Is.EqualTo("seed"),
                        "Existing constructor must keep its reference."
                    );
                    actual = new[] { genericValue.Value, 0, genericSeed.Value, 73 };
                    break;
                case Storage.RefLike:
                    RefState refValue = new(42);
                    RefState refSeed = new(true);
                    actual = new[] { refValue.Value, refValue.State, refSeed.Value, refSeed.State };
                    seededState = 1;
                    break;
                default:
                    Assert.Fail($"Unknown storage shape: {storage}");
                    return;
            }
            Assert.That(
                actual,
                Is.EqualTo(new[] { 42, 0, -1, seededState }),
                $"{storage}: preserve generated payload and seeded constructor state; default excluded storage."
            );
        }

        [DxAutoConstructor]
        private readonly partial struct PrivateState
        {
            public readonly int Value;
            private readonly int _state;
            public int State => _state;

            public PrivateState(bool seeded)
            {
                Value = -1;
                _state = 73;
            }
        }

        [DxAutoConstructor]
        private readonly partial struct InternalState
        {
            public readonly int Value;
            internal readonly int State;

            public InternalState(bool seeded)
            {
                Value = -1;
                State = 73;
            }
        }

        [DxAutoConstructor]
        private readonly partial struct PropertyState
        {
            public readonly int Value;
            public int State { get; }

            public PropertyState(bool seeded)
            {
                Value = -1;
                State = 73;
            }
        }

        private interface IState
        {
            int State { get; }
        }

        [DxAutoConstructor]
        private readonly partial struct ExplicitState : IState
        {
            public readonly int Value;
            int IState.State { get; }

            public ExplicitState(bool seeded)
            {
                this = default;
                Value = -1;
            }
        }

        [DxAutoConstructor]
        private partial struct EventState
        {
            public readonly int Value;
            public event Action Callbacks;
            private static int _calls;

            public int State
            {
                get
                {
                    int before = _calls;
                    Callbacks?.Invoke();
                    return _calls - before;
                }
            }

            public EventState(bool seeded)
            {
                Value = -1;
                Callbacks = Callback;
            }

            private static void Callback()
            {
                ++_calls;
            }
        }

        [DxAutoConstructor]
        private readonly partial struct GenericState<T>
        {
            public readonly int Value;
            private readonly T _state;
            public T State => _state;

            public GenericState(bool seeded, T state)
            {
                Value = -1;
                _state = state;
            }
        }

        [DxAutoConstructor]
        private ref partial struct RefState
        {
            public readonly int Value;
            private readonly Span<int> _state;
            public int State => _state.Length;

            public RefState(bool seeded)
            {
                Value = -1;
                _state = new[] { 1 };
            }
        }
    }
}
