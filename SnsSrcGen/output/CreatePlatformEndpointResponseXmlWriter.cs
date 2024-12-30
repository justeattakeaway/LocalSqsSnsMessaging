using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class CreatePlatformEndpointResponseXmlWriter
    {
public static MemoryStream Write(CreatePlatformEndpointResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(CreatePlatformEndpointResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("CreatePlatformEndpointResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("CreatePlatformEndpointResult"u8);
    if (response.EndpointArn != null)
    {
        utf8XmlWriter.WriteElement("EndpointArn"u8, response.EndpointArn);
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
