using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class PublishBatchResponseXmlWriter
    {
public static MemoryStream Write(PublishBatchResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(PublishBatchResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("PublishBatchResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("PublishBatchResult"u8);

                  if (IsSetFailed(response))
                  {
                      utf8XmlWriter.WriteStartElement("Failed"u8);
                      foreach (var item in response.Failed)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.Code != null)
               {
                   utf8XmlWriter.WriteElement("Code", Convert.ToString(item.Code, CultureInfo.InvariantCulture)!);
               }
               if (item.Id != null)
               {
                   utf8XmlWriter.WriteElement("Id", Convert.ToString(item.Id, CultureInfo.InvariantCulture)!);
               }
               if (item.Message != null)
               {
                   utf8XmlWriter.WriteElement("Message", Convert.ToString(item.Message, CultureInfo.InvariantCulture)!);
               }
               utf8XmlWriter.WriteElement("SenderFault", Convert.ToString(item.SenderFault, CultureInfo.InvariantCulture)!);
                           utf8XmlWriter.WriteEndElement(); // member
                       }
                       utf8XmlWriter.WriteEndElement(); // collection element
                   }

                  if (IsSetSuccessful(response))
                  {
                      utf8XmlWriter.WriteStartElement("Successful"u8);
                      foreach (var item in response.Successful)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.Id != null)
               {
                   utf8XmlWriter.WriteElement("Id", Convert.ToString(item.Id, CultureInfo.InvariantCulture)!);
               }
               if (item.MessageId != null)
               {
                   utf8XmlWriter.WriteElement("MessageId", Convert.ToString(item.MessageId, CultureInfo.InvariantCulture)!);
               }
               if (item.SequenceNumber != null)
               {
                   utf8XmlWriter.WriteElement("SequenceNumber", Convert.ToString(item.SequenceNumber, CultureInfo.InvariantCulture)!);
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

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetFailed))]
        public static extern bool IsSetFailed(PublishBatchResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetSuccessful))]
        public static extern bool IsSetSuccessful(PublishBatchResponse response);
    }
}
