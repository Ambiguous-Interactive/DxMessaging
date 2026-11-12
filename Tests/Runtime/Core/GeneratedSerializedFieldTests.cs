#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Reflection;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>Exercises the packaged generator and Unity's real private-field serializer.</summary>
    [TestFixture]
    public sealed partial class GeneratedSerializedFieldTests
    {
        [TestCase(0, false)]
        [TestCase(-1, true)]
        [TestCase(int.MaxValue, false)]
        [TestCase(int.MinValue, true)]
        public void GeneratedGettersAndConstructorRetainSerializedStorage(
            int count,
            bool explicitLabel
        )
        {
            Settings settings = explicitLabel
                ? new Settings(count: count, mode: ScreenMode.Play, label: "manual")
                : new Settings(count: count, mode: ScreenMode.Play);
            string expectedLabel = explicitLabel ? "manual" : "unset";
            string context = $"count={count}, explicitLabel={explicitLabel}";
            Assert.That(settings.Count, Is.EqualTo(count), context);
            Assert.That(settings.Mode, Is.EqualTo(ScreenMode.Play), context);
            Assert.That(settings.Label, Is.EqualTo(expectedLabel), context);

            string json = JsonUtility.ToJson(settings);
            Assert.That(
                json,
                Does.Contain("\"_count\"").And.Contain("\"_mode\"").And.Contain("\"_label\""),
                context
            );
            Assert.That(
                json,
                Does.Not.Contain("\"Count\"")
                    .And.Not.Contain("\"Mode\"")
                    .And.Not.Contain("\"Label\""),
                context
            );
            Settings restored = JsonUtility.FromJson<Settings>(json);
            Assert.That(restored.Count, Is.EqualTo(count), context);
            Assert.That(restored.Mode, Is.EqualTo(ScreenMode.Play), context);
            Assert.That(restored.Label, Is.EqualTo(expectedLabel), context);
        }

        [Test]
        public void SerializationDoesNotApplyGeneratedConstructorDefaults()
        {
            Settings restored = JsonUtility.FromJson<Settings>("{\"_count\":17,\"_mode\":1}");
            Assert.That(restored.Count, Is.EqualTo(17));
            Assert.That(restored.Mode, Is.EqualTo(ScreenMode.Play));
            Assert.That(
                restored.Label,
                Is.Null,
                "Unity restores storage without invoking the optional-argument constructor."
            );
            Assert.That(new Settings(count: 17, mode: ScreenMode.Play).Label, Is.EqualTo("unset"));
        }

        [Test]
        public void GeneratedPropertiesAreReadOnlyAndOriginalFieldsKeepTheirAttributes()
        {
            Type type = typeof(Settings);
            foreach (string name in new[] { "Count", "Mode", "Label" })
            {
                PropertyInfo property = type.GetProperty(name);
                Assert.That(property, Is.Not.Null, name);
                Assert.That(property.CanWrite, Is.False, name);
            }
            foreach (string name in new[] { "_count", "_mode", "_label" })
            {
                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
                Assert.That(field, Is.Not.Null, name);
                Assert.That(field.IsPrivate, Is.True, name);
                Assert.That(
                    field.GetCustomAttributes(typeof(SerializeField), false),
                    Has.Length.EqualTo(1),
                    name
                );
            }
        }

        [TestCase(0)]
        [TestCase(42)]
        public void ConstructorOnlyOptInKeepsExistingGettersAndFieldInitializers(int count)
        {
            ExistingGetters settings = new ExistingGetters(count: count);
            string context = $"count={count}";
            Assert.That(settings.Count, Is.EqualTo(count), context);
            Assert.That(settings.Sentinel, Is.EqualTo(73), context);
            ExistingGetters restored = JsonUtility.FromJson<ExistingGetters>(
                JsonUtility.ToJson(settings)
            );
            Assert.That(restored.Count, Is.EqualTo(count), context);
        }

        [Test]
        public void SerializedGettersComposeWithGeneratedMessageMetadata()
        {
            Score score = new Score(count: 42);
            Assert.That(score.Count, Is.EqualTo(42));
            Assert.That(score.MessageType, Is.EqualTo(typeof(Score)));
        }

        public enum ScreenMode
        {
            None = 0,
            Play = 1,
        }

        [Serializable, DxUntargetedMessage, DxAutoConstructor(GenerateSerializedProperties = true)]
        public partial struct Score
        {
            [SerializeField]
            private int _count;
        }

        [Serializable]
        [DxAutoConstructor(GenerateSerializedProperties = true)]
        public partial struct Settings
        {
            [SerializeField]
            private int _count;

            [SerializeField]
            private ScreenMode _mode;

            [SerializeField, DxOptionalParameter("unset")]
            private string _label;
        }

        [Serializable]
        [DxAutoConstructor(IncludeSerializedFields = true)]
        public sealed partial class ExistingGetters
        {
            [SerializeField]
            private int _count;

            private readonly int _sentinel = 73;

            public int Count => _count;
            public int Sentinel => _sentinel;
        }
    }
}
#endif
