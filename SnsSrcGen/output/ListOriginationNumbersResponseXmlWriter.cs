using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListOriginationNumbersResponseXmlWriter
    {
public static MemoryStream Write(ListOriginationNumbersResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListOriginationNumbersResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("ListOriginationNumbersResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("ListOriginationNumbersResult"u8);
    if (response.NextToken != null)
    {
        utf8XmlWriter.WriteElement("NextToken"u8, response.NextToken);
    }
    if (response.PhoneNumbers != null)
    {
utf8XmlWriter.WriteStartElement("PhoneNumbers"u8);
foreach (var item in response.PhoneNumbers)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.CreatedAt != null)
    {
utf8XmlWriter.WriteElement("CreatedAt"u8, Convert.ToString(item.CreatedAt, CultureInfo.InvariantCulture)!);
    }
    if (item.PhoneNumber != null)
    {
        utf8XmlWriter.WriteElement("PhoneNumber"u8, item.PhoneNumber);
    }
    if (item.Status != null)
    {
        utf8XmlWriter.WriteElement("Status"u8, item.Status);
    }
    if (item.Iso2CountryCode != null)
    {
        utf8XmlWriter.WriteElement("Iso2CountryCode"u8, item.Iso2CountryCode);
    }
    if (item.RouteType != null)
    {
        utf8XmlWriter.WriteElement("RouteType"u8, item.RouteType);
    }
    if (item.NumberCapabilities != null)
    {
utf8XmlWriter.WriteStartElement("NumberCapabilities"u8);
foreach (var item in item.NumberCapabilities)
{
    utf8XmlWriter.WriteStartElement("member"u8);
utf8XmlWriter.WriteString(Convert.ToString(item, CultureInfo.InvariantCulture)!);
    utf8XmlWriter.WriteEndElement(); // member
}
utf8XmlWriter.WriteEndElement(); // collection
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
