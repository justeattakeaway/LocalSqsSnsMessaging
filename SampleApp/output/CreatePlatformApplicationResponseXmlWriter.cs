using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class CreatePlatformApplicationResponseXmlWriter
    {
public static MemoryStream Write(CreatePlatformApplicationResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(CreatePlatformApplicationResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("CreatePlatformApplicationResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("CreatePlatformApplicationResult"u8);

                  if (IsSetPlatformApplicationArn(response))
                  {
                      utf8XmlWriter.WriteElement("PlatformApplicationArn", Convert.ToString(response.PlatformApplicationArn, CultureInfo.InvariantCulture)!);
                   }
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetPlatformApplicationArn))]
        public static extern bool IsSetPlatformApplicationArn(CreatePlatformApplicationResponse response);
    }
}
