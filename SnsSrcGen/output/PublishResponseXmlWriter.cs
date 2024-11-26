using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class PublishResponseXmlWriter
    {
public static MemoryStream Write(PublishResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(PublishResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("PublishResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("PublishResult"u8);

                  if (IsSetMessageId(response))
                  {
                      utf8XmlWriter.WriteElement("MessageId", Convert.ToString(response.MessageId, CultureInfo.InvariantCulture)!);
                   }

                  if (IsSetSequenceNumber(response))
                  {
                      utf8XmlWriter.WriteElement("SequenceNumber", Convert.ToString(response.SequenceNumber, CultureInfo.InvariantCulture)!);
                   }
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetMessageId))]
        public static extern bool IsSetMessageId(PublishResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetSequenceNumber))]
        public static extern bool IsSetSequenceNumber(PublishResponse response);
    }
}
