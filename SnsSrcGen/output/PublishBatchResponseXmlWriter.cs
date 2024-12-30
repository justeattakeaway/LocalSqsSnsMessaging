using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class PublishBatchResponseXmlWriter
    {
public static MemoryStream Write(PublishBatchResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(PublishBatchResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("PublishBatchResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("PublishBatchResult"u8);
    if (response.Successful != null)
    {
utf8XmlWriter.WriteStartElement("Successful"u8);
foreach (var item in response.Successful)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.Id != null)
    {
        utf8XmlWriter.WriteElement("Id"u8, item.Id);
    }
    if (item.MessageId != null)
    {
        utf8XmlWriter.WriteElement("MessageId"u8, item.MessageId);
    }
    if (item.SequenceNumber != null)
    {
        utf8XmlWriter.WriteElement("SequenceNumber"u8, item.SequenceNumber);
    }
    utf8XmlWriter.WriteEndElement(); // member
}
utf8XmlWriter.WriteEndElement(); // collection
    }
    if (response.Failed != null)
    {
utf8XmlWriter.WriteStartElement("Failed"u8);
foreach (var item in response.Failed)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.Id != null)
    {
        utf8XmlWriter.WriteElement("Id"u8, item.Id);
    }
    if (item.Code != null)
    {
        utf8XmlWriter.WriteElement("Code"u8, item.Code);
    }
    if (item.Message != null)
    {
        utf8XmlWriter.WriteElement("Message"u8, item.Message);
    }
    if (item.SenderFault != null)
    {
utf8XmlWriter.WriteElement("SenderFault"u8, Convert.ToString(item.SenderFault, CultureInfo.InvariantCulture)!);
    }
    utf8XmlWriter.WriteEndElement(); // member
}
utf8XmlWriter.WriteEndElement(); // collection
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
