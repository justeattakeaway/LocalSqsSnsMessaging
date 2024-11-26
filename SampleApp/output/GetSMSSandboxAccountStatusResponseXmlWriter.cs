using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class GetSMSSandboxAccountStatusResponseXmlWriter
    {
public static MemoryStream Write(GetSMSSandboxAccountStatusResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(GetSMSSandboxAccountStatusResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("GetSMSSandboxAccountStatusResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("GetSMSSandboxAccountStatusResult"u8);

                  if (IsSetIsInSandbox(response))
                  {
                      utf8XmlWriter.WriteElement("IsInSandbox", Convert.ToString(response.IsInSandbox, CultureInfo.InvariantCulture)!);
                   }
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetIsInSandbox))]
        public static extern bool IsSetIsInSandbox(GetSMSSandboxAccountStatusResponse response);
    }
}
