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

                  if (IsSetEndpoints(response))
                  {
                      utf8XmlWriter.WriteStartElement("Endpoints"u8);
                      foreach (var item in response.Endpoints)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.Attributes != null)
               {
                   utf8XmlWriter.WriteElement("Attributes", Convert.ToString(item.Attributes, CultureInfo.InvariantCulture)!);
               }
               if (item.EndpointArn != null)
               {
                   utf8XmlWriter.WriteElement("EndpointArn", Convert.ToString(item.EndpointArn, CultureInfo.InvariantCulture)!);
               }
                           utf8XmlWriter.WriteEndElement(); // member
                       }
                       utf8XmlWriter.WriteEndElement(); // collection element
                   }

                  if (IsSetNextToken(response))
                  {
                      utf8XmlWriter.WriteElement("NextToken", Convert.ToString(response.NextToken, CultureInfo.InvariantCulture)!);
                   }
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetEndpoints))]
        public static extern bool IsSetEndpoints(ListEndpointsByPlatformApplicationResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetNextToken))]
        public static extern bool IsSetNextToken(ListEndpointsByPlatformApplicationResponse response);
    }
}
