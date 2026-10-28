namespace DxMessaging.Tests.Runtime.Core
{
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;
    using System.Xml;
    using DxMessaging.Core;
    using NUnit.Framework;

    [TestFixture]
    public sealed class InstanceIdDataContractTests
    {
        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(int.MaxValue)]
        public void XmlRoundTripPreservesNumericIdentityAndWireMember(int value)
        {
            InstanceId original = new(value);
            DataContractSerializer serializer = new(typeof(InstanceId));
            using MemoryStream stream = new();
            serializer.WriteObject(stream, original);
            string xml = Encoding.UTF8.GetString(stream.ToArray());
            XmlDocument document = new();
            document.LoadXml(xml);
            XmlElement root = document.DocumentElement;
            Assert.That(root.LocalName, Is.EqualTo("InstanceId"), $"value={value}");
            Assert.That(root.ChildNodes.Count, Is.EqualTo(1), $"value={value}; xml={xml}");
            Assert.That(root.FirstChild.LocalName, Is.EqualTo("id"), $"value={value}; xml={xml}");
            Assert.That(
                root.FirstChild.InnerText,
                Is.EqualTo(value.ToString(CultureInfo.InvariantCulture)),
                $"value={value}; xml={xml}"
            );
            stream.Position = 0;
            InstanceId restored = (InstanceId)serializer.ReadObject(stream);
            Assert.That(restored.Id, Is.EqualTo(value), $"value={value}");
            Assert.That(restored, Is.EqualTo(original), $"value={value}");
            Assert.That(
                restored.GetHashCode(),
                Is.EqualTo(original.GetHashCode()),
                $"value={value}"
            );
        }

        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(int.MaxValue)]
        public void XmlReadsIndependentNumericWireValue(int value)
        {
            string xml =
                "<InstanceId xmlns=\"http://schemas.datacontract.org/2004/07/DxMessaging.Core\"><id>"
                + value.ToString(CultureInfo.InvariantCulture)
                + "</id></InstanceId>";
            DataContractSerializer serializer = new(typeof(InstanceId));
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
            InstanceId restored = (InstanceId)serializer.ReadObject(stream);
            Assert.That(restored.Id, Is.EqualTo(value), $"value={value}; xml={xml}");
            Assert.That(restored, Is.EqualTo(new InstanceId(value)), $"value={value}; xml={xml}");
        }

        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(int.MaxValue)]
        public void JsonRoundTripPreservesNumericIdentityAndWireMember(int value)
        {
            InstanceId original = new(value);
            DataContractJsonSerializer serializer = new(typeof(InstanceId));
            using MemoryStream stream = new();
            serializer.WriteObject(stream, original);
            string json = Encoding.UTF8.GetString(stream.ToArray());
            Assert.That(
                json,
                Is.EqualTo("{\"id\":" + value.ToString(CultureInfo.InvariantCulture) + "}"),
                $"value={value}; json={json}"
            );
            stream.Position = 0;
            InstanceId restored = (InstanceId)serializer.ReadObject(stream);
            Assert.That(restored.Id, Is.EqualTo(value), $"value={value}");
            Assert.That(restored, Is.EqualTo(original), $"value={value}");
            Assert.That(
                restored.GetHashCode(),
                Is.EqualTo(original.GetHashCode()),
                $"value={value}"
            );
        }
    }
}
