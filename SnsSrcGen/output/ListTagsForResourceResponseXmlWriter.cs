using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListTagsForResourceResponseXmlWriter
    {
public static MemoryStream Write(ListTagsForResourceResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListTagsForResourceResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("ListTagsForResourceResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("ListTagsForResourceResult"u8);

                  if (IsSetTags(response))
                  {
                      utf8XmlWriter.WriteStartElement("Tags"u8);
                      foreach (var item in response.Tags)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.Key != null)
               {
                   utf8XmlWriter.WriteElement("Key", Convert.ToString(item.Key, CultureInfo.InvariantCulture)!);
               }
               if (item.Value != null)
               {
                   utf8XmlWriter.WriteElement("Value", Convert.ToString(item.Value, CultureInfo.InvariantCulture)!);
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

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetTags))]
        public static extern bool IsSetTags(ListTagsForResourceResponse response);
    }
}
