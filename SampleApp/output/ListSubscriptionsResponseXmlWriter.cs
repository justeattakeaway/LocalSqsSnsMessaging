using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListSubscriptionsResponseXmlWriter
    {
public static MemoryStream Write(ListSubscriptionsResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListSubscriptionsResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("ListSubscriptionsResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("ListSubscriptionsResult"u8);

                  if (IsSetNextToken(response))
                  {
                      utf8XmlWriter.WriteElement("NextToken", Convert.ToString(response.NextToken, CultureInfo.InvariantCulture)!);
                   }

                  if (IsSetSubscriptions(response))
                  {
                      utf8XmlWriter.WriteStartElement("Subscriptions"u8);
                      foreach (var item in response.Subscriptions)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               if (item.Endpoint != null)
               {
                   utf8XmlWriter.WriteElement("Endpoint", Convert.ToString(item.Endpoint, CultureInfo.InvariantCulture)!);
               }
               if (item.Owner != null)
               {
                   utf8XmlWriter.WriteElement("Owner", Convert.ToString(item.Owner, CultureInfo.InvariantCulture)!);
               }
               if (item.Protocol != null)
               {
                   utf8XmlWriter.WriteElement("Protocol", Convert.ToString(item.Protocol, CultureInfo.InvariantCulture)!);
               }
               if (item.SubscriptionArn != null)
               {
                   utf8XmlWriter.WriteElement("SubscriptionArn", Convert.ToString(item.SubscriptionArn, CultureInfo.InvariantCulture)!);
               }
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
        public static extern bool IsSetNextToken(ListSubscriptionsResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetSubscriptions))]
        public static extern bool IsSetSubscriptions(ListSubscriptionsResponse response);
    }
}
