using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListTopicsResponseXmlWriter
    {
public static MemoryStream Write(ListTopicsResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListTopicsResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("ListTopicsResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("ListTopicsResult"u8);
    if (response.Topics != null)
    {
utf8XmlWriter.WriteStartElement("Topics"u8);
foreach (var item in response.Topics)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.TopicArn != null)
    {
        utf8XmlWriter.WriteElement("TopicArn"u8, item.TopicArn);
    }
    utf8XmlWriter.WriteEndElement(); // member
}
utf8XmlWriter.WriteEndElement(); // collection
    }
    if (response.NextToken != null)
    {
        utf8XmlWriter.WriteElement("NextToken"u8, response.NextToken);
    }
    utf8XmlWriter.WriteEndElement(); // Result wrapper
    
    SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
    utf8XmlWriter.WriteEndElement(); // Response
    utf8XmlWriter.Flush();
    
    memoryStream.Position = 0;
    return memoryStream;
}
    }
}
