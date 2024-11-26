using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class SetSubscriptionAttributesResponseXmlWriter
    {
public static MemoryStream Write(SetSubscriptionAttributesResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(SetSubscriptionAttributesResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("SetSubscriptionAttributesResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("SetSubscriptionAttributesResult"u8);
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }
    }
}
