using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListSMSSandboxPhoneNumbersResponseXmlWriter
    {
public static MemoryStream Write(ListSMSSandboxPhoneNumbersResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListSMSSandboxPhoneNumbersResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("ListSMSSandboxPhoneNumbersResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("ListSMSSandboxPhoneNumbersResult"u8);
    if (response.PhoneNumbers != null)
    {
utf8XmlWriter.WriteStartElement("PhoneNumbers"u8);
foreach (var item in response.PhoneNumbers)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.PhoneNumber != null)
    {
        utf8XmlWriter.WriteElement("PhoneNumber"u8, item.PhoneNumber);
    }
    if (item.Status != null)
    {
        utf8XmlWriter.WriteElement("Status"u8, item.Status);
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
