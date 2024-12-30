
using System;
using System.IO;
using System.Xml;
using Amazon.Runtime;

namespace Amazon.SimpleNotificationService.Model
{
    internal static class SnsXmlWriterHelpers
    {
        public static XmlWriterSettings DefaultSettings => new XmlWriterSettings 
        { 
            Indent = true,
            IndentChars = "    ",
            NamespaceHandling = NamespaceHandling.OmitDuplicates,
            OmitXmlDeclaration = true,
            Encoding = Encoding.UTF8
        };

        public static void WriteResponseMetadata(Utf8XmlWriter writer, AmazonWebServiceResponse response)
        {
            writer.WriteStartElement("ResponseMetadata"u8);
            writer.WriteElement("RequestId"u8, response.ResponseMetadata.RequestId);
            writer.WriteEndElement();
        }
    }
}