using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
namespace Amazon.SimpleNotificationService.Model
{
    public static class ListOriginationNumbersResponseXmlWriter
    {
public static MemoryStream Write(ListOriginationNumbersResponse response)
{
    Debug.Assert(response is not null);
    var memoryStream = MemoryStreamFactory.GetStream(nameof(ListOriginationNumbersResponse));
    using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);

    utf8XmlWriter.WriteStartElement("ListOriginationNumbersResponse"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);

    utf8XmlWriter.WriteStartElement("ListOriginationNumbersResult"u8);

                  if (IsSetNextToken(response))
                  {
                      utf8XmlWriter.WriteElement("NextToken", Convert.ToString(response.NextToken, CultureInfo.InvariantCulture)!);
                   }

                  if (IsSetPhoneNumbers(response))
                  {
                      utf8XmlWriter.WriteStartElement("PhoneNumbers"u8);
                      foreach (var item in response.PhoneNumbers)
                      {
                          utf8XmlWriter.WriteStartElement("member"u8);
               utf8XmlWriter.WriteElement("CreatedAt", Convert.ToString(item.CreatedAt, CultureInfo.InvariantCulture)!);
               if (item.Iso2CountryCode != null)
               {
                   utf8XmlWriter.WriteElement("Iso2CountryCode", Convert.ToString(item.Iso2CountryCode, CultureInfo.InvariantCulture)!);
               }
               if (item.NumberCapabilities != null)
               {
                   utf8XmlWriter.WriteElement("NumberCapabilities", Convert.ToString(item.NumberCapabilities, CultureInfo.InvariantCulture)!);
               }
               if (item.PhoneNumber != null)
               {
                   utf8XmlWriter.WriteElement("PhoneNumber", Convert.ToString(item.PhoneNumber, CultureInfo.InvariantCulture)!);
               }
               if (item.RouteType != null)
               {
                   utf8XmlWriter.WriteElement("RouteType", Convert.ToString(item.RouteType, CultureInfo.InvariantCulture)!);
               }
               if (item.Status != null)
               {
                   utf8XmlWriter.WriteElement("Status", Convert.ToString(item.Status, CultureInfo.InvariantCulture)!);
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
        public static extern bool IsSetNextToken(ListOriginationNumbersResponse response);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSetPhoneNumbers))]
        public static extern bool IsSetPhoneNumbers(ListOriginationNumbersResponse response);
    }
}
