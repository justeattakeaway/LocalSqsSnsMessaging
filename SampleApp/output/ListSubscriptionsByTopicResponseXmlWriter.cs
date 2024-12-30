using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListSubscriptionsByTopicResponseXmlWriter
    {
public static MemoryStream Write(ListSubscriptionsByTopicResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListSubscriptionsByTopicResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("ListSubscriptionsByTopicResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("ListSubscriptionsByTopicResult"u8);
    if (response.Subscriptions != null)
    {
utf8XmlWriter.WriteStartElement("Subscriptions"u8);
foreach (var item in response.Subscriptions)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.SubscriptionArn != null)
    {
        utf8XmlWriter.WriteElement("SubscriptionArn"u8, item.SubscriptionArn);
    }
    if (item.Owner != null)
    {
        utf8XmlWriter.WriteElement("Owner"u8, item.Owner);
    }
    if (item.Protocol != null)
    {
        utf8XmlWriter.WriteElement("Protocol"u8, item.Protocol);
    }
    if (item.Endpoint != null)
    {
        utf8XmlWriter.WriteElement("Endpoint"u8, item.Endpoint);
    }
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
