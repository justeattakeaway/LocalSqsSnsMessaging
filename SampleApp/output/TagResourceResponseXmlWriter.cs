using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class TagResourceResponseXmlWriter
    {
public static MemoryStream Write(TagResourceResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(TagResourceResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
    
    utf8XmlWriter.WriteStartElement("TagResourceResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
    utf8XmlWriter.WriteStartElement("TagResourceResult"u8);
    utf8XmlWriter.WriteEndElement(); // Result wrapper
    
    SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
    utf8XmlWriter.WriteEndElement(); // Response
    utf8XmlWriter.Flush();
    
    memoryStream.Position = 0;
    return memoryStream;
}
    }
}
