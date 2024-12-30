using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListEndpointsByPlatformApplicationResponseXmlWriter
    {
public static MemoryStream Write(ListEndpointsByPlatformApplicationResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListEndpointsByPlatformApplicationResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("ListEndpointsByPlatformApplicationResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("ListEndpointsByPlatformApplicationResult"u8);
    if (response.Endpoints != null)
    {
utf8XmlWriter.WriteStartElement("Endpoints"u8);
foreach (var item in response.Endpoints)
{
    utf8XmlWriter.WriteStartElement("member"u8);
    if (item.EndpointArn != null)
    {
        utf8XmlWriter.WriteElement("EndpointArn"u8, item.EndpointArn);
    }
    if (item.Attributes != null)
    {
utf8XmlWriter.WriteStartElement("Attributes"u8);
foreach (var kvp in item.Attributes)
{
    utf8XmlWriter.WriteStartElement("entry"u8);
    utf8XmlWriter.WriteElement("key"u8, Convert.ToString(kvp.Key, CultureInfo.InvariantCulture)!);
    utf8XmlWriter.WriteElement("value"u8, Convert.ToString(kvp.Value, CultureInfo.InvariantCulture)!);
    utf8XmlWriter.WriteEndElement(); // entry
}
utf8XmlWriter.WriteEndElement(); // map
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
