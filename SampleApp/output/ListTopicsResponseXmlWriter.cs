using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListTopicsResponseXmlWriter
    {
public static MemoryStream Write(ListTopicsResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListTopicsResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("ListTopicsResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("ListTopicsResult"u8);

                  if (IsSetNextToken(response))
                  {
                      utf8XmlWriter.WriteElement("NextToken", Convert.ToString(response.NextToken, CultureInfo.InvariantCulture)!);
                   }

                  if (IsSetTopics(response))
                  {
                      utf8XmlWriter.WriteStartElement("Topics"u8);
                      foreach (var item in response.Topics)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.TopicArn != null)
               {
                   utf8XmlWriter.WriteElement("TopicArn", Convert.ToString(item.TopicArn, CultureInfo.InvariantCulture)!);
               }
                           utf8XmlWriter.WriteEndElement(); // member
                       }
                       utf8XmlWriter.WriteEndElement(); // collection element
                   }
    
                          utf8XmlWriter.WriteEndElement(); // Result
    
                          SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
    
                          utf8XmlWriter.WriteEndElement(); // Response
                          utf8XmlWriter.WriteEndElement();
                          utf8XmlWriter.Flush();
                          
                          memoryStream.Position = 0;
    
                          return memoryStream;
                      }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetNextToken))]
        public static extern bool IsSetNextToken(ListTopicsResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetTopics))]
        public static extern bool IsSetTopics(ListTopicsResponse response);
    }
}
