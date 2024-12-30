using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class GetPlatformApplicationAttributesResponseXmlWriter
    {
public static MemoryStream Write(GetPlatformApplicationAttributesResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(GetPlatformApplicationAttributesResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("GetPlatformApplicationAttributesResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("GetPlatformApplicationAttributesResult"u8);
    if (response.Attributes != null)
    {
utf8XmlWriter.WriteStartElement("Attributes"u8);
foreach (var kvp in response.Attributes)
{
    utf8XmlWriter.WriteStartElement("entry"u8);
    utf8XmlWriter.WriteElement("key"u8, Convert.ToString(kvp.Key, CultureInfo.InvariantCulture)!);
    utf8XmlWriter.WriteElement("value"u8, Convert.ToString(kvp.Value, CultureInfo.InvariantCulture)!);
    utf8XmlWriter.WriteEndElement(); // entry
}
utf8XmlWriter.WriteEndElement(); // map
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
